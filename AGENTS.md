# Agent Instructions

## Formatting

- `.editorconfig` is the source of truth for C# formatting.
- Check formatting with `python scripts/ci/format-check.py`.
- Auto-fix formatting with `python scripts/ci/format-check.py --fix`.

## C#

- Prefer primary constructors when dependencies are only captured; keep explicit constructors when setup or accessibility constraints require them.
- Do not add `!` immediately after FluentAssertions `Should().NotBeNull()`; retain it where compiler flow analysis cannot prove non-null.
- Remove unused `using` directives before completing a change. Run `python scripts/ci/format-check.py` (or `--fix`) to catch stragglers.

## Comments

- Prefer few comments.
- Do not add routine XML doc comments to hand-written code.
- Inline comments explain non-obvious why, not restate what.
- Comments are acceptable when they clarify messy CSS/Razor markup, complex regex, or hard-to-name structure.

## Styles

- Avoid inline styles; use separate CSS files.
- For Razor pages and components, use the companion `.razor.css` file.
- When modifying files that already contain inline styles, extract them into the appropriate CSS file.
- Prefer one vertical scroll owner per dialog tab or page region. Avoid nested scrollbars and horizontal scrolling for long topic values.
- MudBlazor adds intermediate layout elements. Inspect rendered DOM and use correctly anchored `::deep` selectors before overriding tab or expansion-panel layout.

## Verification

- Run unit tests with `dotnet test tests/MqttProbe.Core.Tests` and `dotnet test tests/MqttProbe.UI.Tests`.
- Integration tests (`dotnet test tests/MqttProbe.IntegrationTests`) need Docker (Testcontainers Mosquitto). CI runs them in a separate job; without Docker they skip rather than fail. Prefer the unit projects for the fast local gate.
- Build with `dotnet build MqttProbe.slnx`.
- For code changes, check coverage with `python scripts/ci/coverage.py` (unit projects only; integration is excluded from coverage).
- Treat 80% coverage as a hard minimum for new code.
- Check for routine XML docs with `python scripts/ci/check-comments.py`.
- Browser-verify layout changes; bUnit does not prove dimensions, clipping, overflow, or scrollbar behavior.
- Test layout changes with long topics, overflowing lists, expanded and collapsed panels, and a short viewport. Use DOM measurements when diagnosing constrained flex layouts.

## When to add integration tests

Unit and UI tests are the default. Also add or extend tests under `tests/MqttProbe.IntegrationTests` when the change touches **real MQTT broker boundary** behavior that fakes cannot prove, for example:

- TLS / mTLS connect and cert material (PFX, PEM, trust, reject paths)
- Live connect, reconnect, subscribe, or publish against a broker
- Sparkplug (or other protocol) sessions that only fail or succeed on the wire
- Auth or session options that only surface with a real broker

Rules:

- Integration tests are **additive**, not a substitute for unit coverage of the same logic.
- Reuse `MtlsBrokerFixture` and helpers in `tests/MqttProbe.TestInfrastructure`. Do not stand up a second broker harness.
- Folders named `Integration` under `MqttProbe.Core.Tests` are in-process fakes, not broker tests. Real-broker cases go in `MqttProbe.IntegrationTests`.
- **Still write the test** even if you cannot run Docker locally. CI runs the integration job; local skip-without-Docker is not a reason to omit the test.
- If unsure whether a change is broker-boundary, prefer adding a focused integration test over assuming unit coverage is enough.

## Agent Browser acceptance testing (Windows)

### Launching the app

- Start the long-running ASP.NET app via a batch file that redirects internally. Launch a hidden `cmd.exe /c` with `Start-Process` and save the launcher PID (not PowerShell's `$pid`).
- Pass explicit `--urls`. Bounded-poll the port before proceeding.
- Verify port ownership before assuming a listener belongs to mqttprobe; inspect the process command line if unsure.
- If the app is already running on the expected port, reuse it; do not start a duplicate.

### Agent Browser setup

- The `agent-browser` executable may not be on PATH. Use the full installed path (e.g. `agent-browser-win32-x64.exe`).
- For visible user-observable testing, create one named session with `--headed`. If the named session already exists, reuse it; do not start a duplicate.
- A visible browser window does not mean an agent is driving it. Every command must use the same `--session` and `--headed` flags plus an actual operation.
- Always include an actual command when invoking the tool. In PowerShell use the call operator `&`, not `&&`.
- Quote `@`-prefixed refs (e.g. `"@e13"`) in PowerShell; bare `@e13` is parsed as splatting.

### Snapshots and interaction

- Take fresh snapshots after renders; stale DOM refs may fail.
- Scope duplicate labels to the active dialog.
- A covered-by-overlay result usually means a duplicate background control was selected. Scope to the active dialog and do not bypass via JS click.
- Prefer role, label, or scoped selectors over positional ones.
- `scroll down` may target the page rather than a nested dialog; verify the intended scroll owner before asserting scroll behavior.

### Broker connections and errors

- Use bounded waits for broker connections. Capture exact errors and evidence on failure.
- If one configured broker fails, try another before giving up. Continue non-connect test cases if a broker connection is blocked.

### Cleanup

- Close the named browser session, then stop the saved launcher tree with `taskkill` and verify the port stopped listening.
- If the launcher PID is stale but the port remains open, inspect the listener process command line. Only terminate it when confirmed to be this repo's app. Never kill unrelated dotnet, MSBuild, or browser infrastructure.

### Viewport and screenshots

- Headed `set viewport` may EOF; do not claim responsive testing unless the viewport change is actually verified.
- Save screenshots under the approved temp directory, not inside the repo.
