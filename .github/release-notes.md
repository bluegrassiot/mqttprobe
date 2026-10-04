## MQTT Probe {{VERSION}}

Requires the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the web builds. The desktop and Android builds ship their own runtime.

### Downloads

Pick the desktop app if you want a window, or one of the web builds if you would rather host it yourself.

| Artifact | Description |
|----------|-------------|
| `MQTTProbe-win-Setup.exe` | Windows desktop app, recommended: installs per-user and auto-updates. Unsigned, so SmartScreen asks you to confirm. |
| `mqttprobe-windows-{{VERSION}}.zip` | Windows desktop app, portable: the MAUI build with no installer. Unsigned, so SmartScreen asks you to confirm. |
| `MQTTProbe-osx-Setup.pkg` | macOS desktop app, recommended: universal Intel and Apple Silicon, signed and notarized, auto-updates. |
| `MQTTProbe-osx-Portable.zip` | macOS desktop app, portable: signed and notarized, nothing to install. |
| `MQTTProbe.AppImage` | Linux desktop app, recommended: bundles WebKit, GTK, and GStreamer, so it needs only `libfuse2` on Ubuntu 22.04+. Auto-updates. |
| `mqttprobe-desktop-linux-x64-{{VERSION}}.zip` | Linux desktop app, portable: Photino and Blazor, self-contained .NET. Does not bundle WebKit, so it needs `sudo apt install libwebkit2gtk-4.1-0`. |
| `mqttprobe-android-{{VERSION}}.apk` | Android app: sideload the APK on Android 7.0 or newer (API 24). |
| `mqttprobe-web-linux-x64-{{VERSION}}.zip` | Web app (Blazor Server): single binary for Linux x64. |
| `mqttprobe-web-linux-arm64-{{VERSION}}.zip` | Web app (Blazor Server): single binary for Linux arm64, for a Raspberry Pi or NAS. |
| `mqttprobe-web-win-x64-{{VERSION}}.zip` | Web app (Blazor Server): single exe for Windows x64. |
| `docker pull bluegrassiot/mqttprobe:{{VERSION_NUM}}` | Docker image for amd64 and arm64: nothing to install on the host. |
| `mqttprobe-third-party-sources-{{VERSION}}.tar.gz` | Source for the third-party code bundled in the AppImage, with licences and the manifest that lists it. |

### Quick Start

```bash
# Docker, recommended for servers
docker compose up -d

# Linux bare metal: extract the zip and run
chmod +x MqttProbe.Web && ./MqttProbe.Web

# Windows bare metal: extract the zip and run
MqttProbe.Web.exe

# Linux desktop from the zip: install WebKitGTK first, then run
sudo apt install libwebkit2gtk-4.1-0
chmod +x MqttProbe.Desktop && ./MqttProbe.Desktop
```

### Windows

Download `MQTTProbe-win-Setup.exe` for a per-user install with auto-updates, or `mqttprobe-windows-{{VERSION}}.zip` if you prefer a portable copy. Both are unsigned, so Windows SmartScreen shows a warning the first time: choose More info, then Run anyway.

### Linux

Download `MQTTProbe.AppImage`, run `chmod +x MQTTProbe.AppImage`, and start it. It bundles WebKit, GTK, and GStreamer, so on Ubuntu 22.04 or newer the only extra dependency is `libfuse2` (`sudo apt install libfuse2`). The AppImage auto-updates.

Choose the zip instead if your distribution cannot run AppImages. It does not bundle WebKit, so install `libwebkit2gtk-4.1-0` first.

### macOS

Download the `.pkg` installer: it installs to Applications and enables in-app auto-updates. Both the installer and the portable `MQTTProbe-osx-Portable.zip` are signed and notarized by Apple, so Gatekeeper opens them without a security prompt.

### Android

Download the APK to your device and open it to install. Your browser asks you to allow installing unknown apps the first time, and Play Protect warns about apps from outside the Play Store: choose Install anyway.

Read the [README](https://github.com/bluegrassiot/mqttprobe#readme) for configuration, broker setup, and plugin docs.
