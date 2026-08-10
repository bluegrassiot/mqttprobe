# Agent Instructions

## Formatting

- `.editorconfig` is the source of truth for C# formatting.
- Check formatting with `python scripts/ci/format-check.py`.
- Auto-fix formatting with `python scripts/ci/format-check.py --fix`.

## C#

- Prefer primary constructors when dependencies are only captured; keep explicit constructors when setup or accessibility constraints require them.
- Do not add `!` immediately after FluentAssertions `Should().NotBeNull()`; retain it where compiler flow analysis cannot prove non-null.

## Comments

- Prefer few comments.
- Do not add routine XML doc comments to hand-written code.
- Inline comments explain non-obvious why, not restate what.
- Comments are acceptable when they clarify messy CSS/Razor markup, complex regex, or hard-to-name structure.

## Styles

- Avoid inline styles; use separate CSS files.
- For Razor pages and components, use the companion `.razor.css` file.
- When modifying files that already contain inline styles, extract them into the appropriate CSS file.

## Verification

- Run unit tests with `dotnet test tests/MqttProbe.Core.Tests` and `dotnet test tests/MqttProbe.UI.Tests`.
- Integration tests (`dotnet test tests/MqttProbe.IntegrationTests`) need Docker (Testcontainers Mosquitto). CI runs them in a separate job; without Docker they skip rather than fail. Prefer the unit projects for the fast local gate.
- Build with `dotnet build MqttProbe.slnx`.
- For code changes, check coverage with `python scripts/ci/coverage.py` (unit projects only; integration is excluded from coverage).
- Treat 80% coverage as a hard minimum for new code.
- Check for routine XML docs with `python scripts/ci/check-comments.py`.

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
