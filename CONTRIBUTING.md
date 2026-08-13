# Contributing to MQTT Probe

Thanks for your interest in MQTT Probe!

## Reporting Issues

Open a GitHub issue with:
- Steps to reproduce
- Expected behavior
- Actual behavior
- Your environment (OS, browser, .NET version)

## Pull Requests

Bug fixes and small improvements are welcome. For larger changes, open an issue first to discuss.

### Setup

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Clone the repo with submodules: `git clone --recurse-submodules https://github.com/bluegrassiot/mqttprobe`. If you already cloned without that flag, run `git submodule update --init --recursive`.
3. Run tests: `dotnet test`

### Architecture and Development

For the solution layout, library choices, and common development commands, see the [Development wiki](https://github.com/bluegrassiot/mqttprobe/wiki/07-Development).

### Git hooks

This repo uses hooks in `.githooks` (not `.git/hooks`). After clone:

```bash
git config core.hooksPath .githooks
```

| Hook | What it runs |
|------|----------------|
| `pre-commit` | Security scan (`devskim`, ~3s) on any staged change; workflow lint (`python scripts/ci/actionlint.py`, ~1s) when staged files include `.github/workflows/*.yml`; format check (`python scripts/ci/format-check.py`) when staged files include C#/Razor/project/editorconfig; comment check (`python scripts/ci/check-comments.py`) when staged files include C#/Razor under `src/` or `tests/` |
| `pre-push` | Path-aware build (usually `MqttProbe.NoMaui.slnf`) and unit tests when code changes |

Both hooks print per-step and total timing. Docs-only changes (markdown under `docs/`, `*.md`, license files) skip the heavy steps automatically — but not the security scan, since a pasted token in a README is exactly what it looks for.

The security scan blocks the commit on a devskim finding at `warning` or above; notes are reported by `scripts/ci/inspect.py` but do not block. If a finding is a false positive, put a `DevSkim: ignore DS######` comment on the flagged line using that file's comment syntax. It needs the local tools, so run `dotnet tool restore` after cloning.

The workflow lint prefers a locally installed `actionlint` and otherwise runs the pinned Docker image, so it needs one of the two — with neither, the step skips rather than blocking. Install the binary if you would rather not depend on Docker; `scripts/ci/actionlint.py` picks it up automatically.

To skip intentionally:

- `SKIP_PRE_COMMIT=1 git commit ...`
- `SKIP_PRE_PUSH=1 git push ...`
- `git commit --no-verify` / `git push --no-verify` (bypasses the hook entirely)

CI still runs full checks on pull requests. Prefer fixing failures over skipping.

### Code Style

- Allman braces (open brace on its own line)
- File-scoped namespaces
- `_camelCase` for private fields
- `var` everywhere
- **Prefer few comments.** Good code names and structure make most comments unnecessary.
- **Comments explain non-obvious *why*, not restate *what*.** `// increment i` is noise; `// retry after transient disconnect because the library does not handle this` is useful.
- **Acceptable when they improve clarity** in messy CSS/Razor markup, complex regex, or otherwise hard-to-name structure.

### Tests and Coverage

- Run `dotnet test` before opening a PR (or the unit/integration project commands under CI Checks).
- Add or update tests for behavior changes and bug fixes.
- Prefer unit/UI tests for pure logic. For **real MQTT broker boundary** changes (TLS/mTLS, live connect/subscribe/publish, Sparkplug on the wire, broker-only auth), also add or extend `tests/MqttProbe.IntegrationTests`, reusing `MtlsBrokerFixture` in `tests/MqttProbe.TestInfrastructure`. Write those tests even if you lack Docker locally; CI runs them, and without Docker they skip rather than fail.
- Keep unit test coverage at or above 80% (`python scripts/ci/coverage.py --open`). Integration tests are excluded from that gate on purpose.

### CI Checks

Before submitting changes, make sure the same checks used by CI pass locally:

- `dotnet build MqttProbe.slnx`
- Unit: `dotnet test tests/MqttProbe.Core.Tests` and `dotnet test tests/MqttProbe.UI.Tests`
- Integration (needs Docker; CI always runs these): `dotnet test tests/MqttProbe.IntegrationTests`
- `python scripts/ci/format-check.py`
- `python scripts/ci/inspect.py --tool devskim --fail-on warning`
- `python scripts/ci/check-comments.py`

Local hooks cover a faster subset (unit tests only, no integration). Still run the commands above before a PR if you skipped hooks. Without Docker, integration tests skip rather than fail.

### Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):
```
feat: add new feature
fix: resolve bug
docs: update documentation
```

## License

By contributing, you agree that your contributions will be licensed under the Apache License 2.0.
