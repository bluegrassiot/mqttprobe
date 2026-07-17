#!/usr/bin/env bash
#
# Build and launch the Mac Catalyst app locally for client-certificate testing.
# Run this ON the Mac (it cannot cross-compile from Windows).
#
#
# Usage:
#   ./scripts/build-macos-catalyst.sh
#   ./scripts/build-macos-catalyst.sh --sign              # real identity; needed for Keychain
#   ./scripts/build-macos-catalyst.sh --reset             # clear stored certs first
#   ./scripts/build-macos-catalyst.sh -c Release --clean
#   ./scripts/build-macos-catalyst.sh --no-launch
#
# --sign is required for anything touching SecureStorage/Keychain: an ad-hoc signature
# carries no team, so the sandbox denies Keychain access with errSecMissingEntitlement.
# Both signing inputs are discovered from the local keychain and profile directory. Set
# these only to disambiguate when more than one candidate is installed:
#   CODESIGN_KEY        codesigning identity, as a SHA-1 hash
#   CODESIGN_PROVISION  provisioning profile name

set -euo pipefail

CONFIGURATION=Debug
LAUNCH=1
RESET=0
CLEAN=0
SIGN=0

PROJECT="src/MqttProbe.Maui/MqttProbe.Maui.csproj"
TFM="net10.0-maccatalyst"

# Empty means "discover it"; nothing machine-specific is baked into this script.
CODESIGN_KEY="${CODESIGN_KEY:-}"
CODESIGN_PROVISION="${CODESIGN_PROVISION:-}"

die() { printf '\033[31merror:\033[0m %s\n' "$1" >&2; exit 1; }
info() { printf '\033[36m==>\033[0m %s\n' "$1"; }

# Print the header comment block (from line 3 to the first non-comment line) as help,
# so the two cannot drift apart.
usage() {
    awk 'NR<3 {next} /^#/ {sub(/^# ?/, ""); print; next} {exit}' "${BASH_SOURCE[0]}"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration) CONFIGURATION="${2:-}"; shift 2 ;;
        --no-launch)        LAUNCH=0; shift ;;
        --reset)            RESET=1; shift ;;
        --clean)            CLEAN=1; shift ;;
        --sign)             SIGN=1; shift ;;
        -h|--help)          usage; exit 0 ;;
        *)                  usage; die "unknown option: $1" ;;
    esac
done

[[ "$CONFIGURATION" == "Debug" || "$CONFIGURATION" == "Release" ]] \
    || die "configuration must be Debug or Release (got '$CONFIGURATION')"

# --- preflight ---------------------------------------------------------------

[[ "$(uname -s)" == "Darwin" ]] || die "this script must run on macOS"

command -v dotnet >/dev/null 2>&1 || die "dotnet not found on PATH — install the .NET 10 SDK"

# The Apple workloads pin one exact Xcode version per band; a mismatch surfaces here
# as a clearer message than the build's "requires Xcode X.Y" failure.
command -v xcodebuild >/dev/null 2>&1 || die "Xcode command line tools not found"

if ! dotnet workload list 2>/dev/null | grep -qE 'maui-maccatalyst|maui\b'; then
    die "Mac Catalyst workload missing. Install it with:
    dotnet workload install maui-maccatalyst"
fi

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Read the bundle id from the project rather than hardcoding it: it has been renamed once
# already, and a stale copy here would silently point the container check at nothing.
BUNDLE_ID="$(sed -n 's:.*<ApplicationId>\(.*\)</ApplicationId>.*:\1:p' "$PROJECT" | head -1)"
[[ -n "$BUNDLE_ID" ]] || die "could not read <ApplicationId> from $PROJECT"

# --- signing -----------------------------------------------------------------

SIGN_ARGS=()
if [[ "$SIGN" -eq 1 ]]; then
    if [[ -z "$CODESIGN_KEY" ]]; then
        # Two Developer ID certs on one machine make the name ambiguous, which is why the
        # release doc records a SHA-1. Resolve to a hash and refuse to guess between them.
        # (while-read rather than mapfile: macOS still ships bash 3.2.)
        _ids=()
        while IFS= read -r _line; do
            [[ -n "$_line" ]] && _ids+=("$_line")
        done < <(security find-identity -v -p codesigning 2>/dev/null \
            | grep 'Developer ID Application' | awk '{print $2}')
        case "${#_ids[@]}" in
            0) die "no 'Developer ID Application' identity in the keychain.
    security find-identity -v -p codesigning
    Then set CODESIGN_KEY=<sha1> and re-run." ;;
            1) CODESIGN_KEY="${_ids[0]}" ;;
            *) die "${#_ids[@]} 'Developer ID Application' identities found; the name is ambiguous.
    Pick one and set it explicitly:
    security find-identity -v -p codesigning
    CODESIGN_KEY=<sha1> $0 --sign" ;;
        esac
    fi
    info "signing identity: $CODESIGN_KEY"

    # Only .provisionprofile is considered: .mobileprovision files in the same directory
    # are iOS profiles and can never sign a Mac Catalyst app.
    PROFILE_DIR="$HOME/Library/MobileDevice/Provisioning Profiles"
    PROFILE_FILE=""
    _found_names=()
    if [[ -d "$PROFILE_DIR" ]]; then
        while IFS= read -r -d '' p; do
            name="$(security cms -D -i "$p" 2>/dev/null | plutil -extract Name raw - 2>/dev/null || true)"
            [[ -n "$name" ]] || continue
            if [[ -n "$CODESIGN_PROVISION" ]]; then
                if [[ "$name" == "$CODESIGN_PROVISION" ]]; then PROFILE_FILE="$p"; break; fi
            else
                _found_names+=("$name")
                PROFILE_FILE="$p"
            fi
        done < <(find "$PROFILE_DIR" -name '*.provisionprofile' -print0 2>/dev/null)
    fi

    if [[ -n "$CODESIGN_PROVISION" ]]; then
        [[ -n "$PROFILE_FILE" ]] || die "no installed profile named '$CODESIGN_PROVISION' in:
    $PROFILE_DIR"
    else
        # Collapse to distinct names. The same profile is often installed more than once
        # under different filenames, and CodesignProvision takes a NAME -- so duplicates of
        # one name are not ambiguous as input. Only distinct names need a human to choose.
        # (MSBuild re-resolves the name itself and errors if the profile lacks the signing
        # cert, so a stale same-named duplicate fails loudly rather than mis-signing.)
        _uniq_names=()
        for _n in "${_found_names[@]+"${_found_names[@]}"}"; do
            _dup=0
            for _u in "${_uniq_names[@]+"${_uniq_names[@]}"}"; do
                [[ "$_u" == "$_n" ]] && { _dup=1; break; }
            done
            [[ "$_dup" -eq 0 ]] && _uniq_names+=("$_n")
        done

        case "${#_uniq_names[@]}" in
            0) die "no Mac Catalyst provisioning profile installed in:
    $PROFILE_DIR
    Mac Catalyst profiles are .provisionprofile (.mobileprovision is iOS).
    Install one, then re-run." ;;
            1) CODESIGN_PROVISION="${_uniq_names[0]}" ;;
            *) die "${#_uniq_names[@]} differently-named Mac Catalyst profiles installed; pick one:
$(printf '      %s\n' "${_uniq_names[@]}")
    CODESIGN_PROVISION='<name>' $0 --sign" ;;
        esac

        if [[ "${#_found_names[@]}" -gt "${#_uniq_names[@]}" ]]; then
            info "note: ${#_found_names[@]} profile files resolve to ${#_uniq_names[@]} name(s);"
            info "      duplicates of one name are harmless, but a stale copy is worth pruning."
        fi
    fi
    info "provisioning profile: $CODESIGN_PROVISION"

    # The profile grants com.apple.application-identifier, which is where a sandboxed app's
    # Keychain access group comes from. If its App ID does not match the bundle id, Keychain
    # writes fail at runtime with errSecMissingEntitlement -- and nothing at build time says so.
    # PlistBuddy, not plutil: the key contains dots, and PlistBuddy separates on ':' so it
    # needs no escaping. It must read a real file though -- it cannot read a pipe, and a
    # here-string is only safe while bash backs those with a temp file.
    _profile_plist="$(mktemp)"
    security cms -D -i "$PROFILE_FILE" >"$_profile_plist" 2>/dev/null || true
    PROFILE_APPID="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:com.apple.application-identifier' \
        "$_profile_plist" 2>/dev/null || true)"
    rm -f "$_profile_plist"
    if [[ -n "$PROFILE_APPID" ]]; then
        info "profile application-identifier: $PROFILE_APPID"
        # Strip the leading team prefix (TEAMID.) to compare the bundle id itself.
        # A wildcard profile (TEAMID.*) matches anything, so accept it.
        if [[ "${PROFILE_APPID#*.}" != "*" && "${PROFILE_APPID#*.}" != "$BUNDLE_ID" ]]; then
            die "profile/bundle id MISMATCH -- this is what causes errSecMissingEntitlement.
    profile is for : ${PROFILE_APPID#*.}
    app bundle id  : $BUNDLE_ID   (<ApplicationId> in $PROJECT)
    Fix one of the two so they agree: either register an App ID + profile for
    '$BUNDLE_ID' at developer.apple.com, or set <ApplicationId> back to the
    registered value."
        fi
        info "profile matches bundle id"
    else
        info "WARNING: profile has no application-identifier; cannot verify it matches $BUNDLE_ID"
    fi

    # codesign has to read the private key out of the login keychain. Over SSH that keychain
    # is usually locked (it only auto-unlocks on GUI login), and MSBuild reports the failure
    # as an opaque errSecInternalComponent, so check it here where the message can be useful.
    LOGIN_KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"
    if [[ -f "$LOGIN_KEYCHAIN" ]] && ! security show-keychain-info "$LOGIN_KEYCHAIN" >/dev/null 2>&1; then
        info "login keychain is locked; unlocking so codesign can read the signing key"
        security unlock-keychain "$LOGIN_KEYCHAIN" || die "could not unlock the login keychain.
    codesign fails with errSecInternalComponent until it is unlocked."
    fi

    # Unlocking is not sufficient on its own: the key also needs an ACL that lets Apple's
    # tools use it without a GUI consent prompt. Probe with a throwaway signature so that
    # shows up in a second, rather than as an opaque MSBuild error after a long build.
    _probe="$(mktemp)"
    printf 'probe' >"$_probe"
    if ! _probe_err="$(codesign -s "$CODESIGN_KEY" --force "$_probe" 2>&1)"; then
        rm -f "$_probe"
        if [[ "$_probe_err" == *errSecInternalComponent* ]]; then
            die "codesign cannot use the signing key (errSecInternalComponent).
    The keychain is reachable but Apple's tools lack standing access to the key.
    This is persistent once fixed -- run it once per machine, not per session:

        security set-key-partition-list -S apple-tool:,apple: -s \\
            -k '<login password>' \"$LOGIN_KEYCHAIN\"

    Omit -k to be prompted instead of putting the password in shell history.
    Last resort: run this script from a GUI terminal and click \"Always Allow\"."
        fi
        # Anything else: the probe itself may be at fault, so warn rather than block.
        info "WARNING: signing probe failed, continuing anyway:"
        printf '%s\n' "$_probe_err" | sed 's/^/    /'
    else
        rm -f "$_probe"
        info "signing key is usable"
    fi

    SIGN_ARGS=(
        -p:EnableCodeSigning=true
        -p:CodesignKey="$CODESIGN_KEY"
        -p:CodesignProvision="$CODESIGN_PROVISION"
        -p:UseHardenedRuntime=true
    )
fi

# --- reset stored certificate state ------------------------------------------

# Fresh-import testing needs both halves gone: the encrypted blob in the app
# container AND the envelope key in the Keychain. Leaving only one behind is a
# valid test case of its own, but not the one you get by accident.
if [[ "$RESET" -eq 1 ]]; then
    CONTAINER="$HOME/Library/Containers/$BUNDLE_ID"
    if [[ -d "$CONTAINER" ]]; then
        info "removing app container: $CONTAINER"
        rm -rf "$CONTAINER"
    else
        info "no app container found (nothing to remove)"
    fi

    cat <<EOF

  NOTE: the Keychain envelope key is NOT removed by this flag. macOS keeps it
  outside the container, and MAUI's SecureStorage picks its own service name.
  To find and remove any leftovers:

      security dump-keychain | grep -i mqttprobe
      # then, for each match:
      security delete-generic-password -s '<service-shown-above>'

  Or search "mqttprobe" in Keychain Access.app.

EOF
fi

# --- build -------------------------------------------------------------------

if [[ "$CLEAN" -eq 1 ]]; then
    info "cleaning $CONFIGURATION/$TFM"
    dotnet clean "$PROJECT" -c "$CONFIGURATION" -f "$TFM" >/dev/null
    rm -rf "src/MqttProbe.Maui/bin/$CONFIGURATION/$TFM"
fi

info "building $CONFIGURATION | $TFM"

# -f is mandatory: on macOS the csproj's TargetFrameworks is "net10.0-ios;net10.0-maccatalyst",
# so an unqualified build tries to build the iOS head too.
#
# CodesignEntitlements matches what .github/workflows/build-macos.yml passes. Without it the
# app runs unsandboxed, which changes where AppDataDirectory lands and how the Keychain
# behaves -- i.e. it would not be testing what actually ships.
if ! dotnet build "$PROJECT" \
    -c "$CONFIGURATION" \
    -f "$TFM" \
    -p:MqttProbeMauiMacTargetFrameworksOverride="$TFM" \
    -p:CodesignEntitlements=Platforms/MacCatalyst/Entitlements.plist \
    "${SIGN_ARGS[@]+"${SIGN_ARGS[@]}"}"
then
    if [[ "$SIGN" -eq 1 ]]; then
        cat >&2 <<EOF

  If that failed at codesign with errSecInternalComponent, codesign could not use the
  signing key. The keychain re-locks on sleep/timeout, so this can recur per session.
  Try, in order:

      security unlock-keychain "$LOGIN_KEYCHAIN"

      # grant Apple's signing tools standing access to the key (what CI does):
      security set-key-partition-list -S apple-tool:,apple: "$LOGIN_KEYCHAIN"

  Last resort: run this once from a GUI terminal on the Mac and click "Always Allow".

EOF
    fi
    die "build failed"
fi

APP="$(find "src/MqttProbe.Maui/bin/$CONFIGURATION/$TFM" -maxdepth 2 -name '*.app' -print -quit)"
[[ -n "$APP" ]] || die "build succeeded but no .app bundle was found under src/MqttProbe.Maui/bin/$CONFIGURATION/$TFM"

info "built: $APP"

# An unsigned or entitlement-less bundle fails Keychain access at runtime in confusing
# ways, so surface the signature state now rather than mid-test.
if codesign -dv "$APP" 2>&1 | grep -q 'Signature=adhoc'; then
    if [[ "$SIGN" -eq 1 ]]; then
        die "asked to sign but the bundle came out ad-hoc -- signing did not take effect"
    fi
    info "signature: ad-hoc. Keychain/SecureStorage WILL fail (errSecMissingEntitlement)."
    info "           re-run with --sign to test certificate storage."
else
    info "signature:"
    codesign -dvv "$APP" 2>&1 | grep -E 'Authority|TeamIdentifier' | sed 's/^/    /' || true
    # application-identifier is the entitlement Keychain access actually depends on.
    if codesign -d --entitlements - --xml "$APP" 2>/dev/null \
        | plutil -convert xml1 -o - - 2>/dev/null | grep -q 'application-identifier'; then
        info "           application-identifier present (Keychain should work)"
    else
        info "WARNING: no application-identifier entitlement; Keychain writes will likely fail"
    fi
fi

# --- launch ------------------------------------------------------------------

if [[ "$LAUNCH" -eq 0 ]]; then
    info "skipping launch (--no-launch). Start it with:  open '$APP'"
    exit 0
fi

cat <<EOF

  Need test certificates and an mTLS broker?
      python scripts/generate-mtls-certs.py
      docker compose -f docker-compose.mtls.yml up -d

EOF

info "launching"
open "$APP"
