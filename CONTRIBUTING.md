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
2. Clone the repo
3. Run tests: `dotnet test tests/MqttProbe.Tests`

### Code Style

- Allman braces (open brace on its own line)
- File-scoped namespaces
- `_camelCase` for private fields
- `var` everywhere
- **Prefer few comments.** Good code names and structure make most comments unnecessary.
- **Comments explain non-obvious *why*, not restate *what*.** `// increment i` is noise; `// retry after transient disconnect because the library does not handle this` is useful.
- **Acceptable when they improve clarity** in messy CSS/Razor markup, complex regex, or otherwise hard-to-name structure.

### Tests and Coverage

- Run `dotnet test tests/MqttProbe.Tests` before opening a PR.
- Add or update tests for behavior changes and bug fixes.
- Keep test coverage at or above 75%.
- Use `python scripts/coverage.py --open` to inspect coverage when needed.

### CI Checks

Before submitting changes, make sure the same checks used by CI pass locally:

- `dotnet build MqttProbe.slnx --warnaserror`
- `dotnet test tests/MqttProbe.Tests`
- `python scripts/format-check.py`

### Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):
```
feat: add new feature
fix: resolve bug
docs: update documentation
```

## License

By contributing, you agree that your contributions will be licensed under the Apache License 2.0.
