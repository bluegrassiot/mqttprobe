<#
.SYNOPSIS
    Backup, delete, or restore the MQTTProbe (GitHub release / unpackaged) config.
.EXAMPLE
    .\manage-windows-config.ps1 -Backup
    .\manage-windows-config.ps1 -Backup -Delete
    .\manage-windows-config.ps1 -Delete -Full     # also removes data from older builds
    .\manage-windows-config.ps1 -Restore "$env:USERPROFILE\Documents\MqttProbeBackups\MqttProbeConfig_20260711_180000"
#>
[CmdletBinding()]
param(
    [switch]$Backup,
    [switch]$Delete,
    [switch]$Full,
    [string]$BackupPath = "$env:USERPROFILE\Documents\MqttProbeBackups",
    [string]$Restore
)

# Active config for the unpackaged GitHub-release build.
# Path is <publisher>\<app id>, where publisher comes from PublisherDisplayName
# in Platforms\Windows\Package.appxmanifest.
$appRoot   = Join-Path $env:LOCALAPPDATA 'Bluegrass IoT\com.bluegrassiot.mqttprobe'
$configDir = Join-Path $appRoot 'Data\config'

# Stale data from older builds (only touched with -Full):
# v1.0.1 and earlier shipped with the template placeholder publisher "User Name",
# and dev/MSIX builds used other identities.
$staleRoots = @(
    (Join-Path $env:LOCALAPPDATA 'User Name\com.bluegrassiot.mqttprobe'),
    (Join-Path $env:LOCALAPPDATA 'User Name\com.bluegrassiot.mqttprobemaui'),
    (Join-Path $env:LOCALAPPDATA 'Packages\com.bluegrassiot.mqttprobemaui_pj3kaw9m8kc5y')
)

if (-not ($Backup -or $Delete -or $Restore)) {
    Write-Host "Nothing to do. Use -Backup, -Delete, and/or -Restore <path>." -ForegroundColor Yellow
    return
}

# Stop the app first — it rewrites appsettings.json from memory on exit,
# which would resurrect the config right after deletion.
$proc = Get-Process -Name 'MqttProbe.Maui' -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Stopping running MQTTProbe instance..."
    $proc | Stop-Process -Force
    Start-Sleep -Seconds 1
}

if ($Restore) {
    if (-not (Test-Path $Restore)) { throw "Backup folder not found: $Restore" }
    New-Item -ItemType Directory -Force $configDir | Out-Null
    Copy-Item -Path (Join-Path $Restore '*') -Destination $configDir -Recurse -Force
    Write-Host "Restored config from: $Restore" -ForegroundColor Green
    return
}

if ($Backup) {
    if (Test-Path $configDir) {
        $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $dest  = Join-Path $BackupPath "MqttProbeConfig_$stamp"
        New-Item -ItemType Directory -Force $dest | Out-Null
        Copy-Item -Path (Join-Path $configDir '*') -Destination $dest -Recurse -Force
        Write-Host "Backed up config to: $dest" -ForegroundColor Green
    } else {
        Write-Host "No config found at $configDir - nothing to back up." -ForegroundColor Yellow
        if ($Delete) { return }   # never delete when a requested backup couldn't happen
    }
}

if ($Delete) {
    $targets = @($appRoot)          # whole app data dir: config + securestorage.dat
    if ($Full) { $targets += $staleRoots }
    foreach ($t in $targets) {
        if (Test-Path $t) {
            Remove-Item $t -Recurse -Force
            Write-Host "Deleted: $t" -ForegroundColor Green
        } else {
            Write-Host "Not found (already clean): $t" -ForegroundColor Yellow
        }
    }
}
