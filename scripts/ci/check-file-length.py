#!/usr/bin/env python3
"""Checks that source files do not exceed a line-count limit."""

import subprocess
import sys
from pathlib import Path, PurePosixPath


def find_repo_root() -> Path:
    here = Path(__file__).resolve().parent
    for d in [here, *here.parents]:
        if (d / "MqttProbe.slnx").is_file():
            return d
    raise SystemExit("Could not find repo root (MqttProbe.slnx)")


ROOT = find_repo_root()
DEFAULT_LIMIT = 500

# Ceilings freeze current size: may stay over DEFAULT_LIMIT but cannot grow.
GRANDFATHER = {
    "src/MqttProbe.Shared/Components/Browser/ConnectionDialog.razor": 1002,
    "src/MqttProbe.Shared/Services/Configuration/SettingsStore.cs": 775,
    "src/MqttProbe.Shared/Services/Security/CertificateAssetStore.cs": 700,
    "src/MqttProbe.Shared/Services/Plugins/Packaging/PluginPackageInstaller.cs": 643,
    "src/MqttProbe.Shared/Services/Plugins/Registry/PluginRegistry.cs": 535,
    "src/MqttProbe.Shared/Components/Sparkplug/SparkplugNodesView.razor": 535,
    "src/MqttProbe.Shared/Services/Emulation/NodeRunners.cs": 528,
    "src/MqttProbe.Shared/Services/Mqtt/MessageStoreManager.cs": 512,
}

SKIP_BASENAMES = {"SparkplugBProtobuf.cs"}
RELEVANT_EXTENSIONS = {".cs", ".razor"}


def is_relevant(path_str: str) -> bool:
    p = PurePosixPath(path_str.replace("\\", "/"))
    if p.name in SKIP_BASENAMES:
        return False
    if not p.parts or p.parts[0] != "src":
        return False
    return p.suffix.lower() in RELEVANT_EXTENSIONS


def limit_for(path_str: str) -> int:
    norm = path_str.replace("\\", "/")
    return GRANDFATHER.get(norm, DEFAULT_LIMIT)


def count_lines(text: str) -> int:
    return len(text.splitlines())


def get_staged_paths() -> list[str]:
    result = subprocess.run(
        ["git", "diff", "--cached", "--name-only", "--diff-filter=ACMR"],
        capture_output=True, text=True, cwd=ROOT,
    )
    return [p for p in result.stdout.splitlines() if p.strip()]


def staged_blob(path_str: str) -> str:
    result = subprocess.run(
        ["git", "show", f":{path_str}"],
        capture_output=True, cwd=ROOT,
    )
    return result.stdout.decode("utf-8-sig", errors="replace")


def disk_text(path_str: str) -> str:
    return (ROOT / path_str).read_text(encoding="utf-8-sig")


def main() -> int:
    args = [a for a in sys.argv[1:] if a != "--staged"]
    use_staged = "--staged" in sys.argv

    print("\n=== File length ===")

    if use_staged:
        paths = [p for p in get_staged_paths() if is_relevant(p)]
        read = staged_blob
    elif args:
        paths = [p.replace("\\", "/") for p in args if is_relevant(p.replace("\\", "/"))]
        read = lambda p: disk_text(p)
    else:
        print("No files to check.")
        return 0

    if not paths:
        print("No relevant files to check.")
        return 0

    violations = []
    for p in paths:
        n = count_lines(read(p))
        lim = limit_for(p)
        if n > lim:
            violations.append((p, n, lim))

    if violations:
        for path, actual, lim in violations:
            print(f"  FAIL  {path}: {actual} lines (limit {lim})")
        return 1

    print(f"  OK  {len(paths)} file(s) within limits")
    return 0


if __name__ == "__main__":
    sys.exit(main())
