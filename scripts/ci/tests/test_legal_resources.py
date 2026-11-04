#!/usr/bin/env python3
"""Packaging contract for the offline legal texts.

LICENSE and NOTICES are embedded into MqttProbe.UI so every host (web, desktop,
MAUI iOS) shows the same canonical text from one source. These checks pin the
items, the logical names the in-app reader requests, and the absence of a second
maintained copy.

Run: python -m unittest discover -s scripts/ci/tests
"""

import os
import unittest
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
UI_PROJECT = ROOT / "src" / "MqttProbe.UI" / "MqttProbe.UI.csproj"

LOGICAL_NAMES = {
    "LICENSE": "MqttProbe.UI.Legal.LICENSE",
    "NOTICES": "MqttProbe.UI.Legal.NOTICES",
}

SOURCE_DIRECTORIES = ("src", "tests", "samples", "Plugins", "benchmarks")
GENERATED_DIRECTORIES = {"bin", "obj", "node_modules"}


def walk(directory):
    for base, subdirectories, filenames in os.walk(directory):
        subdirectories[:] = [name for name in subdirectories if name not in GENERATED_DIRECTORIES]
        yield Path(base), filenames


def walk_source_directories():
    for name in SOURCE_DIRECTORIES:
        directory = ROOT / name
        if directory.is_dir():
            yield from walk(directory)


def embedded_legal_items(project):
    """Map legal file name to its EmbeddedResource element, for one csproj."""
    items = {}
    for element in ElementTree.parse(project).getroot().iter("EmbeddedResource"):
        include = element.get("Include")
        if not include:
            continue
        name = include.replace("\\", "/").rsplit("/", 1)[-1]
        if name in LOGICAL_NAMES:
            items[name] = element
    return items


class LegalResourcePackagingTests(unittest.TestCase):
    def test_root_files_are_the_canonical_texts(self):
        for name in LOGICAL_NAMES:
            self.assertTrue((ROOT / name).is_file())

    def test_no_second_copy_in_the_source_tree(self):
        copies = [
            (Path(base) / name).relative_to(ROOT).as_posix()
            for base, filenames in walk_source_directories()
            for name in filenames
            if name in LOGICAL_NAMES
        ]
        self.assertEqual(copies, [])

    def test_ui_assembly_embeds_both_texts_from_the_root(self):
        items = embedded_legal_items(UI_PROJECT)
        self.assertEqual(set(items), set(LOGICAL_NAMES))
        for name, element in items.items():
            source = (UI_PROJECT.parent / element.get("Include").replace("\\", "/")).resolve()
            self.assertEqual(source, ROOT / name)

    def test_logical_names_match_the_reader_contract(self):
        for name, element in embedded_legal_items(UI_PROJECT).items():
            self.assertEqual(element.get("LogicalName"), LOGICAL_NAMES[name])

    def test_resources_are_linked_under_a_legal_folder(self):
        for name, element in embedded_legal_items(UI_PROJECT).items():
            self.assertEqual((element.get("Link") or "").replace("\\", "/"), f"Legal/{name}")

    def test_no_other_project_embeds_the_same_texts(self):
        projects = {
            Path(base) / name
            for base, filenames in walk_source_directories()
            for name in filenames
            if name.endswith(".csproj")
        }
        duplicates = [
            str(project.relative_to(ROOT))
            for project in projects - {UI_PROJECT}
            if embedded_legal_items(project)
        ]
        self.assertEqual(duplicates, [])


class LegalNoticeContentTests(unittest.TestCase):
    """Guard the shipped notice against losing required texts or attribution."""

    @classmethod
    def setUpClass(cls):
        cls.text = (ROOT / "NOTICES").read_text(encoding="utf-8")

    def test_full_licence_texts_are_present(self):
        for marker in (
            "Apache License",
            "END OF TERMS AND CONDITIONS",
            "MIT License",
            "Redistribution and use in source and binary forms",
            "SIL OPEN FONT LICENSE Version 1.1",
            "Eclipse Public License - v 2.0",
            "ISC License",
            "Bitstream",
        ):
            self.assertIn(marker, self.text)

    def test_distributed_components_are_attributed(self):
        for marker in (
            "Blazor-ApexCharts 7.1.0",
            "ApexCharts JavaScript library 6.5.0",
            "MQTTnet 5.2.0.1603",
            "SparkplugNet 1.4.1",
            "Google.Protobuf 3.36.2",
            "protobuf-net 3.2.56",
            "FluentValidation 12.2.0",
            "MudBlazor 9.12.0",
            "Photino.NET",
            "Velopack 1.2.161",
            "Microsoft.Maui.Controls",
            "Windows App SDK",
            "WebView2",
            "Lucide",
            "Inter",
            "JetBrains Mono",
            "Chakra Petch",
            "OpenDyslexic",
        ):
            self.assertIn(marker, self.text)

    def test_microsoft_package_notices_are_reproduced(self):
        for marker in (
            "8.3 Microsoft package third-party notices",
            "Microsoft.AspNetCore.Components packages third-party notices",
            "Microsoft.Extensions, System and cryptography packages third-party notices",
            "Microsoft.AspNetCore app packages third-party notices",
            "Microsoft.Extensions.Logging and System.Numerics.Tensors third-party notices",
        ):
            self.assertIn(marker, self.text)

    def test_platform_notices_are_reproduced(self):
        for marker in (
            ".NET runtime 10.0.14",
            "Microsoft.iOS",
            "xamarin-android",
            "Android binding packages",
            "THIRD-PARTY SOFTWARE NOTICES",
            "Windows App SDK",
            "Microsoft.Graphics.Win2D",
            "Nerd Fonts patcher",
            "PROVENANCE NOTES",
        ):
            self.assertIn(marker, self.text)

    def test_sparkplug_modification_and_source_are_stated(self):
        start = self.text.index("6.1 Sparkplug")
        sparkplug = self.text[start:self.text.index("7. Serialization")]
        for marker in (
            "src/MqttProbe.Core/Models/Sparkplug/SparkplugBProtobuf.cs",
            "eclipse/tahu/blob/e33d56d2",
            "https://github.com/bluegrassiot/mqttprobe",
            "#pragma",
        ):
            self.assertIn(marker, sparkplug)
        self.assertNotIn("unmodified", sparkplug)

    def test_stale_attribution_is_gone(self):
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
            self.assertNotIn(marker, self.text)

    def test_notice_is_large_enough_to_hold_the_full_texts(self):
        self.assertGreater(len(self.text), 300_000)


if __name__ == "__main__":
    unittest.main()
