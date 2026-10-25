#!/usr/bin/env python3
"""Tests for scripts/ci/package-schema-plugin.py.

Run: python -m unittest discover -s scripts/ci/tests
"""

import importlib.util
import json
import shutil
import tempfile
import unittest
import zipfile
from fnmatch import fnmatch
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]

_SPEC = importlib.util.spec_from_file_location(
    "ci_package_schema_plugin",
    Path(__file__).resolve().parents[1] / "package-schema-plugin.py",
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/package-schema-plugin.py")
packaging = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(packaging)

MANIFEST = {
    "id": "chirpstack",
    "name": "ChirpStack Protobuf Schemas",
    "version": "1.0.2",
    "kind": "protobuf-schemas",
}

SCHEMA_MANIFEST = {
    "schemas": [
        {
            "files": ["integration/integration.proto"],
            "topicPattern": "application/+/device/+/event/up",
            "messageType": "integration.UplinkEvent",
        }
    ]
}


class Fixture:
    def __init__(self, test: unittest.TestCase, manifest: dict | None = MANIFEST) -> None:
        self.root = Path(tempfile.mkdtemp())
        test.addCleanup(shutil.rmtree, self.root, True)
        self.source = self.root / "source"
        self.output = self.root / "output"
        self.source.mkdir()
        if manifest is not None:
            (self.source / packaging.MANIFEST_NAME).write_text(
                json.dumps(manifest), encoding="utf-8"
            )
        self.write("protobuf-schemas.json", json.dumps(SCHEMA_MANIFEST))
        self.write("integration/integration.proto", "syntax = \"proto3\";\n")
        self.write("README.md", "# ChirpStack\n")
        self.write("sample-uplink.b64", "AA==\n")
        self.write("bin/ignored.proto", "syntax = \"proto3\";\n")

    def write(self, rel: str, content: str) -> Path:
        path = self.source / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def run(self) -> int:
        return packaging.main(
            ["--source", str(self.source), "--output-dir", str(self.output)]
        )

    @property
    def zip_path(self) -> Path:
        return self.output / "chirpstack-1.0.2.zip"


class TestPackageSchemaPlugin(unittest.TestCase):
    def test_packages_manifests_at_the_archive_root(self) -> None:
        fixture = Fixture(self)

        self.assertEqual(fixture.run(), 0)
        with zipfile.ZipFile(fixture.zip_path) as archive:
            names = archive.namelist()

        self.assertIn("mqttprobe-plugin.json", names)
        self.assertIn("protobuf-schemas.json", names)
        self.assertIn("integration/integration.proto", names)

    def test_leaves_out_files_schema_packages_cannot_install(self) -> None:
        fixture = Fixture(self)

        self.assertEqual(fixture.run(), 0)
        with zipfile.ZipFile(fixture.zip_path) as archive:
            names = archive.namelist()

        self.assertNotIn("sample-uplink.b64", names)
        self.assertNotIn("bin/ignored.proto", names)

    def test_rejects_a_manifest_for_another_package_kind(self) -> None:
        fixture = Fixture(self, manifest={**MANIFEST, "kind": "assembly"})

        self.assertEqual(fixture.run(), 1)
        self.assertFalse(fixture.zip_path.exists())

    def test_requires_the_plugin_manifest(self) -> None:
        fixture = Fixture(self, manifest=None)

        self.assertEqual(fixture.run(), 1)
        self.assertFalse(fixture.zip_path.exists())

    def test_validation_reports_a_missing_schema_manifest(self) -> None:
        path = Path(tempfile.mkdtemp()) / "broken.zip"
        self.addCleanup(shutil.rmtree, path.parent, True)
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("mqttprobe-plugin.json", "{}")

        problems = packaging.validate_archive(path)

        self.assertTrue(any("protobuf-schemas.json" in p for p in problems), problems)

    def test_repository_sample_builds_the_release_package(self) -> None:
        source = ROOT / "samples" / "protobuf" / "chirpstack"
        manifest = json.loads(
            (source / packaging.MANIFEST_NAME).read_text(encoding="utf-8")
        )
        output = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, output, True)

        rc = packaging.main(["--source", str(source), "--output-dir", str(output)])

        self.assertEqual(rc, 0)
        package = output / packaging.package_name(manifest)
        self.assertTrue(package.is_file())
        with zipfile.ZipFile(package) as archive:
            names = archive.namelist()

        self.assertIn(packaging.MANIFEST_NAME, names)
        self.assertIn(packaging.SCHEMA_MANIFEST_NAME, names)
        self.assertIn("gw/gw.proto", names)
        self.assertNotIn("sample-uplink.b64", names)
        self.assertEqual(packaging.validate_archive(package), [])

    def test_release_assets_include_the_generated_package(self) -> None:
        source = ROOT / "samples" / "protobuf" / "chirpstack"
        manifest = json.loads(
            (source / packaging.MANIFEST_NAME).read_text(encoding="utf-8")
        )
        package = packaging.package_name(manifest)
        patterns = [
            line.strip()
            for line in (ROOT / ".github" / "release-assets.txt")
            .read_text(encoding="utf-8")
            .splitlines()
            if line.strip() and not line.lstrip().startswith("#")
        ]

        self.assertTrue(
            any(fnmatch(package, pattern) for pattern in patterns),
            f"{package} is not matched by .github/release-assets.txt",
        )


if __name__ == "__main__":
    unittest.main()
