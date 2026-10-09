$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repositoryRoot 'src\MqttProbe.Desktop\MqttProbe.Desktop.csproj'
$executable = Join-Path $repositoryRoot 'src\MqttProbe.Desktop\bin\Debug\net10.0\MqttProbe.Desktop.exe'
$dataRoot = Join-Path $env:TEMP 'MqttProbe-Desktop-Update-Simulation'
$runDirectory = Join-Path $dataRoot ([Guid]::NewGuid().ToString('N'))

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Desktop project not found: $project"
}

$existingInstance = Get-CimInstance Win32_Process -Filter "Name = 'MqttProbe.Desktop.exe'" |
    Where-Object { $_.ExecutablePath -ieq $executable } |
    Select-Object -First 1
if ($existingInstance) {
    throw "Close the existing Desktop process (PID $($existingInstance.ProcessId)) before building the simulation."
}

Write-Host "Initializing pinned submodules in $repositoryRoot"
& git -C $repositoryRoot submodule update --init --recursive
if ($LASTEXITCODE -ne 0) {
    throw "Submodule initialization failed with exit code $LASTEXITCODE"
}

Write-Host 'Building Debug Desktop'
& dotnet build $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    throw "Debug Desktop build failed with exit code $LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Expected Desktop executable was not produced: $executable"
}

$resolvedDataRoot = [IO.Path]::GetFullPath($dataRoot).TrimEnd('\') + '\'
$resolvedRunDirectory = [IO.Path]::GetFullPath($runDirectory)
if (-not $resolvedRunDirectory.StartsWith($resolvedDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create run data outside its temp root: $resolvedRunDirectory"
}

New-Item -ItemType Directory -Path (Join-Path $runDirectory 'config') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runDirectory 'webview2') -Force | Out-Null

$oldConfigHome = [Environment]::GetEnvironmentVariable('XDG_CONFIG_HOME', 'Process')
$oldWebViewFolder = [Environment]::GetEnvironmentVariable('WEBVIEW2_USER_DATA_FOLDER', 'Process')
$oldSimulation = [Environment]::GetEnvironmentVariable('MQTTPROBE_DESKTOP_UPDATE_SIMULATION', 'Process')
$process = $null
$cleanupSafe = $false
try {
    [Environment]::SetEnvironmentVariable('XDG_CONFIG_HOME', (Join-Path $runDirectory 'config'), 'Process')
    [Environment]::SetEnvironmentVariable('WEBVIEW2_USER_DATA_FOLDER', (Join-Path $runDirectory 'webview2'), 'Process')
    [Environment]::SetEnvironmentVariable('MQTTPROBE_DESKTOP_UPDATE_SIMULATION', '1', 'Process')

    Write-Host 'Launching isolated Desktop update simulation. Close its window to finish.'
    $process = Start-Process -FilePath $executable -WorkingDirectory $repositoryRoot -PassThru
    $process.WaitForExit()
    $process.Refresh()
    $cleanupSafe = $process.HasExited
} finally {
    [Environment]::SetEnvironmentVariable('XDG_CONFIG_HOME', $oldConfigHome, 'Process')
    [Environment]::SetEnvironmentVariable('WEBVIEW2_USER_DATA_FOLDER', $oldWebViewFolder, 'Process')
    [Environment]::SetEnvironmentVariable('MQTTPROBE_DESKTOP_UPDATE_SIMULATION', $oldSimulation, 'Process')

    if ($cleanupSafe -and $process -and $process.HasExited) {
        try {
            Remove-Item -LiteralPath $resolvedRunDirectory -Recurse -Force -ErrorAction Stop
            if (Test-Path -LiteralPath $resolvedRunDirectory) {
                Write-Warning "Cleanup could not be confirmed; retained run data at $resolvedRunDirectory"
            }
        } catch {
            Write-Warning "Could not safely clean up run data; retained it at $resolvedRunDirectory ($($_.Exception.Message))"
        }
    } else {
        Write-Warning "Process exit was not confirmed; retained run data at $resolvedRunDirectory"
    }
}
