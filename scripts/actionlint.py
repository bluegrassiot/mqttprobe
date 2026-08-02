#!/usr/bin/env python3
"""
Lints the GitHub Actions workflows in .github/workflows with actionlint.

Prefers a locally installed `actionlint` binary; otherwise falls back to the
official Docker image (no local install needed, and it bundles shellcheck so
`run:` scripts are checked too). Exits non-zero if any workflow has problems, so
it can gate CI or a pre-push hook.

Running from Python (not a shell) also sidesteps Git Bash's MSYS path mangling of
the Docker volume/workdir arguments on Windows.

Usage:
  ./scripts/actionlint.py            # lint all workflows
  ./scripts/actionlint.py -color     # any extra args pass through to actionlint
"""

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Pinned for reproducible results across machines/CI. Bump deliberately.
DOCKER_IMAGE = "rhysd/actionlint:1.7.7"


def main() -> int:
    extra = sys.argv[1:]

    print("\n=== actionlint ===")

    local = shutil.which("actionlint")
    if local:
        print("  using local actionlint")
        cmd = [local, *extra]
    elif shutil.which("docker"):
        print(f"  actionlint not on PATH; using Docker image {DOCKER_IMAGE}")
        # as_posix() keeps forward slashes so Docker Desktop accepts the Windows path.
        cmd = [
            "docker", "run", "--rm",
            "-v", f"{ROOT.as_posix()}:/repo",
            "-w", "/repo",
            DOCKER_IMAGE,
            *extra,
        ]
    else:
        print("  ERROR: neither 'actionlint' nor 'docker' found on PATH.")
        print("  Install actionlint (https://github.com/rhysd/actionlint#installation)")
        print("  or Docker, then re-run.")
        return 1

    result = subprocess.run(cmd, cwd=ROOT)

    print()
    if result.returncode == 0:
        print("=== Workflows OK ===")
    else:
        print("=== actionlint reported problems (see above) ===")
    return result.returncode


if __name__ == "__main__":
    sys.exit(main())
