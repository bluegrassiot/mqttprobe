param(
    [string]$Version = "1.0.0"
)

Set-Location (Split-Path $PSScriptRoot -Parent)

$publishDir = "publish/windows-local"
$outputDir  = "publish/velopack-local"

Write-Host "Building version: $Version" -ForegroundColor Cyan

dotnet publish src/MqttProbe.Maui/MqttProbe.Maui.csproj `
    -f:net10.0-windows10.0.19041.0 `
    -p:MqttProbeMauiWindowsTargetFrameworksOverride=net10.0-windows10.0.19041.0 `
    -c:Release `
    --output $publishDir `
    -p:ApplicationDisplayVersion=$Version `
    -p:ApplicationVersion=1

Write-Host "Packing with Velopack..." -ForegroundColor Cyan

vpk pack --packId MQTTProbe `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe MqttProbe.Maui.exe `
    --packTitle MQTTProbe `
    --packAuthors "Bluegrass IoT" `
    --icon src/MqttProbe.Desktop/Assets/icon.ico `
    --outputDir $outputDir

# ── Verify expected outputs ──────────────────────────────────────
Write-Host "`nVerifying output files..." -ForegroundColor Cyan

$expected = @(
    "MQTTProbe-win-Setup.exe",
    "releases.win.json",
    "assets.win.json"
)

$missing = @()
foreach ($file in $expected) {
    $path = Join-Path $outputDir $file
    if (Test-Path $path) {
        $size = (Get-Item $path).Length / 1MB
        Write-Host "  [PASS] $file ($size MB)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $file (MISSING)" -ForegroundColor Red
        $missing += $file
    }
}

# Check for nupkg (wildcard in case version format varies)
$nupkgFiles = Get-ChildItem $outputDir -Filter "MQTTProbe-*-full.nupkg"
if ($nupkgFiles.Count -gt 0) {
    foreach ($nupkg in $nupkgFiles) {
        $size = $nupkg.Length / 1MB
        Write-Host "  [PASS] $($nupkg.Name) ($size MB)" -ForegroundColor Green
    }
} else {
    Write-Host "  [FAIL] MQTTProbe-*-full.nupkg (MISSING)" -ForegroundColor Red
    $missing += "MQTTProbe-*-full.nupkg"
    
    # Show what IS there
    Write-Host "  Available files:" -ForegroundColor Yellow
    Get-ChildItem $outputDir | ForEach-Object {
        Write-Host "    $($_.Name)" -ForegroundColor DarkYellow
    }
}

if ($missing.Count -gt 0) {
    Write-Host "`nMissing files: $($missing -join ', ')" -ForegroundColor Yellow
    exit 1
}

Write-Host "`nAll expected files verified!" -ForegroundColor Green
