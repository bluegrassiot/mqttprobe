#!/usr/bin/env python3
"""Tests for the narrow legal-data DevSkim exception.

Findings mimic DevSkim SARIF: 0-based half-open columns over the quoted value
token, and a snippet equal to the source slice. Run:
  python -m unittest discover -s scripts/ci/tests
"""

import base64
import hashlib
import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location(
    "ci_inspect_legal_exceptions",
    Path(__file__).resolve().parents[1] / "inspect_legal_exceptions.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/inspect_legal_exceptions.py")
legal = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(legal)

RULE = "DS173237"
CATALOGUE = legal.CATALOGUE
EXTRACTED = legal.EXTRACTED
DOCUMENTS = legal.DOCUMENTS
BINDINGS = legal.BINDINGS
PROFILE = "legal/profiles/web-win-x64.json"


def sha256(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def finding(line, rule=RULE, file=CATALOGUE, start=0, end=1, snippet=None):
    return {
        "rule": rule, "file": file, "line": line, "level": "error", "message": "",
        "startColumn": start, "endColumn": end, "snippet": snippet,
    }


class Fixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def write_json(self, relative, data):
        return self.write(relative, json.dumps(data, indent=2) + "\n")

    def lines(self, relative):
        return (self.root / relative).read_text(encoding="utf-8").splitlines()

    def at(self, relative, token, rule=RULE, occurrence=0, snippet=None):
        hits = [(number, line.index(token)) for number, line in enumerate(
            self.lines(relative), 1) if token in line]
        self.assertGreater(len(hits), occurrence, f"{token!r} not found in {relative}")
        number, start = hits[occurrence]
        return finding(number, rule, relative, start, start + len(token),
                       token if snippet is None else snippet)

    def text_file(self, directory, text):
        digest = sha256(text)
        self.write(f"{directory}/{digest}.txt", text)
        return digest

    def catalogue(self, text="Alpha licence text.\n", component_extra=None):
        digest = self.text_file("legal/generated/texts", text)
        document = {
            "id": digest, "sha256": digest, "file": f"{digest}.txt",
            "title": "Alpha licence", "sourceUrls": ["https://example.com/alpha"],
        }
        component = {
            "id": "alpha", "name": "Alpha", "version": "1.0.0", "license": "MIT",
            "documentIds": [digest],
        }
        component.update(component_extra or {})
        self.write_json(CATALOGUE, {
            "schemaVersion": 1, "components": [component], "documents": [document],
        })
        return digest

    def package_record(self, digest, title):
        return {"title": title, "sha256": digest, "source": "embedded",
                "sourcePath": "LICENSE", "sourceUrl": "https://example.com/alpha"}

    def extracted(self, payload=None, null_extractor=False):
        digest = self.text_file("legal/extracted/texts", "Extracted alpha licence.\n")
        extractor_digest = self.text_file("legal/extracted/texts", "Extractor notice.\n")
        package = {
            "id": "Alpha", "version": "1.0.0",
            "documents": [self.package_record(digest, "Alpha licence")],
            "extractor": (None if null_extractor else
                          self.package_record(extractor_digest, "Alpha extractor")),
        }
        if payload is not None:
            package["payload"] = payload
        self.write_json(EXTRACTED, {"schemaVersion": 1, "packages": [package]})
        return digest, extractor_digest

    def profile(self, name="web-win-x64", input_text="<Project />\n"):
        input_digest = sha256(input_text)
        self.write("global.json", input_text)
        sha512 = base64.b64encode(b"x" * 64).decode("ascii")
        self.write_json(f"legal/profiles/{name}.json", {
            "schemaVersion": 1, "name": name, "host": "any",
            "project": "src/MqttProbe.Web/MqttProbe.Web.csproj", "framework": "net10.0",
            "runtimeIdentifier": None, "runtimeIdentifiers": None, "selfContained": False,
            "targets": ["net10.0"],
            "inputs": {"files": {"global.json": input_digest}, "submodules": {},
                       "sdk": "10.0.403"},
            "packages": [{"id": "Alpha", "version": "1.0.0", "sha512": sha512}],
        })
        return input_digest, sha512

    def git_repo(self, relative, content="submodule\n"):
        path = self.root / relative
        path.mkdir(parents=True, exist_ok=True)
        self.write(f"{relative}/file.txt", content)
        env = {"GIT_AUTHOR_NAME": "T", "GIT_AUTHOR_EMAIL": "t@t",
               "GIT_COMMITTER_NAME": "T", "GIT_COMMITTER_EMAIL": "t@t"}
        saved = {key: os.environ.get(key) for key in env}
        os.environ.update(env)
        try:
            for command in (["git", "init", "-q"], ["git", "add", "."],
                            ["git", "commit", "-qm", "init"]):
                result = __import__("subprocess").run(
                    command, cwd=path, capture_output=True, check=False)
                self.assertEqual(result.returncode, 0, result.stderr)
        finally:
            for key, value in saved.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value
        head = __import__("subprocess").run(
            ["git", "-C", str(path), "rev-parse", "HEAD"],
            capture_output=True, text=True, check=True).stdout.strip()
        return head


class TestCatalogue(Fixture):
    def test_document_id_and_reference_are_accepted(self):
        digest = self.catalogue()
        for occurrence in range(3):
            self.assertTrue(legal.is_legal_hash_exception(
                self.at(CATALOGUE, f'"{digest}"', occurrence=occurrence), self.root))

    def test_public_hash_under_another_field_blocks(self):
        other = "d" * 64
        self.catalogue(component_extra={"copyright": other})
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, f'"{other}"'), self.root))

    def test_token_sharing_a_line_with_the_value_blocks(self):
        digest = self.catalogue()
        item = self.at(CATALOGUE, f'"{digest}"', occurrence=2)
        self.assertTrue(legal.is_legal_hash_exception(item, self.root))
        wide = dict(item, startColumn=item["startColumn"] - 12,
                    snippet=None)
        self.assertFalse(legal.is_legal_hash_exception(wide, self.root))

    def test_column_boundaries_must_match_the_value_token(self):
        digest = self.catalogue()
        exact = self.at(CATALOGUE, f'"{digest}"')
        self.assertTrue(legal.is_legal_hash_exception(exact, self.root))
        for shift in (-1, 1):
            for field in ("startColumn", "endColumn"):
                moved = dict(exact, **{field: exact[field] + shift}, snippet=None)
                self.assertFalse(legal.is_legal_hash_exception(moved, self.root))
        self.assertFalse(legal.is_legal_hash_exception(
            dict(exact, startColumn=exact["startColumn"] + 1,
                 endColumn=exact["endColumn"] + 1, snippet=None), self.root))

    def test_missing_region_columns_block(self):
        digest = self.catalogue()
        exact = self.at(CATALOGUE, f'"{digest}"')
        for field in ("startColumn", "endColumn"):
            missing = dict(exact)
            missing.pop(field)
            self.assertFalse(legal.is_legal_hash_exception(missing, self.root))
        self.assertFalse(legal.is_legal_hash_exception(
            dict(exact, startColumn=0, endColumn=0), self.root))

    def test_snippet_must_match_the_source_slice(self):
        digest = self.catalogue()
        exact = self.at(CATALOGUE, f'"{digest}"')
        self.assertFalse(legal.is_legal_hash_exception(
            dict(exact, snippet='"deadbeef"'), self.root))

    def test_duplicate_property_line_blocks(self):
        digest = self.catalogue()
        path = self.root / CATALOGUE
        text = path.read_text(encoding="utf-8")
        text = text.replace(f'"id": "{digest}",', f'"id": "{digest}",\n      "id": "{digest}",', 1)
        path.write_text(text, encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, f'"id": "{digest}"'), self.root))

    def test_unknown_field_in_file_blocks_every_line(self):
        digest = self.catalogue()
        path = self.root / CATALOGUE
        data = json.loads(path.read_text(encoding="utf-8"))
        data["components"][0]["apiKey"] = "not-a-public-digest"
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, f'"id": "{digest}"'), self.root))

    def test_unsupported_schema_version_blocks(self):
        digest = self.catalogue()
        path = self.root / CATALOGUE
        data = json.loads(path.read_text(encoding="utf-8"))
        data["schemaVersion"] = 2
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, f'"id": "{digest}"'), self.root))

    def test_text_that_no_longer_matches_its_id_blocks(self):
        digest = self.catalogue()
        self.write(f"legal/generated/texts/{digest}.txt", "Rewritten text.\n")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, f'"id": "{digest}"'), self.root))

    def test_missing_text_blocks(self):
        digest = self.catalogue()
        (self.root / f"legal/generated/texts/{digest}.txt").unlink()
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, f'"sha256": "{digest}"'), self.root))

    def test_unknown_document_reference_blocks(self):
        self.catalogue()
        path = self.root / CATALOGUE
        data = json.loads(path.read_text(encoding="utf-8"))
        data["components"][0]["documentIds"] = ["f" * 64]
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, f'"{"f" * 64}"'), self.root))

    def test_wrong_line_blocks(self):
        self.catalogue()
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, '"title": "Alpha licence"'), self.root))
        self.assertFalse(legal.is_legal_hash_exception(finding(0, start=0, end=1), self.root))
        self.assertFalse(legal.is_legal_hash_exception(finding(9999, start=0, end=10), self.root))

    def test_http_url_line_is_never_accepted(self):
        url = "http" + "://example.com/alpha"
        self.catalogue()
        path = self.root / CATALOGUE
        data = json.loads(path.read_text(encoding="utf-8"))
        data["documents"][0]["sourceUrls"] = [url]
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        finding_http = self.at(CATALOGUE, f'"{url}"')
        self.assertFalse(legal.is_legal_hash_exception(finding_http, self.root))
        self.assertFalse(
            legal.is_legal_hash_exception(dict(finding_http, rule="DS137138"), self.root))

    def test_text_symlink_escape_blocks(self):
        digest = self.catalogue()
        outside = Path(self.tmp.name).parent / f"{Path(self.tmp.name).name}-outside.txt"
        self.addCleanup(lambda: outside.unlink(missing_ok=True))
        outside.write_text("outside\n", encoding="utf-8")
        text = self.root / f"legal/generated/texts/{digest}.txt"
        text.unlink()
        try:
            text.symlink_to(outside)
        except (OSError, NotImplementedError) as error:
            self.skipTest(f"symlinks unavailable: {error}")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(CATALOGUE, f'"id": "{digest}"'), self.root))


class TestExtracted(Fixture):
    def test_document_extractor_and_payload_are_accepted(self):
        payload = {"staticwebassets/js/payload.js": "c" * 64}
        digest, extractor = self.extracted(payload=payload)
        for value in (digest, extractor):
            self.assertTrue(legal.is_legal_hash_exception(
                self.at(EXTRACTED, f'"{value}"'), self.root))
        self.assertTrue(legal.is_legal_hash_exception(
            self.at(EXTRACTED, f'"{"c" * 64}"'), self.root))

    def test_payload_key_that_looks_like_a_token_blocks(self):
        token = "d" * 40
        payload = {token: "c" * 64}
        self.extracted(payload=payload)
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(EXTRACTED, f'"{token}"'), self.root))
        self.assertTrue(legal.is_legal_hash_exception(
            self.at(EXTRACTED, f'"{"c" * 64}"'), self.root))

    def test_null_extractor_is_accepted(self):
        digest, _ = self.extracted(null_extractor=True)
        self.assertIn('"extractor": null', (self.root / EXTRACTED).read_text(encoding="utf-8"))
        self.assertTrue(legal.is_legal_hash_exception(
            self.at(EXTRACTED, f'"{digest}"'), self.root))

    def test_missing_extracted_text_blocks(self):
        digest, _ = self.extracted()
        (self.root / f"legal/extracted/texts/{digest}.txt").unlink()
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(EXTRACTED, f'"{digest}"'), self.root))

    def test_invalid_digest_blocks(self):
        self.extracted()
        path = self.root / EXTRACTED
        data = json.loads(path.read_text(encoding="utf-8"))
        data["packages"][0]["documents"][0]["sha256"] = "a" * 63
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(EXTRACTED, f'"{"a" * 63}"'), self.root))

    def test_unknown_package_field_blocks(self):
        digest, _ = self.extracted()
        path = self.root / EXTRACTED
        data = json.loads(path.read_text(encoding="utf-8"))
        data["packages"][0]["credential"] = "not-a-public-digest"
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(EXTRACTED, f'"{digest}"'), self.root))


class TestSupplements(Fixture):
    def documents(self, extra=None):
        text = "Supplement licence text.\n"
        digest = sha256(text)
        self.write("legal/supplements/texts/mit.txt", text)
        document = {
            "key": "mit", "title": "MIT", "file": "texts/mit.txt", "sha256": digest,
            "sourceUrls": [],
        }
        document.update(extra or {})
        self.write_json(DOCUMENTS, {"schemaVersion": 1, "documents": [document]})
        return digest

    def test_document_hash_is_accepted(self):
        digest = self.documents()
        self.assertTrue(legal.is_legal_hash_exception(
            self.at(DOCUMENTS, f'"{digest}"'), self.root))

    def test_changed_text_blocks(self):
        digest = self.documents()
        self.write("legal/supplements/texts/mit.txt", "Changed.\n")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(DOCUMENTS, f'"{digest}"'), self.root))

    def test_path_outside_texts_blocks(self):
        digest = self.documents({"file": "../documents.json"})
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(DOCUMENTS, f'"{digest}"'), self.root))


class TestBindings(Fixture):
    def bindings(self):
        text = "font bytes\n"
        digest = sha256(text)
        self.write("src/fonts/Inter.woff2", text)
        self.write_json(BINDINGS, {
            "schemaVersion": 1, "global": {"sdkVersion": "10.0.403"},
            "repoFiles": [{"component": "font-inter", "path": "src/fonts/Inter.woff2",
                           "sha256": digest}],
            "packageFiles": [{"component": "font-inter", "id": "Inter", "version": "1.0.0",
                              "path": "package/Inter.woff2", "sha256": "b" * 64}],
        })
        return digest

    def test_repo_and_package_hashes_are_accepted(self):
        digest = self.bindings()
        for value, occurrence in ((digest, 0), ("b" * 64, 0)):
            self.assertTrue(legal.is_legal_hash_exception(
                self.at(BINDINGS, f'"{value}"', occurrence=occurrence), self.root))

    def test_repo_file_that_changed_blocks(self):
        digest = self.bindings()
        self.write("src/fonts/Inter.woff2", "different bytes\n")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(BINDINGS, f'"{digest}"'), self.root))

    def test_repo_path_traversal_blocks(self):
        self.bindings()
        path = self.root / BINDINGS
        data = json.loads(path.read_text(encoding="utf-8"))
        data["repoFiles"][0]["path"] = "../global.json"
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(BINDINGS, '"sha256": "' + sha256("font bytes\n") + '"'), self.root))

    def test_repo_symlink_escape_blocks(self):
        self.bindings()
        outside = Path(self.tmp.name).parent / f"{Path(self.tmp.name).name}-font.bin"
        self.addCleanup(lambda: outside.unlink(missing_ok=True))
        outside.write_text("font bytes\n", encoding="utf-8")
        target = self.root / "src/fonts/Inter.woff2"
        target.unlink()
        try:
            target.symlink_to(outside)
        except (OSError, NotImplementedError) as error:
            self.skipTest(f"symlinks unavailable: {error}")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(BINDINGS, f'"sha256": "{sha256("font bytes\n")}"'), self.root))


class TestProfiles(Fixture):
    def test_input_package_and_submodule_pins_are_accepted(self):
        input_digest, sha512 = self.profile()
        self.assertTrue(legal.is_legal_hash_exception(
            self.at(PROFILE, f'"{input_digest}"'), self.root))
        self.assertTrue(legal.is_legal_hash_exception(
            self.at(PROFILE, f'"{sha512}"'), self.root))

        head = self.git_repo("external/Sub")
        path = self.root / PROFILE
        data = json.loads(path.read_text(encoding="utf-8"))
        data["inputs"]["submodules"] = {"external/Sub": head}
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertTrue(legal.is_legal_hash_exception(
            self.at(PROFILE, f'"{head}"'), self.root))

    def test_submodule_pin_that_differs_from_head_blocks(self):
        self.profile()
        head = self.git_repo("external/Sub")
        path = self.root / PROFILE
        data = json.loads(path.read_text(encoding="utf-8"))
        data["inputs"]["submodules"] = {"external/Sub": "e" * 40}
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(PROFILE, f'"external/Sub": "{"e" * 40}"'), self.root))
        self.assertNotEqual(head, "e" * 40)

    def test_submodule_head_ignores_inherited_git_dir(self):
        head = self.git_repo("external/Sub")
        previous = os.environ.get("GIT_DIR")
        os.environ["GIT_DIR"] = str(self.root / "parent.git")
        try:
            self.assertEqual(legal._submodule_head(self.root / "external/Sub"), head)
        finally:
            if previous is None:
                os.environ.pop("GIT_DIR", None)
            else:
                os.environ["GIT_DIR"] = previous

    def test_renamed_profile_blocks(self):
        self.profile()
        renamed = "legal/profiles/web-linux-x64.json"
        (self.root / PROFILE).rename(self.root / renamed)
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(renamed, f'"global.json": "{sha256("<Project />\n")}"'), self.root))

    def test_path_traversal_blocks(self):
        self.profile()
        exact = self.at(PROFILE, f'"global.json": "{sha256("<Project />\n")}"')
        traversed = "legal/profiles/../profiles/web-win-x64.json"
        self.assertFalse(legal.is_legal_hash_exception(
            dict(exact, file=traversed), self.root))

    def test_unsupported_field_blocks(self):
        input_digest, _ = self.profile()
        path = self.root / PROFILE
        data = json.loads(path.read_text(encoding="utf-8"))
        data["apiToken"] = "not-a-public-digest"
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(PROFILE, f'"global.json": "{input_digest}"'), self.root))

    def test_changed_input_blocks(self):
        self.profile()
        self.write("global.json", '<Project Sdk="Changed" />\n')
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(PROFILE, f'"{sha256("<Project />\n")}"'), self.root))

    def test_non_sha512_package_value_blocks(self):
        _, sha512 = self.profile()
        path = self.root / PROFILE
        data = json.loads(path.read_text(encoding="utf-8"))
        data["packages"][0]["sha512"] = sha512.rstrip("=")
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(PROFILE, f'"sha512": "{sha512.rstrip("=")}"'), self.root))


class TestScope(Fixture):
    def test_wrong_rule_blocks(self):
        digest = self.catalogue()
        exact = self.at(CATALOGUE, f'"{digest}"')
        for rule in ("DS137138", "DS126858", "DS000000"):
            self.assertFalse(legal.is_legal_hash_exception(dict(exact, rule=rule), self.root))

    def test_unknown_legal_file_blocks(self):
        digest = self.catalogue()
        other = "legal/generated/catalogue-copy.json"
        (self.root / CATALOGUE).rename(self.root / other)
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(other, f'"{digest}"'), self.root))

    def test_same_content_outside_legal_blocks(self):
        digest = self.catalogue()
        other = "docs/catalogue.json"
        self.write(other, (self.root / CATALOGUE).read_text(encoding="utf-8"))
        self.assertFalse(legal.is_legal_hash_exception(
            self.at(other, f'"{digest}"'), self.root))

    def test_windows_paths_cannot_escape(self):
        digest = self.catalogue()
        exact = self.at(CATALOGUE, f'"{digest}"')
        for file in ("C:/outside/catalogue.json", "C:outside/catalogue.json",
                     "//server/share/catalogue.json", "\\\\server\\share\\catalogue.json",
                     "/etc/catalogue.json", "legal/../legal/generated/catalogue.json"):
            self.assertFalse(legal.is_legal_hash_exception(
                dict(exact, file=file), self.root), file)

    def test_manifest_symlink_escape_blocks(self):
        digest = self.catalogue()
        exact = self.at(CATALOGUE, f'"{digest}"')
        outside = Path(self.tmp.name).parent / f"{Path(self.tmp.name).name}-catalogue.json"
        self.addCleanup(lambda: outside.unlink(missing_ok=True))
        outside.write_text((self.root / CATALOGUE).read_text(encoding="utf-8"), encoding="utf-8")
        (self.root / CATALOGUE).unlink()
        try:
            (self.root / CATALOGUE).symlink_to(outside)
        except (OSError, NotImplementedError) as error:
            self.skipTest(f"symlinks unavailable: {error}")
        self.assertFalse(legal.is_legal_hash_exception(exact, self.root))

    def test_contained_backslash_path_is_normalised(self):
        digest = self.catalogue()
        exact = self.at(CATALOGUE, f'"{digest}"')
        windows = CATALOGUE.replace("/", "\\")
        self.assertTrue(legal.is_legal_hash_exception(dict(exact, file=windows), self.root))

    def test_missing_file_blocks(self):
        self.assertFalse(legal.is_legal_hash_exception(
            finding(1, start=0, end=10), self.root))

    def test_malformed_finding_blocks(self):
        self.catalogue()
        self.assertFalse(legal.is_legal_hash_exception(
            {"rule": RULE, "file": CATALOGUE}, self.root))
        self.assertFalse(legal.is_legal_hash_exception(
            {"rule": RULE, "file": CATALOGUE, "line": "7",
             "startColumn": 0, "endColumn": 10}, self.root))


if __name__ == "__main__":
    unittest.main()
