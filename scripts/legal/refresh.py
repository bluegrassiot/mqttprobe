"""Explicit, reviewed refresh of legal profiles and extracted inputs.

Normal CI never runs this. Refresh restores one scenario at a time, exports its
profile, runs the pinned nuget-license tool for that same evaluation, and stores
normalized metadata and texts under legal/extracted.
"""

from __future__ import annotations

import argparse
import json
import platform
import subprocess

import catalogue
import extract
import profiles
from common import PROFILES, ROOT, LegalError, read_json, write_json


def restore_command(scenario):
    command = ["dotnet", "restore", scenario["project"], "--nologo"]
    framework = scenario["framework"]
    if "MqttProbe.Maui" in scenario["project"]:
        command.append(f"-p:TargetFrameworks={framework}")
    else:
        command.append(f"-p:TargetFramework={framework}")
    if not scenario.get("runtimeIdentifiers") and scenario.get("runtimeIdentifier"):
        command.extend(["-r", scenario["runtimeIdentifier"]])
    if scenario.get("selfContained"):
        command.append("-p:SelfContained=true")
    for key, value in profiles.scenario_properties(scenario).items():
        command.append(f"-p:{key}={value}")
    return command


def run(command, cwd=ROOT, environment=None):
    result = subprocess.run(
        command, cwd=cwd, capture_output=True, text=True, check=False, env=environment
    )
    if result.returncode != 0:
        raise LegalError("command failed: " + " ".join(command) + "\n" + result.stdout + result.stderr)
    return result.stdout


def default_scenarios(scenarios):
    system = platform.system().lower()
    host = {
        "windows": {"windows", "any"},
        "linux": {"linux", "any"},
        "darwin": {"macos", "ios"},
    }.get(system, {"any"})
    return [
        name
        for name, scenario in scenarios.items()
        if not scenario.get("pending") and scenario.get("host") in host
    ]


def refresh_scenario(scenario, packages):
    environment = extract.scenario_environment(scenario)
    print(f"restoring {scenario['name']}: {' '.join(restore_command(scenario))}")
    run(restore_command(scenario), environment=environment)
    current = profiles.inventory(scenario)
    write_json(PROFILES / f"{scenario['name']}.json", current)
    print(f"exported {scenario['name']}: {len(current['packages'])} packages")
    records = extract.extract_scenario(scenario, current, packages)
    print(f"extracted {scenario['name']}: {len(records)} packages")
    return current, records


def union_identities():
    identities = set()
    for path in PROFILES.glob("*.json"):
        profile = read_json(path)
        identities.update((item["id"], item["version"]) for item in profile["packages"])
    return identities


def comparable(record):
    copy = json.loads(json.dumps(record))
    for document in copy.get("documents", []):
        document.pop("text", None)
    if copy.get("extractor"):
        copy["extractor"].pop("text", None)
    return copy


def refresh(names, all_scenarios=False):
    extract.verify_tool()
    scenarios = profiles.scenario_map()
    if names:
        selected = names
    elif all_scenarios:
        selected = [name for name, scenario in scenarios.items() if not scenario.get("pending")]
    else:
        selected = default_scenarios(scenarios)
    if not selected:
        raise LegalError(
            "no host-appropriate scenarios selected. Pass --scenario NAME or --all explicitly."
        )
    for name in selected:
        if name not in scenarios:
            raise LegalError(f"unknown scenario: {name}")

    packages, _, _ = catalogue.load_overrides()
    records = extract.read_extracted()
    for name in selected:
        _, fresh = refresh_scenario(scenarios[name], packages)
        for key, value in fresh.items():
            if key in records and comparable(records[key]) != comparable(value):
                raise LegalError(
                    f"{key[0]} {key[1]}: extracted data differs between scenarios. "
                    "Run refresh with the same reviewed inputs and restore graph."
                )
            records[key] = value

    identities = union_identities()
    records = {key: value for key, value in records.items() if key in identities}
    missing = sorted(identities - set(records))
    if missing:
        raise LegalError(
            "legal/extracted is missing packages from unselected profiles: "
            + ", ".join(f"{package} {version}" for package, version in missing)
            + ". Run refresh for those scenarios too."
        )
    extract.write_extracted(records)
    catalogue.generate()
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scenario", action="append", dest="scenarios")
    parser.add_argument("--all", action="store_true", dest="all_scenarios")
    args = parser.parse_args(argv)
    return refresh(args.scenarios, args.all_scenarios)


if __name__ == "__main__":
    raise SystemExit(main())
