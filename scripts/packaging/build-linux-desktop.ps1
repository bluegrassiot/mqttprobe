param(
    [ValidateSet("linux-x64", "linux-arm64")]
    [string]$Rid = "linux-x64"
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    $dir = $PSScriptRoot
    while ($dir) {
        if (Test-Path -LiteralPath (Join-Path $dir 'MqttProbe.slnx')) { return $dir }
        $parent = Split-Path $dir -Parent
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    throw 'Could not find repo root (MqttProbe.slnx)'
}
$RepoRoot = Get-RepoRoot
Set-Location $RepoRoot

$outputDir = "publish/desktop-$Rid"
$archive = "mqttprobe-desktop-$Rid-test.zip"

# Clear and recreate the RID-specific output directory
if (Test-Path $outputDir) {
    Remove-Item $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outputDir | Out-Null

Write-Host "Publishing MqttProbe.Desktop for $Rid..." -ForegroundColor Cyan

dotnet publish src/MqttProbe.Desktop/MqttProbe.Desktop.csproj `
    -c Release `
    -r $Rid `
    --self-contained true `
    --output $outputDir

# Delete existing archive before creating a new one
$archivePath = Join-Path $RepoRoot $archive
if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force
}

# Archive contents of the output directory (not the directory itself)
Compress-Archive -Path "$outputDir\*" -DestinationPath $archivePath

Write-Host ""
Write-Host "Archive created: $archivePath" -ForegroundColor Green
Write-Host ""
Write-Host "To test on Linux:"
Write-Host "  unzip $archive -d mqttprobe-desktop-test"
Write-Host "  cd mqttprobe-desktop-test"
Write-Host "  sudo apt install libwebkit2gtk-4.1-0"
Write-Host "  # Windows zips often drop +x on dirs; without it WebKit cannot read wwwroot/_content"
Write-Host "  chmod -R a+rX wwwroot && chmod +x MqttProbe.Desktop"
Write-Host "  ./MqttProbe.Desktop"
