# Linux Clean AppImage Build

Reproducible wrapper that builds the MQTTProbe Linux AppImage inside a disposable Ubuntu 22.04 Docker container. Freezes a source SHA, extracts submodules from the same SHA, and runs publish, CSS gate, Photino rebuild, and AppImage assembly without touching the host workspace.

## Prerequisites

- Docker Desktop (running)
- Git
- PowerShell 5.1 (Windows built-in) or pwsh 7+ (tested on Windows)

No .NET SDK, vpk, or Linux build dependencies are needed on the host.

## Quick start

```powershell
# Current HEAD has uncommitted packaging fixes; use -IncludeWorkingTreeChanges
.\scripts\packaging\build-linux-appimage.ps1 -Version "1.0.6" -IncludeWorkingTreeChanges

# Committed baseline (once packaging fixes are merged)
.\scripts\packaging\build-linux-appimage.ps1 -Version "1.0.6"
```

Output lands in `publish/clean-linux-<timestamp>/` with the AppImage, releases metadata, and build logs.

## Options

| Parameter | Description |
|---|---|
| `-Version` (required) | SemVer version, e.g. `"1.0.6"` or `"v1.0.6"` |
| `-OutputDir` | Custom output path. Errors if non-empty. |
| `-IncludeWorkingTreeChanges` | Overlay tracked binary diffs onto the committed snapshot. Untracked application source files are excluded (commit them first). Dirty submodules are rejected. |
| `-PrepareOnly` | Materialize sources in Docker (extract, submodules, patch) without running SDK build. Requires Docker. |
| `-KeepScratch` | Retain the temp staging directory after success |

## Examples

```powershell
# Include tracked packaging fixes (current repo state)
.\scripts\packaging\build-linux-appimage.ps1 -Version "1.0.6" -IncludeWorkingTreeChanges

# Stage sources only (materialize in Docker, no SDK build)
.\scripts\packaging\build-linux-appimage.ps1 -Version "1.0.6" -PrepareOnly

# Custom output
.\scripts\packaging\build-linux-appimage.ps1 -Version "1.0.6" -OutputDir "publish/my-build"
```

## Source state

**Default (committed only):** Freezes the current HEAD SHA, creates `git archive` from that SHA, extracts submodule gitlinks (recursive) from the same SHA. `.gitmodules` is read from the committed tree, not the worktree. The archive and tooling hashes are verified inside the container before build steps begin.

**Named tooling overlay (both modes):** The gate script (`check-scoped-css.py`) and build driver (`build-clean-appimage.sh`) are staged to `scratch/tooling/` with SHA256 hashes recorded in a manifest. The container runs the driver from `/scratch/tooling/`, not from the read-only scripts mount. The gate is always available regardless of `-IncludeWorkingTreeChanges`.

**With `-IncludeWorkingTreeChanges`:** Applies `git diff HEAD --binary` (preserves exact bytes) onto the committed snapshot. Only tracked changes are included. Untracked application source files are not overlaid; commit them or they will be absent from the build. Dirty submodules are rejected with an explicit error; commit or stash submodule changes before using this flag.

**Known limitation:** The Velopack packer (vpk 1.2.161) excludes `createdump` via an embedded regex, so the final AppImage omits it even though `dotnet publish` stages it. `appdir.sh` prunes the helper before manifest generation to keep the inventory honest. The .NET runtime itself is unaffected; built-in dump collection still works, but the standalone `createdump` binary is not available inside the AppImage.

## What happens inside the container

1. Tooling integrity verified (SHA256 manifest check against staged files)
2. Source snapshot extracted; archive hash compared to PS1-provided metadata
3. Submodules initialized at exact pinned commits (JSON metadata, verified against gitlinks from same frozen SHA)
4. Tracked changes applied via `git apply --binary` (if `-IncludeWorkingTreeChanges`)
5. .NET SDK installed from the snapshot's `global.json` (currently 10.0.401)
6. Velopack CLI (vpk 1.2.161) installed
7. `dotnet publish` with Release config, linux-x64, self-contained
8. `check-scoped-css.py` gate from frozen tooling (fails build if rejects)
9. `rebuild-photino.sh` rebuilds Photino.Native (GLIBC <= 2.35, exact symbol parity verified)
10. `appdir.sh` builds the AppImage
11. `MQTTProbe.AppImage` existence verified (exact name, no fallback)
12. `releases.linux.json` required with nonempty MQTTProbe Full version matching `-Version`
13. Metadata generated via python/json (not shell interpolation)
14. Artifacts copied with fatal error handling (no swallowed failures)
15. Build log written to scratch directory (streaming output from Docker)

## Output

- `MQTTProbe.AppImage` (and Velopack files)
- `package-manifest.txt` (bundled library inventory)
- `metadata.json` (source commit, SDK version, vpk version, container image, build date)
- Build log (`build.log` in scratch, copied to output on success)

## Limitations

This wrapper runs publish, CSS gate, Photino rebuild, and AppImage assembly only. It does NOT run:

- `verify.py` baseline verification (requires clean Ubuntu 22.04 rootfs export)
- Third-party source archive collection (`collect-sources.sh`)
- Plain zip packaging
- Velopack delta download from previous release
- Artifact upload

Those steps are handled by the CI workflow (`build-linux-desktop.yml`).

## Troubleshooting

**Build fails, scratch preserved:** The staging directory is always kept on failure. The path is printed in the error output. Inspect logs, extracted source, and intermediate artifacts there.

**Dirty submodule error:** The wrapper rejects dirty submodules in `-IncludeWorkingTreeChanges` mode. Commit or stash submodule changes before running.

**Archive hash mismatch:** The container verifies the archive SHA256 against a hash file written by the PS1 wrapper. If they differ, the scratch directory may have been tampered with.

**Tooling integrity failure:** The container verifies SHA256 hashes of staged gate and driver scripts before executing them. If the manifest does not match, re-run the wrapper.

**`releases.linux.json` not found or version mismatch:** The build requires a nonempty MQTTProbe Full asset with a version matching `-Version`. If the Velopack step did not produce this, the build fails.

**Docker not running:** Start Docker Desktop before running the wrapper (required for both full builds and `-PrepareOnly`).