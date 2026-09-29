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

### Test filtering and build/test integrity

- Command-line filters (`--filter`, `--test-adapter-path`, repeated runs of a single test) are fine for focused TDD, diagnosis, repetition, or documented platform/category splits. Report the exact filter used. Never describe a filtered run as "the full suite."
- Final validation must run the complete, unfiltered Core and UI suites (plus any required integration gates) before declaring the change ready.
- Never add persistent source or test exclusions such as `<Compile Remove>` or alter project test discovery to bypass failures.
- Do not add `NoWarn`, analyzer suppressions, or skip annotations merely to make a lane pass.
- If concurrent or dependent work causes compilation failures, report blocked and wait or reconcile rather than changing the build graph.
- Investigate unexplained test-count drops. Use `dotnet test --list-tests` after any project-file or test-discovery change to confirm the expected set still appears.

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

## Browser acceptance testing (Windows)

- Prefer Playwright for .NET for browser acceptance. An opt-in manual browser-smoke project exists at `tests/MqttProbe.BrowserSmokeTests/README.md`, deliberately kept out of the solution and CI, so run it only when asked instead of treating it as a default gate. For one-off exploratory checks, keep using a temporary harness under the approved temp directory, outside the repo, and never treat that scratch path as a permanent command or add planning docs for it.
- Use a fresh browser context per check, with `IgnoreHTTPSErrors = true` for a local CA and no saved auth state unless the test requires it.
- Bound every wait: action timeout around 15s, navigation timeout 30-45s, and an overall process budget for the run.
- Launch headed when the check is user-visible, headless otherwise, and report which one ran. Close the browser in `finally`.
- Click real controls. Dismiss overlays through visible UI (Cancel, close, backdrop) and wait for the overlay to clear before acting underneath; never bypass with a scripted POST or a JS click.
- After each action, assert the resulting state from a fresh DOM read; a click that returns without error is not evidence. Prefer role, label and scoped selectors, and confirm the interactive UI is live rather than a static render.
- For redirects and external integrations, assert the actual browser request that was made and the final state reached, not just a click, a 302 Location header or landing on a known page.
- Prove responsive and layout behaviour by changing the viewport and measuring or asserting the result; cover long topics, overflowing lists and a short viewport.
- Keep evidence sanitized: network entries as method, status, host and path, screenshots and run logs under the approved temp directory, never inside the repo.
- Never log query strings, tokens, cookies or passwords; redact before reporting.
- Start the app once per check with explicit `--urls`, bounded-poll the port, verify port ownership before assuming a listener is this repo's app, and reuse an existing listener instead of starting a duplicate.
- Use bounded waits for broker connections and capture exact errors; if one configured broker fails, try another and keep running the non-connect cases. Lab-specific flows: see `deploy/authentik/README.md`.
- After the run, close the browser, stop only the processes this run started, and verify the port stopped listening. Never kill a Docker-owned listener or unrelated dotnet, MSBuild or browser infrastructure.
- If you stop making progress, report the last action and the blocker instead of idling.
