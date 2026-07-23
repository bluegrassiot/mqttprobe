# CSV Sample Plugin

A sample **payload format plugin** that detects and decodes CSV (comma-separated values) sensor payloads.

This plugin implements `IMqttProbePlugin` with a detector and decoder. When loaded, MqttProbe recognizes CSV messages and displays them as a JSON array of objects in the Payload Browser tree view. The `FormatId` is `csv`.

## Build

```powershell
dotnet build samples/CustomDemoPlugin -c Release
```

## Install

Copy **only** `CustomDemoPlugin.dll` (not `MqttProbe.Shared.dll`) into a `CustomDemoPlugin` subfolder under the host plugins directory. Create folders if needed. Restart the app after copying; plugins do not hot-reload.

**Web (Visual Studio debug / `dotnet run`):**

`ContentRoot` is the project folder:

```
src/MqttProbe.Web/Plugins/CustomDemoPlugin/CustomDemoPlugin.dll
```

**Web (Docker):** volume `mqttprobe-plugins` → `/app/Plugins` inside the container (same subfolder layout). Restart the container after copying.

**Desktop Photino (Windows):**

```
%USERPROFILE%\.config\mqttprobe\plugins\CustomDemoPlugin\CustomDemoPlugin.dll
```

**MAUI Windows / Mac Catalyst** (not the Desktop path above):

```
{AppDataDirectory}/plugins/CustomDemoPlugin/CustomDemoPlugin.dll
```

A typical Windows MAUI path looks like:

```
%LOCALAPPDATA%\Bluegrass IoT\com.bluegrassiot.mqttprobe\Data\plugins\CustomDemoPlugin\CustomDemoPlugin.dll
```

`AppDataDirectory` is `FileSystem.Current.AppDataDirectory` in MAUI. If unsure, create `plugins` under that folder after running the app once (MAUI creates it at startup).

**MAUI Android / iOS:** no default external plugin folders; built-ins only unless you bake plugins into the app package.

### PowerShell (MAUI Windows)

```powershell
dotnet build samples/CustomDemoPlugin -c Release
$dir = Join-Path $env:LOCALAPPDATA "Bluegrass IoT\com.bluegrassiot.mqttprobe\Data\plugins\CustomDemoPlugin"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item samples\CustomDemoPlugin\bin\Release\net10.0\CustomDemoPlugin.dll $dir -Force
```

Then fully restart MAUI and publish again.

## Publish benchmark payloads

With the plugin loaded, publish CSV benchmark messages:

```powershell
dotnet run --project benchmarks/MqttProbe.Benchmarks -c Release -- publish --format Csv
```

## What to expect

- The **Format** row in the message detail view shows `csv` (raw FormatId; host does not hardcode plugin display names).
- The **Payload** section renders a JSON tree (array of objects), so you can expand individual rows and fields.
- The metrics flyout counts messages under the `csv` format.

## Without the plugin

If the plugin DLL is not loaded, the same CSV payload falls through to the plaintext detector
(priority 200) and displays as plain text instead.

## Extending the plugin

This sample only registers a detector and decoder. The same plugin class can also call `context.RegisterEncoder(...)` in `RegisterServices` to add Emulator write support. Users would then set `PayloadFormatId` to `csv` on an emulator node to publish CSV-formatted messages.
