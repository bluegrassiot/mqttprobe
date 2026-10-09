# Development scripts

## Simulate a Desktop update

On Windows, double-click `scripts/dev/simulate-desktop-update.cmd` or run it from a terminal. Close any existing Desktop instance first. The launcher initializes the repository's pinned submodules, builds the Debug Desktop app, and launches it with a temporary, isolated configuration and WebView2 profile. It locates the repository from its own path, so it also works in a fresh checkout.

The simulator reports version `999.0.0-simulation`. Choosing to apply it leaves the button pending until you close the app. It does not download an update, shut down, or restart the app. During a real update, a separate updater window shows progress and the About button reads `Restarting…`.

The app cannot see your normal saved settings, secrets, plugins, or connections during this run. Temporary data is removed after the launched app exits; if exit or cleanup cannot be confirmed, the data is retained under `%TEMP%\MqttProbe-Desktop-Update-Simulation`.

The simulator is available only in Debug and only when `MQTTPROBE_DESKTOP_UPDATE_SIMULATION=1` is explicitly set for that process. Release builds and ordinary Debug launches use the real updater.
