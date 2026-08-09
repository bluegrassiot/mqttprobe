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

- Run tests with `dotnet test tests/MqttProbe.Core.Tests` and `dotnet test tests/MqttProbe.UI.Tests`.
- Build with `dotnet build MqttProbe.slnx`.
- For code changes, check coverage with `python scripts/ci/coverage.py`.
- Treat 75% coverage as a hard minimum for new code.
