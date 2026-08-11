# CSV Sample Plugin

A sample **payload format plugin** that detects and decodes CSV (comma-separated values) sensor payloads.

This plugin implements `IMqttProbePlugin` with a detector and decoder. When loaded, MqttProbe recognizes CSV messages and displays them as a JSON array of objects in the Payload Browser tree view. The `FormatId` is `csv`.

## Reference model

Your plugin project references **only** `MqttProbe.PluginContracts` (not `MqttProbe.UI` or
`MqttProbe.Core`). The host loads plugins into a separate `AssemblyLoadContext` that shares the
`MqttProbe.PluginContracts` assembly so that interface types (`IMqttProbePlugin`, `IPayloadDetector`,
etc.) have the same identity in both host and plugin. Do not ship `MqttProbe.PluginContracts.dll`
alongside your plugin; the host provides it.

MQTTnet types used by detector/decoder interfaces flow transitively from PluginContracts.

## Compatibility / SemVer

`MqttProbe.PluginContracts` versions independently of the app using SemVer:

- **MAJOR** = breaking API change (removed/renamed interface, changed method signature). Requires plugin rebuild.
- **MINOR** = additive API change (new interface, new method). Existing plugin DLLs keep loading.
- **PATCH** = non-API change (docs, build infra). No plugin impact.

`PluginContract.Version` (currently `1`) is the API level the host logs at startup for diagnostics. It is not tied to the app version.

Old plugins built against the former `MqttProbe.Shared` assembly are not binary-compatible; rebuild against `MqttProbe.PluginContracts`.

## Build

```powershell
dotnet build samples/CustomDemoPlugin -c Release
```

## Install from the UI

Build a package and install it from **Settings → Plugins → Install plugin**. This is the only route
on a hardened host where you have no filesystem access.

```powershell
python scripts/packaging/pack-plugin.py samples/CustomDemoPlugin --dll samples/CustomDemoPlugin/bin/Release/net10.0/CustomDemoPlugin.dll
```

`--dll` renames the assembly to `sample-csv.dll` to match the `id` in
[mqttprobe-plugin.json](mqttprobe-plugin.json). The loader resolves a package's primary assembly by
its id, and ids are lowercase-only, so `CustomDemoPlugin.dll` would never be found on a
case-sensitive filesystem. Repeat `--dll` for any additional dependencies; only the first is renamed.

Two things differ from schema packages:

- Restart afterwards. Only protobuf schema packages activate without one.
- If the install is refused, `Plugins:AllowBinaryPackages` has been set to `false` on that host.
  It defaults to `true`; a deployment that does not want in-process third-party code opts out.

## Install by hand

Copy **only** `CustomDemoPlugin.dll` (not `MqttProbe.PluginContracts.dll`) into a `CustomDemoPlugin` subfolder under the host plugins directory. Create folders if needed. Restart the app after copying; plugins do not hot-reload.

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

- The **Format** row in the message detail view shows `CSV` (the display name supplied by the plugin's detector).
- The **Payload** section renders a JSON tree (array of objects), so you can expand individual rows and fields.
- The metrics flyout counts messages under the `csv` FormatId internally and shows `CSV` as the display name.

## Without the plugin

If the plugin DLL is not loaded, the same CSV payload falls through to the plaintext detector
(priority 200) and displays as plain text instead.

## Extending the plugin

This sample only registers a detector and decoder. The same plugin class can also call `context.RegisterEncoder(...)` in `RegisterServices` to add Emulator write support. Users would then set `PayloadFormatId` to `csv` on an emulator node to publish CSV-formatted messages.
