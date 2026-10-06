#!/usr/bin/env bash
# collect-sources.sh — Collect actual third-party source archives for AppImage compliance
#
# Usage:
#   ./collect-sources.sh --version <version> --output-dir <dir>
#                        --manifest <path>
#
# Creates a source archive containing:
#   - Photino.Native source at the pinned v4.0.22 commit
#   - Actual Ubuntu 22.04 package source archives (.dsc + .orig + .debian)
#   - Actual copyright/license texts from /usr/share/doc/<pkg>/copyright
#   - Common license texts from /usr/share/common-licenses/
#   - Vendored script sources (linuxdeploy plugins)
#   - Build and relocation scripts
#   - Package version manifest (from build manifest)
#
# --manifest is REQUIRED (from appdir.sh's manifest generation).
# Package list and source versions are parsed from the manifest RECORD;
# no hardcoded fallback or dpkg-query re-query.
#
# Requires: git, apt-get (ubuntu-22.04)
# Must run on ubuntu-22.04 with sudo access to enable deb-src and fetch sources.
#
# Vendor provenance: scripts/packaging/linux/vendor/NOTICE.md

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

VERSION=""
OUTPUT_DIR=""
MANIFEST_PATH=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
        --manifest) MANIFEST_PATH="$2"; shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

if [[ -z "$VERSION" ]]; then
    echo "ERROR: --version is required"
    exit 1
fi
if [[ -z "$OUTPUT_DIR" ]]; then
    echo "ERROR: --output-dir is required"
    exit 1
fi

VERSION="${VERSION#v}"
OUTPUT_DIR="$(mkdir -p "$OUTPUT_DIR" && cd "$OUTPUT_DIR" && pwd)"

# Resolve manifest path to absolute BEFORE any cd (avoids relative-path loss).
# Manifest is REQUIRED: no fallback to hardcoded package lists.
if [[ -z "$MANIFEST_PATH" ]]; then
    echo "ERROR: --manifest is required (path to package-manifest.txt from appdir.sh)"
    exit 1
fi
if [[ ! -f "$MANIFEST_PATH" ]]; then
    echo "ERROR: Manifest file not found: $MANIFEST_PATH"
    exit 1
fi
MANIFEST_PATH="$(cd "$(dirname "$MANIFEST_PATH")" && pwd)/$(basename "$MANIFEST_PATH")"

ARCHIVE_NAME="mqttprobe-third-party-sources-v${VERSION}"
WORK_DIR=$(mktemp -d)
COLLECT_DIR="$WORK_DIR/$ARCHIVE_NAME"

cleanup() { rm -rf "$WORK_DIR"; }
trap cleanup EXIT

echo "=== Collecting third-party sources for v${VERSION} ==="
mkdir -p "$COLLECT_DIR"

# ── 1. Photino.Native source ────────────────────────────────────────────────

echo "Cloning Photino.Native at pinned commit..."
PHOTINO_COMMIT="3ba4b937d6337b5c58344445db08a5a9a63f07c8"
PHOTINO_DIR="$COLLECT_DIR/photino-native"
git clone --depth=1 --branch master \
    https://github.com/tryphotino/photino.Native.git "$PHOTINO_DIR" 2>&1
cd "$PHOTINO_DIR"
git fetch --depth=1 origin "$PHOTINO_COMMIT" 2>&1
git checkout FETCH_HEAD 2>&1
echo "Checked out: $(git rev-parse HEAD)"
rm -rf "$PHOTINO_DIR/.git"
cd "$WORK_DIR"

# ── 2. Enable deb-src repositories ──────────────────────────────────────────
#
# Only a script-owned deb822 file is written.  Third-party .list/.sources
# files are never edited: rewriting shared apt configuration would mutate
# the build host's state for unrelated suites and can break apt itself.
# APT_SOURCES_DIR exists so tests can redirect this to a temp directory.

echo ""
echo "=== Configuring deb-src repositories ==="
if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
else
    SUDO="sudo"
fi

# MQTTProbe ships an Ubuntu 22.04 AppImage, so the source baseline is jammy.
# Do not probe the running host: a non-jammy build host must not silently
# change which source versions get archived.
CODENAME="jammy"
APT_SOURCES_DIR="${APT_SOURCES_DIR:-/etc/apt/sources.list.d}"
APT_KEYRING="${APT_KEYRING:-/usr/share/keyrings/ubuntu-archive-keyring.gpg}"

DEB822_FILE="$APT_SOURCES_DIR/mqttprobe-${CODENAME}-deb-src.sources"
mkdir -p "$APT_SOURCES_DIR" 2>/dev/null || $SUDO mkdir -p "$APT_SOURCES_DIR"

# Overwrite unconditionally so the file always holds known-good contents
# rather than whatever a previous run (or filename collision) left behind.
echo "Writing deterministic deb-src configuration for $CODENAME..."
$SUDO tee "$DEB822_FILE" > /dev/null << DEBSRC_EOF
Types: deb-src
URIs: https://archive.ubuntu.com/ubuntu/
Suites: ${CODENAME} ${CODENAME}-updates
Components: main restricted universe multiverse
Signed-By: ${APT_KEYRING}

Types: deb-src
URIs: https://security.ubuntu.com/ubuntu/
Suites: ${CODENAME}-security
Components: main restricted universe multiverse
Signed-By: ${APT_KEYRING}
DEBSRC_EOF

echo "Running apt-get update..."
$SUDO apt-get update -qq 2>&1 || {
    echo "FATAL: apt-get update failed, source packages unavailable"
    exit 1
}

# ── 3. Parse bundled packages from manifest (REQUIRED, no fallback) ──────────

echo ""
echo "=== Parsing bundled packages from manifest ==="
echo "Using build manifest: $MANIFEST_PATH"
BINARY_PACKAGES=()
IN_SYSTEM_SECTION=0
while IFS= read -r line; do
    if [[ "$line" == "## System Packages" ]]; then
        IN_SYSTEM_SECTION=1
        continue
    fi
    if [[ "$line" == "## "* ]] && [[ "$IN_SYSTEM_SECTION" -eq 1 ]]; then
        break
    fi
    if [[ "$IN_SYSTEM_SECTION" -eq 1 ]] && [[ -n "$line" ]] && [[ "$line" != "#"* ]] && [[ "$line" != "| "* ]]; then
        pkg_name=$(echo "$line" | cut -d'|' -f1 | xargs)
        if [[ -n "$pkg_name" ]]; then
            BINARY_PACKAGES+=("$pkg_name")
        fi
    fi
done < "$MANIFEST_PATH"

if [[ "${#BINARY_PACKAGES[@]}" -eq 0 ]]; then
    echo "FATAL: No packages found in manifest. Cannot collect sources."
    exit 1
fi
echo "Found ${#BINARY_PACKAGES[@]} packages from manifest"

# ── 4. Parse source versions from manifest (authoritative) ───────────────────

echo ""
echo "=== Parsing source versions from manifest ==="
VERSIONS_DIR="$COLLECT_DIR"
VERSIONS_FILE="$VERSIONS_DIR/package-versions.txt"

echo "# Package version manifest for MQTTProbe AppImage v${VERSION}" > "$VERSIONS_FILE"
echo "# Format: binary-package version source-package source-version" >> "$VERSIONS_FILE"
echo "# Generated: $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$VERSIONS_FILE"
echo "" >> "$VERSIONS_FILE"

declare -A SOURCE_PACKAGES
RECORDED=0
MISSING_VERSIONS=0

# Parse manifest RECORD lines: "binary-package | binary-version | source-package | source-version | file-count"
IN_SYSTEM_SECTION=0
while IFS= read -r line; do
    if [[ "$line" == "## System Packages" ]]; then
        IN_SYSTEM_SECTION=1
        continue
    fi
    if [[ "$line" == "## "* ]] && [[ "$IN_SYSTEM_SECTION" -eq 1 ]]; then
        break
    fi
    if [[ "$IN_SYSTEM_SECTION" -eq 1 ]] && [[ -n "$line" ]] && [[ "$line" != "#"* ]] && [[ "$line" != "| "* ]]; then
        bin_pkg=$(echo "$line" | cut -d'|' -f1 | xargs)
        bin_ver=$(echo "$line" | cut -d'|' -f2 | xargs)
        src_pkg=$(echo "$line" | cut -d'|' -f3 | xargs)
        src_ver=$(echo "$line" | cut -d'|' -f4 | xargs)
        if [[ -z "$bin_pkg" ]]; then
            continue
        fi
        if [[ -z "$src_ver" ]] || [[ "$src_ver" == "unknown" ]] || [[ "$src_ver" == "-" ]]; then
            echo "ERROR: Missing source version for $bin_pkg (got '$src_ver')"
            MISSING_VERSIONS=$((MISSING_VERSIONS + 1))
            continue
        fi
        echo "$bin_pkg $bin_ver $src_pkg $src_ver" >> "$VERSIONS_FILE"
        # Track unique source packages; reject conflicting versions
        if [[ -n "${SOURCE_PACKAGES[$src_pkg]+x}" ]] && [[ "${SOURCE_PACKAGES[$src_pkg]}" != "$src_ver" ]]; then
            echo "ERROR: Conflicting source versions for $src_pkg: ${SOURCE_PACKAGES[$src_pkg]} vs $src_ver"
            MISSING_VERSIONS=$((MISSING_VERSIONS + 1))
        else
            SOURCE_PACKAGES["$src_pkg"]="$src_ver"
        fi
        RECORDED=$((RECORDED + 1))
    fi
done < "$MANIFEST_PATH"

if [[ "$MISSING_VERSIONS" -gt 0 ]]; then
    echo "FATAL: $MISSING_VERSIONS missing or conflicting source versions in manifest"
    exit 1
fi

if [[ "$RECORDED" -eq 0 ]]; then
    echo "FATAL: No package records parsed from manifest"
    exit 1
fi

echo "Recorded $RECORDED binary packages, ${#SOURCE_PACKAGES[@]} unique source packages from manifest"

# ── 5. Download actual source archives (exact version, failures fatal) ──────

echo ""
echo "=== Downloading source archives ==="
SOURCES_DIR="$COLLECT_DIR/ubuntu-sources"
mkdir -p "$SOURCES_DIR"

FETCH_SCRIPT="$SCRIPT_DIR/fetch_source.py"
DOWNLOADED=0
FAILED=0
FAIL_LIST=""

for src_name in "${!SOURCE_PACKAGES[@]}"; do
    src_ver="${SOURCE_PACKAGES[$src_name]}"
    echo "  Downloading: $src_name=$src_ver"
    pkg_dir="$SOURCES_DIR/$src_name"
    mkdir -p "$pkg_dir"

    # Try Python fetcher (apt-get first, then Launchpad fallback)
    if python3 "$FETCH_SCRIPT" "$src_name" "$src_ver" --dest "$pkg_dir" 2>&1; then
        DOWNLOADED=$((DOWNLOADED + 1))
    else
        echo "  ERROR: Failed to download source for $src_name=$src_ver"
        FAILED=$((FAILED + 1))
        FAIL_LIST="$FAIL_LIST $src_name=$src_ver"
    fi
done

TOTAL_SIZE="unknown"
if command -v du &>/dev/null; then
    TOTAL_SIZE=$(du -sh "$SOURCES_DIR" 2>/dev/null | cut -f1 || echo "unknown")
fi
echo "Downloaded $DOWNLOADED source packages ($FAILED failed), total size: $TOTAL_SIZE"

if [ "$FAILED" -gt 0 ]; then
    echo "FATAL: $FAILED source package(s) failed to download:$FAIL_LIST"
    echo "Cannot claim source compliance with missing archives."
    exit 1
fi

if [ "$DOWNLOADED" -eq 0 ]; then
    echo "FATAL: No source packages were downloaded. Cannot claim compliance."
    exit 1
fi

# ── 6. Copy actual copyright/license texts ───────────────────────────────────

echo ""
echo "=== Copying copyright/license texts ==="
COPYRIGHT_DIR="$COLLECT_DIR/copyright"
mkdir -p "$COPYRIGHT_DIR"

COPIED=0
MISSING_COPYRIGHT=0
for pkg in "${BINARY_PACKAGES[@]}"; do
    # Strip architecture suffix for /usr/share/doc/ lookup
    # (e.g., "libfoo:amd64" -> "libfoo") but keep original name for filename
    doc_pkg="${pkg%%:*}"
    COPYRIGHT_FILE="/usr/share/doc/$doc_pkg/copyright"
    if [ -f "$COPYRIGHT_FILE" ]; then
        cp "$COPYRIGHT_FILE" "$COPYRIGHT_DIR/${pkg}.copyright"
        COPIED=$((COPIED + 1))
    else
        echo "  ERROR: No copyright file for $pkg (looked in /usr/share/doc/$doc_pkg/copyright)"
        if [ -d "/usr/share/doc/$doc_pkg" ]; then
            echo "    Reason: directory exists but has no copyright file"
        elif dpkg -s "$doc_pkg" >/dev/null 2>&1; then
            echo "    Reason: package installed but /usr/share/doc/$doc_pkg missing (usr-merge or stripped)"
        else
            echo "    Reason: package not installed (may be virtual or provided by another package)"
        fi
        MISSING_COPYRIGHT=$((MISSING_COPYRIGHT + 1))
    fi
done

if [ "$MISSING_COPYRIGHT" -gt 0 ]; then
    echo "FATAL: $MISSING_COPYRIGHT required copyright file(s) missing"
    exit 1
fi
echo "Copied $COPIED copyright files"

# ── 7. Copy common license texts ────────────────────────────────────────────

echo ""
echo "=== Copying common license texts ==="
COMMON_LICENSES_DIR="$COLLECT_DIR/common-licenses"
mkdir -p "$COMMON_LICENSES_DIR"

COMMON_LICENSE_NAMES=(
    Apache-2.0
    LGPL-2.1
    GPL-2
    MIT
    BSD-3-Clause
    MPL-2.0
    Artistic
)

COMMON_COPIED=0
for lic in "${COMMON_LICENSE_NAMES[@]}"; do
    LIC_FILE="/usr/share/common-licenses/$lic"
    if [ -f "$LIC_FILE" ]; then
        cp "$LIC_FILE" "$COMMON_LICENSES_DIR/$lic"
        COMMON_COPIED=$((COMMON_COPIED + 1))
    fi
done
echo "Copied $COMMON_COPIED common license texts"

# ── 8. Build recipes (preserving repository layout) ─────────────────────────
#
# Scripts use SCRIPT_DIR/../../.. to resolve REPO_ROOT, so the archive
# must preserve the scripts/packaging/linux/ tree. Extracting into a
# flat vendor-scripts/build-scripts directory breaks that resolution.

echo ""
echo "Copying build recipes (preserving repository layout)..."
RECIPES_DIR="$COLLECT_DIR/build-recipes"
RECIPES_LINUX="$RECIPES_DIR/scripts/packaging/linux"
RECIPES_WORKFLOW="$RECIPES_DIR/.github/workflows"
mkdir -p "$RECIPES_LINUX/vendor" "$RECIPES_WORKFLOW"

cp "$SCRIPT_DIR/appdir.sh" "$RECIPES_LINUX/"
cp "$SCRIPT_DIR/rebuild-photino.sh" "$RECIPES_LINUX/"
cp "$SCRIPT_DIR/collect-sources.sh" "$RECIPES_LINUX/"
cp "$SCRIPT_DIR/fetch_source.py" "$RECIPES_LINUX/"
cp "$SCRIPT_DIR/manifest.py" "$RECIPES_LINUX/"
cp "$SCRIPT_DIR/notices.py" "$RECIPES_LINUX/"
cp "$SCRIPT_DIR/verify.py" "$RECIPES_LINUX/"
cp "$SCRIPT_DIR/_elf.py" "$RECIPES_LINUX/"
cp "$SCRIPT_DIR/vendor/linuxdeploy-plugin-gtk.sh" "$RECIPES_LINUX/vendor/"
cp "$SCRIPT_DIR/vendor/linuxdeploy-plugin-gstreamer.sh" "$RECIPES_LINUX/vendor/"
cp "$SCRIPT_DIR/vendor/NOTICE.md" "$RECIPES_LINUX/vendor/"

echo "Copying CI workflow..."
cp "$REPO_ROOT/.github/workflows/build-linux-desktop.yml" "$RECIPES_WORKFLOW/"

# ── 9. Copy manifest and NOTICE from build output if available ──────────────

if [[ -n "$MANIFEST_PATH" ]] && [[ -f "$MANIFEST_PATH" ]]; then
    echo "Copying build manifest..."
    cp "$MANIFEST_PATH" "$COLLECT_DIR/package-manifest.txt"
fi

# ── 10. WebKit modification notice ─────────────────────────────────────────

echo "Creating WebKit modification notice..."
cat > "$COLLECT_DIR/webkit-modification-notice.md" << 'EOF'
# WebKit Binary Modification Notice

The bundled libwebkit2gtk-4.1.so.0 and related WebKit libraries have been
modified with a narrow binary patch replacing the absolute prefix
/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1 (40 bytes) with
././/lib/x86_64-linux-gnu/webkit2gtk-4.1 (40 bytes) in the ELF string
table. File size and ELF header integrity are validated post-patch.

This is the minimum modification required for the AppImage runtime to
locate bundled WebKit helpers and the injected bundle. The replacement
prefix is functionally identical on Linux (the double slash is collapsed).

## Corresponding Source

The corresponding source for the modified WebKit libraries is available in
the ubuntu-sources/ directory of this archive as the webkit2gtk source
package, or from https://packages.ubuntu.com/jammy/

## Date of Modification

This modification is applied during the AppImage build process.
The exact binary that is modified is the one installed by the
libwebkit2gtk-4.1-0 Ubuntu 22.04 package at the version recorded
in package-versions.txt.
EOF

# ── 11. JACK audio plugin exclusion notice ───────────────────────────────────

echo "Creating JACK exclusion notice..."
cat > "$COLLECT_DIR/jack-exclusion-notice.md" << 'EOF'
# GStreamer JACK Plugin Exclusion Notice

The GStreamer JACK audio plugin (libgstjack.so) is deliberately excluded
from this AppImage. The bundled plugin links libjack.so.0 (JACK Audio
Connection Kit client library) which is not a standard desktop dependency
and is not installed on most target systems.

MQTTProbe does not use JACK audio. The plugin is pulled in transitively
by the gstreamer1.0-plugins-good package as part of its full plugin set.

Excluding it avoids a runtime dependency on libjack.so.0 that would
cause the AppImage to fail on clean desktop installs. All other
GStreamer plugins (PulseAudio, ALSA, V4L2, etc.) are bundled normally.

If JACK support is needed in the future, libjack.so.0 must be explicitly
bundled via the existing linuxdeploy deploy-deps-only mechanism, and the
dpkg source package (jackd2) must be added to the source archive.
EOF

# ── 12. Create archive ─────────────────────────────────────────────────────

echo ""
echo "=== Creating archive ==="
cd "$WORK_DIR"
tar -czf "$OUTPUT_DIR/${ARCHIVE_NAME}.tar.gz" "$ARCHIVE_NAME"

ARCHIVE_SIZE=$(du -sh "$OUTPUT_DIR/${ARCHIVE_NAME}.tar.gz" 2>/dev/null | cut -f1 || echo "unknown")

echo ""
echo "=== Third-party source archive created ==="
echo "Archive: $OUTPUT_DIR/${ARCHIVE_NAME}.tar.gz"
echo "Size: $ARCHIVE_SIZE"
echo "Contents:"
tar -tzf "$OUTPUT_DIR/${ARCHIVE_NAME}.tar.gz" > "$WORK_DIR/filelist.txt" 2>&1
head -40 "$WORK_DIR/filelist.txt" || true
TOTAL_ENTRIES=$(wc -l < "$WORK_DIR/filelist.txt")
echo "..."
echo "Total entries: $TOTAL_ENTRIES"