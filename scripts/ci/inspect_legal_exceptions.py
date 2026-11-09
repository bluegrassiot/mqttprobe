"""Narrow DevSkim DS173237 exception for public integrity hashes in legal data.

These files are generated from reviewed inputs and carry only public digests:
licence-text SHA-256 ids, extracted-package and payload hashes, NuGet SHA-512
digests, repo-file snapshots, and submodule commit pins. The exception matches
one schema field and value occurrence, at the exact columns DevSkim reports, and
checks the digest against the bytes it claims to describe wherever those bytes
are in this repository.

Supported files and fields:
  legal/generated/catalogue.json      documents[].id/.sha256, documentIds[]
  legal/extracted/packages.json       documents[].sha256, extractor.sha256, payload values
  legal/supplements/documents.json    documents[].sha256
  legal/supplements/bindings.json     repoFiles[].sha256, packageFiles[].sha256
  legal/profiles/<name>.json          inputs.files values, inputs.submodules pins,
                                      packages[].sha512

Every digest-shaped value in a supported file must appear in a supported field,
exactly as often as the schema declares it; otherwise the whole file blocks.
Payload, package-file, and package SHA-512 values are shape-checked only: their
bytes are not in the repository. Paths are resolved and confined to the
repository (and to their text directory) before any read, so symlinks and
Windows drive or UNC syntax cannot escape.
"""

import base64
import collections
import hashlib
import json
import os
import re
import subprocess
from pathlib import PurePosixPath

RULE = "DS173237"
HEX40 = re.compile(r"\A[0-9a-f]{40}\Z")
HEX64 = re.compile(r"\A[0-9a-f]{64}\Z")
SHA512 = re.compile(r"\A[A-Za-z0-9+/]{86}==\Z")
DIGEST_VALUE = re.compile(r"\A(?:[0-9a-f]{40}|[0-9a-f]{64}|[A-Za-z0-9+/]{86}==)\Z")
PROFILE_NAME = re.compile(r"\A[a-z0-9][a-z0-9-]*\Z")
PROPERTY_LINE = re.compile(r'^(?P<indent>\s*)"(?P<key>[^"\\]*)"\s*:\s*"(?P<value>[^"\\]*)"\s*,?\s*$')
ITEM_LINE = re.compile(r'^(?P<indent>\s*)"(?P<value>[^"\\]*)"\s*,?\s*$')

CATALOGUE = "legal/generated/catalogue.json"
EXTRACTED = "legal/extracted/packages.json"
DOCUMENTS = "legal/supplements/documents.json"
BINDINGS = "legal/supplements/bindings.json"


class SchemaError(ValueError):
    pass


def is_legal_hash_exception(finding, root, cache=None):
    if finding.get("rule") != RULE:
        return False
    file = normalise_file(finding.get("file"))
    if file is None:
        return False
    handler = _handler(file)
    if handler is None:
        return False
    line = finding.get("line")
    start, end = finding.get("startColumn"), finding.get("endColumn")
    if not all(isinstance(value, int) for value in (line, start, end)):
        return False
    if line < 1 or start < 0 or end <= start:
        return False
    try:
        spans = _spans_for(root, file, handler, cache)
        lines = _checked(root, ".", file).read_text(encoding="utf-8").splitlines()
    except (OSError, ValueError, TypeError, subprocess.SubprocessError):
        return False
    if line > len(lines) or (start, end) not in spans.get(line, ()):
        return False
    snippet = finding.get("snippet")
    if snippet:
        return lines[line - 1][start:end] == snippet
    return True


def _spans_for(root, file, handler, cache):
    if cache is None:
        return _approve(root, file, handler)
    key = (str(root), file)
    if key not in cache:
        try:
            cache[key] = _approve(root, file, handler)
        except (OSError, ValueError, TypeError, subprocess.SubprocessError):
            cache[key] = None
    spans = cache[key]
    if spans is None:
        raise SchemaError("file failed validation")
    return spans


def _approve(root, file, handler):
    properties, items = handler(root, file)
    lines = _checked(root, ".", file).read_text(encoding="utf-8").splitlines()
    return _digest_spans(lines, properties, items)


def normalise_file(value):
    if not isinstance(value, str) or not value:
        return None
    text = value.replace("\\", "/")
    if text.startswith("/") or re.match(r"\A[A-Za-z]:", text):
        return None
    path = PurePosixPath(text)
    if any(part in ("..", ".") or ":" in part for part in path.parts):
        return None
    return str(path)


def _handler(file):
    if file == CATALOGUE:
        return _catalogue
    if file == EXTRACTED:
        return _extracted
    if file == DOCUMENTS:
        return _documents
    if file == BINDINGS:
        return _bindings
    parts = PurePosixPath(file).parts
    if len(parts) == 3 and parts[:2] == ("legal", "profiles") and parts[2].endswith(".json"):
        if PROFILE_NAME.fullmatch(parts[2].removesuffix(".json")):
            return _profile
    return None


def _is_within(path, base):
    try:
        path.relative_to(base)
        return True
    except ValueError:
        return False


def _checked(root, base, relative):
    root_path = root.resolve()
    base_path = (root_path / base).resolve()
    if not _is_within(base_path, root_path):
        raise SchemaError("base directory escapes the repository")
    path = (base_path / relative).resolve()
    if not _is_within(path, base_path) or not _is_within(path, root_path):
        raise SchemaError("path escapes its directory")
    return path


def _read_json(root, file):
    return json.loads(_checked(root, ".", file).read_text(encoding="utf-8"))


def _keys(record, required, optional=()):
    if not isinstance(record, dict):
        raise SchemaError("record is not an object")
    missing = set(required) - set(record)
    unknown = set(record) - set(required) - set(optional)
    if missing:
        raise SchemaError(f"missing fields: {sorted(missing)}")
    if unknown:
        raise SchemaError(f"unsupported fields: {sorted(unknown)}")


def _require(condition, message):
    if not condition:
        raise SchemaError(message)


def _safe_relative(value):
    if not isinstance(value, str) or not value:
        return False
    text = value.replace("\\", "/")
    if text.startswith("/") or re.match(r"\A[A-Za-z]:", text):
        return False
    path = PurePosixPath(text)
    return all(part not in ("..", ".") and ":" not in part for part in path.parts)


def _hex64(value):
    return isinstance(value, str) and HEX64.match(value) is not None


def _sha512_value(value):
    return isinstance(value, str) and SHA512.match(value) is not None


def _text_digest(path):
    text = path.read_bytes().decode("utf-8-sig", errors="replace").replace("\ufeff", "")
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = "\n".join(line.rstrip() for line in text.split("\n")).strip("\n")
    return hashlib.sha256((text + "\n" if text else "").encode("utf-8")).hexdigest()


def _content_matches(path, digest):
    """Raw bytes, CRLF->LF bytes, or fully normalised text of the same file."""
    raw = path.read_bytes()
    lf = raw.replace(b"\r\n", b"\n")
    text = raw.decode("utf-8-sig", errors="replace").replace("\ufeff", "")
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = "\n".join(line.rstrip() for line in text.split("\n")).strip("\n")
    return digest in {
        hashlib.sha256(raw).hexdigest(),
        hashlib.sha256(lf).hexdigest(),
        hashlib.sha256((text + "\n" if text else "").encode("utf-8")).hexdigest(),
    }


def _submodule_head(path):
    # Git exports an absolute GIT_DIR to hooks, which would make -C resolve the
    # parent repository instead of the submodule checkout.
    environment = {
        key: value
        for key, value in os.environ.items()
        if key not in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY")
    }
    result = subprocess.run(
        ["git", "-C", str(path), "rev-parse", "HEAD"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        check=False, timeout=30, env=environment,
    )
    if result.returncode != 0:
        raise SchemaError("cannot resolve submodule commit")
    return result.stdout.strip()


def _digest_spans(lines, properties, items):
    """Map each approved digest line to the columns of its quoted value token.

    Every digest-shaped value in the file must occupy a supported field, exactly
    as often as the schema declares it. Anything extra (an unknown key, a
    duplicated property, a stray array item) blocks the whole file.
    """
    seen_properties = collections.Counter()
    seen_items = collections.Counter()
    spans = collections.defaultdict(set)
    for number, line in enumerate(lines, 1):
        prop = PROPERTY_LINE.match(line)
        if prop is not None and DIGEST_VALUE.match(prop.group("value")):
            seen_properties[(prop.group("key"), prop.group("value"))] += 1
            spans[number].add((prop.start("value") - 1, prop.end("value") + 1))
            continue
        item = ITEM_LINE.match(line)
        if item is not None and DIGEST_VALUE.match(item.group("value")):
            seen_items[item.group("value")] += 1
            spans[number].add((item.start("value") - 1, item.end("value") + 1))
    if seen_properties != properties or seen_items != items:
        raise SchemaError("file does not match the approved digest positions")
    return spans


def _catalogue(root, file):
    data = _read_json(root, file)
    if not isinstance(data, dict):
        raise SchemaError("catalogue is not an object")
    _keys(data, {"schemaVersion", "components", "documents"}, {"provenance", "generatedAt"})
    _require(data["schemaVersion"] == 1, "unsupported catalogue schema")
    docs, components = data["documents"], data["components"]
    if not isinstance(docs, list) or not isinstance(components, list):
        raise SchemaError("catalogue lists are malformed")

    properties = collections.Counter()
    items = collections.Counter()
    doc_ids = set()
    for document in docs:
        _keys(document, {"id", "title", "file", "sha256", "sourceUrls"}, {"kind"})
        doc_id = document["id"]
        _require(_hex64(doc_id), "document id is not a sha256")
        _require(document["sha256"] == doc_id, "document sha256 differs from its id")
        _require(document["file"] == f"{doc_id}.txt", "document file does not match its id")
        text = _checked(root, "legal/generated/texts", document["file"])
        _require(_text_digest(text) == doc_id, "generated text does not match its id")
        doc_ids.add(doc_id)
        properties[("id", doc_id)] += 1
        properties[("sha256", doc_id)] += 1

    for component in components:
        _keys(
            component,
            {"id", "name", "version", "license", "documentIds"},
            {"copyright", "sourceUrl", "applicability", "notes", "licenseSourceUrl"},
        )
        references = component["documentIds"]
        if not isinstance(references, list):
            raise SchemaError("documentIds is not a list")
        for reference in references:
            _require(reference in doc_ids, "component references an unknown document")
            items[reference] += 1
    return properties, items


def _extracted(root, file):
    data = _read_json(root, file)
    _keys(data, {"schemaVersion", "packages"})
    _require(data["schemaVersion"] == 1, "unsupported extracted schema")
    _require(isinstance(data["packages"], list), "packages is not a list")

    properties = collections.Counter()
    for package in data["packages"]:
        _keys(package, {"id", "version", "documents", "extractor"}, {"metadata", "payload"})
        if "metadata" in package:
            _require(isinstance(package["metadata"], dict), "metadata is not an object")
        if not isinstance(package["documents"], list):
            raise SchemaError("documents is not a list")
        if package["extractor"] is not None and not isinstance(package["extractor"], dict):
            raise SchemaError("extractor is not an object or null")

        for record in [*package["documents"], package["extractor"]]:
            if record is None:
                continue
            _keys(record, {"title", "sha256", "source", "sourcePath", "sourceUrl"})
            digest = record["sha256"]
            _require(_hex64(digest), "extracted digest is not a sha256")
            text = _checked(root, "legal/extracted/texts", f"{digest}.txt")
            _require(_text_digest(text) == digest, "extracted text does not match its digest")
            properties[("sha256", digest)] += 1

        payload = package.get("payload") or {}
        if not isinstance(payload, dict):
            raise SchemaError("payload is not an object")
        for relative, digest in payload.items():
            _require(_hex64(digest), "payload digest is not a sha256")
            _require(_safe_relative(relative), "payload path is not relative")
            properties[(relative, digest)] += 1
    return properties, collections.Counter()


def _documents(root, file):
    data = _read_json(root, file)
    _keys(data, {"schemaVersion", "documents"})
    _require(data["schemaVersion"] == 1, "unsupported supplements schema")
    _require(isinstance(data["documents"], list), "documents is not a list")

    properties = collections.Counter()
    for document in data["documents"]:
        _keys(document, {"key", "title", "file", "sha256", "sourceUrls"}, {"kind"})
        digest = document["sha256"]
        _require(_hex64(digest), "supplement digest is not a sha256")
        _require(_safe_relative(document["file"]), "supplement file is not relative")
        text = _checked(root, "legal/supplements", document["file"])
        _require(_text_digest(text) == digest, "supplement text does not match its digest")
        properties[("sha256", digest)] += 1
    return properties, collections.Counter()


def _bindings(root, file):
    data = _read_json(root, file)
    _keys(data, {"schemaVersion", "repoFiles", "packageFiles"}, {"global"})
    _require(data["schemaVersion"] == 1, "unsupported bindings schema")
    if not isinstance(data["repoFiles"], list) or not isinstance(data["packageFiles"], list):
        raise SchemaError("bindings lists are malformed")

    properties = collections.Counter()
    for entry in data["repoFiles"]:
        _keys(entry, {"component", "path", "sha256"})
        digest = entry["sha256"]
        _require(_hex64(digest), "repo file digest is not a sha256")
        _require(_safe_relative(entry["path"]), "repo file path is not relative")
        _require(_content_matches(_checked(root, ".", entry["path"]), digest),
                 "repo file does not match its digest")
        properties[("sha256", digest)] += 1

    for entry in data["packageFiles"]:
        _keys(entry, {"component", "id", "version", "path", "sha256"})
        _require(_hex64(entry["sha256"]), "package file digest is not a sha256")
        _require(_safe_relative(entry["path"]), "package file path is not relative")
        properties[("sha256", entry["sha256"])] += 1
    return properties, collections.Counter()


def _profile(root, file):
    name = PurePosixPath(file).stem
    data = _read_json(root, file)
    _keys(
        data,
        {"schemaVersion", "name", "inputs", "packages"},
        {
            "host", "project", "framework", "runtimeIdentifier",
            "runtimeIdentifiers", "selfContained", "targets",
        },
    )
    _require(data["schemaVersion"] == 1, "unsupported profile schema")
    _require(data["name"] == name, "profile name does not match its file")
    _keys(data["inputs"], {"files", "submodules", "sdk"})
    _require(isinstance(data["inputs"]["files"], dict), "profile inputs are malformed")
    _require(isinstance(data["inputs"]["submodules"], dict), "profile submodules are malformed")
    _require(isinstance(data["packages"], list), "profile packages are malformed")

    properties = collections.Counter()
    for relative, digest in data["inputs"]["files"].items():
        _require(_hex64(digest), "input digest is not a sha256")
        _require(_safe_relative(relative), "input path is not relative")
        _require(_content_matches(_checked(root, ".", relative), digest),
                 "input file does not match its digest")
        properties[(relative, digest)] += 1

    for relative, commit in data["inputs"]["submodules"].items():
        _require(_safe_relative(relative), "submodule path is not relative")
        _require(isinstance(commit, str) and HEX40.match(commit) is not None,
                 "submodule pin is not a commit id")
        _require(_submodule_head(_checked(root, ".", relative)) == commit,
                 "submodule commit does not match its pin")
        properties[(relative, commit)] += 1

    for package in data["packages"]:
        _keys(package, {"id", "version", "sha512"})
        _require(_sha512_value(package["sha512"]), "package digest is not a sha512")
        properties[("sha512", package["sha512"])] += 1
    return properties, collections.Counter()
