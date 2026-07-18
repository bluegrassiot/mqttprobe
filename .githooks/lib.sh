#!/usr/bin/env bash
# Shared helpers for .githooks/* (sourced, not executed).

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
RESET='\033[0m'

HOOK_START_SECONDS=$SECONDS
STEP_START_SECONDS=$SECONDS
STEP_NAME=""

hook_root() {
    # Caller must set HOOK_FILE to BASH_SOURCE of the hook entrypoint before sourcing,
    # or we derive from BASH_SOURCE[0] of this lib when sourced as .githooks/lib.sh.
    local here
    here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    cd "$here/.." && pwd
}

now_secs() {
    echo "$SECONDS"
}

elapsed_since() {
    local start=$1
    echo $(( SECONDS - start ))
}

step() {
    STEP_NAME="$1"
    STEP_START_SECONDS=$SECONDS
    printf "\n${CYAN}==> %s${RESET}\n" "$STEP_NAME"
}

pass() {
    local msg=${1:-$STEP_NAME}
    local secs
    secs=$(elapsed_since "$STEP_START_SECONDS")
    printf "${GREEN}  PASS${RESET}  %s (%ss)\n" "$msg" "$secs"
}

fail() {
    local msg=${1:-$STEP_NAME}
    local secs
    secs=$(elapsed_since "$STEP_START_SECONDS")
    printf "${RED}  FAIL${RESET}  %s (%ss)\n" "$msg" "$secs"
}

skip_msg() {
    local msg=$1
    local secs
    secs=$(elapsed_since "$STEP_START_SECONDS")
    printf "${YELLOW}  SKIP${RESET}  %s (%ss)\n" "$msg" "$secs"
}

print_total() {
    local secs
    secs=$(elapsed_since "$HOOK_START_SECONDS")
    printf "\nTotal: %ss\n\n" "$secs"
}

env_truthy() {
    local v=${1:-}
    case "$v" in
        1|true|TRUE|yes|YES|on|ON) return 0 ;;
        *) return 1 ;;
    esac
}

# Returns 0 if path is docs/non-code only material.
is_docs_only_path() {
    local p=$1
    p=${p//\\//}
    case "$p" in
        docs/*|*.md) return 0 ;;
        LICENSE|NOTICES|SECURITY.md|CONTRIBUTING.md|README.md) return 0 ;;
        *) return 1 ;;
    esac
}

# Returns 0 if path should trigger format-check on commit.
is_format_relevant_path() {
    local p=$1
    p=${p//\\//}
    local base
    base=$(basename "$p")
    case "$base" in
        .editorconfig) return 0 ;;
    esac
    case "$p" in
        *.cs|*.razor|*.csproj|*.props|*.targets) return 0 ;;
        *) return 1 ;;
    esac
}

# stdin: one path per line. Returns 0 if every path is docs-only (and at least one path).
# Empty input returns 1 (not docs-only skip; caller handles empty separately).
all_paths_docs_only() {
    local p any=0
    while IFS= read -r p || [[ -n "$p" ]]; do
        [[ -z "$p" ]] && continue
        any=1
        if ! is_docs_only_path "$p"; then
            return 1
        fi
    done
    [[ $any -eq 1 ]]
}

# stdin: one path per line. Returns 0 if any path is format-relevant.
any_format_relevant() {
    local p
    while IFS= read -r p || [[ -n "$p" ]]; do
        [[ -z "$p" ]] && continue
        if is_format_relevant_path "$p"; then
            return 0
        fi
    done
    return 1
}

# Classify push paths from stdin. Sets globals:
#   NEED_NOMAUI_BUILD NEED_UNIT_TESTS NEED_MAUI_BUILD NEED_DESKTOP_BUILD
#   CLASSIFY_DOCS_ONLY (1/0)
classify_push_paths() {
    NEED_NOMAUI_BUILD=0
    NEED_UNIT_TESTS=0
    NEED_MAUI_BUILD=0
    NEED_DESKTOP_BUILD=0
    CLASSIFY_DOCS_ONLY=0

    local p any=0
    local has_shared=0 has_integration=0 has_infra=0 has_scripts=0 has_other=0
    local has_maui=0 has_desktop=0
    local all_docs=1

    while IFS= read -r p || [[ -n "$p" ]]; do
        [[ -z "$p" ]] && continue
        any=1
        p=${p//\\//}

        if ! is_docs_only_path "$p"; then
            all_docs=0
        fi

        case "$p" in
            src/MqttProbe.Shared/*|src/MqttProbe.Web/*|tests/MqttProbe.Tests/*|tests/MqttProbe.TestInfrastructure/*)
                has_shared=1
                ;;
            tests/MqttProbe.IntegrationTests/*)
                has_integration=1
                ;;
            src/MqttProbe.Maui/*)
                has_maui=1
                ;;
            src/MqttProbe.Desktop/*)
                has_desktop=1
                ;;
            Directory.Build.props|Directory.Packages.props|global.json|MqttProbe.slnx|MqttProbe.NoMaui.slnf|dotnet-tools.json)
                has_infra=1
                ;;
            scripts/*|.github/*|.githooks/*|Dockerfile|docker-compose.yml|docker-compose.*.yml|.dockerignore)
                has_scripts=1
                ;;
            *)
                if ! is_docs_only_path "$p"; then
                    has_other=1
                fi
                ;;
        esac
    done

    if [[ $any -eq 0 ]]; then
        CLASSIFY_DOCS_ONLY=1
        return 0
    fi

    if [[ $all_docs -eq 1 ]]; then
        CLASSIFY_DOCS_ONLY=1
        return 0
    fi

    CLASSIFY_DOCS_ONLY=0

    if [[ $has_maui -eq 1 ]]; then
        NEED_MAUI_BUILD=1
    fi
    if [[ $has_desktop -eq 1 ]]; then
        NEED_DESKTOP_BUILD=1
    fi

    # Unit tests for shared/web/unit, infra, scripts/CI, or unknown non-docs.
    if [[ $has_shared -eq 1 || $has_infra -eq 1 || $has_scripts -eq 1 || $has_other -eq 1 ]]; then
        NEED_UNIT_TESTS=1
        NEED_NOMAUI_BUILD=1
    fi

    # Integration-only still needs NoMaui compile check.
    if [[ $has_integration -eq 1 ]]; then
        NEED_NOMAUI_BUILD=1
    fi
}

# Resolve merge-base candidate for new-branch pushes. Echoes sha or empty.
find_push_merge_base() {
    local local_sha=$1
    local cand base
    for cand in "@{upstream}" "origin/main" "origin/master" "main" "master"; do
        if base=$(git merge-base "$cand" "$local_sha" 2>/dev/null); then
            if [[ -n "$base" ]]; then
                echo "$base"
                return 0
            fi
        fi
    done
    return 1
}

ZERO_SHA="0000000000000000000000000000000000000000"

# Read pre-push stdin (local_ref local_sha remote_ref remote_sha) and print changed paths.
collect_push_changed_paths() {
    local local_ref local_sha remote_ref remote_sha base
    while read -r local_ref local_sha remote_ref remote_sha; do
        [[ -z "${local_sha:-}" ]] && continue
        if [[ "$local_sha" == "$ZERO_SHA" ]]; then
            # delete remote ref; nothing to check
            continue
        fi
        if [[ "$remote_sha" == "$ZERO_SHA" ]]; then
            if base=$(find_push_merge_base "$local_sha"); then
                git diff --name-only "$base..$local_sha"
            else
                git diff-tree --no-commit-id --name-only -r "$local_sha"
            fi
        else
            git diff --name-only "$remote_sha..$local_sha"
        fi
    done
}