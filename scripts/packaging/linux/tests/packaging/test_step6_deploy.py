"""Behavioral regression tests for Step 6 batched deploy-deps-only.

Extracts the actual Step 6 code from appdir.sh and exercises it with
fake file/linuxdeploy commands and a temporary AppDir structure. The
fake linuxdeploy logs each argument on its own line (one call per block
separated by a blank line) so Python can assert exact argument structure
without shell echo "$@" flattening concerns.
"""

import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
LINUX_DIR = REPO_ROOT / "scripts" / "packaging" / "linux"


def _extract_step6() -> str:
    content = (LINUX_DIR / "appdir.sh").read_text(encoding="utf-8", errors="replace")
    start = content.find("# ── Step 6:")
    end = content.find("# ── Step 7:")
    if start == -1 or end == -1:
        raise RuntimeError("Step 6/7 markers not found")
    return content[start:end].rstrip()


def _to_wsl(path: str) -> str:
    p = path.replace("\\", "/")
    if len(p) >= 2 and p[1] == ":":
        return f"/mnt/{p[0].lower()}{p[2:]}"
    return p


def _parse_ld_log(log_path: Path) -> list[list[str]]:
    """Parse the structured linuxdeploy log into a list of argument lists.

    Format: one argument per line, blank line between invocations.
    """
    if not log_path.exists():
        return []
    text = log_path.read_text(encoding="utf-8")
    calls: list[list[str]] = []
    current: list[str] = []
    for line in text.splitlines():
        if line == "":
            if current:
                calls.append(current)
                current = []
        else:
            current.append(line)
    if current:
        calls.append(current)
    return calls


def _run_step6(tmpdir: Path, setup: str, fail_pattern: str = "") -> tuple[int, str, str, Path]:
    """Run Step 6 in a bash subprocess with fake commands.

    Returns (returncode, stdout, stderr, log_path).
    """
    wsl_tmp = _to_wsl(str(tmpdir))
    step6 = _extract_step6()
    log_wsl = f"{wsl_tmp}/ld.log"
    fail_wsl = f"{wsl_tmp}/fail_pattern"
    log_win = tmpdir / "ld.log"

    # Write fail pattern file only if non-empty
    if fail_pattern:
        fail_setup = f"printf '{fail_pattern}\\n' > '{fail_wsl}'"
    else:
        fail_setup = f": > '{fail_wsl}'"  # empty file, no patterns

    script = f"""#!/usr/bin/env bash
set -uo pipefail

file() {{
    local magic
    magic=$(od -An -tx1 -N4 "$1" 2>/dev/null | tr -d ' \\n')
    if [[ "$magic" == "7f454c46" ]]; then
        echo "$1: ELF 64-bit LSB shared object"
    else
        echo "$1: data"
    fi
}}

LINUXDEPLOY="{wsl_tmp}/fake-linuxdeploy"
cat > "$LINUXDEPLOY" << 'FAKE'
#!/usr/bin/env bash
DIR="$(cd "$(dirname "$0")" && pwd)"
LOG="$DIR/ld.log"
FAIL="$DIR/fail_pattern"
for a in "$@"; do printf '%s\\n' "$a" >> "$LOG"; done
printf '\\n' >> "$LOG"
if [[ -s "$FAIL" ]]; then
    while IFS= read -r pat; do
        [[ -z "$pat" ]] && continue
        for a in "$@"; do
            if [[ "$a" == *"$pat"* ]]; then
                echo "FATAL: fake linuxdeploy rejected $a" >&2
                exit 1
            fi
        done
    done < "$FAIL"
fi
FAKE
chmod +x "$LINUXDEPLOY"

: > "{log_wsl}"
{fail_setup}

APPDIR="{wsl_tmp}/AppDir"
WEBKIT_LIBDIR="/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1"

{setup}

{step6}
"""
    sp = tmpdir / "run.sh"
    sp.write_text(script, encoding="utf-8")
    r = subprocess.run(
        ["bash", _to_wsl(str(sp))],
        capture_output=True, text=True, timeout=30,
    )
    return r.returncode, r.stdout, r.stderr, log_win


class TestStep6BatchedDeploy(unittest.TestCase):
    """Step 6 must batch all --deploy-deps-only into one linuxdeploy call."""

    def test_multiple_elves_batched_single_invocation(self):
        """5 ELFs across all 4 globs produce one batched + one final call."""
        with tempfile.TemporaryDirectory() as td:
            rc, _, stderr, log = _run_step6(Path(td), r"""
mkdir -p "$APPDIR$WEBKIT_LIBDIR"
mkdir -p "$APPDIR$WEBKIT_LIBDIR/injected-bundle"
mkdir -p "$APPDIR/usr/lib/gstreamer-1.0"
mkdir -p "$APPDIR/usr/lib/gstreamer-1.0/gstreamer-1.0"
printf '\x7fELF' > "$APPDIR$WEBKIT_LIBDIR/WebKitNetworkProcess"
printf '\x7fELF' > "$APPDIR$WEBKIT_LIBDIR/WebKitWebProcess"
printf '\x7fELF' > "$APPDIR$WEBKIT_LIBDIR/injected-bundle/libwebkitinjected.so"
printf '\x7fELF' > "$APPDIR/usr/lib/gstreamer-1.0/libgstcoreelements.so"
printf '\x7fELF' > "$APPDIR/usr/lib/gstreamer-1.0/gstreamer-1.0/gst-plugin-scanner"
""")
            self.assertEqual(rc, 0, f"Script failed: {stderr}")
            calls = _parse_ld_log(log)
            self.assertEqual(len(calls), 2, f"Expected 2 calls, got {len(calls)}")

            batched, final = calls[0], calls[1]

            # Batched call: --appdir APPDIR then 5 (--deploy-deps-only ELF) pairs
            self.assertEqual(batched[0], "--appdir")
            ddo_args = [a for a in batched if a == "--deploy-deps-only"]
            self.assertEqual(len(ddo_args), 5, f"Expected 5 --deploy-deps-only, got {len(ddo_args)}")
            elf_args = [batched[i+1] for i, a in enumerate(batched) if a == "--deploy-deps-only"]
            expected_names = {
                "WebKitNetworkProcess", "WebKitWebProcess",
                "libwebkitinjected.so", "libgstcoreelements.so", "gst-plugin-scanner",
            }
            actual_names = {Path(e).name for e in elf_args}
            self.assertEqual(actual_names, expected_names)
            # Each path is a single argument in the structured log (one line = one arg)
            for e in elf_args:
                self.assertNotIn("\n", e)

            # Final call: --executable, --desktop-file, --icon-file (no --deploy-deps-only)
            self.assertIn("--executable", final)
            self.assertNotIn("--deploy-deps-only", final)

    def test_spaces_in_paths_preserved(self):
        """Paths with spaces are passed as single arguments."""
        with tempfile.TemporaryDirectory() as td:
            rc, _, stderr, log = _run_step6(Path(td), r"""
mkdir -p "$APPDIR$WEBKIT_LIBDIR/injected-bundle"
printf '\x7fELF' > "$APPDIR$WEBKIT_LIBDIR/injected-bundle/my bundle libfoo.so"
""")
            self.assertEqual(rc, 0, f"Script failed: {stderr}")
            calls = _parse_ld_log(log)
            self.assertEqual(len(calls), 2)
            batched = calls[0]

            # Find the ELF arg after --deploy-deps-only
            elf_args = [batched[i+1] for i, a in enumerate(batched) if a == "--deploy-deps-only"]
            self.assertEqual(len(elf_args), 1)
            self.assertEqual(Path(elf_args[0]).name, "my bundle libfoo.so",
                             "Spaces must be preserved as single argument")

    def test_non_elf_and_missing_skipped(self):
        """Non-ELF files and missing globs produce no --deploy-deps-only."""
        with tempfile.TemporaryDirectory() as td:
            rc, _, stderr, log = _run_step6(Path(td), r"""
mkdir -p "$APPDIR$WEBKIT_LIBDIR"
mkdir -p "$APPDIR/usr/lib/gstreamer-1.0"
printf '\x7fELF' > "$APPDIR$WEBKIT_LIBDIR/WebKitNetworkProcess"
echo "not an elf" > "$APPDIR$WEBKIT_LIBDIR/WebKitWebProcess"
echo "text" > "$APPDIR/usr/lib/gstreamer-1.0/libgstcore.so"
""")
            self.assertEqual(rc, 0, f"Script failed: {stderr}")
            calls = _parse_ld_log(log)
            self.assertEqual(len(calls), 2)
            batched = calls[0]

            elf_args = [batched[i+1] for i, a in enumerate(batched) if a == "--deploy-deps-only"]
            self.assertEqual(len(elf_args), 1)
            self.assertEqual(Path(elf_args[0]).name, "WebKitNetworkProcess")

    def test_empty_batch_no_deploy_deps_call(self):
        """No ELFs: zero batched calls, only final deploy runs."""
        with tempfile.TemporaryDirectory() as td:
            rc, _, stderr, log = _run_step6(Path(td), r"""
mkdir -p "$APPDIR"
""")
            self.assertEqual(rc, 0, f"Script failed: {stderr}")
            calls = _parse_ld_log(log)
            self.assertEqual(len(calls), 1, f"Expected 1 call (final only), got {len(calls)}")
            self.assertNotIn("--deploy-deps-only", calls[0])
            self.assertIn("--executable", calls[0])

    def test_failure_stops_before_final_deploy(self):
        """Batch failure: nonzero exit, no final --executable call in log."""
        with tempfile.TemporaryDirectory() as td:
            rc, stdout, stderr, log = _run_step6(Path(td), r"""
mkdir -p "$APPDIR$WEBKIT_LIBDIR"
printf '\x7fELF' > "$APPDIR$WEBKIT_LIBDIR/WebKitNetworkProcess"
printf '\x7fELF' > "$APPDIR$WEBKIT_LIBDIR/WebKitWebProcess"
""", fail_pattern="WebKitWebProcess")
            self.assertNotEqual(rc, 0, "Must exit nonzero on batch failure")
            # FATAL goes to stdout because appdir.sh uses 2>&1 on the linuxdeploy call
            combined = stdout + stderr
            self.assertIn("FATAL", combined)
            calls = _parse_ld_log(log)
            self.assertEqual(len(calls), 1, "Failed batch must still be recorded")
            self.assertIn("--deploy-deps-only", calls[0])
            self.assertNotIn("--executable", calls[0])


if __name__ == "__main__":
    unittest.main()