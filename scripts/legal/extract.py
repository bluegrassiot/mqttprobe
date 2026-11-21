"""Capture reviewed nuget-license and package-payload data as committed inputs."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

import catalogue
import profiles
from common import (
    ROOT,
    SUPPLEMENTS,
    LegalError,
    package_cache_dirs,
    package_directory,
    read_json,
    sha256_text,
    write_json,
    write_text,
)

TOOL_NAME = "nuget-license"
TOOL_VERSION = "4.0.18"
TOOL_MANIFEST = ROOT / "dotnet-tools.json"
EXTRACTED = ROOT / "legal" / "extracted"
EXTRACTED_MANIFEST = EXTRACTED / "packages.json"
EXTRACTED_TEXTS = EXTRACTED / "texts"
BINDINGS = SUPPLEMENTS / "bindings.json"

PAYLOAD_NAME = re.compile(r"(?i)^(licen[cs]e|notice|copying|third[-_. ]?party|sdk_license)")
ATTRIBUTION_NAME = re.compile(r"(?i)(notice|third[-_. ]?party|attribution)")
LICENSE_NAME = re.compile(r"(?i)(licen[cs]e|copying|sdk_license)")


def is_license_document(path):
    name = path.name.lower()
    return bool(LICENSE_NAME.search(name)) and not ATTRIBUTION_NAME.search(name)


def verify_tool():
    manifest = read_json(TOOL_MANIFEST)
    entry = manifest.get("tools", {}).get(TOOL_NAME)
    if not entry or entry.get("version") != TOOL_VERSION:
        raise LegalError(f"dotnet-tools.json must pin {TOOL_NAME} {TOOL_VERSION}")
    result = subprocess.run(
        ["dotnet", "tool", "run", TOOL_NAME, "--", "--version"],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )
    output = (result.stdout + result.stderr).strip()
    if result.returncode != 0 or TOOL_VERSION not in output:
        raise LegalError(
            f"{TOOL_NAME} {TOOL_VERSION} is not installed. Run: dotnet tool restore\n{output}"
        )


def load_bindings():
    if not BINDINGS.is_file():
        return {"schemaVersion": 1, "packageFiles": []}
    return read_json(BINDINGS)


def scenario_environment(scenario):
    environment = os.environ.copy()
    environment["TargetFramework"] = scenario["framework"]
    if scenario.get("runtimeIdentifiers"):
        environment["RuntimeIdentifiers"] = ";".join(scenario["runtimeIdentifiers"])
    elif scenario.get("runtimeIdentifier"):
        environment["RuntimeIdentifier"] = scenario["runtimeIdentifier"]
    if scenario.get("selfContained"):
        environment["SelfContained"] = "true"
    environment.update(profiles.scenario_properties(scenario))
    return environment


def run_nuget_license(scenario):
    work = Path(tempfile.mkdtemp(prefix="mqttprobe-licenses-"))
    try:
        raw = work / "raw.json"
        downloads = work / "downloads"
        downloads.mkdir()
        command = [
            "dotnet",
            "tool",
            "run",
            TOOL_NAME,
            "--",
            "-i",
            scenario["project"],
            "-f",
            scenario["framework"],
            "-t",
            "-o",
            "JsonPretty",
            "-fo",
            str(raw),
            "-d",
            str(downloads),
        ]
        result = subprocess.run(
            command,
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
            env=scenario_environment(scenario),
        )
        if not raw.is_file():
            raise LegalError(
                f"{TOOL_NAME} produced no JSON for {scenario['project']} ({scenario['framework']})\n"
                + result.stdout
                + result.stderr
            )
        data = json.loads(raw.read_text(encoding="utf-8-sig"))
        if not isinstance(data, list):
            raise LegalError(f"{TOOL_NAME} returned a non-array result for {scenario['project']}")
        return data, downloads, result
    except Exception:
        shutil.rmtree(work, ignore_errors=True)
        raise


def payload_files(package_dir, raw):
    found = []
    declared = raw.get("License") or ""
    if declared and ("/" in declared or "\\" in declared):
        candidate = package_dir / declared.replace("\\", "/")
        if candidate.is_file():
            found.append(candidate)
    for path in sorted(package_dir.rglob("*")):
        if not path.is_file() or path.name.lower().endswith((".nupkg", ".nuspec", ".sha512")):
            continue
        relative = path.relative_to(package_dir)
        if len(relative.parts) > 3:
            continue
        if PAYLOAD_NAME.match(path.name):
            if path not in found:
                found.append(path)
    return sorted(found)


def download_for(downloads, package_id, version):
    for path in sorted(downloads.iterdir()):
        if path.name.lower() in (f"{package_id.lower()}__{version}.txt", f"{package_id.lower()}__{version}.html"):
            return path
    return None


def document_title(text, package_id, path):
    first = catalogue.usable_title(next((line for line in text.split("\n") if line.strip()), ""))
    return first or f"{package_id} package licence ({path.name})"


def extract_document(path, package_id):
    text = catalogue.read_document_file(path)
    if len(text.strip()) < 40:
        raise LegalError(f"{path}: extracted licence text is empty")
    return {
        "title": document_title(text, package_id, path),
        "sha256": sha256_text(text),
        "text": text,
    }


def binding_payload(package_dir, package_id, version, bindings):
    payload = {}
    for entry in bindings.get("packageFiles", []):
        if entry["id"].lower() != package_id.lower() or entry["version"] != version:
            continue
        path = package_dir / entry["path"]
        if not path.is_file():
            raise LegalError(
                f"{package_id} {version}: reviewed binding path is missing: {entry['path']}. "
                "Review legal/supplements/bindings.json."
            )
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if digest != entry["sha256"]:
            raise LegalError(
                f"{package_id} {version}: {entry['path']} does not match the reviewed binding hash. "
                "Review legal/supplements/bindings.json."
            )
        payload[entry["path"].replace("\\", "/")] = digest
    return payload


def extract_package(raw, package_dir, downloads, bindings):
    package_id = raw.get("PackageId")
    version = raw.get("PackageVersion")
    if not package_id or not version:
        raise LegalError(f"{TOOL_NAME} entry without PackageId/PackageVersion")
    documents = []
    files = payload_files(package_dir, raw)
    for path in files:
        document = extract_document(path, package_id)
        document["source"] = "embedded"
        document["sourcePath"] = path.relative_to(package_dir).as_posix()
        document["sourceUrl"] = catalogue.canonical_source_url(
            raw.get("LicenseUrl") or raw.get("PackageProjectUrl") or ""
        )
        documents.append(document)

    declared = raw.get("License") or ""
    path_like = "/" in declared or "\\" in declared or declared.lower().endswith((".txt", ".md", ".html"))
    expression = bool(raw.get("LicenseInformationOrigin") == 0 and declared and not path_like)
    has_license_document = any(is_license_document(path) for path in files)

    extractor = None
    download = download_for(downloads, package_id, version)
    if download is not None and not expression and not has_license_document:
        text = catalogue.read_document_file(download)
        extractor = {
            "title": document_title(text, package_id, download),
            "sha256": sha256_text(text),
            "text": text,
            "source": "extractor",
            "sourcePath": download.name,
            "sourceUrl": catalogue.canonical_source_url(
                raw.get("LicenseUrl") or raw.get("PackageProjectUrl") or ""
            ),
        }

    record = {
        "id": package_id,
        "version": version,
        "metadata": {
            "license": catalogue.canonical_source_url(raw.get("License") or ""),
            "licenseUrl": catalogue.canonical_source_url(raw.get("LicenseUrl") or ""),
            "licenseInformationOrigin": int(raw.get("LicenseInformationOrigin") or 0),
            "projectUrl": catalogue.canonical_source_url(raw.get("PackageProjectUrl") or ""),
            "copyright": raw.get("Copyright") or "",
            "authors": raw.get("Authors") or "",
            "validationErrors": raw.get("ValidationErrors") or [],
        },
        "documents": documents,
        "extractor": extractor,
        "payload": binding_payload(package_dir, package_id, version, bindings),
    }
    return record


def profile_packages(profile):
    return {(entry["id"], entry["version"]) for entry in profile["packages"]}


def extract_scenario(scenario, current_profile, reviewed_packages):
    entries, downloads, result = run_nuget_license(scenario)
    if result.returncode != 0:
        raise LegalError(
            f"{TOOL_NAME} exited {result.returncode} for {scenario['name']}\n"
            + result.stdout
            + result.stderr
        )
    audit = []
    for raw in entries:
        if raw.get("ValidationErrors") and not catalogue.reviewed_exception(
            raw.get("PackageId"), raw.get("PackageVersion"), reviewed_packages
        ):
            audit.append(
                f"{raw.get('PackageId')} {raw.get('PackageVersion')}: {raw.get('ValidationErrors')}"
            )
    if audit:
        raise LegalError(
            "nuget-license reported unreviewed licence errors. Add an exact PackageId@Version "
            "entry to legal/supplements/packages.json:\n  " + "\n  ".join(audit)
        )
    assets = profiles.read_assets(scenario)
    folders = package_cache_dirs(assets)
    bindings = load_bindings()
    try:
        records = {}
        for raw in entries:
            package_id = raw.get("PackageId")
            version = raw.get("PackageVersion")
            package_dir = package_directory(package_id, version, folders)
            if package_dir is None:
                raise LegalError(
                    f"{package_id} {version} is not in the NuGet cache; restore {scenario['project']} first"
                )
            records[(package_id, version)] = extract_package(
                raw, package_dir, downloads, bindings
            )
        expected = profile_packages(current_profile)
        actual = set(records)
        if actual != expected:
            missing = sorted(expected - actual)
            extra = sorted(actual - expected)
            raise LegalError(
                f"{scenario['name']}: extracted package set does not match the exported profile. "
                f"Missing: {missing or 'none'}. Extra: {extra or 'none'}."
            )
        return records
    finally:
        shutil.rmtree(downloads.parent, ignore_errors=True)


def read_extracted():
    if not EXTRACTED_MANIFEST.is_file():
        return {}
    data = read_json(EXTRACTED_MANIFEST)
    records = {}
    for record in data["packages"]:
        records[(record["id"], record["version"])] = record
    return records


def write_extracted(records):
    packages = [records[key] for key in sorted(records)]
    referenced = set()
    for record in packages:
        for document in record["documents"]:
            referenced.add(document["sha256"])
        if record.get("extractor"):
            referenced.add(record["extractor"]["sha256"])
    for record in packages:
        for document in record["documents"]:
            text = document.pop("text", None)
            if text is not None:
                write_text(EXTRACTED_TEXTS / f"{document['sha256']}.txt", text)
        if record.get("extractor"):
            text = record["extractor"].pop("text", None)
            if text is not None:
                write_text(EXTRACTED_TEXTS / f"{record['extractor']['sha256']}.txt", text)
    write_json(EXTRACTED_MANIFEST, {"schemaVersion": 1, "packages": packages})
    if EXTRACTED_TEXTS.is_dir():
        for path in EXTRACTED_TEXTS.glob("*.txt"):
            if path.stem not in referenced:
                path.unlink()
