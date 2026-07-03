# Photino Desktop Polish — Design Spec

## Context

The Photino desktop host (`MqttProbe.Desktop`) shares the `AppShellBar` component with the Web and MAUI hosts. Two gaps exist:

1. **Missing favicon.svg** — `AppShellBar.razor` line 13 renders `<MudImage Src="favicon.svg" .../>`. The Web host has `wwwroot/favicon.svg` (4 310 bytes) and the MAUI host has its own copy, but the Desktop host's `wwwroot/` contains only `index.html` and `css/app.css`. Photino serves static files from the host project's `wwwroot`, so the `<img>` 404s and the AppBar shows a broken image placeholder.

2. **Raw version tooltip** — The same `<MudImage>` sets `title="@_version"` which resolves to the assembly version string (e.g. `1.0.0.0`). This is developer-facing noise; users expect a friendlier label like "Version 1.0.0".

3. **Native title bar text** — `Program.cs` line 90 calls `.SetTitle("MqttProbe")`, which puts "MqttProbe" in the OS window title and taskbar. Branding should live exclusively in the Blazor AppBar wordmark; the native title text should be blank so the taskbar/dock shows only the icon.

4. **Icon packaging** — `Assets/icon.ico` contains five sizes (16, 24, 32, 48, 256 px) and `Assets/icon.png` is a 256 px PNG. The `.csproj` sets `<ApplicationIcon>Assets\icon.ico</ApplicationIcon>` and copies both files to output. This is already correct for Windows (taskbar/titlebar pick sizes from the ICO) and Linux (Photino uses the PNG for WM/dock). No source changes are needed for icon packaging; verification is enough.

## Goals

| # | Goal | Success criterion |
|---|------|-------------------|
| G1 | Desktop host serves `favicon.svg` so the AppBar icon loads | `<img src="favicon.svg">` returns 200 in the Photino webview; no 404 in dev-tools network tab |
| G2 | Hover text on the AppBar icon reads "Version {service version}" | bUnit test asserts `title` attribute matches `"Version 1.0.0-test"`; Desktop host shows `"Version 1.0.0.0"` (four components, untrimmed — version shape is a service concern) |
| G3 | Native window title is blank; branding appears only in Blazor AppBar | `SetTitle("")` in `Program.cs`; taskbar shows icon only |
| G4 | Windows taskbar/titlebar icon renders at normal sizes; Linux dock icon uses PNG | `icon.ico` contains 16/24/32/48/256 px images (already true); `icon.png` copied to output (already true) |
| G5 | All repo verification gates pass | `format-check.py`, `dotnet test`, `dotnet build --warnaserror`, coverage ≥ 75 % |

## Non-goals

- No changes to the MAUI or Web hosts.
- No new icon artwork; existing `icon.ico` and `icon.png` are used as-is.
- No changes to `WindowsTitleBar.ApplyBrandTint` (DWM caption color logic).
- No `<link rel="icon">` in `index.html` — the favicon is only needed inside the Blazor app chrome, not the outer HTML shell.

## Design

### D1 — Add `favicon.svg` to Desktop `wwwroot`

Copy `src/MqttProbe.Web/wwwroot/favicon.svg` to `src/MqttProbe.Desktop/wwwroot/favicon.svg`. The file is identical across hosts (same 4 310-byte SVG). The existing `<Content Update="wwwroot\**">` item group in the `.csproj` already handles copy-to-output, so no `.csproj` change is needed.

### D2 — Format the version tooltip

In `AppShellBar.razor` line 13, change:

```razor
<MudImage title="@_version" Src="favicon.svg" Height="34" Class="mr-2" />
```

to:

```razor
<MudImage title="@($"Version {_version}")" Src="favicon.svg" Height="34" Class="mr-2" />
```

The `_version` field is already populated by `AppInfoService.GetVersion()` which strips the `+buildmetadata` suffix and returns `"unknown"` when sources are unavailable. The Desktop implementation (`DesktopAppInfoService`) returns `Assembly.GetExecutingAssembly().GetName().Version?.ToString()` which produces `"1.0.0.0"` (four components). No trimming is needed in the component itself — the version string is a service concern.

### D3 — Blank native window title

In `Program.cs` line 90, change:

```csharp
.SetTitle("MqttProbe")
```

to:

```csharp
.SetTitle("")
```

This results in an empty native title bar text. The OS taskbar/dock still shows the window icon (set via `.SetIconFile`). The Blazor AppBar wordmark ("MQTTProbe") remains the sole visible branding.

### D4 — Verify icon packaging (no source changes)

Confirm that:

- `Assets/icon.ico` contains standard Windows sizes (16, 24, 32, 48, 256 px). **Already true.**
- `Assets/icon.png` is a 256 px PNG for Linux. **Already true.**
- The `.csproj` `<Content Include="Assets\**">` copies both to output. **Already true.**
- `<ApplicationIcon>Assets\icon.ico</ApplicationIcon>` embeds the ICO as the Win32 resource. **Already true.**

No code changes required; verification is a build + inspect step.

## Files likely affected

| File | Change type |
|------|-------------|
| `src/MqttProbe.Desktop/wwwroot/favicon.svg` | **New file** (copy of `src/MqttProbe.Web/wwwroot/favicon.svg`) |
| `src/MqttProbe.Shared/Components/Layout/AppShellBar.razor` | Edit line 13 — wrap `_version` in `"Version …"` |
| `src/MqttProbe.Desktop/Program.cs` | Edit line 90 — `SetTitle("")` |
| `tests/MqttProbe.Tests/Components/Layout/MainLayoutTests.cs` | Update assertion at line 130 — expect `"Version 1.0.0-test"` instead of `"1.0.0-test"` |

## Verification

All steps run in order. Each must pass before the next.

1. **Format check**
   ```
   python scripts/format-check.py
   ```
   Fix any violations with `python scripts/format-check.py --fix`.

2. **Unit tests**
   ```
   dotnet test tests/MqttProbe.Tests
   ```
   The `Renders_AppBarWithVersion_FromAppInfoService` test must assert the new `"Version …"` title format.

3. **Build (warnings as errors)**
   ```
   dotnet build MqttProbe.slnx --warnaserror
   ```

4. **Coverage**
   ```
   python scripts/coverage.py
   ```
   Confirm ≥ 75 % line coverage on new/changed lines.

5. **Manual spot-check (optional)**
   - Launch the Desktop host on Windows; confirm the taskbar icon is the branded icon and the title bar text is blank.
   - Hover the AppBar icon; confirm tooltip reads "Version x.y.z".
   - Launch on Linux; confirm the dock icon is the branded PNG.
