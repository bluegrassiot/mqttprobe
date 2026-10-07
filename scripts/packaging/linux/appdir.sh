#!/usr/bin/env bash
# appdir.sh — Build MQTTProbe AppImage for linux-x64
# Usage: ./appdir.sh --version <version> --publish-dir <dir> [--output-dir <dir>]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

VERSION=""
PUBLISH_DIR=""
OUTPUT_DIR="$REPO_ROOT/publish/appimage"
LINUXDEPLOY_URL="https://github.com/tauri-apps/binary-releases/releases/download/linuxdeploy-07333c6/linuxdeploy-x86_64.AppImage"
LINUXDEPLOY_SHA256="36a2d7e274d12e1050d0e9ecfe11d339ed54720b2bec464c286d53f8b07f5c62"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --publish-dir) PUBLISH_DIR="$2"; shift 2 ;;
        --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

if [[ -z "$VERSION" ]]; then
    echo "ERROR: --version is required"
    exit 1
fi
if [[ -z "$PUBLISH_DIR" ]]; then
    echo "ERROR: --publish-dir is required (path to dotnet publish output)"
    exit 1
fi

PUBLISH_DIR="$(cd "$PUBLISH_DIR" && pwd)"
OUTPUT_DIR="$(mkdir -p "$OUTPUT_DIR" && cd "$OUTPUT_DIR" && pwd)"

VERSION="${VERSION#v}"
if [[ -z "$VERSION" ]] || [[ "$VERSION" == "0.0.0" ]]; then
    echo "ERROR: Invalid version '$VERSION'"
    exit 1
fi

echo "========================================"
echo "Building MQTTProbe AppImage v${VERSION}"
echo "========================================"

WORK_DIR=$(mktemp -d)
APPDIR="$WORK_DIR/MQTTProbe.AppDir"

cleanup() {
    echo "Cleaning up $WORK_DIR"
    rm -rf "$WORK_DIR"
}
trap cleanup EXIT

cd "$REPO_ROOT"

# ── Step 1: Validate Photino.Native (already rebuilt by caller) ─────────────

echo "=== Step 1: Validating Photino.Native in publish dir ==="
PUBLISH_SO="$PUBLISH_DIR/Photino.Native.so"

if [[ ! -f "$PUBLISH_SO" ]]; then
    echo "ERROR: Photino.Native.so not found at $PUBLISH_SO"
    echo "  The caller must rebuild + replace Photino.Native before invoking this script."
    exit 1
fi

PUBLISH_GLIBC=$(objdump -T "$PUBLISH_SO" | grep -oP 'GLIBC_\d+\.\d+(\.\d+)?' | sort -Vu || true)
echo "Publish .so GLIBC requirements:"
echo "$PUBLISH_GLIBC"
GLIBC_MAX=$(echo "$PUBLISH_GLIBC" | sed 's/GLIBC_//' | sort -V | tail -1)
if [ "$(printf '%s\n' "2.35" "$GLIBC_MAX" | sort -V | tail -1)" != "2.35" ]; then
    echo "FATAL: Published .so requires GLIBC > 2.35 (found GLIBC_${GLIBC_MAX})"
    exit 1
fi
echo "PASS: GLIBC max ${GLIBC_MAX} (acceptable)"

PUBLISH_SYMS=$(nm -D --defined-only "$PUBLISH_SO" | awk '/[TtWw]/{print $3}' | grep -i '^photino' | sort -u || true)
SYM_COUNT=$(echo "$PUBLISH_SYMS" | grep -c '.' || true)
if [[ "$SYM_COUNT" -lt 50 ]]; then
    echo "FATAL: Expected ~60 Photino_* exports, found $SYM_COUNT"
    exit 1
fi
echo "PASS: $SYM_COUNT Photino exports, GLIBC max ${GLIBC_MAX}"

# ── Step 2: Prepare AppDir ──────────────────────────────────────────────────

echo "=== Step 2: Preparing AppDir structure ==="
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/lib" "$APPDIR/usr/share"
cp -a "$PUBLISH_DIR/." "$APPDIR/usr/bin/"

# ── Step 3: Stage WebKit helpers and injected-bundle ────────────────────────

echo "=== Step 3: Staging WebKit helpers and injected-bundle ==="
WEBKIT_LIBDIR="/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1"

if [[ ! -d "$WEBKIT_LIBDIR" ]]; then
    echo "ERROR: WebKit library dir not found at $WEBKIT_LIBDIR"
    exit 1
fi

mkdir -p "$APPDIR$WEBKIT_LIBDIR"
cp -a "$WEBKIT_LIBDIR"/libwebkit2gtk-4.1.so* "$APPDIR$WEBKIT_LIBDIR/" 2>/dev/null || true

for entry in "$WEBKIT_LIBDIR"/*; do
    name="$(basename "$entry")"
    if [[ -d "$entry" ]]; then
        cp -a "$entry" "$APPDIR$WEBKIT_LIBDIR/"
    elif [[ -f "$entry" ]]; then
        cp -a "$entry" "$APPDIR$WEBKIT_LIBDIR/"
    fi
done

# ── Step 4: Create .desktop + icon (metadata MUST precede linuxdeploy) ──────

echo "=== Step 4: Creating .desktop and icon ==="
cat > "$APPDIR/MQTTProbe.desktop" << 'DESKTOP_EOF'
[Desktop Entry]
Type=Application
Name=MQTTProbe
Exec=MqttProbe.Desktop
Icon=icon
Categories=Development;Network;
Comment=MQTT client and probe tool
DESKTOP_EOF

ICON_SRC="$REPO_ROOT/src/MqttProbe.Desktop/Assets/icon.png"
if [[ -f "$ICON_SRC" ]]; then
    cp "$ICON_SRC" "$APPDIR/icon.png"
else
    echo "WARNING: icon.png not found at $ICON_SRC"
fi

# AppImageHub appdir-lint.sh requires .DirIcon in the AppDir root and vpk pack
# never creates one. A relative symlink keeps a single PNG in the image and
# still resolves after linuxdeploy rewrites icon.png into a symlink pointing
# into usr/share/icons.
ensure_diricon() {
    local appdir="$1"
    if [[ ! -f "$appdir/icon.png" ]]; then
        echo "FATAL: $appdir/icon.png missing, cannot create .DirIcon"
        exit 1
    fi
    ln -sf icon.png "$appdir/.DirIcon"
    echo "  .DirIcon -> icon.png"
}

ensure_diricon "$APPDIR"

# ── Step 5: Download linuxdeploy + install prerequisites ────────────────────

echo "=== Step 5: Running linuxdeploy with GTK/GStreamer plugins ==="
LINUXDEPLOY_APPIMAGE="$WORK_DIR/linuxdeploy-x86_64.AppImage"

echo "Downloading linuxdeploy..."
curl -sL "$LINUXDEPLOY_URL" -o "$LINUXDEPLOY_APPIMAGE"
echo "$LINUXDEPLOY_SHA256  $LINUXDEPLOY_APPIMAGE" | sha256sum -c -
chmod +x "$LINUXDEPLOY_APPIMAGE"

echo "Extracting linuxdeploy..."
cd "$WORK_DIR"
"$LINUXDEPLOY_APPIMAGE" --appimage-extract > /dev/null 2>&1 || true
LINUXDEPLOY="$WORK_DIR/squashfs-root/AppRun"
if [[ ! -x "$LINUXDEPLOY" ]]; then
    echo "FATAL: linuxdeploy extraction failed"
    exit 1
fi
chmod +x "$LINUXDEPLOY"

echo "Installing plugin prerequisites..."
if [ "$(id -u)" -eq 0 ]; then
    apt-get update -qq
    apt-get install -y -qq \
        patchelf librsvg2-common librsvg2-dev gobject-introspection \
        libgirepository1.0-dev libgstreamer1.0-0 libgstreamer-plugins-base1.0-0 \
        gstreamer1.0-plugins-base gstreamer1.0-plugins-good squashfs-tools \
        2>&1
else
    sudo apt-get update -qq
    sudo apt-get install -y -qq \
        patchelf librsvg2-common librsvg2-dev gobject-introspection \
        libgirepository1.0-dev libgstreamer1.0-0 libgstreamer-plugins-base1.0-0 \
        gstreamer1.0-plugins-base gstreamer1.0-plugins-good squashfs-tools \
        2>&1
fi

# LTTng tracing provider links liblttng-ust.so.0 (missing on Ubuntu 22.04).
# App functions fully without it; only the native LTTng sink is lost.
TRACE_PROVIDER="$APPDIR/usr/bin/libcoreclrtraceptprovider.so"
if [[ -f "$TRACE_PROVIDER" ]]; then
    echo "Removing optional LTTng tracing provider: $(basename "$TRACE_PROVIDER")"
    rm -f "$TRACE_PROVIDER"
fi

# .NET crash-dump helper: not needed at runtime, absent from the final
# AppImage (Velopack/vpk exclude via regex). Prune early so dependency
# scanning does not waste time resolving its transitive libs.
CREATEDUMP="$APPDIR/usr/bin/createdump"
if [[ -f "$CREATEDUMP" ]]; then
    echo "Removing .NET crash-dump helper: createdump (excluded from AppImage)"
    rm -f "$CREATEDUMP"
fi

cp "$SCRIPT_DIR/vendor/linuxdeploy-plugin-gtk.sh" "$WORK_DIR/linuxdeploy-plugin-gtk.sh"
cp "$SCRIPT_DIR/vendor/linuxdeploy-plugin-gstreamer.sh" "$WORK_DIR/linuxdeploy-plugin-gstreamer.sh"
chmod +x "$WORK_DIR/linuxdeploy-plugin-gtk.sh" "$WORK_DIR/linuxdeploy-plugin-gstreamer.sh"

DEPLOY_GTK_VERSION=3 \
LINUXDEPLOY_PLUGIN_MODE=1 \
LINUXDEPLOY="$LINUXDEPLOY" \
bash "$WORK_DIR/linuxdeploy-plugin-gtk.sh" --appdir "$APPDIR"

LINUXDEPLOY="$LINUXDEPLOY" \
bash "$WORK_DIR/linuxdeploy-plugin-gstreamer.sh" --appdir "$APPDIR"

# Must run after the GStreamer plugin: libgstjack.so arrives with the plugin
# deploy, so pruning earlier finds nothing and leaves libjack.so.0 unresolved.
# JACK audio is not a standard desktop dependency and MQTTProbe does not use it.
JACK_PLUGIN=$(find "$APPDIR" -name "libgstjack.so" -type f 2>/dev/null || true)
if [[ -n "$JACK_PLUGIN" ]]; then
    echo "Removing JACK audio plugin: $(basename "$JACK_PLUGIN")"
    rm -f "$JACK_PLUGIN"
fi

# ── Step 5b: Bundle font/text stack (harfbuzz, fontconfig, freetype, fribidi) ──
# Previously a separate helper; consolidated here as an
# internal function since it is called exactly once and is self-contained.

echo "=== Step 5b: Bundling font/text stack ==="

_bundle_font_libs() {
    local appdir="$1"
    local linuxdeploy="$2"

    local font_libs=(
        "libharfbuzz.so.0"
        "libfontconfig.so.1"
        "libfreetype.so.6"
        "libfribidi.so.0"
    )

    for lib_name in "${font_libs[@]}"; do
        # Check if already bundled by linuxdeploy
        if find "$appdir/usr/lib" -name "$lib_name" -type f 2>/dev/null | grep -q .; then
            echo "  Already bundled: $lib_name"
            continue
        fi

        # Find on the system
        local sys_lib=""
        for search_dir in /usr/lib/x86_64-linux-gnu /usr/lib64 /usr/lib; do
            local candidate="$search_dir/$lib_name"
            if [[ -f "$candidate" ]]; then
                sys_lib="$candidate"
                break
            fi
            candidate=$(find "$search_dir" -name "${lib_name}*" -type f 2>/dev/null | head -1 || true)
            if [[ -n "$candidate" ]]; then
                sys_lib="$candidate"
                break
            fi
        done

        if [[ -z "$sys_lib" ]]; then
            echo "FATAL: $lib_name not found on system"
            exit 1
        fi

        echo "  Bundling: $sys_lib"
        mkdir -p "$appdir/usr/lib"
        cp -aL "$sys_lib" "$appdir/usr/lib/"
        local actual_name
        actual_name=$(basename "$sys_lib")
        if [[ "$actual_name" != "$lib_name" ]]; then
            ln -sf "$actual_name" "$appdir/usr/lib/$lib_name"
        fi

        "$linuxdeploy" --appdir "$appdir" --deploy-deps-only "$appdir/usr/lib/$actual_name" 2>&1 || {
            echo "FATAL: linuxdeploy --deploy-deps-only failed for $lib_name"
            exit 1
        }
    done

    echo "PASS: Font/text stack bundled"
}

_bundle_font_libs "$APPDIR" "$LINUXDEPLOY"

# ── Step 6: Deploy-deps-only for each helper/injected/plugin ELF ────────────
# Batch all --deploy-deps-only targets into a single linuxdeploy invocation.
# Each invocation previously rescanned usr/bin and recursively usr/lib;
# batching amortizes the full scan across all helpers/plugins.

echo "=== Step 6: Deploy-deps-only for WebKit/GStreamer ELFs ==="
_deploy_args=()

deploy_deps_for_elf() {
    local elf="$1"
    if [[ ! -f "$elf" ]]; then return; fi
    if ! file "$elf" | grep -q "ELF"; then return; fi
    _deploy_args+=("--deploy-deps-only" "$elf")
}

for helper in "$APPDIR$WEBKIT_LIBDIR"/WebKit*; do
    [[ -f "$helper" ]] && deploy_deps_for_elf "$helper"
done

if [[ -d "$APPDIR$WEBKIT_LIBDIR/injected-bundle" ]]; then
    for so in "$APPDIR$WEBKIT_LIBDIR/injected-bundle"/*.so; do
        [[ -f "$so" ]] && deploy_deps_for_elf "$so"
    done
fi

for plugin_dir in "$APPDIR"/usr/lib/gstreamer-*; do
    if [[ -d "$plugin_dir" ]]; then
        for so in "$plugin_dir"/*.so; do
            [[ -f "$so" ]] && deploy_deps_for_elf "$so"
        done
    fi
done

for gst_helper_dir in "$APPDIR"/usr/lib/gstreamer*/gstreamer-*; do
    if [[ -d "$gst_helper_dir" ]]; then
        for helper in "$gst_helper_dir"/*; do
            [[ -f "$helper" ]] && deploy_deps_for_elf "$helper"
        done
    fi
done

if [[ "${#_deploy_args[@]}" -gt 0 ]]; then
    "$LINUXDEPLOY" --appdir "$APPDIR" "${_deploy_args[@]}" 2>&1 || {
        echo "FATAL: linuxdeploy --deploy-deps-only batch failed"
        exit 1
    }
fi

"$LINUXDEPLOY" --appdir "$APPDIR" \
    --executable "$APPDIR/usr/bin/MqttProbe.Desktop" \
    --desktop-file "$APPDIR/MQTTProbe.desktop" \
    --icon-file "$APPDIR/icon.png"

# ── Step 7: Narrow WebKit relocation ────────────────────────────────────────

echo "=== Step 7: Applying narrow WebKit relocation ==="
apply_webkit_narrow_relocation() {
    local appdir="$1"
    local OLD_PREFIX="/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1"
    local NEW_PREFIX="././/lib/x86_64-linux-gnu/webkit2gtk-4.1"

    local old_len=${#OLD_PREFIX}
    local new_len=${#NEW_PREFIX}
    if [[ "$old_len" -ne "$new_len" ]]; then
        echo "FATAL: prefix length mismatch: old=$old_len new=$new_len"
        exit 1
    fi

    local relocated_count=0

    while IFS= read -r -d '' lib; do
        if ! file "$lib" | grep -q "ELF"; then
            continue
        fi

        local before_size
        before_size=$(stat -c%s "$lib")

        local occurrences
        occurrences=$(grep -cF "$OLD_PREFIX" "$lib" 2>/dev/null || true)

        if [[ "$occurrences" -eq 0 ]]; then
            continue
        fi

        echo "Relocating: $(basename "$lib") ($occurrences occurrences, size=$before_size)"

        perl -0777 -pi -e "s|\Q$OLD_PREFIX\E|${NEW_PREFIX}|g" "$lib"

        local after_size
        after_size=$(stat -c%s "$lib")

        if [[ "$before_size" -ne "$after_size" ]]; then
            echo "FATAL: File size changed: $before_size -> $after_size"
            exit 1
        fi

        if ! readelf -h "$lib" >/dev/null 2>&1; then
            echo "FATAL: ELF header invalid after relocation"
            exit 1
        fi

        local remaining
        remaining=$(grep -cF "$OLD_PREFIX" "$lib" 2>/dev/null || true)
        if [[ "$remaining" -gt 0 ]]; then
            echo "FATAL: Old prefix still present ($remaining occurrences)"
            exit 1
        fi

        relocated_count=$((relocated_count + 1))
    done < <(find "$appdir" -name 'libwebkit*' -print0)

    if [[ "$relocated_count" -eq 0 ]]; then
        echo "FATAL: Zero WebKit files found for relocation"
        exit 1
    fi

    echo "Relocated $relocated_count WebKit library files"
}

apply_webkit_narrow_relocation "$APPDIR"

# ── Step 8: Create custom AppRun ────────────────────────────────────────────

echo "=== Step 8: Creating custom AppRun ==="
cat > "$APPDIR/AppRun" << 'APPRUN_EOF'
#!/usr/bin/env bash
# MQTTProbe AppRun — Custom launcher for AppImage

SELF="$(readlink -f "$0")"
APPDIR="$(dirname "$SELF")"
export APPDIR

_LD="$APPDIR/usr/lib:$APPDIR/usr/lib/x86_64-linux-gnu:$APPDIR/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1"
if [[ -n "${LD_LIBRARY_PATH:-}" ]]; then
    export LD_LIBRARY_PATH="${_LD}:${LD_LIBRARY_PATH}"
else
    export LD_LIBRARY_PATH="$_LD"
fi

# WebKit injected bundle path (absolute). NOT WEBKIT_EXEC_PATH (developer only).
export WEBKIT_INJECTED_BUNDLE_PATH="$APPDIR/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1/injected-bundle"

if [[ -d "$APPDIR/apprun-hooks" ]]; then
    for hook in "$APPDIR/apprun-hooks"/*.sh; do
        [[ -f "$hook" ]] && source "$hook"
    done
fi

cd "$APPDIR/usr" || { echo "ERROR: Cannot cd to $APPDIR/usr"; exit 1; }
exec "$APPDIR/usr/bin/MqttProbe.Desktop" "$@"
APPRUN_EOF
chmod +x "$APPDIR/AppRun"

# ── Step 9: Generate manifest and NOTICE (single inventory) ─────────────────

echo "=== Step 9: Generating package manifest and NOTICE ==="
BUILD_DATE="${SOURCE_DATE_EPOCH:-}"
if [[ -z "$BUILD_DATE" ]]; then
    BUILD_DATE="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
else
    BUILD_DATE="$(date -u -d "@$BUILD_DATE" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u +%Y-%m-%dT%H:%M:%SZ)"
fi

SOURCE_ARCHIVE_NAME="mqttprobe-third-party-sources-v${VERSION}.tar.gz"
python3 "$SCRIPT_DIR/notices.py" \
    --appdir "$APPDIR" \
    --version "$VERSION" \
    --source-archive "$SOURCE_ARCHIVE_NAME" \
    --build-date "$BUILD_DATE" \
    --output-dir "$OUTPUT_DIR" \
    --strict-unknown || {
    echo "FATAL: Python manifest/NOTICE generation failed"
    exit 1
}

MANIFEST_FILE="$OUTPUT_DIR/package-manifest.txt"
NOTICE_PATH="$APPDIR/usr/share/doc/mqttprobe/NOTICE"

if [[ ! -f "$MANIFEST_FILE" ]]; then
    echo "ERROR: Manifest file not created at $MANIFEST_FILE"
    exit 1
fi
echo "Manifest written to: $MANIFEST_FILE"

if [[ ! -f "$NOTICE_PATH" ]]; then
    echo "ERROR: NOTICE file not created"
    exit 1
fi
echo "NOTICE written to: $NOTICE_PATH"

# ── Step 10: Feed to vpk pack ───────────────────────────────────────────────

# Re-assert: linuxdeploy rewrites the AppDir root icon set (icon.png becomes a
# symlink into usr/share/icons), so the link is rebuilt immediately before
# packing rather than trusted from Step 4.
ensure_diricon "$APPDIR"

echo "=== Step 10: Building AppImage via vpk pack ==="
vpk pack \
    --packId MQTTProbe \
    --packVersion "$VERSION" \
    --packDir "$APPDIR" \
    --mainExe usr/bin/MqttProbe.Desktop \
    --packTitle MQTTProbe \
    --packAuthors "Bluegrass IoT" \
    --icon "$ICON_SRC" \
    --outputDir "$OUTPUT_DIR"

echo "=== Build complete ==="
echo "Output directory: $OUTPUT_DIR"
ls -la "$OUTPUT_DIR"

APPIMAGE_FILE="$OUTPUT_DIR/MQTTProbe.AppImage"
if [[ ! -f "$APPIMAGE_FILE" ]]; then
    APPIMAGE_FILE=$(find "$OUTPUT_DIR" -name "*.AppImage" -type f 2>/dev/null | head -1 || true)
fi
if [[ -z "$APPIMAGE_FILE" ]] || [[ ! -f "$APPIMAGE_FILE" ]]; then
    echo "ERROR: AppImage not found in $OUTPUT_DIR"
    ls -la "$OUTPUT_DIR" 2>/dev/null || echo "Output dir empty"
    exit 1
fi

echo "AppImage: $APPIMAGE_FILE"
echo "Size: $(du -h "$APPIMAGE_FILE" | cut -f1)"
file "$APPIMAGE_FILE"