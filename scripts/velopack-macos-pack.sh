#!/usr/bin/env bash
#
# Local Mac-only script that reproduces the CI MacCatalyst publish + Velopack
# pack path for diagnosis.  Run this ON the Mac.
#
# Usage:
#   ./scripts/velopack-macos-pack.sh
#   ./scripts/velopack-macos-pack.sh --version 0.0.1
#   ./scripts/velopack-macos-pack.sh --sign
#   ./scripts/velopack-macos-pack.sh --sign --notarize
#   ./scripts/velopack-macos-pack.sh --skip-publish   # pack existing Release .app only
#   ./scripts/velopack-macos-pack.sh --no-inst        # vpk --noInst (skip .pkg; useful if productbuild hangs)

set -euo pipefail

VERSION=0.0.1
SIGN=0
NOTARIZE=0
SKIP_PUBLISH=0
NO_INST=0

PROJECT="src/MqttProbe.Maui/MqttProbe.Maui.csproj"
TFM="net10.0-maccatalyst"
OUTPUT_DIR="publish/velopack-osx-local"

SIGN_APP_IDENTITY="${SIGN_APP_IDENTITY:-Developer ID Application: Bluegrass IOT LLC (XVKMB6CQ72)}"
SIGN_INSTALL_IDENTITY="${SIGN_INSTALL_IDENTITY:-Developer ID Installer: Bluegrass IOT LLC (XVKMB6CQ72)}"
NOTARY_PROFILE="${NOTARY_PROFILE:-velopack-profile}"

die() { printf '\033[31merror:\033[0m %s\n' "$1" >&2; exit 1; }
info() { printf '\033[36m==>\033[0m %s\n' "$1"; }

usage() {
    awk 'NR<3 {next} /^#/ {sub(/^# ?/, ""); print; next} {exit}' "${BASH_SOURCE[0]}"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)      VERSION="${2:-}"; shift 2 ;;
        --sign)         SIGN=1; shift ;;
        --notarize)     NOTARIZE=1; shift ;;
        --skip-publish) SKIP_PUBLISH=1; shift ;;
        --no-inst)      NO_INST=1; shift ;;
        -h|--help)      usage; exit 0 ;;
        *)              usage; die "unknown option: $1" ;;
    esac
done

# --- version validation -------------------------------------------------------

_raw="${VERSION#v}"
if [[ ! "$_raw" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-].*)?$ ]] || [[ "$_raw" == "0.0.0" ]]; then
    die "version must be >= 0.0.1 (got '$VERSION')"
fi
IFS='.-' read -r _major _minor _patch _ <<< "$_raw"
if ! [[ "$_major" =~ ^[0-9]+$ && "$_minor" =~ ^[0-9]+$ && "$_patch" =~ ^[0-9]+$ ]]; then
    die "version must start with numeric major.minor.patch (got '$VERSION')"
fi
CODE=$(( _major * 10000 + _minor * 100 + _patch ))

# --- preflight ----------------------------------------------------------------

[[ "$(uname -s)" == "Darwin" ]] || die "this script must run on macOS"

command -v dotnet >/dev/null 2>&1 || die "dotnet not found on PATH — install the .NET 10 SDK"
command -v vpk >/dev/null 2>&1 || die "vpk not found on PATH. Install it with:
    dotnet tool install -g vpk --version 1.2.0"

_xcode_ver="$(xcodebuild -version 2>/dev/null | head -1 | awk '{print $2}' || true)"
if [[ -n "$_xcode_ver" && "$_xcode_ver" != "26.6" ]]; then
    info "xcodebuild version is $_xcode_ver (CI pins 26.6); mismatched Xcode may cause build failures"
fi

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# --- notarize requires sign ---------------------------------------------------

if [[ "$NOTARIZE" -eq 1 && "$SIGN" -eq 0 ]]; then
    die "--notarize requires --sign"
fi

# --- publish ------------------------------------------------------------------

if [[ "$SKIP_PUBLISH" -eq 0 ]]; then
    info "publishing $TFM (version=$VERSION, code=$CODE)"
    dotnet publish "$PROJECT" \
        -f:"$TFM" \
        -p:MqttProbeMauiMacTargetFrameworksOverride="$TFM" \
        -c:Release \
        -p:RuntimeIdentifiers='"maccatalyst-x64;maccatalyst-arm64"' \
        -p:CreatePackage=false \
        -p:ApplicationDisplayVersion="$VERSION" \
        -p:ApplicationVersion="$CODE" \
        || die "publish failed"
else
    info "skipping publish (--skip-publish)"
fi

# --- resolve app bundle -------------------------------------------------------

APP="$(find "src/MqttProbe.Maui/bin/Release/$TFM" -maxdepth 1 -name '*.app' -print -quit)"
[[ -n "$APP" ]] || die "no .app bundle found under src/MqttProbe.Maui/bin/Release/$TFM"
info "app bundle: $APP"

EXE_NAME="$(defaults read "$(pwd)/$APP/Contents/Info.plist" CFBundleExecutable)"
[[ -x "$APP/Contents/MacOS/$EXE_NAME" ]] || die "main executable not found: $APP/Contents/MacOS/$EXE_NAME"
info "executable: $EXE_NAME"

info "architectures: $(lipo -archs "$APP/Contents/MacOS/$EXE_NAME")"

# --- signing identities -------------------------------------------------------

V_SIGN_APP=""
V_SIGN_INSTALL=""

if [[ "$SIGN" -eq 1 ]]; then
    # Verify app identity
    if security find-identity -v -p codesigning 2>/dev/null | grep -qF "$SIGN_APP_IDENTITY"; then
        V_SIGN_APP="$SIGN_APP_IDENTITY"
        info "app identity found: $V_SIGN_APP"
    else
        die "app signing identity not found in keychain:
    $SIGN_APP_IDENTITY
    Check with: security find-identity -v -p codesigning"
    fi

    # Verify installer identity (try both with and without -p codesigning)
    if [[ "$NO_INST" -eq 0 ]]; then
        if security find-identity -v -p codesigning 2>/dev/null | grep -qF "$SIGN_INSTALL_IDENTITY"; then
            V_SIGN_INSTALL="$SIGN_INSTALL_IDENTITY"
        elif security find-identity -v 2>/dev/null | grep -qF "$SIGN_INSTALL_IDENTITY"; then
            V_SIGN_INSTALL="$SIGN_INSTALL_IDENTITY"
        else
            die "installer signing identity not found in keychain:
    $SIGN_INSTALL_IDENTITY
    Check with: security find-identity -v"
        fi
        info "installer identity found: $V_SIGN_INSTALL"
    fi
fi

# --- pack ---------------------------------------------------------------------

mkdir -p "$OUTPUT_DIR"

V_ARGS=(
    --packId MQTTProbe
    --packVersion "$VERSION"
    --packDir "$APP"
    --mainExe "$EXE_NAME"
    --packTitle MQTTProbe
    --packAuthors "Bluegrass IoT"
    --outputDir "$OUTPUT_DIR"
)

if [[ "$NO_INST" -eq 1 ]]; then
    V_ARGS+=(--noInst)
fi

if [[ "$SIGN" -eq 1 ]]; then
    V_ARGS+=(--signAppIdentity "$V_SIGN_APP")
    if [[ "$NO_INST" -eq 0 && -n "$V_SIGN_INSTALL" ]]; then
        V_ARGS+=(--signInstallIdentity "$V_SIGN_INSTALL")
    fi
fi

if [[ "$NOTARIZE" -eq 1 ]]; then
    V_ARGS+=(--notaryProfile "$NOTARY_PROFILE")
fi

info "packing with vpk"
vpk pack "${V_ARGS[@]}" || die "vpk pack failed"

# --- summary ------------------------------------------------------------------

ls -la "$OUTPUT_DIR"

cat <<EOF

  If vpk hangs after "Creating installer '.pkg'", productbuild may be stuck
  waiting for a GUI keychain prompt.  Try:
      ./scripts/velopack-macos-pack.sh --skip-publish --no-inst
  or watch the process tree with:
      ps aux | grep -E 'productbuild|productsign|pkgbuild'

EOF