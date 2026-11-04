#!/usr/bin/env python3
"""Packaging and content contract for the offline legal catalogue.

Run: python -m unittest discover -s scripts/ci/tests
"""

import hashlib
import json
import os
import sys
import unittest
import xml.etree.ElementTree as ElementTree
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts" / "legal"))

import catalogue as legal_catalogue  # noqa: E402

UI_PROJECT = ROOT / "src" / "MqttProbe.UI" / "MqttProbe.UI.csproj"
CATALOGUE = ROOT / "legal" / "generated" / "catalogue.json"
TEXTS = ROOT / "legal" / "generated" / "texts"
LOGICAL_NAMES = {
    "LICENSE": ("MqttProbe.UI.Legal.LICENSE", "LICENSE"),
    "catalogue.json": ("MqttProbe.UI.Legal.catalogue.json", "legal/generated/catalogue.json"),
}
SOURCE_DIRECTORIES = ("src", "tests", "samples", "Plugins", "benchmarks")
GENERATED_DIRECTORIES = {"bin", "obj", "node_modules"}


def walk(directory):
    for base, subdirectories, filenames in os.walk(directory):
        subdirectories[:] = [name for name in subdirectories if name not in GENERATED_DIRECTORIES]
        yield Path(base), filenames


def embedded_legal_items(project):
    items = {}
    for element in ElementTree.parse(project).getroot().iter("EmbeddedResource"):
        include = element.get("Include") or ""
        name = include.replace("\\", "/").rsplit("/", 1)[-1]
        if name in LOGICAL_NAMES or "*" in name:
            items[name] = element
    return items


def sha256_text(text):
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    normalized = "\n".join(line.rstrip() for line in normalized.split("\n")).strip("\n") + "\n"
    return hashlib.sha256(normalized.encode("utf-8")).hexdigest()


class LegalCatalogueTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.catalogue = json.loads(CATALOGUE.read_text(encoding="utf-8"))
        cls.documents = {document["id"]: document for document in cls.catalogue["documents"]}
        cls.components = {component["id"]: component for component in cls.catalogue["components"]}
        cls.text = (ROOT / "NOTICES").read_text(encoding="utf-8")

    def test_catalogue_schema_is_stable(self):
        self.assertEqual(self.catalogue["schemaVersion"], 1)
        self.assertEqual(set(self.catalogue) - {"provenance"}, {"schemaVersion", "components", "documents"})
        for component in self.catalogue["components"]:
            for field, kind in (
                ("id", str),
                ("name", str),
                ("version", str),
                ("license", str),
                ("copyright", str),
                ("sourceUrl", str),
                ("documentIds", list),
            ):
                self.assertIsInstance(component[field], kind, field)
            for document_id in component["documentIds"]:
                self.assertIsInstance(document_id, str)
        for document in self.catalogue["documents"]:
            for field, kind in (
                ("id", str),
                ("title", str),
                ("file", str),
                ("sha256", str),
                ("sourceUrls", list),
            ):
                self.assertIsInstance(document[field], kind, field)

    def test_documents_are_content_addressed_and_offline(self):
        for document in self.catalogue["documents"]:
            self.assertRegex(document["id"], r"^[0-9a-f]{64}$")
            self.assertEqual(document["sha256"], document["id"])
            self.assertEqual(document["file"], f"{document['id']}.txt")
            path = TEXTS / document["file"]
            self.assertTrue(path.is_file(), path)
            self.assertEqual(sha256_text(path.read_text(encoding="utf-8")), document["id"])
            self.assertEqual(document["sourceUrls"], sorted(set(document["sourceUrls"])))

    def test_components_reference_existing_texts_and_are_sorted(self):
        ids = [component["id"] for component in self.catalogue["components"]]
        self.assertEqual(ids, sorted(ids))
        self.assertEqual(len(ids), len(set(ids)))
        document_ids = sorted(self.documents)
        self.assertEqual([document["id"] for document in self.catalogue["documents"]], document_ids)
        for component in self.catalogue["components"]:
            self.assertEqual(component["documentIds"], sorted(set(component["documentIds"])))
            for document_id in component["documentIds"]:
                self.assertIn(document_id, self.documents, component["id"])
            if component.get("applicability") != "build":
                self.assertTrue(component["documentIds"], component["id"])
            self.assertNotIn("\\", component["sourceUrl"])
            self.assertNotIn(str(ROOT), component["sourceUrl"])

    def test_full_licence_texts_are_present(self):
        text = "\n".join(
            (TEXTS / document["file"]).read_text(encoding="utf-8")
            for document in self.catalogue["documents"]
        )
        for marker in (
            "Apache License",
            "END OF TERMS AND CONDITIONS",
            "MIT License",
            "Redistribution and use in source and binary forms",
            "SIL OPEN FONT LICENSE Version 1.1",
            "Eclipse Public License",
            "ISC License",
            "Bitstream",
            "Microsoft Windows App SDK",
            "Microsoft Edge WebView2",
        ):
            self.assertIn(marker, text)

    def test_distributed_components_are_attributed(self):
        names = {component["name"] for component in self.catalogue["components"]}
        for marker in (
            "Blazor-ApexCharts",
            "ApexCharts JavaScript library",
            "MQTTnet",
            "SparkplugNet",
            "Google.Protobuf",
            "protobuf-net",
            "FluentValidation",
            "MudBlazor",
            "Photino.NET",
            "Velopack",
            "Microsoft.Maui.Controls",
            "Microsoft.WindowsAppSDK",
            "Microsoft.Web.WebView2",
            "Microsoft.Graphics.Win2D",
            "Inter",
            "JetBrains Mono",
            "Chakra Petch",
            "OpenDyslexic, Nerd Font patched builds",
            "Lucide and Feather icon path data",
            "Sparkplug B generated code (SparkplugBProtobuf.cs)",
        ):
            self.assertIn(marker, names)

    def test_android_and_platform_notices_are_reproduced(self):
        titles = {document["title"] for document in self.catalogue["documents"]}
        for marker in (
            "Microsoft .NET runtime third-party notices (10.0.14)",
            "Microsoft.iOS SDK License (27.1.10018)",
            "xamarin-android third-party notices (.NET for Android runtime 36.1.118)",
            "Android binding notices - AndroidX and Material set",
            "Android binding notices - Xamarin.Android.Glide",
            "Android binding notices - Kotlin standard library",
            ".NET MAUI third-party notices (10.0.110)",
            "Microsoft Windows App SDK NOTICE",
            "Microsoft Edge WebView2 NOTICE",
            "Microsoft.Graphics.Win2D repository license (MIT)",
            "Nerd Fonts patcher license",
            "LLVM Release License",
        ):
            self.assertIn(marker, titles)

    def test_all_supplement_documents_are_shipped_and_referenced(self):
        manifest = json.loads((ROOT / "legal" / "supplements" / "documents.json").read_text(encoding="utf-8"))
        used = {document_id for component in self.catalogue["components"] for document_id in component["documentIds"]}
        for entry in manifest["documents"]:
            text = (ROOT / "legal" / "supplements" / entry["file"]).read_text(encoding="utf-8")
            self.assertEqual(sha256_text(text), entry["sha256"], entry["file"])
            self.assertIn(entry["sha256"], self.documents)
            self.assertIn(entry["sha256"], used)

    def test_extracted_inputs_cover_every_profile(self):
        extracted = json.loads((ROOT / "legal" / "extracted" / "packages.json").read_text(encoding="utf-8"))
        records = {(item["id"], item["version"]): item for item in extracted["packages"]}
        expected = set()
        for path in (ROOT / "legal" / "profiles").glob("*.json"):
            profile = json.loads(path.read_text(encoding="utf-8"))
            expected.update((item["id"], item["version"]) for item in profile["packages"])
        self.assertEqual(set(records), expected)
        for record in records.values():
            documents = list(record.get("documents", []))
            if record.get("extractor"):
                documents.append(record["extractor"])
            for document in documents:
                path = ROOT / "legal" / "extracted" / "texts" / f"{document['sha256']}.txt"
                self.assertTrue(path.is_file(), path)
                self.assertEqual(sha256_text(path.read_text(encoding="utf-8")), document["sha256"])

    def test_bindings_reference_shipped_components_and_inputs(self):
        bindings = json.loads(
            (ROOT / "legal" / "supplements" / "bindings.json").read_text(encoding="utf-8")
        )
        component_ids = {component["id"] for component in self.catalogue["components"]}
        for entry in bindings["repoFiles"]:
            self.assertIn(entry["component"], component_ids)
            self.assertTrue((ROOT / entry["path"]).is_file())
            self.assertEqual(
                legal_catalogue.binding_digest(ROOT / entry["path"], entry["path"]), entry["sha256"]
            )
        for entry in bindings["packageFiles"]:
            self.assertIn(entry["component"], component_ids)
        self.assertIn("apexcharts.esm.js", {entry["path"].split("/")[-1] for entry in bindings["packageFiles"]})

    def test_sparkplug_modification_and_source_are_stated(self):
        component = self.components["sparkplug-b-generated-code"]
        self.assertIn("e33d56d2", component["version"])
        self.assertIn("eclipse/tahu", component["sourceUrl"])
        self.assertIn("EPL-2.0", component["license"])
        self.assertIn("pragma", component["notes"])
        self.assertIn("MQTTProbe public repository", component["notes"])

    def test_unresolved_terms_are_not_allowlisted(self):
        apex = self.components["apexcharts-javascript"]
        self.assertIn("unresolved", apex["license"])
        win2d = self.components["nuget:microsoft.graphics.win2d@1.4.0"]
        self.assertIn("Unresolved", win2d["license"])
        self.assertIn("EULA", win2d["notes"])
        open_dyslexic = self.components["font-opendyslexic-nerd"]
        self.assertIn("patcher version", open_dyslexic["notes"])

    def test_old_attribution_is_gone(self):
        text = "\n".join(
            [self.text]
            + [(TEXTS / document["file"]).read_text(encoding="utf-8") for document in self.catalogue["documents"]]
        )
        for marker in (
            "src/MqttProbe.Shared",
            "MQTTnet.Extensions.ManagedClient",
            "CurrieTechnologies",
            "Cadson Demak",
            "unmodified official generated",
            "All four font families",
            "protobuf-net contributors",
            "HOW MQTTPROBE STANDS",
            "SCOPE NOTES",
        ):
            self.assertNotIn(marker, text)


class LegalResourcePackagingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.items = embedded_legal_items(UI_PROJECT)

    def test_root_license_and_catalogue_are_embedded(self):
        for name, (logical_name, source_path) in LOGICAL_NAMES.items():
            self.assertIn(name, self.items)
            self.assertEqual(self.items[name].get("LogicalName"), logical_name)
            source = (UI_PROJECT.parent / self.items[name].get("Include").replace("\\", "/")).resolve()
            self.assertEqual(source, ROOT / source_path)

    def test_notices_is_no_longer_embedded(self):
        self.assertNotIn("NOTICES", self.items)
        project = UI_PROJECT.read_text(encoding="utf-8")
        self.assertNotIn("MqttProbe.UI.Legal.NOTICES", project)

    def test_every_generated_text_is_embedded_with_the_reader_contract(self):
        wildcard = self.items.get("*.txt")
        self.assertIsNotNone(wildcard)
        self.assertEqual(wildcard.get("LogicalName"), "MqttProbe.UI.Legal.Texts.%(Filename).txt")
        self.assertEqual(wildcard.get("Link"), "Legal\\Texts\\%(Filename)%(Extension)")
        files = sorted(path.name for path in TEXTS.glob("*.txt"))
        self.assertGreater(len(files), 20)
        self.assertEqual(len(files), len(set(files)))

    def test_no_second_copy_in_the_source_tree(self):
        copies = [
            (Path(base) / name).relative_to(ROOT).as_posix()
            for base, filenames in walk(ROOT / "src")
            if Path(base).relative_to(ROOT).parts[0] != "legal"
            for name in filenames
            if name in ("NOTICES", "catalogue.json")
        ]
        self.assertEqual(copies, [])


class LegalIndexTests(unittest.TestCase):
    def test_notices_is_a_generated_index_not_a_text_dump(self):
        text = (ROOT / "NOTICES").read_text(encoding="utf-8")
        self.assertIn("generated by scripts/legal/notices.py", text)
        self.assertIn("python scripts/legal/notices.py generate", text)
        self.assertIn("1. Shipped components", text)
        self.assertIn("2. Build-only components", text)
        self.assertIn("PROVENANCE NOTES", text)
        self.assertLess(len(text), 200_000)

    def test_no_insecure_go_microsoft_metadata_urls(self):
        catalogue = json.loads(CATALOGUE.read_text(encoding="utf-8"))
        for component in catalogue["components"]:
            for key in ("sourceUrl", "licenseSourceUrl"):
                value = component.get(key)
                if not value:
                    continue
                parts = urlsplit(value)
                if (parts.hostname or "").lower() in {"go.microsoft.com", "www.microsoft.com"}:
                    self.assertEqual(parts.scheme, "https", value)

    def test_notices_has_no_machine_paths_or_timestamps(self):
        text = (ROOT / "NOTICES").read_text(encoding="utf-8")
        self.assertNotIn(str(ROOT), text)
        self.assertNotIn("\\", text.replace("\n", ""))
        self.assertNotRegex(text, r"\b(19|20)\d\d-\d\d-\d\d\b")

    def test_provenance_survives(self):
        text = (ROOT / "NOTICES").read_text(encoding="utf-8")
        for marker in (
            "ApexCharts",
            "Nerd Fonts",
            "Microsoft.Graphics.Win2D",
            "Android binding packages",
            "Microsoft package notices",
        ):
            self.assertIn(marker, text)


if __name__ == "__main__":
    unittest.main()
