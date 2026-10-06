"""Source-line exception definitions and matcher for the inspect gate.

Extracted from inspect.py to keep that module within the repository's
500-line limit.  Public symbols are re-exported by inspect.py so existing
callers (including the test suite) do not need to change.
"""

import subprocess
from pathlib import Path

# Source-line exceptions for public checksums, open-source commit pins, and
# Velopack schema field names that DevSkim flags as secrets or weak crypto.
# Each entry locks to an exact (rule, file, line, stripped content) tuple so a
# changed line stops matching and the finding blocks again.
#
# DS173237: hardcoded SHA256 / hex strings.  All values below are public:
#   - linuxdeploy binary hash from the Tauri GitHub releases page
#   - Photino.Native commit pin (open-source, Apache-2.0)
#   - Test fixtures asserting those same public values against NOTICE.md
#
# DS126858: SHA1 is a weak hash.  The Velopack releases.linux.json schema uses
# a SHA1 field; the production workflow parses it inline.  No hashlib.sha1
# call exists in any of these files.
SOURCE_LINE_EXCEPTIONS = [
    # ── DS173237: public binary integrity hash ─────────────────────────────
 {
        "rule": "DS173237",
        "file": "scripts/packaging/linux/appdir.sh",
        "line": 14,
        "content": 'LINUXDEPLOY_SHA256="36a2d7e274d12e1050d0e9ecfe11d339ed54720b2bec464c286d53f8b07f5c62"',  # DevSkim: ignore DS173237 mirrors the value the gate checks
        "rationale": "Public linuxdeploy binary hash for download integrity verification",
    },
    # ── DS173237: public open-source commit pins ───────────────────────────
 {
        "rule": "DS173237",
        "file": "scripts/packaging/linux/rebuild-photino.sh",
        "line": 19,
        "content": 'PHOTINO_COMMIT="3ba4b937d6337b5c58344445db08a5a9a63f07c8"',  # DevSkim: ignore DS173237 mirrors the value the gate checks
        "rationale": "Public Photino.Native commit pin (Apache-2.0, github.com/tryphotino)",
    },
    {
        "rule": "DS173237",
        "file": "scripts/packaging/linux/collect-sources.sh",
        "line": 81,
        "content": 'PHOTINO_COMMIT="3ba4b937d6337b5c58344445db08a5a9a63f07c8"',  # DevSkim: ignore DS173237 mirrors the value the gate checks
        "rationale": "Same public Photino.Native commit pin in a second build script",
    },
    # ── DS173237: test fixtures asserting public values ────────────────────
 {
        "rule": "DS173237",
        "file": "scripts/packaging/linux/tests/packaging/test_appimage_packaging.py",
        "line": 38,
        "content": 'expected = "2a15ce9da8de6e20159e1ab27861a7a5ef8758c81a6278ba4ab30cefa1d74c9f"',  # DevSkim: ignore DS173237 mirrors the fixture the gate checks
        "rationale": "Test fixture verifying vendor GStreamer script SHA256 hash",
    },
    {
        "rule": "DS173237",
        "file": "scripts/packaging/linux/tests/packaging/test_appimage_packaging.py",
        "line": 74,
        "content": '"3ba4b937d6337b5c58344445db08a5a9a63f07c8",',  # DevSkim: ignore DS173237 mirrors the fixture the gate checks
        "rationale": "Test fixture asserting NOTICE.md contains Photino commit pin",
    },
    {
        "rule": "DS173237",
        "file": "scripts/packaging/linux/tests/packaging/test_appimage_packaging.py",
        "line": 75,
        "content": '"30da1fd6e17de6107ecc850c95dfb16b5729f2dd"]:',  # DevSkim: ignore DS173237 mirrors the fixture the gate checks
        "rationale": "Test fixture asserting NOTICE.md contains Tauri commit pin",
    },
    {
        "rule": "DS173237",
        "file": "scripts/packaging/linux/tests/packaging/test_appimage_packaging.py",
        "line": 396,
        "content": 'self.assertIn("3ba4b937d6337b5c58344445db08a5a9a63f07c8", content)',  # DevSkim: ignore DS173237 mirrors the fixture the gate checks
        "rationale": "Test asserting collect-sources.sh contains pinned commit",
    },
]


def is_source_line_exception(finding, root):
    """True only for an exact (rule, file, line number, stripped source line) match.

    Fail closed: unreadable file, out-of-range line, or content mismatch all
    keep the finding blocking.  This mirrors the GATED_EXCEPTIONS pattern but
    matches plain source lines rather than JSON properties.
    """
    for exception in SOURCE_LINE_EXCEPTIONS:
        if finding["rule"] != exception["rule"] or finding["file"] != exception["file"]:
            continue
        if finding["line"] != exception["line"]:
            continue
        try:
            text = (root / exception["file"]).read_text(encoding="utf-8")
        except (OSError, ValueError):
            return False  # unreadable file: fail closed
        lines = text.splitlines()
        if exception["line"] < 1 or exception["line"] > len(lines):
            return False  # line out of range: fail closed
        actual = lines[exception["line"] - 1].strip()
        if actual != exception["content"]:
            return False  # content changed: fail closed
        return True
    return False


def git_ignored_paths(paths, repo_root):
    """Return the subset of *paths* that .gitignore marks as ignored.

    Uses ``git check-ignore --stdin`` so the result matches what ``git status``
    would hide.  Tracked files are never reported as ignored even when a glob
    matches, so the scanner keeps scanning committed secrets.  On any error
    (missing git, non-repo cwd, etc.) an empty set is returned, which means no
    paths are excluded -- fail-open on discovery, since the scanner still sees
    every file and gated findings still block.
    """
    if not paths:
        return set()
    normalised = [p.replace("\\", "/") for p in paths]
    try:
        result = subprocess.run(
            ["git", "check-ignore", "--stdin", "-z"],
            input="\0".join(normalised) + "\0",
            cwd=repo_root,
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            timeout=30,
        )
        # git check-ignore outputs NUL-terminated paths for matched entries.
        # Exit 0: at least one path matched.  Exit 1: none matched.
        if result.returncode not in (0, 1):
            return set()
        raw = result.stdout
        if not raw:
            return set()
        # Split on NUL; the trailing NUL produces an empty last element.
        return {p for p in raw.split("\0") if p}
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return set()
