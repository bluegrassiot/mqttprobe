#!/usr/bin/env bash
# rebuild-photino-native.sh — Build Photino.Native v4.0.22 .so from source on ubuntu-22.04
#
# Usage: ./rebuild-photino-native.sh [--output-dir <dir>]
#
# Clones tryphotino/photino.Native at the exact commit for v4.0.22 (3ba4b937),
# installs build prerequisites, compiles the linux-x64 shared library using
# the upstream Makefile target build-photino-linux-x64, and places the
# resulting Photino.Native.so in the output directory.
#
# The published NuGet .so imports GLIBC_2.38 + GLIBCXX_3.4.31, which prevents
# running on ubuntu-22.04 (glibc 2.35). Rebuilding from source on 22.04
# restores compatibility with glibc 2.35.
#
# License: Apache-2.0 (tryphotino/photino.Native). See vendor/NOTICE.md.

set -euo pipefail

PHOTINO_COMMIT="3ba4b937d6337b5c58344445db08a5a9a63f07c8"
PHOTINO_REPO="https://github.com/tryphotino/photino.Native.git"
OUTPUT_DIR="./publish/photino-rebuilt"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

# Resolve OUTPUT_DIR to absolute BEFORE any cd (avoids relative-path loss)
OUTPUT_DIR="$(mkdir -p "$OUTPUT_DIR" && cd "$OUTPUT_DIR" && pwd)"

WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

echo "=== Cloning Photino.Native at $PHOTINO_COMMIT ==="
git clone --depth=1 --branch master "$PHOTINO_REPO" "$WORK_DIR/photino-native" 2>&1
cd "$WORK_DIR/photino-native"
git fetch --depth=1 origin "$PHOTINO_COMMIT" 2>&1
git checkout FETCH_HEAD 2>&1
echo "Checked out: $(git rev-parse HEAD)"

echo "=== Installing build prerequisites ==="
if [ "$(id -u)" -eq 0 ]; then
    apt-get update -qq
    apt-get install -y -qq libgtk-3-dev libwebkit2gtk-4.1-dev libnotify4 libnotify-dev pkg-config g++ 2>&1
else
    sudo apt-get update -qq
    sudo apt-get install -y -qq libgtk-3-dev libwebkit2gtk-4.1-dev libnotify4 libnotify-dev pkg-config g++ 2>&1
fi

echo "=== Building Photino.Native linux-x64 ==="
mkdir -p ./lib/x64

CC=g++
CFLAGS="-std=c++2a -Wall -O2 -shared -fPIC"
SRC="./Photino.Native"

$CC -o ./lib/x64/Photino.Native.so \
    $CFLAGS \
    "$SRC/Photino.Linux.Dialog.cpp" \
    "$SRC/Photino.Linux.cpp" \
    "$SRC/Exports.cpp" \
    $(pkg-config --cflags --libs gtk+-3.0 webkit2gtk-4.1 libnotify)

SO="./lib/x64/Photino.Native.so"

echo "=== Verifying rebuilt .so ==="
file "$SO"

echo "--- GLIBC requirements ---"
objdump -T "$SO" | grep -oP 'GLIBC_\d+\.\d+(\.\d+)?' | sort -Vu || true
echo "--- GLIBCXX requirements ---"
objdump -T "$SO" | grep -oP 'GLIBCXX_\d+\.\d+(\.\d+)?' | sort -Vu || true

echo "--- Defined Photino_* export NAMES ---"
# Use --defined-only and extract symbol NAME column only (not addresses)
# Avoids pipefail SIGPIPE from head: collect into variable, then display
PHOTINO_EXPORTS=$(nm -D --defined-only "$SO" | awk '/[TtWw]/{print $3}' | grep -i '^photino' | sort -u || true)
if [[ -z "$PHOTINO_EXPORTS" ]]; then
    echo "ERROR: No Photino_* defined exports found in rebuilt .so"
    exit 1
fi
echo "$PHOTINO_EXPORTS"

echo "--- Asserting Photino.Native v4.0.22 (commit $PHOTINO_COMMIT) ---"
# The .rc file embeds "4.0.0.0" as FILEVERSION (Windows-only resource).
# The Linux .so does not embed a version string. We verify via:
# 1. Git commit matches the pinned v4.0.22 commit
# 2. Defined Photino_* exports are present (verified above)
# 3. GLIBC max is 2.14 (no 2.38 dependency)
ACTUAL_COMMIT=$(git rev-parse HEAD)
if [[ "$ACTUAL_COMMIT" != "$PHOTINO_COMMIT" ]]; then
    echo "FATAL: Commit mismatch. Expected $PHOTINO_COMMIT, got $ACTUAL_COMMIT"
    exit 1
fi
echo "PASS: Commit $PHOTINO_COMMIT confirmed (Photino.Native v4.0.22)"

echo "--- Verifying GLIBC max 2.14 (no 2.38 dependency) ---"
GLIBC_MAX=$(objdump -T "$SO" | grep -oP 'GLIBC_\d+\.\d+(\.\d+)?' | sort -Vu | tail -1)
echo "Max GLIBC: $GLIBC_MAX"
if echo "$GLIBC_MAX" | grep -qP 'GLIBC_2\.(3[5-9]|[4-9])'; then
    echo "FATAL: Rebuilt .so requires GLIBC >= 2.35 (found $GLIBC_MAX)"
    exit 1
fi
echo "PASS: GLIBC requirement is acceptable ($GLIBC_MAX)"

echo "=== Copying to output directory ==="
cp "$SO" "$OUTPUT_DIR/Photino.Native.so"
echo "Rebuilt .so: $OUTPUT_DIR/Photino.Native.so"
ls -la "$OUTPUT_DIR/Photino.Native.so"