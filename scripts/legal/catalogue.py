"""Generate and verify the offline legal catalogue and the root NOTICES index."""

from __future__ import annotations

import fnmatch
import json
import re

from urllib.parse import urlsplit, urlunsplit

from common import (
    GENERATED,
    PROFILES,
    ROOT,
    SUPPLEMENTS,
    TEXTS,
    LegalError,
    normalize_text,
    read_json,
    relative,
    sha256_bytes,
    sha256_text,
    write_text,
)

HEX64 = re.compile(r"^[0-9a-f]{64}$")
EXTRACTED = ROOT / "legal" / "extracted"
EXTRACTED_MANIFEST = EXTRACTED / "packages.json"
EXTRACTED_TEXTS = EXTRACTED / "texts"
BINDINGS = SUPPLEMENTS / "bindings.json"
HTTPS_METADATA_HOSTS = frozenset({"go.microsoft.com", "www.microsoft.com"})
TEXT_BINDING_PATHS = frozenset(
    {
        "src/MqttProbe.UI/wwwroot/fonts/OpenDyslexic-LICENSE.txt",
        "src/MqttProbe.Core/Models/Sparkplug/SparkplugBProtobuf.cs",
    }
)


def expression_terms(expression):
    terms = re.split(r"\s+(?:AND|OR)\s+", expression.strip(), flags=re.IGNORECASE)
    return [term.strip().strip("()") for term in terms if term.strip()]


def read_document_file(path):
    suffix = path.suffix.lower()
    if suffix in (".html", ".htm"):
        raw = path.read_bytes().decode("utf-8-sig", errors="replace")
        text = normalize_text(raw, html=True)
        if len(text.strip()) < 40:
            raise LegalError(f"{relative(path)}: HTML licence text is empty after extraction")
        return text
    if suffix in ("", ".txt", ".md", ".license"):
        return normalize_text(path.read_bytes())
    raise LegalError(
        f"{relative(path)}: unsupported licence file type {suffix or '(none)'}; "
        "add the text as a supplement instead"
    )


def body_without_heading(text):
    lines = text.split("\n")
    if len(lines) > 2 and lines[0].strip() and re.match(r"^-{3,}$", lines[1]):
        lines = lines[3:]
    return normalize_text("\n".join(lines))


def usable_title(value):
    value = value.strip().strip("*_#` \t")
    lower = value.lower()
    if not value or len(value) > 120 or lower.startswith(("note:", "this ", "copyright", "http")):
        return ""
    if lower.startswith("the ") and not (len(value) < 60 and "license" in lower):
        return ""
    if not any(word in lower for word in ("license", "licence", "notice", "terms", "copyright")):
        return ""
    return value


class Documents:
    def __init__(self):
        self.items = {}
        self.body_index = {}

    def add(self, text, title, source_urls, kind="license"):
        text = normalize_text(text)
        if not text.strip():
            raise LegalError(f"empty licence document: {title}")
        digest = sha256_text(text)
        source_urls = sorted({url for url in source_urls if url})
        if digest in self.items:
            entry = self.items[digest]
            entry["sourceUrls"] = sorted(set(entry["sourceUrls"]) | set(source_urls))
            return digest
        entry = {
            "id": digest,
            "title": title,
            "file": f"{digest}.txt",
            "sha256": digest,
            "sourceUrls": source_urls,
            "kind": kind,
            "_text": text,
        }
        self.items[digest] = entry
        self.body_index[sha256_text(body_without_heading(text))] = digest
        return digest

    def find_body(self, text):
        return self.body_index.get(sha256_text(body_without_heading(text)))

    def catalogue_entries(self):
        return [
            {key: value for key, value in entry.items() if not key.startswith("_")}
            for entry in sorted(self.items.values(), key=lambda item: item["id"])
        ]


def load_supplements(documents):
    manifest = read_json(SUPPLEMENTS / "documents.json")
    keys = {}
    for entry in manifest["documents"]:
        path = SUPPLEMENTS / entry["file"]
        if not path.is_file():
            raise LegalError(f"missing supplement source: {relative(path)}")
        text = path.read_text(encoding="utf-8")
        digest = sha256_text(text)
        if digest != entry["sha256"]:
            raise LegalError(
                f"{relative(path)}: sha256 does not match documents.json "
                f"(expected {entry['sha256']}, got {digest})"
            )
        keys[entry["key"]] = documents.add(
            text,
            entry["title"],
            entry.get("sourceUrls", []),
            entry.get("kind", "license"),
        )
    return keys


def load_overrides():
    data = read_json(SUPPLEMENTS / "packages.json")
    return data.get("packages", {}), data.get("annotations", []), data.get("requiredReview", [])


def override_for(package_id, version, packages, annotations):
    merged = {}
    exact = packages.get(f"{package_id}@{version}")
    if exact:
        for key, value in exact.items():
            if key == "documents":
                merged.setdefault(key, []).extend(value)
            else:
                merged[key] = value
    for rule in annotations:
        if fnmatch.fnmatchcase(package_id, rule["match"]) and rule.get("notes"):
            merged["notes"] = rule["notes"]
    return merged


EXEMPTION_KEYS = ("documents", "license", "applicability")


def reviewed_exception(package_id, version, packages):
    entry = packages.get(f"{package_id}@{version}")
    if entry and any(key in entry for key in EXEMPTION_KEYS):
        return entry
    return None


def canonical_source_url(value):
    parts = urlsplit(value)
    if parts.scheme.lower() == "http" and (parts.hostname or "").lower() in HTTPS_METADATA_HOSTS:
        return urlunsplit(("https", parts.netloc, parts.path, parts.query, parts.fragment))
    return value


def binding_digest(path, relative_path=None):
    data = path.read_bytes()
    relative_path = (relative_path or path.as_posix()).replace("\\", "/")
    if relative_path in TEXT_BINDING_PATHS:
        data = data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return sha256_bytes(data)


def add_extracted_document(document, documents):
    digest = document["sha256"]
    if not HEX64.match(digest):
        raise LegalError(f"extracted document has invalid sha256: {digest}")
    path = EXTRACTED_TEXTS / f"{digest}.txt"
    if not path.is_file():
        raise LegalError(f"missing extracted text: {relative(path)}")
    text = path.read_text(encoding="utf-8")
    if sha256_text(text) != digest:
        raise LegalError(f"extracted text hash mismatch: {relative(path)}")
    if document.get("source") == "extractor":
        lower = text.lower()
        if len(text.strip()) < 200 or not any(word in lower for word in ("license", "licence", "copyright", "permission")):
            raise LegalError(
                f"{document.get('sourcePath', digest)}: extractor text does not contain licence content. "
                "Add a reviewed supplement document instead."
            )
    matched = documents.find_body(text)
    if matched:
        return matched
    source_urls = [canonical_source_url(document.get("sourceUrl", ""))]
    return documents.add(text, document["title"], source_urls, "notice" if "notice" in document["title"].lower() else "license")


def load_extracted_package(record, documents, expression_docs, supplement_keys, packages, annotations, required_review):
    package_id = record["id"]
    version = record["version"]
    if any(fnmatch.fnmatchcase(package_id, pattern) for pattern in required_review) and f"{package_id}@{version}" not in packages:
        raise LegalError(
            f"{package_id} {version}: this package family requires an exact reviewed entry in "
            "legal/supplements/packages.json. Review the new version before accepting it."
        )
    metadata = record["metadata"]
    overrides = override_for(package_id, version, packages, annotations)
    declared = metadata.get("license") or ""
    path_like = "/" in declared or "\\" in declared or declared.lower().endswith((".txt", ".md", ".html"))
    expression = declared if metadata.get("licenseInformationOrigin") == 0 and not path_like else ""

    document_ids = []
    if expression:
        for term in expression_terms(expression):
            if term not in expression_docs:
                raise LegalError(
                    f"{package_id} {version}: no reviewed supplement for licence term {term!r}. "
                    f"Add it to {relative(SUPPLEMENTS / 'expressions.json')}."
                )
            document_ids.extend(expression_docs[term])
    for document in record.get("documents", []):
        document_ids.append(add_extracted_document(document, documents))
    override_documents = overrides.get("documents", [])
    for key in override_documents:
        if key not in supplement_keys:
            raise LegalError(f"{package_id} {version}: unknown supplement document {key!r}")
        document_ids.append(supplement_keys[key])
    if record.get("extractor") and not expression and not override_documents:
        document_ids.append(add_extracted_document(record["extractor"], documents))

    applicability = overrides.get("applicability", "runtime")
    if not document_ids and applicability != "build":
        raise LegalError(
            f"{package_id} {version}: no licence text in legal/extracted. "
            "Re-run refresh, or add a reviewed entry to legal/supplements/packages.json."
        )

    copyright_value = overrides.get("copyright", metadata.get("copyright", ""))
    if "://" in copyright_value and copyright_value.lower().startswith("see "):
        copyright_value = ""
    component = {
        "id": f"nuget:{package_id.lower()}@{version}",
        "name": package_id,
        "version": version,
        "license": expression or overrides.get("license") or "See licence documents",
        "copyright": copyright_value,
        "sourceUrl": canonical_source_url(
            overrides.get("sourceUrl")
            or metadata.get("projectUrl")
            or f"https://www.nuget.org/packages/{package_id}/{version}"
        ),
        "documentIds": sorted(set(document_ids)),
        "applicability": applicability,
    }
    if overrides.get("notes"):
        component["notes"] = overrides["notes"]
    if not expression and metadata.get("licenseUrl"):
        component["licenseSourceUrl"] = canonical_source_url(metadata["licenseUrl"])
    return component


def load_non_nuget(supplement_keys):
    data = read_json(SUPPLEMENTS / "components.json")
    components = []
    for entry in data["components"]:
        document_ids = []
        for key in entry["documents"]:
            if key not in supplement_keys:
                raise LegalError(f"non-NuGet component {entry['id']}: unknown document {key!r}")
            document_ids.append(supplement_keys[key])
        components.append(
            {
                "id": entry["id"],
                "name": entry["name"],
                "version": entry["version"],
                "license": entry["license"],
                "copyright": entry.get("copyright", ""),
                "sourceUrl": entry.get("sourceUrl", ""),
                "documentIds": sorted(set(document_ids)),
                "applicability": entry.get("applicability", "runtime"),
            }
        )
        if entry.get("notes"):
            components[-1]["notes"] = entry["notes"]
    return components


def load_extracted_records():
    data = read_json(EXTRACTED_MANIFEST)
    if data.get("schemaVersion") != 1:
        raise LegalError("legal/extracted/packages.json schemaVersion must be 1")
    records = {}
    for record in data["packages"]:
        key = (record["id"], record["version"])
        if key in records:
            raise LegalError(f"duplicate extracted package: {record['id']} {record['version']}")
        records[key] = record
    return records


def validate_bindings(catalogue):
    if not BINDINGS.is_file():
        return
    data = read_json(BINDINGS)
    components = {component["id"]: component for component in catalogue["components"]}
    global_pin = data.get("global", {})
    sdk = read_json(ROOT / "global.json")["sdk"]
    if global_pin.get("sdkVersion") != sdk.get("version"):
        raise LegalError(
            "legal/supplements/bindings.json sdkVersion does not match global.json. "
            "Review the runtime and SDK licence bindings."
        )
    if global_pin.get("workloadVersion") != sdk.get("workloadVersion"):
        raise LegalError(
            "legal/supplements/bindings.json workloadVersion does not match global.json. "
            "Review the runtime and SDK licence bindings."
        )
    for component_id in global_pin.get("components", []):
        if component_id not in components:
            raise LegalError(f"bindings.json references missing component: {component_id}")
    for entry in data.get("repoFiles", []):
        path = ROOT / entry["path"]
        if not path.is_file():
            raise LegalError(f"reviewed binding source is missing: {entry['path']}")
        if binding_digest(path, entry["path"]) != entry["sha256"]:
            raise LegalError(
                f"{entry['path']} changed since the licence binding was reviewed. "
                "Review legal/supplements/bindings.json and the affected component text."
            )
        if entry["component"] not in components:
            raise LegalError(f"bindings.json references missing component: {entry['component']}")
    for entry in data.get("packageFiles", []):
        record = next(
            (
                item
                for item in catalogue.get("_records", {}).values()
                if item["id"].lower() == entry["id"].lower() and item["version"] == entry["version"]
            ),
            None,
        )
        if record is None:
            raise LegalError(
                f"reviewed package binding {entry['id']} {entry['version']} is absent from legal/extracted. "
                "Review legal/supplements/bindings.json after the dependency update."
            )
        digest = record.get("payload", {}).get(entry["path"].replace("\\", "/"))
        if digest != entry["sha256"]:
            raise LegalError(
                f"{entry['id']} {entry['version']} payload {entry['path']} does not match the reviewed binding. "
                "Review legal/supplements/bindings.json."
            )


def build_catalogue():
    profiles = [read_json(path) for path in sorted(PROFILES.glob("*.json"))]
    if not profiles:
        raise LegalError("no committed profiles in legal/profiles")
    identities = {}
    for profile in profiles:
        for entry in profile["packages"]:
            key = (entry["id"], entry["version"])
            previous = identities.get(key)
            if previous and previous != entry["sha512"]:
                raise LegalError(f"profiles disagree on sha512 for {entry['id']} {entry['version']}")
            identities[key] = entry["sha512"]
    records = load_extracted_records()
    if set(records) != set(identities):
        missing = sorted(set(identities) - set(records))
        extra = sorted(set(records) - set(identities))
        raise LegalError(
            "legal/extracted does not match the committed profiles. "
            f"Missing: {missing or 'none'}. Extra: {extra or 'none'}. "
            "Run: python scripts/legal/notices.py refresh"
        )

    documents = Documents()
    supplement_keys = load_supplements(documents)
    license_map = read_json(SUPPLEMENTS / "expressions.json")["licenses"]
    expression_docs = {}
    for name, keys in license_map.items():
        resolved = []
        for key in keys:
            if key not in supplement_keys:
                raise LegalError(f"expression {name}: unknown document {key!r}")
            resolved.append(supplement_keys[key])
        expression_docs[name] = resolved
    packages, annotations, required_review = load_overrides()
    components = []
    for identity in sorted(records):
        component = load_extracted_package(
            records[identity], documents, expression_docs, supplement_keys, packages, annotations, required_review
        )
        components.append(component)
    components.extend(load_non_nuget(supplement_keys))
    components.sort(key=lambda item: item["id"])

    catalogue = {
        "schemaVersion": 1,
        "components": components,
        "documents": documents.catalogue_entries(),
        "provenance": (SUPPLEMENTS / "provenance.txt").read_text(encoding="utf-8").strip(),
        "_records": records,
    }
    validate_catalogue(catalogue)
    validate_bindings(catalogue)
    catalogue.pop("_records")
    return catalogue, documents


def validate_catalogue(catalogue):
    if catalogue.get("schemaVersion") != 1:
        raise LegalError("catalogue schemaVersion must be 1")
    document_ids = set()
    for document in catalogue["documents"]:
        for field in ("id", "title", "file", "sha256", "sourceUrls"):
            if field not in document:
                raise LegalError(f"document missing {field}: {document.get('id')}")
        if not HEX64.match(document["id"]) or document["sha256"] != document["id"]:
            raise LegalError(f"document id is not a sha256: {document.get('id')}")
        if document["file"] != f"{document['id']}.txt":
            raise LegalError(f"document file does not match id: {document['file']}")
        if not document["title"] or not isinstance(document["sourceUrls"], list):
            raise LegalError(f"document has invalid title or sourceUrls: {document['id']}")
        if document["sourceUrls"] != sorted(set(document["sourceUrls"])):
            raise LegalError(f"document sourceUrls are not sorted: {document['id']}")
        document_ids.add(document["id"])
    seen = set()
    for component in catalogue["components"]:
        for field in ("id", "name", "version", "license", "copyright", "sourceUrl", "documentIds"):
            if field not in component:
                raise LegalError(f"component missing {field}: {component.get('id')}")
        if component["id"] in seen:
            raise LegalError(f"duplicate component id: {component['id']}")
        seen.add(component["id"])
        if component["documentIds"] != sorted(set(component["documentIds"])):
            raise LegalError(f"component documentIds are not sorted: {component['id']}")
        for document_id in component["documentIds"]:
            if document_id not in document_ids:
                raise LegalError(f"component {component['id']} references missing document {document_id}")
        if component.get("applicability") == "build" and not component["documentIds"]:
            continue
        if not component["documentIds"]:
            raise LegalError(f"component {component['id']} has no offline licence text")


def render_notices(catalogue):
    runtime = [item for item in catalogue["components"] if item.get("applicability") != "build"]
    build = [item for item in catalogue["components"] if item.get("applicability") == "build"]
    titles = {document["id"]: document["title"] for document in catalogue["documents"]}
    lines = [
        "MQTT Probe",
        "Copyright 2026 Bluegrass IoT",
        "Licensed under the Apache License, Version 2.0; see LICENSE.",
        "",
        "THIRD-PARTY NOTICES",
        "",
        "This file is generated by scripts/legal/notices.py. Do not edit it by hand.",
        "It is the component index for the offline legal catalogue embedded in the",
        "application. Full licence and notice texts live in legal/generated/texts",
        "and are shown in the app without network access.",
        "",
        "Regenerate: python scripts/legal/notices.py generate",
        "Verify:     python scripts/legal/notices.py check",
        "",
        "=" * 78,
        "",
        "1. Shipped components",
        "",
    ]
    if not runtime:
        lines.append("  None.")
    for component in runtime:
        lines.append(f"  {component['name']} {component['version']}")
        lines.append(f"    Licence: {component['license']}")
        if component["copyright"]:
            lines.append(f"    Copyright: {component['copyright']}")
        if component["sourceUrl"]:
            lines.append(f"    Source: {component['sourceUrl']}")
        lines.append("    Texts: " + "; ".join(titles[item] for item in component["documentIds"]))
        if component.get("notes"):
            lines.append(f"    Note: {component['notes']}")
        lines.append("")
    lines.extend(["=" * 78, "", "2. Build-only components", "", "Not included in a shipped build. Listed so the set is auditable.", ""])
    if not build:
        lines.append("  None.")
    for component in build:
        lines.append(f"  {component['name']} {component['version']}")
        lines.append(f"    Licence: {component['license']}")
        lines.append(f"    Source: {component['sourceUrl']}")
    build_only = SUPPLEMENTS / "build-only.txt"
    if build_only.is_file():
        lines.extend(["", build_only.read_text(encoding="utf-8").strip()])
    lines.extend(["", "=" * 78, "", catalogue["provenance"].strip(), ""])
    return "\n".join(lines)


def render():
    catalogue, documents = build_catalogue()
    files = {GENERATED / "catalogue.json": json.dumps(catalogue, indent=2, ensure_ascii=False) + "\n"}
    files[ROOT / "NOTICES"] = render_notices(catalogue)
    for document in documents.items.values():
        files[TEXTS / document["file"]] = document["_text"]
    return files


def generate():
    files = render()
    for path, text in files.items():
        write_text(path, text)
    expected = {path.name for path in files if path.parent == TEXTS}
    for path in TEXTS.glob("*.txt") if TEXTS.is_dir() else []:
        if path.name not in expected:
            path.unlink()
    print(f"wrote {relative(GENERATED / 'catalogue.json')}, NOTICES and {len(expected)} texts")
    return 0


def check():
    files = render()
    problems = []
    for path, expected in files.items():
        if not path.is_file():
            problems.append(f"missing generated file: {relative(path)}")
        elif path.read_text(encoding="utf-8") != expected:
            problems.append(f"stale generated file: {relative(path)}")
    if TEXTS.is_dir():
        for path in sorted(TEXTS.glob("*")):
            if path not in files:
                problems.append(f"unexpected generated text: {relative(path)}")
    if problems:
        raise LegalError("\n".join(problems) + "\nRun: python scripts/legal/notices.py generate")
    print(f"legal catalogue is current ({len(files)} files)")
    return 0


def verify(profile_name):
    profile = read_json(PROFILES / f"{profile_name}.json")
    catalogue = read_json(GENERATED / "catalogue.json")
    validate_catalogue(catalogue)
    records = load_extracted_records()
    components = {item["id"]: item for item in catalogue["components"]}
    problems = []
    for package in profile["packages"]:
        key = (package["id"], package["version"])
        if key not in records:
            problems.append(f"legal/extracted has no record for {package['id']} {package['version']}")
        component_id = f"nuget:{package['id'].lower()}@{package['version']}"
        component = components.get(component_id)
        if component is None:
            problems.append(f"catalogue has no component for {package['id']} {package['version']}")
            continue
        if component["version"] != package["version"]:
            problems.append(f"catalogue version mismatch for {package['id']}")
        if component.get("applicability") != "build" and not component["documentIds"]:
            problems.append(f"component has no offline text: {component_id}")
        if not component["license"]:
            problems.append(f"component has no licence value: {component_id}")
    for document in catalogue["documents"]:
        path = TEXTS / document["file"]
        if not path.is_file():
            problems.append(f"missing generated text: {relative(path)}")
        elif sha256_text(path.read_text(encoding="utf-8")) != document["id"]:
            problems.append(f"generated text hash mismatch: {relative(path)}")
    catalogue["_records"] = records
    try:
        validate_bindings(catalogue)
    except LegalError as error:
        problems.append(str(error))
    if problems:
        raise LegalError("\n".join(problems) + "\nRun: python scripts/legal/notices.py refresh")
    print(f"{profile_name}: {len(profile['packages'])} packages covered by the catalogue")
    return 0


def main(argv=None):
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("generate", "check", "verify"))
    parser.add_argument("--scenario")
    args = parser.parse_args(argv)
    if args.action == "verify":
        if not args.scenario:
            parser.error("verify requires --scenario")
        return verify(args.scenario)
    return generate() if args.action == "generate" else check()


if __name__ == "__main__":
    raise SystemExit(main())
