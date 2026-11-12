# Offline legal notices

The app ships its licence and third-party notice set inside `MqttProbe.UI`, so
every host shows the same text without network access.

## Layout

- `legal/generated/catalogue.json` is the reader contract. It lists components
  and content-addressed documents. Do not edit it by hand.
- `legal/generated/texts/<sha>.txt` holds the normalized full texts. The file
  name is the SHA-256 of the UTF-8 text with LF line endings.
- `legal/profiles/<host>.json` records the reviewed NuGet graph for one publish
  target: package id, version, sha512 and the dependency input fingerprints.
- `legal/extracted/packages.json` records the normalized nuget-license metadata
  and extracted package texts for every profile package. `check` reads only this
  file and `legal/supplements`, never the NuGet cache or the tool.
- `legal/supplements/` holds hand-reviewed inputs: split source texts, SPDX
  expression mappings, non-NuGet components, exact package exceptions, and
  `bindings.json` for fonts, generated source, bundled JavaScript and SDK pins.
  `import_notices.py` is the one-time migrator that produced the split texts
  from the previous root `NOTICES`; it is not part of normal regeneration.
  If you edit a file under `supplements/texts`, update its `sha256` in
  `supplements/documents.json`; generation fails on a mismatch.
- root `NOTICES` is a generated human-readable index. It is not embedded.

## Checks

`check` is deterministic and needs no restore, tool, cache or network:

```
python scripts/legal/notices.py check
```

`profiles-check` compares a restored `project.assets.json` with its reviewed
profile. `verify` checks that one profile's packages and the reviewed bindings
are present in the committed catalogue:

```
python scripts/legal/notices.py profiles-check --scenario web-win-x64
python scripts/legal/notices.py verify --scenario web-win-x64
```

CI runs `check` in the script-test job before any restore, then runs
`profiles-check` and `verify` in the publish jobs. The generic Web profile is
`web-framework-dependent`; RID-specific Web profiles are checked where the
release publishes with that RID.

## Refresh

Refresh is deliberate and network-capable. For each selected scenario it
restores, exports the profile, runs the pinned `nuget-license` tool with the same
framework and RID properties, and writes `legal/extracted`. It never updates
`bindings.json` or package exception decisions.

```
dotnet tool restore
python scripts/legal/notices.py refresh
```

With no `--scenario`, refresh selects scenarios appropriate to the current host.
Use `--scenario NAME` for one target and `--all` for every non-pending target.
A shared project is restored and exported one scenario at a time, so the last
restore cannot overwrite an earlier profile.

Pin `nuget-license` to the version in `dotnet-tools.json`. Do not replace the
offline checker with a licence extractor: the checker exists to catch an
unreviewed package graph, not to make licence decisions at build time.

## First new package

1. Add the package reference and restore.
2. Run `python scripts/legal/notices.py refresh --scenario <host>`.
3. Review the profile diff, the extracted metadata and the new generated texts.
4. If the package declares an SPDX expression that is not in
   `legal/supplements/expressions.json`, add the term and a source text. Do not
   add a catch-all allowlist.
5. If a package has no usable extracted text, add an exact `PackageId@Version`
   entry to `legal/supplements/packages.json` with the text and source URL.
6. If the package is a font, generated source, bundled JavaScript or platform
   SDK input, update the reviewed hashes in `legal/supplements/bindings.json`.
7. Run the checks above and commit the profile, extracted inputs, supplements
   and generated files together.

Build-only packages may have no shipped text. Mark them
`"applicability": "build"` in `packages.json`; they still appear in the index.

## Exceptions

Exceptions live in `legal/supplements/packages.json` and are reviewed like code.
Every licence or validation exception is an exact `PackageId@Version` entry with
`license`, `documents` or `applicability`. New versions fail until reviewed.
Glob entries under `annotations` may add a display note, but never grant a
licence or validation exception. `requiredReview` lists families that must have
an exact entry, such as Windows App SDK, WebView2 and Win2D.

Known unresolved items stay explicit in the index: ApexCharts JavaScript 6.5
terms, Win2D EULA versus MIT, Nerd Fonts patcher provenance, and the Android
upstream notice correspondence.

## Host profiles

Committed profiles cover generic Web, Web RIDs (win-x64, linux-x64,
linux-arm64), desktop linux-x64 and osx-arm64, MAUI Windows, MAUI Android, the
Apple targets ios-arm64 and maccatalyst with both RIDs, and
maui-maccatalyst-app-store. CI checks the matching profile after each publish
job, including the Apple publish jobs.

The Apple profiles were exported on a Mac with the pinned SDK, Xcode and
workloads. To regenerate them, use a Mac checkout at the same commit:

```
dotnet --version                  # must print 10.0.403
dotnet workload restore
dotnet tool restore               # installs nuget-license 4.0.18
```

Run one Apple scenario at a time. Each refresh restores that scenario, exports
its profile, extracts its packages, merges them with the existing committed
`legal/extracted` records, and regenerates the catalogue. Do not use `--all` on
the Mac: it would rewrite the Windows and Linux graphs from a non-native host.

```
python3 scripts/legal/notices.py refresh --scenario desktop-osx-arm64
python3 scripts/legal/notices.py refresh --scenario maui-ios-arm64
python3 scripts/legal/notices.py refresh --scenario maui-maccatalyst
python3 scripts/legal/notices.py refresh --scenario maui-maccatalyst-app-store
python3 scripts/legal/notices.py check
python3 scripts/ci/inspect_legal_exceptions.py
```

The Mac App Store publish sets `MqttProbeMacAppStore=true`, which excludes
Velopack from the package graph. That
store graph needs its own reviewed profile, `maui-maccatalyst-app-store`, next to
the direct-download `maui-maccatalyst` profile. This is a legal package profile
under `legal/profiles`, not an Apple provisioning profile, and it plays no part
in App Store signing.

Expected changes are the selected Apple profile, Apple additions or updates in
`legal/extracted`, and catalogue or root `NOTICES` changes only where the Apple
graph changes. The Windows and Linux profiles and extracted records must remain
unchanged. These profile exports do not by themselves claim native UI, device or
legal certification.
