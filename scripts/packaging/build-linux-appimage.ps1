<#
.SYNOPSIS
    Build a clean Linux AppImage inside a disposable Docker container.

.DESCRIPTION
    Freezes HEAD SHA, extracts submodule gitlinks, runs the AppImage build
    pipeline inside ubuntu:22.04. Default: committed snapshot only.
    Use -IncludeWorkingTreeChanges for tracked diffs + named tooling overlay.

.PARAMETER Version         Required. SemVer version string.
.PARAMETER OutputDir       Output directory. Defaults to publish/clean-linux-{timestamp}.
.PARAMETER IncludeWorkingTreeChanges  Overlay tracked diffs + named gate/driver.
.PARAMETER PrepareOnly     Materialize sources in Docker without running SDK build.
.PARAMETER KeepScratch     Retain staging directory after success.
#>

param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$OutputDir,

    [switch]$IncludeWorkingTreeChanges,

    [switch]$PrepareOnly,

    [switch]$KeepScratch
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ── Helpers ──────────────────────────────────────────────────────────────────

function Invoke-Native {
    # Runs a native command with safe stderr handling under EAP=Stop.
    # Stderr is written to console immediately. On failure, throws with
    # collected output so error details are never swallowed.
    param(
        [Parameter(Mandatory)] [string]$Description,
        [Parameter(Mandatory)] [scriptblock]$Command
    )
    $prevEAP = $ErrorActionPreference
    $allLines = [System.Collections.Generic.List[string]]::new()
    try {
        $ErrorActionPreference = "Continue"
        $rawOutput = & $Command 2>&1
        $exitCode = $LASTEXITCODE
        if ($null -ne $rawOutput) {
            foreach ($item in @($rawOutput)) {
                if ($item -is [System.Management.Automation.ErrorRecord]) {
                    $line = $item.Exception.Message
                    Write-Host "$Description stderr: $line" -ForegroundColor DarkYellow
                } else {
                    $line = "$item"
                    Write-Host $line
                }
                $allLines.Add($line)
            }
        }
    } finally { $ErrorActionPreference = $prevEAP }
    if ($exitCode -ne 0) {
        $outputDump = ($allLines | Select-Object -Last 50) -join "`n"
        throw "$Description failed (exit $exitCode). Last 50 lines:`n$outputDump"
    }
    return $allLines.ToArray()
}

function Invoke-Docker {
    # Runs docker with streaming output to console and log file.
    # Uses EAP=Continue so stderr ErrorRecords don't throw.
    # Pipeline ForEach streams each line immediately (no buffering).
    # $LASTEXITCODE captured after pipeline completes.
    # Returns exit code.
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [string]$LogPath
    )
    $prevEAP = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & docker @Arguments 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                $line = $_.Exception.Message
            } else {
                $line = "$_"
            }
            Write-Host $line
            if ($LogPath) {
                $line | Add-Content -LiteralPath $LogPath -Encoding UTF8 -ErrorAction Stop
            }
        }
        $exitCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prevEAP }
    return $exitCode
}

function Get-RepoRoot {
    $dir = $PSScriptRoot
    while ($dir) {
        if (Test-Path -LiteralPath (Join-Path $dir 'MqttProbe.slnx')) { return $dir }
        $parent = Split-Path $dir -Parent
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    throw 'Could not find repo root (MqttProbe.slnx not found)'
}

function Test-SemVer {
    param([string]$v)
    return $v -match '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$'
}

# ── Freeze source SHA ────────────────────────────────────────────────────────

$RepoRoot = Get-RepoRoot
$shaOutput = Invoke-Native "git rev-parse HEAD" { git -C $RepoRoot rev-parse HEAD }
$SourceSHA = ($shaOutput | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($SourceSHA) -or $SourceSHA.Length -ne 40) {
    throw "git rev-parse HEAD returned invalid SHA: '$SourceSHA'"
}
Write-Host "Source SHA: $SourceSHA" -ForegroundColor Cyan

# ── Validate version (SemVer) ────────────────────────────────────────────────

$cleanVersion = $Version -replace '^v', ''
if ([string]::IsNullOrWhiteSpace($cleanVersion)) {
    throw "Version is empty after stripping 'v' prefix."
}
if (-not (Test-SemVer $cleanVersion)) {
    throw "Version '$cleanVersion' is not valid SemVer (expected major.minor.patch)."
}
if ($cleanVersion -eq '0.0.0') {
    throw "Version '0.0.0' is not a valid release version."
}

# ── Locate paths (PS5.1-safe: no Join-Path varargs) ─────────────────────────

$scriptsDir = Join-Path $RepoRoot "scripts"
$linuxScriptDir = Join-Path (Join-Path $scriptsDir "packaging") "linux"
$buildScript = Join-Path $linuxScriptDir "build-clean-appimage.sh"
if (-not (Test-Path -LiteralPath $buildScript)) {
    throw "Required bootstrap tool not found: $buildScript"
}

# ── Validate prerequisites ──────────────────────────────────────────────────

$null = Invoke-Native "git --version" { git --version }
$null = Invoke-Native "docker info" { docker info }

# ── Gate availability (check committed or working tree) ─────────────────────

$gateScript = Join-Path $linuxScriptDir "check-scoped-css.py"
$gateInWorkingTree = Test-Path -LiteralPath $gateScript
$gateCommitted = $false
if (-not $gateInWorkingTree) {
    $lsOut = Invoke-Native "git ls-tree gate" {
        git -C $RepoRoot ls-tree -r $SourceSHA -- "scripts/packaging/linux/check-scoped-css.py"
    }
    if ($lsOut.Count -gt 0 -and $lsOut[0] -match '160000|100644|100755') { $gateCommitted = $true }
}
if (-not $gateInWorkingTree -and -not $gateCommitted) {
    throw "check-scoped-css.py not found. Commit it or have it in working tree."
}

# ── Output directory ────────────────────────────────────────────────────────

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $ts = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = Join-Path (Join-Path $RepoRoot "publish") "clean-linux-$ts"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
if ((Test-Path -LiteralPath $OutputDir) -and
    (Get-ChildItem -LiteralPath $OutputDir -Force | Select-Object -First 1)) {
    throw "Output directory is not empty: $OutputDir"
}

# ── Scratch directory ───────────────────────────────────────────────────────

$scratchRoot = Join-Path $env:TEMP "mqttprobe-clean-build"
if (-not (Test-Path -LiteralPath $scratchRoot)) {
    New-Item -ItemType Directory -Path $scratchRoot | Out-Null
}
$guid = [System.Guid]::NewGuid().ToString("N").Substring(0, 12)
$ts = Get-Date -Format "yyyyMMdd-HHmmss"
$scratchDir = Join-Path $scratchRoot "build-$ts-$guid"
if (Test-Path -LiteralPath $scratchDir) {
    if (Get-ChildItem -LiteralPath $scratchDir -Force | Select-Object -First 1) {
        throw "Scratch exists and non-empty: $scratchDir"
    }
}
New-Item -ItemType Directory -Path $scratchDir | Out-Null

$normS = $scratchDir.TrimEnd('\', '/').ToLowerInvariant()
$normO = $OutputDir.TrimEnd('\', '/').ToLowerInvariant()
if ($normO.StartsWith($normS) -or $normS.StartsWith($normO)) {
    Remove-Item -Path $scratchDir -Recurse -Force -ErrorAction SilentlyContinue
    throw "Output must not be inside scratch."
}

Write-Host "Scratch: $scratchDir" -ForegroundColor Gray
Write-Host "Output:  $OutputDir" -ForegroundColor Gray

$buildSucceeded = $false
$preserveScratch = $false

try {
    # ── Stage tooling to scratch/tooling/ ───────────────────────────────────

    $toolingDir = Join-Path $scratchDir "tooling"
    New-Item -ItemType Directory -Path $toolingDir | Out-Null

    # Stage gate script
    if ($gateInWorkingTree) {
        Copy-Item -LiteralPath $gateScript -Destination (Join-Path $toolingDir "check-scoped-css.py") -Force
    } else {
        # Extract from committed SHA
        $gateLines = Invoke-Native "git show gate" {
            git -C $RepoRoot show "${SourceSHA}:scripts/packaging/linux/check-scoped-css.py"
        }
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllLines((Join-Path $toolingDir "check-scoped-css.py"), $gateLines, $utf8NoBom)
    }
    # Stage build driver
    Copy-Item -LiteralPath $buildScript -Destination (Join-Path $toolingDir "build-clean-appimage.sh") -Force
    # Record hashes (UTF8 no BOM for cross-platform compatibility)
    $toolHashes = @{}
    foreach ($f in Get-ChildItem -LiteralPath $toolingDir -File) {
        $toolHashes[$f.Name] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $toolingDir "manifest.json"),
        ($toolHashes | ConvertTo-Json), $utf8NoBom)
    Write-Host "Tooling staged: $($toolHashes.Keys -join ', ')" -ForegroundColor Gray

    # ── Git archive ─────────────────────────────────────────────────────────

    Write-Host "`n=== Creating source archive ===" -ForegroundColor Cyan
    $sourceTar = Join-Path $scratchDir "source.tar.gz"
    Invoke-Native "git archive" {
        git -C $RepoRoot archive "--format=tar.gz" "--output=$sourceTar" $SourceSHA
    } | Out-Null
    $archiveSize = (Get-Item -LiteralPath $sourceTar).Length
    if ($archiveSize -eq 0) { throw "git archive produced empty file" }
    $archiveHash = (Get-FileHash -LiteralPath $sourceTar -Algorithm SHA256).Hash
    # Write hash for SH-side verification (UTF8 no BOM)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $scratchDir "archive-hash.txt"), $archiveHash.ToLower(), $utf8NoBom)
    Write-Host "Archive: $([math]::Round($archiveSize / 1MB, 1)) MB, SHA256: $($archiveHash.Substring(0,16))..." -ForegroundColor Gray

    # ── Submodule metadata (JSON from frozen SHA) ───────────────────────────

    Write-Host "`n=== Extracting submodule metadata ===" -ForegroundColor Cyan
    $submodulesFile = Join-Path $scratchDir "submodules.json"

    $gitTreeLines = Invoke-Native "git ls-tree -r" {
        git -C $RepoRoot ls-tree -r $SourceSHA
    }
    $gitmodLines = Invoke-Native "git show .gitmodules" {
        git -C $RepoRoot show "${SourceSHA}:.gitmodules"
    }
    $gitmodContent = $gitmodLines -join "`n"

    $submoduleEntries = @()
    foreach ($line in $gitTreeLines) {
        if ($line -match '^160000\s+commit\s+([0-9a-f]{40})\s+(.+)$') {
            $commit = $Matches[1]
            $path = $Matches[2]
            $urlMatch = [regex]::Match(
                $gitmodContent,
                "(?s)\[submodule `"$([regex]::Escape($path))`"\].*?url\s*=\s*(\S+)"
            )
            if (-not $urlMatch.Success) {
                throw "No URL in .gitmodules for submodule $path"
            }
            $submoduleEntries += @{
                path = $path; url = $urlMatch.Groups[1].Value.Trim(); commit = $commit
            }
            Write-Host "  $path -> $commit" -ForegroundColor Gray
        }
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($submodulesFile,
        (ConvertTo-Json -InputObject $submoduleEntries -Depth 3), $utf8NoBom)
    Write-Host "Submodules: $($submoduleEntries.Count) entries" -ForegroundColor Gray

    # ── Working tree changes (tracked only, reject dirty submodules) ────────

    $patchFile = Join-Path $scratchDir "changes.patch"
    [System.IO.File]::WriteAllBytes($patchFile, [byte[]]@())

    if ($IncludeWorkingTreeChanges) {
        Write-Host "`n=== Capturing tracked changes ===" -ForegroundColor Cyan

        # Reject dirty submodules
        foreach ($sm in $submoduleEntries) {
            $dirtyCheck = Invoke-Native "git diff submodule $($sm.path)" {
                git -C $RepoRoot diff --submodule=short HEAD -- $sm.path
            }
            $dirtyText = $dirtyCheck -join "`n"
            if ($dirtyText -match '\+') {
                throw "Dirty submodule: $($sm.path). Commit or stash changes first."
            }
        }

        # Binary diff for tracked changes
        Invoke-Native "git diff --binary" {
            git -C $RepoRoot diff HEAD --binary "--output=$patchFile"
        } | Out-Null
        $patchSize = (Get-Item -LiteralPath $patchFile).Length
        Write-Host "Patch: $patchSize bytes" -ForegroundColor Gray
    }

    # ── Docker run ──────────────────────────────────────────────────────────

    $modeLabel = if ($PrepareOnly) { "Materializing" } else { "Building" }
    Write-Host "`n=== Docker: $modeLabel ===" -ForegroundColor Cyan

    # ALL docker options BEFORE image name; command AFTER image
    $dockerOpts = @(
        "run", "--rm",
        "-v", "${scratchDir}:/scratch",
        "-v", "${scriptsDir}:/scripts:ro",
        "-e", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
        "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1"
    )
    if (-not $PrepareOnly) {
        $dockerOpts += "-e", "NUGET_PACKAGES=/scratch/.nuget/packages"
    }
    $dockerOpts += "ubuntu:22.04"
    # Command and args come after image
    $dockerCmd = @(
        "bash", "/scratch/tooling/build-clean-appimage.sh",
        "--version", $cleanVersion,
        "--source-sha", $SourceSHA,
        "--scratch", "/scratch"
    )
    if ($IncludeWorkingTreeChanges) { $dockerCmd += "--include-changes" }
    if ($PrepareOnly) { $dockerCmd += "--prepare-only" }

    $allDockerArgs = $dockerOpts + $dockerCmd
    $buildLog = Join-Path $scratchDir "build.log"
    $exitCode = Invoke-Docker -Arguments $allDockerArgs -LogPath $buildLog
    if ($exitCode -ne 0) {
        $preserveScratch = $true
        throw "Docker failed with exit code $exitCode. Log: $buildLog"
    }

    # ── PrepareOnly: verify materialization ─────────────────────────────────

    if ($PrepareOnly) {
        $workDir = Join-Path $scratchDir "work"
        if (-not (Test-Path -LiteralPath $workDir)) {
            $preserveScratch = $true
            throw "PrepareOnly: work directory not found"
        }
        $slnxFile = Join-Path $workDir "MqttProbe.slnx"
        if (-not (Test-Path -LiteralPath $slnxFile)) {
            $preserveScratch = $true
            throw "PrepareOnly: MqttProbe.slnx not found"
        }
        if ($submoduleEntries.Count -gt 0) {
            $subGit = Join-Path (Join-Path (Join-Path $workDir "external") "SparkplugNet") ".git"
            if (-not (Test-Path -LiteralPath $subGit)) {
                $preserveScratch = $true
                throw "PrepareOnly: submodule .git not found"
            }
        }
        $allHashes = (Get-ChildItem -LiteralPath $workDir -Recurse -File |
            Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash } | Sort-Object) -join ""
        $fingerprint = [BitConverter]::ToString(
            [System.Security.Cryptography.SHA256]::Create().ComputeHash(
                [System.Text.Encoding]::UTF8.GetBytes($allHashes))
        ).Replace("-", "").Substring(0, 16).ToLower()

        Write-Host "`n=== PrepareOnly: materialized ===" -ForegroundColor Green
        Write-Host "SHA:           $SourceSHA" -ForegroundColor Green
        Write-Host "Version:       $cleanVersion" -ForegroundColor Green
        Write-Host "Submodules:    $($submoduleEntries.Count)" -ForegroundColor Green
        Write-Host "Patch:         $((Get-Item -LiteralPath $patchFile).Length) bytes" -ForegroundColor Green
        Write-Host "Snapshot hash: $fingerprint" -ForegroundColor Green
        Write-Host "Scratch:       $scratchDir" -ForegroundColor Green
        $preserveScratch = $true; $buildSucceeded = $true; return
    }

    # ── Full build: copy artifacts ──────────────────────────────────────────

    $dockerOutput = Join-Path $scratchDir "output"
    if (-not (Test-Path -LiteralPath $dockerOutput)) {
        $preserveScratch = $true; throw "Output not found at $dockerOutput"
    }
    $outputFiles = Get-ChildItem -LiteralPath $dockerOutput -Force
    if ($null -eq $outputFiles -or $outputFiles.Count -eq 0) {
        $preserveScratch = $true; throw "Output empty: $dockerOutput"
    }
    if ((Test-Path -LiteralPath $OutputDir) -and
        (Get-ChildItem -LiteralPath $OutputDir -Force | Select-Object -First 1)) {
        throw "Output became non-empty: $OutputDir"
    }
    if (-not (Test-Path -LiteralPath $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    }
    Get-ChildItem -LiteralPath $dockerOutput -Force |
        Copy-Item -Destination $OutputDir -Recurse -Force
    if (-not (Get-ChildItem -LiteralPath $OutputDir -Force | Select-Object -First 1)) {
        $preserveScratch = $true; throw "Copy failed: no files at $OutputDir"
    }
    Write-Host "`n=== Build complete ===" -ForegroundColor Green
    Write-Host "Output: $OutputDir" -ForegroundColor Green
    $buildSucceeded = $true
}
catch {
    Write-Host "`nERROR: $_" -ForegroundColor Red
    if ($preserveScratch -or (-not $buildSucceeded)) {
        Write-Host "Scratch preserved: $scratchDir" -ForegroundColor Yellow
    }
    exit 1
}
finally {
    if ($buildSucceeded -and -not $KeepScratch -and -not $preserveScratch) {
        if (Test-Path -LiteralPath $scratchDir) {
            Remove-Item -LiteralPath $scratchDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    } elseif ($buildSucceeded -and $KeepScratch) {
        Write-Host "Scratch retained: $scratchDir" -ForegroundColor Yellow
    }
}