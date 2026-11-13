#!/usr/bin/env python3
"""Focused tests for the legal notice generator, extraction and profile checks.

Run: python -m unittest discover -s scripts/ci/tests
"""

import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock
from urllib.parse import urlsplit, urlunsplit

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts" / "legal"))

import catalogue  # noqa: E402
import common  # noqa: E402
import extract  # noqa: E402
import profiles  # noqa: E402
import refresh  # noqa: E402


def fixture(test):
    root = Path(tempfile.mkdtemp())
    test.addCleanup(shutil.rmtree, root, True)
    return root


def tiny_document_set(tmp, text=("MIT License\n\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software "
                                 "and associated documentation files, to deal in the Software without restriction, including without limitation "
                                 "the rights to use, copy, modify, merge, publish, distribute, sublicense, and to permit persons to whom the "
                                 "Software is furnished to do so.\nTHE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.\n")):
    root = tmp / "extracted" / "texts"
    root.mkdir(parents=True)
    digest = common.sha256_text(text)
    (root / f"{digest}.txt").write_text(text, encoding="utf-8")
    documents = catalogue.Documents()
    supplement_digest = documents.add(text, "MIT License", [])
    return documents, {"mit": supplement_digest}, {"MIT": [supplement_digest]}, root, digest


class CatalogueTests(unittest.TestCase):
    def test_normalization_is_lf_and_deterministic(self):
        self.assertEqual(common.normalize_text("a\r\nb\r\n"), "a\nb\n")
        self.assertEqual(common.normalize_text("a\rb"), "a\nb\n")
        self.assertEqual(common.normalize_text("\ufeffhello"), "hello\n")

    def test_documents_deduplicate_by_content(self):
        documents = catalogue.Documents()
        first = documents.add("Same text\n", "First", [])
        second = documents.add("Same text\n", "Second", ["https://example.test"])
        self.assertEqual(first, second)
        self.assertEqual(len(documents.items), 1)
        self.assertEqual(documents.items[first]["sourceUrls"], ["https://example.test"])

    def test_compound_expressions_split(self):
        self.assertEqual(
            catalogue.expression_terms("MIT AND BSD-2-Clause AND Apache-2.0"),
            ["MIT", "BSD-2-Clause", "Apache-2.0"],
        )

    def test_safe_paths_reject_absolute_and_parent(self):
        for value in ("../x", "a/../../x", "/tmp/x", "C:/x"):
            with self.assertRaises(common.LegalError):
                common.safe_relative(value)
        self.assertEqual(common.safe_relative("legal/generated/texts").as_posix(), "legal/generated/texts")

    def test_go_microsoft_metadata_urls_are_canonicalized(self):
        insecure = urlunsplit(("http", "go.microsoft.com", "/fwlink/", "LinkID=519078", ""))
        secure = urlunsplit(("https", "go.microsoft.com", "/fwlink/", "LinkID=519078", ""))
        self.assertEqual(catalogue.canonical_source_url(insecure), secure)
        self.assertEqual(
            catalogue.canonical_source_url(urlunsplit(("https", "example.test", "/licence", "", ""))),
            urlunsplit(("https", "example.test", "/licence", "", "")),
        )

    def test_invalid_json_fails_loudly(self):
        root = fixture(self)
        path = root / "catalogue.json"
        path.write_text("{not json", encoding="utf-8")
        with self.assertRaises(common.LegalError) as caught:
            common.read_json(path)
        self.assertIn("invalid JSON", str(caught.exception))

    def test_extracted_package_uses_expression_and_embedded_text(self):
        root = fixture(self)
        documents, supplements, expressions, extracted, digest = tiny_document_set(root)
        record = {
            "id": "Test.Pkg",
            "version": "1.0",
            "metadata": {
                "license": "MIT",
                "licenseUrl": "https://licenses.nuget.org/MIT",
                "licenseInformationOrigin": 0,
                "projectUrl": "https://example.test/test.pkg",
                "copyright": "Copyright Test",
                "authors": "Test",
                "validationErrors": [],
            },
            "documents": [
                {
                    "title": "Test licence",
                    "sha256": digest,
                    "source": "embedded",
                    "sourcePath": "LICENSE",
                    "sourceUrl": "https://example.test/test.pkg",
                }
            ],
            "extractor": None,
            "payload": {},
        }
        with mock.patch.object(catalogue, "EXTRACTED_TEXTS", extracted):
            component = catalogue.load_extracted_package(
                record, documents, expressions, supplements, {}, [], []
            )
        self.assertEqual(component["id"], "nuget:test.pkg@1.0")
        self.assertEqual(component["license"], "MIT")
        self.assertEqual(component["sourceUrl"], "https://example.test/test.pkg")
        self.assertEqual(component["documentIds"], sorted(set(component["documentIds"])))

    def test_unknown_licence_term_fails_loudly(self):
        root = fixture(self)
        documents, supplements, expressions, extracted, _ = tiny_document_set(root)
        record = {
            "id": "Test.Pkg",
            "version": "1.0",
            "metadata": {
                "license": "Unknown-9.9",
                "licenseInformationOrigin": 0,
                "licenseUrl": "",
                "projectUrl": "",
                "copyright": "",
                "authors": "",
                "validationErrors": [],
            },
            "documents": [],
            "extractor": None,
            "payload": {},
        }
        with mock.patch.object(catalogue, "EXTRACTED_TEXTS", extracted):
            with self.assertRaises(common.LegalError) as caught:
                catalogue.load_extracted_package(record, documents, expressions, supplements, {}, [], [])
        self.assertIn("Unknown-9.9", str(caught.exception))

    def test_missing_offline_text_fails_loudly(self):
        root = fixture(self)
        documents, supplements, expressions, extracted, _ = tiny_document_set(root)
        record = {
            "id": "Test.Pkg",
            "version": "1.0",
            "metadata": {
                "license": "",
                "licenseInformationOrigin": 5,
                "licenseUrl": "https://example.test/eula",
                "projectUrl": "",
                "copyright": "",
                "authors": "",
                "validationErrors": [],
            },
            "documents": [],
            "extractor": None,
            "payload": {},
        }
        with mock.patch.object(catalogue, "EXTRACTED_TEXTS", extracted):
            with self.assertRaises(common.LegalError) as caught:
                catalogue.load_extracted_package(record, documents, expressions, supplements, {}, [], [])
        self.assertIn("no licence text", str(caught.exception))

    def test_extractor_text_is_used_only_when_nothing_else_exists(self):
        root = fixture(self)
        documents, supplements, expressions, extracted, digest = tiny_document_set(root)
        record = {
            "id": "Test.Pkg",
            "version": "1.0",
            "metadata": {
                "license": "",
                "licenseInformationOrigin": 2,
                "licenseUrl": "https://example.test/licence",
                "projectUrl": "",
                "copyright": "",
                "authors": "",
                "validationErrors": [],
            },
            "documents": [],
            "extractor": {
                "title": "Downloaded licence",
                "sha256": digest,
                "source": "extractor",
                "sourcePath": "Test.Pkg__1.0.txt",
                "sourceUrl": "https://example.test/licence",
            },
            "payload": {},
        }
        with mock.patch.object(catalogue, "EXTRACTED_TEXTS", extracted):
            component = catalogue.load_extracted_package(
                record, documents, expressions, supplements, {}, [], []
            )
        self.assertEqual(len(component["documentIds"]), 1)

    def test_required_review_family_version_fails_without_exact_entry(self):
        root = fixture(self)
        documents, supplements, expressions, extracted, digest = tiny_document_set(root)
        record = {
            "id": "Microsoft.WindowsAppSDK",
            "version": "2.0",
            "metadata": {
                "license": "MIT",
                "licenseInformationOrigin": 0,
                "licenseUrl": "",
                "projectUrl": "",
                "copyright": "",
                "authors": "",
                "validationErrors": [],
            },
            "documents": [],
            "extractor": None,
            "payload": {},
        }
        with mock.patch.object(catalogue, "EXTRACTED_TEXTS", extracted):
            with self.assertRaises(common.LegalError) as caught:
                catalogue.load_extracted_package(
                    record, documents, expressions, supplements, {}, [], ["Microsoft.WindowsAppSDK*"]
                )
        self.assertIn("requires an exact reviewed entry", str(caught.exception))

    def test_downloaded_primary_license_survives_notice_only_package(self):
        root = fixture(self)
        package = root / "package"
        package.mkdir()
        notice = "NOTICE\n\nThird-party attribution for bundled components.\n" * 3
        (package / "NOTICE.txt").write_text(notice, encoding="utf-8")
        downloads = root / "downloads"
        downloads.mkdir()
        license_text = (
            "MIT License\n\nPermission is hereby granted, free of charge, to any person obtaining a copy "
            "of this software and associated documentation files, to deal in the Software without "
            "restriction, including without limitation the rights to use, copy, modify, merge, publish, "
            "distribute, sublicense, and to permit persons to whom the Software is furnished to do so.\n"
        )
        (downloads / "Test.Pkg__1.0.txt").write_text(license_text, encoding="utf-8")
        raw = {
            "PackageId": "Test.Pkg",
            "PackageVersion": "1.0",
            "License": "",
            "LicenseUrl": "https://example.test/eula",
            "PackageProjectUrl": "https://example.test",
            "LicenseInformationOrigin": 2,
            "Copyright": "Copyright Test",
            "Authors": "Test",
        }
        record = extract.extract_package(raw, package, downloads, {"packageFiles": []})
        self.assertIsNotNone(record["extractor"])
        self.assertEqual([item["sourcePath"] for item in record["documents"]], ["NOTICE.txt"])

        extracted = root / "extracted"
        extracted.mkdir()
        for document in record["documents"] + [record["extractor"]]:
            (extracted / f"{document['sha256']}.txt").write_text(document["text"], encoding="utf-8")
        with mock.patch.object(catalogue, "EXTRACTED_TEXTS", extracted):
            component = catalogue.load_extracted_package(
                record, catalogue.Documents(), {}, {}, {}, [], []
            )
        self.assertEqual(len(component["documentIds"]), 2)

    def test_build_only_component_may_have_no_text(self):
        root = fixture(self)
        documents, supplements, expressions, extracted, _ = tiny_document_set(root)
        record = {
            "id": "Test.Pkg",
            "version": "1.0",
            "metadata": {
                "license": "",
                "licenseInformationOrigin": 5,
                "licenseUrl": "https://example.test/eula",
                "projectUrl": "",
                "copyright": "",
                "authors": "",
                "validationErrors": [],
            },
            "documents": [],
            "extractor": None,
            "payload": {},
        }
        packages = {"Test.Pkg@1.0": {"license": "Proprietary", "applicability": "build", "documents": []}}
        with mock.patch.object(catalogue, "EXTRACTED_TEXTS", extracted):
            component = catalogue.load_extracted_package(
                record, documents, expressions, supplements, packages, [], []
            )
        self.assertEqual(component["documentIds"], [])
        self.assertEqual(component["applicability"], "build")

    def test_html_license_is_extracted_and_not_shipped_raw(self):
        root = fixture(self)
        path = root / "LICENSE.html"
        path.write_text(
            "<html><body><script>bad()</script><p>MIT License</p>"
            "<p>Copyright 2026 Example</p><p>Permission is hereby granted.</p></body></html>",
            encoding="utf-8",
        )
        text = catalogue.read_document_file(path)
        self.assertNotIn("<", text)
        self.assertNotIn("bad()", text)
        self.assertIn("MIT License", text)
        self.assertIn("Permission is hereby granted", text)

    def test_supplement_manifest_hash_mismatch_fails(self):
        root = fixture(self)
        supplements = root / "supplements"
        (supplements / "texts").mkdir(parents=True)
        (supplements / "texts" / "doc.txt").write_text("changed text\n", encoding="utf-8")
        (supplements / "documents.json").write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "documents": [
                        {
                            "key": "doc",
                            "title": "Doc",
                            "file": "texts/doc.txt",
                            "sha256": "0" * 64,
                            "sourceUrls": [],
                            "kind": "license",
                        }
                    ],
                }
            ),
            encoding="utf-8",
        )
        with mock.patch.object(catalogue, "SUPPLEMENTS", supplements):
            with self.assertRaises(common.LegalError) as caught:
                catalogue.load_supplements(catalogue.Documents())
        self.assertIn("sha256 does not match", str(caught.exception))

    def test_catalogue_validation_rejects_broken_links(self):
        broken = {
            "schemaVersion": 1,
            "components": [
                {
                    "id": "x",
                    "name": "X",
                    "version": "1",
                    "license": "MIT",
                    "copyright": "",
                    "sourceUrl": "https://example.test",
                    "documentIds": ["0" * 64],
                }
            ],
            "documents": [],
            "provenance": "",
        }
        with self.assertRaises(common.LegalError):
            catalogue.validate_catalogue(broken)

    def test_catalogue_validation_rejects_unsafe_document_paths(self):
        broken = {
            "schemaVersion": 1,
            "components": [],
            "documents": [
                {
                    "id": "0" * 64,
                    "title": "X",
                    "file": "../x.txt",
                    "sha256": "0" * 64,
                    "sourceUrls": [],
                }
            ],
            "provenance": "",
        }
        with self.assertRaises(common.LegalError):
            catalogue.validate_catalogue(broken)


class OverrideTests(unittest.TestCase):
    def test_notes_annotation_never_grants_a_validation_exception(self):
        packages = {"Vendor.Lib@1.0": {"notes": "Reviewed family note only"}}
        annotations = [{"match": "Vendor.*", "notes": "Family note"}]
        self.assertEqual(catalogue.reviewed_exception("Vendor.Lib", "1.0", packages), None)
        self.assertEqual(
            catalogue.override_for("Vendor.Lib", "1.0", packages, annotations)["notes"],
            "Family note",
        )

    def test_future_version_does_not_inherit_exact_exception(self):
        packages = {"Vendor.Lib@1.0": {"license": "MIT", "documents": []}}
        self.assertIsNotNone(catalogue.reviewed_exception("Vendor.Lib", "1.0", packages))
        self.assertIsNone(catalogue.reviewed_exception("Vendor.Lib", "2.0", packages))


class CheckInputTests(unittest.TestCase):
    def _patched_tree(self, test):
        root = fixture(test)
        shutil.copytree(ROOT / "legal", root / "legal")
        shutil.copy2(ROOT / "NOTICES", root / "NOTICES")
        shutil.copy2(ROOT / "global.json", root / "global.json")
        for relative in (
            "src/MqttProbe.UI/wwwroot/fonts",
            "src/MqttProbe.UI/wwwroot/js/echarts",
            "src/MqttProbe.Maui/Resources/Fonts",
        ):
            shutil.copytree(ROOT / relative, root / relative)
        sparkplug = root / "src/MqttProbe.Core/Models/Sparkplug"
        sparkplug.mkdir(parents=True)
        shutil.copy2(
            ROOT / "src/MqttProbe.Core/Models/Sparkplug/SparkplugBProtobuf.cs",
            sparkplug / "SparkplugBProtobuf.cs",
        )
        patches = {
            "ROOT": root,
            "GENERATED": root / "legal" / "generated",
            "TEXTS": root / "legal" / "generated" / "texts",
            "PROFILES": root / "legal" / "profiles",
            "SUPPLEMENTS": root / "legal" / "supplements",
            "EXTRACTED": root / "legal" / "extracted",
            "EXTRACTED_MANIFEST": root / "legal" / "extracted" / "packages.json",
            "EXTRACTED_TEXTS": root / "legal" / "extracted" / "texts",
            "BINDINGS": root / "legal" / "supplements" / "bindings.json",
        }
        stack = mock.patch.multiple(catalogue, **patches)
        stack.start()
        test.addCleanup(stack.stop)
        return root

    def test_check_accepts_crlf_checkout_of_text_inputs(self):
        root = self._patched_tree(self)
        catalogue.check()
        for path in (
            root / "NOTICES",
            root / "legal" / "generated" / "catalogue.json",
            *root.glob("legal/generated/texts/*.txt"),
            *root.glob("legal/extracted/texts/*.txt"),
            *root.glob("legal/supplements/texts/*.txt"),
            *root.glob("legal/profiles/*.json"),
            root / "legal" / "extracted" / "packages.json",
            root / "legal" / "supplements" / "documents.json",
        ):
            path.write_bytes(path.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n"))
        catalogue.check()

    def test_check_needs_no_nuget_cache(self):
        self._patched_tree(self)
        with mock.patch.dict("os.environ", {"NUGET_PACKAGES": "/nonexistent/nuget"}):
            catalogue.check()

    def test_check_fails_when_expression_mapping_changes(self):
        root = self._patched_tree(self)
        catalogue.check()
        path = root / "legal" / "supplements" / "expressions.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        data["licenses"]["MIT"] = ["apache-license-2-0"]
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        with self.assertRaises(common.LegalError):
            catalogue.check()

    def test_check_fails_when_supplement_title_changes(self):
        root = self._patched_tree(self)
        catalogue.check()
        path = root / "legal" / "supplements" / "documents.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        data["documents"][0]["title"] += " changed"
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        with self.assertRaises(common.LegalError):
            catalogue.check()

    def test_check_fails_when_provenance_changes(self):
        root = self._patched_tree(self)
        catalogue.check()
        path = root / "legal" / "supplements" / "provenance.txt"
        path.write_text(path.read_text(encoding="utf-8") + "\nChanged.\n", encoding="utf-8")
        with self.assertRaises(common.LegalError):
            catalogue.check()


class BindingTests(unittest.TestCase):
    def _binding_tree(self, test):
        root = fixture(test)
        (root / "global.json").write_text(
            json.dumps({"sdk": {"version": "10.0.403", "workloadVersion": "10.0.403"}}),
            encoding="utf-8",
        )
        source = root / "source.bin"
        source.write_bytes(b"reviewed")
        bindings = root / "bindings.json"
        bindings.write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "global": {
                        "sdkVersion": "10.0.403",
                        "workloadVersion": "10.0.403",
                        "components": ["dotnet-runtime"],
                    },
                    "repoFiles": [
                        {
                            "component": "dotnet-runtime",
                            "path": "source.bin",
                            "sha256": common.sha256_bytes(b"reviewed"),
                        }
                    ],
                    "packageFiles": [
                        {
                            "component": "apexcharts-javascript",
                            "id": "Test.Pkg",
                            "version": "1.0",
                            "path": "js/test.js",
                            "sha256": common.sha256_bytes(b"payload"),
                        }
                    ],
                }
            ),
            encoding="utf-8",
        )
        catalogue_data = {
            "components": [{"id": "dotnet-runtime"}, {"id": "apexcharts-javascript"}],
            "_records": {
                ("Test.Pkg", "1.0"): {
                    "id": "Test.Pkg",
                    "version": "1.0",
                    "payload": {"js/test.js": common.sha256_bytes(b"payload")},
                }
            },
        }
        stack = mock.patch.multiple(catalogue, ROOT=root, BINDINGS=bindings)
        stack.start()
        test.addCleanup(stack.stop)
        return root, source, bindings, catalogue_data

    def test_bindings_accept_unchanged_inputs(self):
        _, _, _, data = self._binding_tree(self)
        catalogue.validate_bindings(data)

    def test_text_bindings_normalize_lf_but_binary_bindings_stay_raw(self):
        root = fixture(self)
        text = root / "license.txt"
        text.write_bytes(b"line one\r\nline two\r\n")
        first = catalogue.binding_digest(
            text, "src/MqttProbe.UI/wwwroot/fonts/OpenDyslexic-LICENSE.txt"
        )
        text.write_bytes(b"line one\nline two\n")
        second = catalogue.binding_digest(
            text, "src/MqttProbe.UI/wwwroot/fonts/OpenDyslexic-LICENSE.txt"
        )
        self.assertEqual(first, second)
        binary = root / "font.woff2"
        binary.write_bytes(b"a\r\nb")
        raw_first = catalogue.binding_digest(binary, "src/MqttProbe.UI/wwwroot/fonts/InterVariable.woff2")
        binary.write_bytes(b"a\nb")
        raw_second = catalogue.binding_digest(binary, "src/MqttProbe.UI/wwwroot/fonts/InterVariable.woff2")
        self.assertNotEqual(raw_first, raw_second)

    def test_changed_repo_file_fails_binding(self):
        _, source, _, data = self._binding_tree(self)
        source.write_bytes(b"changed")
        with self.assertRaises(common.LegalError) as caught:
            catalogue.validate_bindings(data)
        self.assertIn("changed since", str(caught.exception))

    def test_changed_sdk_pin_fails_binding(self):
        root, _, _, data = self._binding_tree(self)
        (root / "global.json").write_text(
            json.dumps({"sdk": {"version": "10.0.999", "workloadVersion": "10.0.403"}}),
            encoding="utf-8",
        )
        with self.assertRaises(common.LegalError) as caught:
            catalogue.validate_bindings(data)
        self.assertIn("sdkVersion", str(caught.exception))

    def test_changed_package_version_fails_binding(self):
        root, _, bindings, data = self._binding_tree(self)
        bindings_data = json.loads(bindings.read_text(encoding="utf-8"))
        bindings_data["packageFiles"][0]["version"] = "2.0"
        bindings.write_text(json.dumps(bindings_data), encoding="utf-8")
        with self.assertRaises(common.LegalError) as caught:
            catalogue.validate_bindings(data)
        self.assertIn("absent from legal/extracted", str(caught.exception))


class ProfileTests(unittest.TestCase):
    def test_committed_profiles_are_sorted_and_content_addressed(self):
        paths = sorted((ROOT / "legal" / "profiles").glob("*.json"))
        self.assertGreaterEqual(len(paths), 7)
        for path in paths:
            profile = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(profile["schemaVersion"], 1)
            self.assertEqual(profile["name"], path.stem)
            identities = [(item["id"], item["version"]) for item in profile["packages"]]
            self.assertEqual(identities, sorted(identities))
            self.assertEqual(len(identities), len(set(identities)))
            for item in profile["packages"]:
                self.assertRegex(item["sha512"], r"^[A-Za-z0-9+/=]+$")
            self.assertTrue(profile["targets"])

    def test_dependency_hash_ignores_crlf(self):
        root = fixture(self)
        lf = root / "lf.json"
        crlf = root / "crlf.json"
        lf.write_bytes(b'{\n  "a": 1\n}\n')
        crlf.write_bytes(b'{\r\n  "a": 1\r\n}\r\n')
        self.assertEqual(profiles.hash_dependency_file(lf), profiles.hash_dependency_file(crlf))

    def test_compare_detects_added_missing_changed_and_input_drift(self):
        committed = {
            "framework": "net10.0",
            "runtimeIdentifier": None,
            "runtimeIdentifiers": None,
            "selfContained": False,
            "targets": ["net10.0"],
            "inputs": {"files": {"Directory.Packages.props": "a"}, "submodules": {}},
            "packages": [
                {"id": "A", "version": "1", "sha512": "one"},
                {"id": "B", "version": "2", "sha512": "two"},
            ],
        }
        current = dict(committed)
        current["targets"] = ["net10.0/win-x64"]
        current["inputs"] = {"files": {"Directory.Packages.props": "b"}, "submodules": {}}
        current["packages"] = [
            {"id": "A", "version": "1", "sha512": "changed"},
            {"id": "C", "version": "3", "sha512": "three"},
        ]
        problems = profiles.compare(committed, current)
        self.assertTrue(any("sha512 changed" in item for item in problems))
        self.assertTrue(any("missing resolved package" in item for item in problems))
        self.assertTrue(any("unreviewed resolved package" in item for item in problems))
        self.assertTrue(any("dependency input changed" in item for item in problems))
        self.assertTrue(any("targets changed" in item for item in problems))

    def test_target_keys_supports_plural_rids(self):
        assets = {"targets": {"net10.0/maccatalyst-x64": {}, "net10.0/maccatalyst-arm64": {}}}
        scenario = {
            "name": "m",
            "framework": "net10.0",
            "runtimeIdentifiers": ["maccatalyst-x64", "maccatalyst-arm64"],
        }
        self.assertEqual(
            profiles.target_keys(assets, scenario),
            ["net10.0/maccatalyst-x64", "net10.0/maccatalyst-arm64"],
        )

    def test_target_keys_fails_when_declared_rid_is_missing(self):
        assets = {"targets": {"net10.0": {}}}
        scenario = {"name": "w", "framework": "net10.0", "runtimeIdentifier": "win-x64", "selfContained": False}
        with self.assertRaises(common.LegalError):
            profiles.target_keys(assets, scenario)


class RefreshTests(unittest.TestCase):
    def test_scenario_environment_inherits_evaluation_properties(self):
        scenario = {
            "framework": "net10.0",
            "runtimeIdentifier": "linux-x64",
            "selfContained": True,
        }
        environment = extract.scenario_environment(scenario)
        self.assertEqual(environment["TargetFramework"], "net10.0")
        self.assertEqual(environment["RuntimeIdentifier"], "linux-x64")
        self.assertEqual(environment["SelfContained"], "true")

    def test_plural_rids_use_environment_not_msbuild_property(self):
        scenario = {
            "name": "mac",
            "project": "src/MqttProbe.Maui/MqttProbe.Maui.csproj",
            "framework": "net10.0-maccatalyst",
            "runtimeIdentifiers": ["maccatalyst-x64", "maccatalyst-arm64"],
        }
        command = refresh.restore_command(scenario)
        self.assertFalse(any("RuntimeIdentifiers" in argument for argument in command))
        self.assertFalse(any(";" in argument for argument in command))
        environment = extract.scenario_environment(scenario)
        self.assertEqual(
            environment["RuntimeIdentifiers"], "maccatalyst-x64;maccatalyst-arm64"
        )

    def test_refresh_scenario_restores_exports_then_extracts(self):
        calls = []
        environments = []
        scenario = {"name": "one", "project": "p.csproj", "framework": "net10.0"}
        with mock.patch.object(
            refresh,
            "run",
            side_effect=lambda command, environment=None: calls.append("restore") or environments.append(environment),
        ), \
             mock.patch.object(refresh.profiles, "inventory", side_effect=lambda item: calls.append("export") or {"packages": []}), \
             mock.patch.object(refresh, "write_json", side_effect=lambda path, data: calls.append("write")), \
             mock.patch.object(refresh.extract, "extract_scenario", side_effect=lambda a, b, c: calls.append("extract") or {}):
            refresh.refresh_scenario(scenario, {})
        self.assertEqual(calls, ["restore", "export", "write", "extract"])
        self.assertEqual(environments[0]["TargetFramework"], "net10.0")

    def test_refresh_restores_exports_and_extracts_each_scenario_in_order(self):
        scenarios = {
            "one": {"name": "one", "project": "p.csproj", "pending": False},
            "two": {"name": "two", "project": "p.csproj", "pending": False},
        }
        calls = []

        def fake_refresh_scenario(scenario, packages):
            calls.append(("scenario", scenario["name"]))
            return {"packages": [{"id": scenario["name"], "version": "1"}]}, {
                (scenario["name"], "1"): {"id": scenario["name"], "version": "1"}
            }

        with mock.patch.object(refresh.profiles, "scenario_map", return_value=scenarios), \
             mock.patch.object(refresh, "refresh_scenario", side_effect=fake_refresh_scenario), \
             mock.patch.object(refresh.extract, "read_extracted", return_value={}), \
             mock.patch.object(refresh.extract, "write_extracted") as write, \
             mock.patch.object(refresh, "union_identities", return_value={("one", "1"), ("two", "1")}), \
             mock.patch.object(refresh.catalogue, "generate"):
            refresh.refresh(["one", "two"])
        self.assertEqual(calls, [("scenario", "one"), ("scenario", "two")])
        written = write.call_args.args[0]
        self.assertEqual(set(written), {("one", "1"), ("two", "1")})

    @mock.patch("refresh.platform.system", return_value="Darwin")
    def test_mac_default_selects_only_the_three_established_apple_scenarios(self, _system):
        scenarios = {
            "desktop-osx-arm64": {"name": "desktop-osx-arm64", "host": "macos"},
            "maui-ios-arm64": {"name": "maui-ios-arm64", "host": "ios"},
            "maui-maccatalyst": {"name": "maui-maccatalyst", "host": "macos"},
            "web-framework-dependent": {"name": "web-framework-dependent", "host": "any"},
            "maui-android": {"name": "maui-android", "host": "any"},
        }
        self.assertEqual(
            sorted(refresh.default_scenarios(scenarios)),
            ["desktop-osx-arm64", "maui-ios-arm64", "maui-maccatalyst"],
        )

    def test_committed_apple_profiles_and_scenarios_are_established(self):
        scenarios = json.loads((ROOT / "legal" / "scenarios.json").read_text(encoding="utf-8"))
        by_name = {entry["name"]: entry for entry in scenarios["scenarios"]}
        for name in ("desktop-osx-arm64", "maui-ios-arm64", "maui-maccatalyst"):
            self.assertNotIn("pending", by_name[name])
            profile = json.loads((ROOT / "legal" / "profiles" / f"{name}.json").read_text(encoding="utf-8"))
            self.assertEqual(profile["name"], name)
        catalyst = json.loads(
            (ROOT / "legal" / "profiles" / "maui-maccatalyst.json").read_text(encoding="utf-8")
        )
        self.assertEqual(
            catalyst["targets"],
            ["net10.0-maccatalyst/maccatalyst-x64", "net10.0-maccatalyst/maccatalyst-arm64"],
        )

    @mock.patch("refresh.platform.system", return_value="Windows")
    def test_default_scenarios_skip_pending(self, _system):
        scenarios = {
            "windows": {"name": "windows", "host": "windows", "pending": None},
            "apple": {"name": "apple", "host": "ios", "pending": "Apple host export required"},
        }
        selected = refresh.default_scenarios(scenarios)
        self.assertIn("windows", selected)
        self.assertNotIn("apple", selected)


if __name__ == "__main__":
    unittest.main()
