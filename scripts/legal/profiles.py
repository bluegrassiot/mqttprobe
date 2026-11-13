"""Export and verify per-host NuGet resolution inventories."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from pathlib import Path

from common import (
    PROFILES,
    ROOT,
    LegalError,
    read_json,
    safe_relative,
    sha256_bytes,
    write_json,
)

SCENARIOS = ROOT / "legal" / "scenarios.json"
PROPERTY_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
FINGERPRINT_FILES = (
    "global.json",
    "Directory.Packages.props",
    "Directory.Build.props",
    ".github/actions/setup-dotnet-workload/action.yml",
)


def scenario_properties(scenario):
    raw = scenario.get("msbuildProperties")
    if raw is None:
        return {}
    if not isinstance(raw, dict):
        raise LegalError(f"{scenario.get('name', '<scenario>')}: msbuildProperties must be an object")
    properties = {}
    for key, value in raw.items():
        if not isinstance(key, str) or not PROPERTY_NAME.match(key):
            raise LegalError(
                f"{scenario.get('name', '<scenario>')}: invalid MSBuild property name: {key!r}"
            )
        if isinstance(value, bool):
            properties[key] = "true" if value else "false"
        elif isinstance(value, int):
            properties[key] = str(value)
        elif isinstance(value, str):
            properties[key] = value
        else:
            raise LegalError(
                f"{scenario.get('name', '<scenario>')}: MSBuild property {key} must be a string, "
                "boolean or integer"
            )
    return properties


def scenario_map():
    data = read_json(SCENARIOS)
    scenarios = {entry["name"]: entry for entry in data["scenarios"]}
    for scenario in scenarios.values():
        scenario_properties(scenario)
    return scenarios


def target_keys(assets, scenario):
    framework = scenario["framework"]
    available = ", ".join(sorted(assets.get("targets", {}))) or "none"
    if scenario.get("runtimeIdentifiers"):
        keys = [f"{framework}/{rid}" for rid in scenario["runtimeIdentifiers"]]
        missing = [key for key in keys if key not in assets.get("targets", {})]
        if missing:
            raise LegalError(
                f"{scenario['name']}: assets have no target {', '.join(missing)}. "
                f"Restore {scenario.get('project', '<project>')} with {scenario['name']} settings first. "
                f"Available targets: {available}"
            )
        return keys
    key = f"{framework}/{scenario['runtimeIdentifier']}" if scenario.get("runtimeIdentifier") else framework
    if key not in assets.get("targets", {}):
        raise LegalError(
            f"{scenario['name']}: assets have no target {key}. Restore {scenario.get('project', '<project>')} "
            f"with {scenario['name']} settings first. Available targets: {available}"
        )
    return [key]


def assets_path(scenario):
    project = ROOT / safe_relative(scenario["project"])
    return project.parent / "obj" / "project.assets.json"


def read_assets(scenario):
    path = assets_path(scenario)
    if not path.is_file():
        raise LegalError(
            f"{scenario['name']}: missing {path.relative_to(ROOT).as_posix()}. "
            "Restore the project before exporting or checking profiles."
        )
    return read_json(path)


def inventory(scenario):
    assets = read_assets(scenario)
    keys = target_keys(assets, scenario)
    libraries = assets.get("libraries", {})
    packages = {}
    for key in keys:
        for identity, entry in assets["targets"][key].items():
            if entry.get("type") != "package":
                continue
            package_id, version = identity.split("/", 1)
            library = libraries.get(identity, {})
            digest = library.get("sha512")
            if not digest:
                raise LegalError(f"{scenario['name']}: package {identity} has no sha512 in assets")
            previous = packages.get(identity)
            if previous and previous != digest:
                raise LegalError(f"{scenario['name']}: sha512 differs between targets for {identity}")
            packages[identity] = digest
    result = [
        {"id": identity.split("/", 1)[0], "version": identity.split("/", 1)[1], "sha512": digest}
        for identity, digest in packages.items()
    ]
    result.sort(key=lambda item: (item["id"], item["version"]))
    metadata = {
        "schemaVersion": 1,
        "name": scenario["name"],
        "host": scenario["host"],
        "project": scenario["project"],
        "framework": scenario["framework"],
        "runtimeIdentifier": scenario.get("runtimeIdentifier"),
        "runtimeIdentifiers": scenario.get("runtimeIdentifiers"),
        "selfContained": scenario.get("selfContained", False),
    }
    properties = scenario_properties(scenario)
    if properties:
        metadata["msbuildProperties"] = properties
    metadata["targets"] = keys
    metadata["inputs"] = input_fingerprint()
    metadata["packages"] = result
    return metadata


def hash_dependency_file(path):
    data = Path(path).read_bytes()
    text = data.decode("utf-8-sig", errors="strict")
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    return sha256_bytes(text.encode("utf-8"))


def input_fingerprint():
    files = {}
    for relative in FINGERPRINT_FILES:
        path = ROOT / relative
        if path.is_file():
            files[relative] = hash_dependency_file(path)
    for path in sorted((ROOT / "src").glob("*/*.csproj")):
        files[path.relative_to(ROOT).as_posix()] = hash_dependency_file(path)
    submodule = {}
    for name in ("external/SparkplugNet",):
        path = ROOT / name
        if (path / ".git").exists() or (path / "src").is_dir():
            try:
                result = subprocess.run(
                    ["git", "rev-parse", "HEAD"],
                    cwd=path,
                    capture_output=True,
                    text=True,
                    check=True,
                )
                submodule[name] = result.stdout.strip()
            except (OSError, subprocess.CalledProcessError):
                pass
    try:
        sdk = read_json(ROOT / "global.json")["sdk"]["version"]
    except (KeyError, LegalError):
        sdk = None
    return {"files": files, "submodules": submodule, "sdk": sdk}


def compare(committed, current):
    problems = []
    for field in ("framework", "runtimeIdentifier", "runtimeIdentifiers", "selfContained", "targets"):
        if committed.get(field) != current.get(field):
            problems.append(f"profile {field} changed: {committed.get(field)!r} != {current.get(field)!r}")
    committed_properties = committed.get("msbuildProperties") or {}
    current_properties = current.get("msbuildProperties") or {}
    if committed_properties != current_properties:
        problems.append(
            f"MSBuild properties changed: {committed_properties!r} != {current_properties!r}"
        )
    expected = {(item["id"], item["version"]): item["sha512"] for item in committed["packages"]}
    actual = {(item["id"], item["version"]): item["sha512"] for item in current["packages"]}
    for identity in sorted(set(expected) | set(actual)):
        label = f"{identity[0]} {identity[1]}"
        if identity not in actual:
            problems.append(f"missing resolved package: {label}")
        elif identity not in expected:
            problems.append(f"unreviewed resolved package: {label}")
        elif expected[identity] != actual[identity]:
            problems.append(f"sha512 changed for {label}")
    for relative, digest in sorted(current["inputs"]["files"].items()):
        previous = committed.get("inputs", {}).get("files", {}).get(relative)
        if previous != digest:
            problems.append(f"dependency input changed: {relative}")
    previous_submodules = committed.get("inputs", {}).get("submodules", {})
    if previous_submodules != current["inputs"].get("submodules", {}):
        problems.append("submodule commit changed")
    return problems


def export(names, check=False):
    scenarios = scenario_map()
    default = [name for name, scenario in scenarios.items() if not scenario.get("pending")]
    selected = names or sorted(default)
    results = {}
    for name in selected:
        if name not in scenarios:
            raise LegalError(f"unknown scenario: {name}")
        current = inventory(scenarios[name])
        path = PROFILES / f"{name}.json"
        if check:
            if not path.is_file():
                raise LegalError(f"missing committed profile: legal/profiles/{name}.json")
            problems = compare(read_json(path), current)
            if problems:
                raise LegalError(
                    f"{name}: profile is stale:\n  " + "\n  ".join(problems)
                    + f"\nRun: python scripts/legal/notices.py profiles export --scenario {name}"
                )
            results[name] = "ok"
        else:
            write_json(path, current)
            results[name] = current
    return results


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("export", "check"))
    parser.add_argument("--scenario", action="append", dest="scenarios")
    args = parser.parse_args(argv)
    results = export(args.scenarios, check=args.action == "check")
    for name, result in results.items():
        count = len(result["packages"]) if isinstance(result, dict) else result
        print(f"{name}: {count}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
