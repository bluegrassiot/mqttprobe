# Agent Instructions

## Formatting

- `.editorconfig` is the source of truth for C# formatting.
- Check formatting with `python scripts/format-check.py`.
- Auto-fix formatting with `python scripts/format-check.py --fix`.

## Comments

- Prefer few comments.
- Do not add routine XML doc comments to hand-written code.
- Inline comments explain non-obvious why, not restate what.
- Comments are acceptable when they clarify messy CSS/Razor markup, complex regex, or hard-to-name structure.

## Verification

- Run tests with `dotnet test tests/MqttProbe.Tests`.
- Build with `dotnet build MqttProbe.slnx --warnaserror`.
- For code changes, check coverage with `python scripts/coverage.py`.
- Treat 75% coverage as a hard minimum for new code.
