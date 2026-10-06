#!/usr/bin/env bash
# build-clean-appimage.sh — Build MQTTProbe AppImage in a disposable container.
#
# Runs inside ubuntu:22.04 with /scratch bind-mounted.
# Tooling (this script + gate) comes from /scratch/tooling/ (frozen overlay).
# Source comes from /scratch/work/ (extracted archive).

set -euo pipefail

VERSION=""
SOURCE_SHA=""
SCRATCH=""
INCLUDE_CHANGES=0
PREPARE_ONLY=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)          VERSION="$2";         shift 2 ;;
        --source-sha)       SOURCE_SHA="$2";      shift 2 ;;
        --scratch)          SCRATCH="$2";         shift 2 ;;
        --include-changes)  INCLUDE_CHANGES=1;    shift ;;
        --prepare-only)     PREPARE_ONLY=1;       shift ;;
        *) echo "ERROR: Unknown argument: $1"; exit 1 ;;
    esac
done

if [[ -z "$VERSION" || -z "$SOURCE_SHA" || -z "$SCRATCH" ]]; then
    echo "ERROR: --version, --source-sha, --scratch are required"
    exit 1
fi

VERSION="${VERSION#v}"
if [[ -z "$VERSION" || "$VERSION" == "0.0.0" ]]; then
    echo "ERROR: Invalid version: $VERSION"; exit 1
fi

SOURCE_TAR="$SCRATCH/source.tar.gz"
SUBMODULES_FILE="$SCRATCH/submodules.json"
PATCH_FILE="$SCRATCH/changes.patch"
TOOLING_DIR="$SCRATCH/tooling"
TOOLING_MANIFEST="$TOOLING_DIR/manifest.json"
WORK="$SCRATCH/work"
OUTPUT="$SCRATCH/output"

mkdir -p "$WORK" "$OUTPUT"

echo "============================================"
echo "Clean AppImage Build v${VERSION}"
echo "SHA:     ${SOURCE_SHA}"
echo "Changes: ${INCLUDE_CHANGES}"
echo "Prepare: ${PREPARE_ONLY}"
echo "============================================"

# ── Step 1: Install git + prereqs ─────────────────────────────────────────────

echo ""
echo "=== Step 1: Installing prerequisites ==="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq 2>&1
apt-get install -y -qq git curl python3 patch ca-certificates 2>&1
echo "Prerequisites installed"

# ── Verify tooling manifest (before executing any tooling) ──────────────────

echo ""
echo "=== Verifying tooling integrity ==="
if [[ ! -f "$TOOLING_MANIFEST" ]]; then
    echo "FATAL: Tooling manifest not found at $TOOLING_MANIFEST"
    exit 1
fi
set +e
VERIFY_RESULT=$(python3 -c "
import json, hashlib, sys, os
manifest_path = sys.argv[1]
tooling_dir = sys.argv[2]
with open(manifest_path, encoding='utf-8-sig') as f:
    expected = json.load(f)
ok = True
for name, expected_hash in expected.items():
    fpath = os.path.join(tooling_dir, name)
    if not os.path.exists(fpath):
        print(f'MISSING: {name}')
        ok = False
        continue
    actual = hashlib.sha256(open(fpath, 'rb').read()).hexdigest().upper()
    if actual != expected_hash.upper():
        print(f'MISMATCH: {name} expected={expected_hash[:16]} actual={actual[:16]}')
        ok = False
    else:
        print(f'OK: {name} ({actual[:16]}...)')
if not ok:
    sys.exit(1)
" "$TOOLING_MANIFEST" "$TOOLING_DIR" 2>&1)
VERIFY_EXIT=$?
set -e
echo "$VERIFY_RESULT"
if [[ $VERIFY_EXIT -ne 0 ]]; then
    echo "FATAL: Tooling integrity check failed"
    exit 1
fi
echo "Tooling integrity verified"

# ── Step 2: Extract source snapshot ──────────────────────────────────────────

echo ""
echo "=== Step 2: Extracting source snapshot ==="
if [[ ! -f "$SOURCE_TAR" ]]; then
    echo "FATAL: Source archive not found at $SOURCE_TAR"
    exit 1
fi

tar xzf "$SOURCE_TAR" -C "$WORK"

if [[ ! -f "$WORK/MqttProbe.slnx" ]]; then
    echo "FATAL: Archive missing MqttProbe.slnx"
    exit 1
fi
if [[ ! -f "$WORK/global.json" ]]; then
    echo "FATAL: Archive missing global.json"
    exit 1
fi

# Verify archive hash matches PS1-provided metadata
ARCHIVE_HASH=$(sha256sum "$SOURCE_TAR" | cut -d' ' -f1)
ARCHIVE_HASH_FILE="$SCRATCH/archive-hash.txt"
if [[ -f "$ARCHIVE_HASH_FILE" ]]; then
    META_HASH=$(cat "$ARCHIVE_HASH_FILE" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')
    ARCHIVE_HASH_LOWER=$(echo "$ARCHIVE_HASH" | tr '[:upper:]' '[:lower:]')
    if [[ "$ARCHIVE_HASH_LOWER" != "$META_HASH" ]]; then
        echo "FATAL: Archive hash mismatch"
        echo "  Expected: $META_HASH"
        echo "  Actual:   $ARCHIVE_HASH_LOWER"
        exit 1
    fi
    echo "Archive hash verified: ${ARCHIVE_HASH_LOWER:0:16}..."
else
    echo "WARNING: archive-hash.txt not found, skipping hash verification"
    echo "Archive SHA256: ${ARCHIVE_HASH:0:16}..."
fi
FILE_COUNT=$(find "$WORK" -maxdepth 1 -type f | wc -l)
echo "Extracted: $FILE_COUNT top-level files"

# ── Step 3: Initialize submodules ────────────────────────────────────────────

echo ""
echo "=== Step 3: Initializing submodules ==="
if [[ -f "$SUBMODULES_FILE" ]] && [[ -s "$SUBMODULES_FILE" ]]; then
    SM_COUNT=$(python3 -c "import json; print(len(json.load(open('$SUBMODULES_FILE'))))")
    echo "  Submodule entries: $SM_COUNT"

    while IFS=$'\t' read -r sm_path sm_url sm_commit; do
        [[ -z "$sm_path" ]] && continue
        echo "  Cloning $sm_path at $sm_commit ..."
        if ! git -c "safe.directory=$WORK/$sm_path" clone --depth=1 "$sm_url" "$WORK/$sm_path" 2>&1; then
            echo "FATAL: Failed to clone $sm_url"
            exit 1
        fi
        pushd "$WORK/$sm_path" > /dev/null
        if ! git -c "safe.directory=$WORK/$sm_path" fetch --depth=1 origin "$sm_commit" 2>&1; then
            echo "FATAL: Failed to fetch $sm_commit"
            exit 1
        fi
        git -c "safe.directory=$WORK/$sm_path" checkout FETCH_HEAD 2>&1
        ACTUAL=$(git -c "safe.directory=$WORK/$sm_path" rev-parse HEAD)
        if [[ "$ACTUAL" != "$sm_commit" ]]; then
            echo "FATAL: Submodule commit mismatch in $sm_path"
            echo "  Expected: $sm_commit"
            echo "  Actual:   $ACTUAL"
            exit 1
        fi
        echo "  OK: $sm_path at $sm_commit"
        popd > /dev/null
    done < <(python3 -c "
import json, sys
with open('$SUBMODULES_FILE') as f:
    data = json.load(f)
for sm in data:
    sys.stdout.write(sm['path'] + '\t' + sm['url'] + '\t' + sm['commit'] + '\n')
")
else
    echo "  No submodules"
fi

# ── Step 4: Apply working tree changes ───────────────────────────────────────

if [[ "$INCLUDE_CHANGES" -eq 1 ]]; then
    echo ""
    echo "=== Step 4: Applying tracked changes ==="
    if [[ -s "$PATCH_FILE" ]]; then
        echo "  Applying binary patch ($(stat -c%s "$PATCH_FILE") bytes)..."
        pushd "$WORK" > /dev/null
        if ! git apply --binary --ignore-whitespace "$PATCH_FILE" 2>&1; then
            echo "FATAL: Failed to apply patch"
            exit 1
        fi
        echo "  Patch applied"
        popd > /dev/null
    else
        echo "  No tracked changes (0-byte patch)"
    fi
else
    echo ""
    echo "=== Step 4: Skipped (committed-only) ==="
fi

# ── PrepareOnly: stop after source materialization ────────────────────────────

if [[ "$PREPARE_ONLY" -eq 1 ]]; then
    echo ""
    echo "=== PrepareOnly: materialization complete ==="
    echo "Work: $WORK"
    echo "SHA:  $SOURCE_SHA"
    echo "Ver:  $VERSION"
    ls -la "$WORK/MqttProbe.slnx" "$WORK/global.json" 2>&1 || true
    ls -d "$WORK/external/SparkplugNet/.git" 2>/dev/null && echo "Submodule: SparkplugNet OK" || echo "Submodule: SparkplugNet not found"
    ls -la "$TOOLING_DIR/" 2>&1 || true
    exit 0
fi

# ── Step 5: Read SDK version from snapshot ───────────────────────────────────

echo ""
echo "=== Step 5: Reading build configuration ==="
GLOBAL_JSON="$WORK/global.json"
DOTNET_SDK_VERSION=$(python3 -c "import json; print(json.load(open('$GLOBAL_JSON'))['sdk']['version'])")
VPK_VERSION="1.2.161"
echo "SDK: $DOTNET_SDK_VERSION, vpk: $VPK_VERSION"

# ── Step 6: System prerequisites ─────────────────────────────────────────────

echo ""
echo "=== Step 6: System prerequisites ==="
apt-get install -y -qq \
    libwebkit2gtk-4.1-0 libgtk-3-dev libwebkit2gtk-4.1-dev \
    libnotify4 libnotify-dev pkg-config g++ patchelf file squashfs-tools 2>&1

# ── Step 7: .NET SDK ─────────────────────────────────────────────────────────

echo ""
echo "=== Step 7: .NET SDK $DOTNET_SDK_VERSION ==="
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version "$DOTNET_SDK_VERSION" --install-dir /usr/share/dotnet 2>&1
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_ROOT="/usr/share/dotnet"
INSTALLED_SDK=$(dotnet --version 2>/dev/null || echo "unknown")
echo "SDK installed: $INSTALLED_SDK"

# ── Step 8: Velopack CLI ─────────────────────────────────────────────────────

echo ""
echo "=== Step 8: vpk $VPK_VERSION ==="
dotnet tool install -g vpk --version "$VPK_VERSION" 2>&1
export PATH="$PATH:$HOME/.dotnet/tools"

# ── Step 9: Build environment ────────────────────────────────────────────────

export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="$WORK/.nuget/packages"
cd "$WORK"

# ── Step 10: Publish ─────────────────────────────────────────────────────────

echo ""
echo "=== Step 10: Publishing ==="
dotnet publish src/MqttProbe.Desktop/MqttProbe.Desktop.csproj \
    -c:Release -r:linux-x64 --self-contained \
    -p:Version="$VERSION" --output publish/desktop 2>&1 | tee "$SCRATCH/publish.log"
if [[ "${PIPESTATUS[0]}" -ne 0 ]]; then echo "FATAL: dotnet publish failed"; exit 1; fi

# ── Step 11: CSS gate (from frozen tooling) ──────────────────────────────────

echo ""
echo "=== Step 11: CSS gate ==="
CSS_GATE="$TOOLING_DIR/check-scoped-css.py"
if [[ ! -f "$CSS_GATE" ]]; then
    echo "FATAL: Gate not found at $CSS_GATE"
    exit 1
fi
python3 "$CSS_GATE" --publish-dir publish/desktop 2>&1 | tee "$SCRATCH/gate.log"
if [[ "${PIPESTATUS[0]}" -ne 0 ]]; then echo "FATAL: CSS gate failed"; exit 1; fi
echo "CSS gate passed"

# ── Step 12: Rebuild Photino.Native ──────────────────────────────────────────

echo ""
echo "=== Step 12: Rebuilding Photino.Native ==="
bash "$WORK/scripts/packaging/linux/rebuild-photino.sh" \
    --output-dir publish/photino-rebuilt 2>&1 | tee "$SCRATCH/photino.log"
if [[ "${PIPESTATUS[0]}" -ne 0 ]]; then echo "FATAL: Photino rebuild failed"; exit 1; fi

REBUILT_SO="publish/photino-rebuilt/Photino.Native.so"
PUBLISH_SO="publish/desktop/Photino.Native.so"
if [[ ! -f "$REBUILT_SO" ]]; then echo "FATAL: Rebuilt .so not found"; exit 1; fi

# Assert deps version 4.0.22
DEPS_JSON="publish/desktop/MqttProbe.Desktop.deps.json"
DEPS_VER=$(python3 -c "
import json
with open('$DEPS_JSON') as f:
    libs = json.load(f).get('libraries', {})
matches = [k for k in libs if k.startswith('Photino.Native/')]
print(matches[0].split('/', 1)[1] if matches else 'NONE', end='')
")
if [[ "$DEPS_VER" != "4.0.22" ]]; then
    echo "FATAL: Expected Photino.Native 4.0.22, found '$DEPS_VER'"; exit 1
fi
echo "PASS: deps.json Photino.Native/$DEPS_VER"

# Assert GLIBC <= 2.35
REBUILT_GLIBC=$(objdump -T "$REBUILT_SO" | grep -oP 'GLIBC_\d+\.\d+(\.\d+)?' | sort -Vu || true)
GLIBC_MAX=$(echo "$REBUILT_GLIBC" | sed 's/GLIBC_//' | sort -V | tail -1)
if [ "$(printf '%s\n' "2.35" "$GLIBC_MAX" | sort -V | tail -1)" != "2.35" ]; then
    echo "FATAL: GLIBC > 2.35 (found ${GLIBC_MAX})"; exit 1
fi
echo "PASS: GLIBC max ${GLIBC_MAX}"

# Assert symbol parity
ORIG_SYMS=$(nm -D --defined-only "$PUBLISH_SO" | awk '/[TtWw]/{print $3}' | grep -i '^photino' | sort -u || true)
REBUILT_SYMS=$(nm -D --defined-only "$REBUILT_SO" | awk '/[TtWw]/{print $3}' | grep -i '^photino' | sort -u || true)
if [[ -z "$REBUILT_SYMS" ]]; then echo "FATAL: No Photino exports"; exit 1; fi
if [[ "$ORIG_SYMS" != "$REBUILT_SYMS" ]]; then
    echo "FATAL: Symbol mismatch"; diff <(echo "$ORIG_SYMS") <(echo "$REBUILT_SYMS") || true; exit 1
fi
echo "PASS: $(echo "$REBUILT_SYMS" | wc -l) Photino exports match"
cp "$REBUILT_SO" "$PUBLISH_SO"

# ── Step 13: Build AppImage ──────────────────────────────────────────────────

echo ""
echo "=== Step 13: AppImage ==="
bash "$WORK/scripts/packaging/linux/appdir.sh" \
    --version "$VERSION" --publish-dir publish/desktop \
    --output-dir publish/velopack-linux 2>&1 | tee "$SCRATCH/appimage.log"
if [[ "${PIPESTATUS[0]}" -ne 0 ]]; then echo "FATAL: AppImage build failed"; exit 1; fi

APPIMAGE_FILE="publish/velopack-linux/MQTTProbe.AppImage"
if [[ ! -f "$APPIMAGE_FILE" ]]; then
    echo "FATAL: Expected MQTTProbe.AppImage not found at $APPIMAGE_FILE"
    ls -la publish/velopack-linux/ 2>/dev/null || true
    exit 1
fi
echo "AppImage: $APPIMAGE_FILE ($(du -h "$APPIMAGE_FILE" | cut -f1))"

# Verify releases.linux.json (REQUIRED, not optional)
RELEASES_JSON="publish/velopack-linux/releases.linux.json"
if [[ ! -f "$RELEASES_JSON" ]]; then
    echo "FATAL: releases.linux.json not found at $RELEASES_JSON"
    exit 1
fi
META_VERSION=$(python3 -c "
import json
with open('$RELEASES_JSON') as f:
    data = json.load(f)
for a in data.get('Assets', []):
    if a.get('PackageId') == 'MQTTProbe' and a.get('Type') == 'Full':
        print(a.get('Version', ''), end='')
        break
")
if [[ -z "$META_VERSION" ]]; then
    echo "FATAL: No MQTTProbe Full asset found in releases.linux.json"
    exit 1
fi
if [[ "$META_VERSION" != "$VERSION" ]]; then
    echo "FATAL: releases.linux.json version mismatch: $META_VERSION vs $VERSION"
    exit 1
fi
echo "PASS: releases.linux.json MQTTProbe Full version $META_VERSION"

# ── Step 14: Metadata ────────────────────────────────────────────────────────

echo ""
echo "=== Step 14: Metadata ==="
UBUNTU_VERSION=$(grep VERSION_ID /etc/os-release | cut -d= -f2 | tr -d '"')
DOTNET_RUNTIME_VER=$(dotnet --list-runtimes 2>/dev/null | grep Microsoft.NETCore.App | tail -1 || echo "unknown")
BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ)

python3 -c "
import json, sys
meta = {
    'version': sys.argv[1],
    'source_commit': sys.argv[2],
    'include_working_tree_changes': sys.argv[3] == '1',
    'container_image': 'ubuntu:' + sys.argv[4],
    'dotnet_sdk_version': sys.argv[5],
    'dotnet_runtime': sys.argv[6],
    'vpk_version': sys.argv[7],
    'build_date': sys.argv[8],
}
with open('$OUTPUT/metadata.json', 'w') as f:
    json.dump(meta, f, indent=2)
    f.write('\n')
" "$VERSION" "$SOURCE_SHA" "$INCLUDE_CHANGES" \
  "$UBUNTU_VERSION" "$INSTALLED_SDK" "$DOTNET_RUNTIME_VER" \
  "$VPK_VERSION" "$BUILD_DATE"

# ── Step 15: Copy artifacts ──────────────────────────────────────────────────

echo ""
echo "=== Step 15: Copying artifacts ==="
if [[ ! -d "publish/velopack-linux" ]]; then echo "FATAL: No velopack-linux dir"; exit 1; fi
cp -v publish/velopack-linux/* "$OUTPUT/" 2>&1 || { echo "FATAL: Copy failed"; exit 1; }
[[ -f "publish/velopack-linux/package-manifest.txt" ]] && cp "publish/velopack-linux/package-manifest.txt" "$OUTPUT/"
for log in "$SCRATCH"/*.log; do [[ -f "$log" ]] && cp "$log" "$OUTPUT/"; done

echo ""
echo "=== Build complete ==="
echo "Output: $OUTPUT"
echo "SHA:    $SOURCE_SHA"
ls -la "$OUTPUT"
echo ""
echo "NOTE: This wrapper runs publish, CSS gate, Photino rebuild, and"
echo "AppImage assembly only. It does NOT run verify.py baseline, source"
echo "archive collection, zip packaging, Velopack delta download, or"
echo "artifact upload. Those are handled by CI (build-linux-desktop.yml)."