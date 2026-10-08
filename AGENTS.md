# Agent Instructions

## Formatting

- `.editorconfig` is the source of truth for C# formatting.
- For a quick local check, use `python scripts/ci/format-check.py --changed` to check staged and unstaged tracked changes relative to `HEAD`, plus untracked files.
- Use `python scripts/ci/format-check.py --staged` to check staged filenames against their current on-disk content. This does not check the staged snapshot, so partially staged files are checked in full as they exist in the working tree.
- `--changed` and `--staged` are mutually exclusive. Either can be combined with `--fix` to apply formatting to the selected scope.
- With neither scope flag, `python scripts/ci/format-check.py` checks the full tree. Keep this as the final validation and CI command. Changes to `.editorconfig`, project or solution files, build configuration, SDK configuration, or NuGet configuration force full-tree scope even when a scoped mode is selected. No applicable files is a successful no-op.
- Auto-fix formatting with `python scripts/ci/format-check.py --fix`.

## C#

- Prefer primary constructors when dependencies are only captured; keep explicit constructors when setup or accessibility constraints require them.
- Do not add `!` immediately after FluentAssertions `Should().NotBeNull()`; retain it where compiler flow analysis cannot prove non-null.
- Remove unused `using` directives before completing a change.

### Code navigation

- Prefer LSP for C# symbol navigation: workspace symbols to find a symbol, then go-to-definition, find-references, find-implementation and call hierarchy from there.
- Use grep for literal strings (topics, config keys, error text) and AST search for code patterns such as method shapes or call sites.
- Fall back to plain search and reading the source when LSP is missing, fails, or returns stale results.

### Roslynk diagnostic fixes

- Use Roslynk first for diagnostics in compiled C#, Razor, and CSHTML files. Try `apply_code_fix` and inspect available code actions before editing manually.
- Exception: if Roslynk reports no applicable fix, cannot map the edit back to Razor source, or offers only fixes that would change the declared intent, you may make a minimal manual repair. For example, migrate a stale API call to the current contract rather than regenerate a deliberately removed type.
- Report the diagnostic, why the Roslynk fixes were unsuitable, and the manual change. Preserve the intended behavior; do not remove declarations, add suppressions, or change the build graph to silence errors. If the intended behavior is unclear, ask before editing.
- After a manual repair, re-query Roslynk diagnostics and run the relevant build, tests, and formatting checks. A solution still loading or a stale snapshot is not grounds for this exception: wait for loading to finish or re-query first.

## Comments

- Prefer few comments. Explain non-obvious *why*, not restate *what*.
- Do not add routine XML doc comments to hand-written code; `python scripts/ci/check-comments.py` gates it.
- Acceptable where they clarify messy CSS/Razor markup, complex regex, or hard-to-name structure. `CONTRIBUTING.md` (Code Style) works through examples.

## Styling and layout

- Avoid inline styles; use separate CSS files.
- For Razor pages and components, use the companion `.razor.css` file.
- When modifying files that already contain inline styles, extract them into the appropriate CSS file.
- Prefer one vertical scroll owner per dialog tab or page region. Avoid nested scrollbars and horizontal scrolling for long topic values.
- MudBlazor adds intermediate layout elements. Inspect rendered DOM and use correctly anchored `::deep` selectors before overriding tab or expansion-panel layout.

## Git hooks

Hooks live in `.githooks`, not `.git/hooks`. On a fresh clone they are inert until you run `git config core.hooksPath .githooks`, so without that every check in Verification passes by never running. Verify with `git config --get core.hooksPath`.

`pre-commit` runs the security scan, actionlint, staged format check (`python scripts/ci/format-check.py --staged`), file-length check, blocking-awaits check, and comment check, each skipped when no staged file is relevant. `pre-push` builds `MqttProbe.NoMaui.slnf` and runs the Core and UI tests. Deliberate escape hatches are `SKIP_PRE_COMMIT=1` and `SKIP_PRE_PUSH=1`, but CI still runs everything on a PR, so prefer fixing failures.

## Verification

- Run unit tests with `dotnet test tests/MqttProbe.Core.Tests` and `dotnet test tests/MqttProbe.UI.Tests`.
- Integration tests (`dotnet test tests/MqttProbe.IntegrationTests`) need Docker (Testcontainers Mosquitto). CI runs them in a separate job; without Docker they skip rather than fail. Prefer the unit projects for the fast local gate.
- Build with `dotnet build MqttProbe.NoMaui.slnf` while iterating. `MqttProbe.slnx` additionally pulls MAUI, Desktop, benchmarks, and the plugin sample, so use it only for final validation.
- Check coverage with `python scripts/ci/coverage.py`. It gates at 80% line coverage, matching CI. Unit projects only; integration is excluded from that gate on purpose.
- Three further gates beyond formatting and comments, easy to miss: `python scripts/ci/check-blocking-awaits.py` (sync blocking on an async chain deadlocks; every call site must be justified), `python scripts/ci/inspect.py --tool devskim --fail-on warning` (secret and injection scan), and `python scripts/ci/check-file-length.py --staged` (500-line default; grandfathered files may shrink but not grow; pass explicit paths to check those, or omit `--staged` to sweep the whole hand-written `src` tree). All three also gate CI, where file length sweeps the whole tree. `python -m unittest discover -s scripts/ci/tests` covers the gate scripts themselves and runs in CI as well.
- Check for routine XML docs with `python scripts/ci/check-comments.py`.
- Browser-verify layout changes; bUnit does not prove dimensions, clipping, overflow, or scrollbar behavior. Method and cases are in [Browser acceptance testing](#browser-acceptance-testing-windows).

## Release notes and assets

The release body is generated, not typed at tag time. Three inputs, all in the repo:

- `.github/release-notes.md` is the hand-written part: downloads table, quick start, per-platform notes. Use `{{VERSION}}` for the tag (`v1.2.3`) and `{{VERSION_NUM}}` for the bare form (`1.2.3`), which is what Docker tags use. A GitHub expression such as `${{ github.ref_name }}` does not work here and the check below rejects it.
- `.github/release-assets.txt` is the single source of truth for what gets uploaded. `release.yml` ships exactly what the resolver prints, so adding an artifact here without documenting it in the notes fails the release.
- `.git-cliff.toml` maps conventional commits to user-facing groups (Features, Fixes, Performance, Engineering) and drops `docs`, `test`, and `style`. Conventional commit types are therefore part of the release surface: a `feat:`/`fix:` prefix reaches the release page, so do not reword or drop one to tidy history.

`scripts/ci/release-notes.py` has `render`, `resolve`, and `check` subcommands; `check` is the gate that runs in `create-release`. Dry-run it before tagging against a directory of built artifacts:

```
python scripts/ci/release-notes.py check --version v1.2.3 --root <dir with the artifacts>
```

Do not hand-edit the published release body after tagging. Fix `.github/release-notes.md` and re-tag, so the page stays reproducible from the repo.

### Test filtering and build/test integrity

- Command-line filters (`--filter`, `--test-adapter-path`, repeated runs of a single test) are fine for focused TDD, diagnosis, repetition, or documented platform/category splits. Report the exact filter used. Never describe a filtered run as "the full suite."
- Final validation must run the complete, unfiltered Core and UI suites (plus any required integration gates) before declaring the change ready.
- Never add persistent source or test exclusions such as `<Compile Remove>` or alter project test discovery to bypass failures.
- Do not add `NoWarn`, analyzer suppressions, or skip annotations merely to make a lane pass.
- Treat a compile error you did not cause as blocked: report it and wait. Do not edit project files, target frameworks, or the build graph to make it disappear.
- Investigate unexplained test-count drops. Use `dotnet test --list-tests` after any project-file or test-discovery change to confirm the expected set still appears.

## When to add integration tests

Unit and UI tests are the default. Also add or extend tests under `tests/MqttProbe.IntegrationTests` when the change touches **real MQTT broker boundary** behavior that fakes cannot prove: TLS/mTLS connect and cert material (PFX, PEM, trust, reject paths), live connect/reconnect/subscribe/publish, Sparkplug sessions that only succeed or fail on the wire, or auth and session options that only surface against a real broker. If unsure whether a change is broker-boundary, prefer adding a focused integration test over assuming unit coverage is enough.

Two traps: a folder named `Integration` under `MqttProbe.Core.Tests` holds in-process fakes, not broker tests; and a local skip because Docker is missing is not a reason to omit a test, since CI runs the integration job. Integration tests are **additive**, never a substitute for unit coverage of the same logic. Reuse `MtlsBrokerFixture` in `tests/MqttProbe.TestInfrastructure` rather than standing up a second broker harness.

## Browser acceptance testing (Windows)

- Prefer Playwright for .NET for browser acceptance.
- An opt-in manual browser-smoke project exists at `tests/MqttProbe.BrowserSmokeTests/README.md`, deliberately kept out of the solution and CI. Run it only when asked, not as a default gate.
- For one-off exploratory checks, keep using a temporary harness under the approved temp directory, outside the repo. Never treat that scratch path as a permanent command.
- Use a fresh browser context per check, with `IgnoreHTTPSErrors = true` for a local CA and no saved auth state unless the test requires it.
- Bound every wait: action timeout around 15s, navigation timeout 30-45s, and an overall process budget for the run.
- Launch headed when the check is user-visible, headless otherwise, and report which one ran. Close the browser in `finally`.
- Click real controls. Dismiss overlays through visible UI (Cancel, close, backdrop) and wait for the overlay to clear before acting underneath; never bypass with a scripted POST or a JS click.
- After each action, assert the resulting state from a fresh DOM read; a click that returns without error is not evidence. Prefer role, label and scoped selectors, and confirm the interactive UI is live rather than a static render.
- For redirects and external integrations, assert the actual browser request that was made and the final state reached, not just a click, a 302 Location header or landing on a known page.
- Prove responsive and layout behaviour by changing the viewport and measuring or asserting the result; cover long topics, overflowing lists, expanded and collapsed panels, and a short viewport.
- Keep evidence sanitized: network entries as method, status, host and path, screenshots and run logs under the approved temp directory, never inside the repo.
- Never log query strings, tokens, cookies or passwords; redact before reporting.
- Start the app once per check with explicit `--urls`, bounded-poll the port, verify port ownership before assuming a listener is this repo's app, and reuse an existing listener instead of starting a duplicate.
- Use bounded waits for broker connections and capture exact errors; if one configured broker fails, try another and keep running the non-connect cases. Lab-specific flows: see `deploy/authentik/README.md`.
- After the run, close the browser, stop only the processes this run started, and verify the port stopped listening. Never kill a Docker-owned listener or unrelated dotnet, MSBuild or browser infrastructure.
- If you stop making progress, report the last action and the blocker instead of idling.
