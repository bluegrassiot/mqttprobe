import importlib.util
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "dotnet-workload.py"
SPEC = importlib.util.spec_from_file_location("dotnet_workload_snapshot", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise SystemExit(f"Cannot load {SCRIPT}")
workload = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(workload)


class WorkloadSnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.sdk = self.root / "sdk-root"
        self.snapshot = self.root / "runner-temp" / "dotnet-workload-snapshot"
        self.write_assets(self.sdk)

    @staticmethod
    def write_assets(root):
        for name in workload.ASSET_DIRS:
            (root / name / "nested").mkdir(parents=True, exist_ok=True)
            (root / name / "nested" / "asset.bin").write_bytes(b"asset")

    def capture(self):
        workload.capture_snapshot(self.sdk, self.snapshot)

    def test_snapshot_restores_assets_to_clean_sdk_without_forbidden_dirs(self):
        self.capture()
        clean = self.root / "clean-sdk"
        workload.restore_snapshot(self.snapshot, clean)
        for name in workload.ASSET_DIRS:
            self.assertEqual((clean / name / "nested" / "asset.bin").read_bytes(), b"asset")
        self.assertFalse((self.snapshot / "NuGet").exists())
        self.assertFalse((self.snapshot / "sdk").exists())

    def test_missing_inventory_and_snapshot_damage_fail_before_live_mutation(self):
        for damage in ("inventory", "missing", "truncated", "metadata", "empty-directory", "unexpected", "malformed"):
            with self.subTest(damage=damage), tempfile.TemporaryDirectory() as temp:
                snapshot = Path(temp) / "snapshot"
                workload.capture_snapshot(self.sdk, snapshot)
                if damage == "inventory":
                    (snapshot / workload.INVENTORY_NAME).unlink()
                elif damage == "missing":
                    (snapshot / "packs/nested/asset.bin").unlink()
                elif damage == "truncated":
                    (snapshot / "packs/nested/asset.bin").write_bytes(b"bad")
                else:
                    if damage == "metadata":
                        (snapshot / "metadata/workloads/nested/asset.bin").unlink()
                    elif damage == "empty-directory":
                        (snapshot / "packs/nested/asset.bin").unlink()
                        (snapshot / "packs/nested").rmdir()
                    elif damage == "unexpected":
                        (snapshot / "packs/extra").write_text("unexpected", encoding="utf-8")
                    else:
                        (snapshot / workload.INVENTORY_NAME).write_text("{", encoding="utf-8")
                clean = Path(temp) / "clean-sdk"
                clean.mkdir()
                sentinel = clean / "untouched"
                sentinel.write_text("keep", encoding="utf-8")
                with self.assertRaises(workload.WorkloadError):
                    workload.restore_snapshot(snapshot, clean)
                self.assertEqual(list(clean.iterdir()), [sentinel])

    def test_rejects_inventory_path_traversal_before_merge(self):
        self.capture()
        inventory = self.snapshot / workload.INVENTORY_NAME
        data = json.loads(inventory.read_text(encoding="utf-8"))
        data["entries"].append({"path": "../escape", "type": "file", "size": 1})
        inventory.write_text(json.dumps(data), encoding="utf-8")
        clean = self.root / "clean-sdk"
        with self.assertRaises(workload.WorkloadError):
            workload.restore_snapshot(self.snapshot, clean)
        self.assertFalse(clean.exists())

    def test_symlinks_are_recorded_without_following_and_restored(self):
        link = self.sdk / "packs" / "link"
        try:
            link.symlink_to("nested/asset.bin")
        except (OSError, NotImplementedError):
            self.skipTest("symlinks are unavailable")
        self.capture()
        clean = self.root / "clean-sdk"
        workload.restore_snapshot(self.snapshot, clean)
        self.assertTrue((clean / "packs/link").is_symlink())
        self.assertEqual((clean / "packs/link").readlink(), Path("nested/asset.bin"))

    def test_changed_symlink_target_is_rejected_before_live_mutation(self):
        link = self.sdk / "packs" / "link"
        try:
            link.symlink_to("nested/asset.bin")
        except (OSError, NotImplementedError):
            self.skipTest("symlinks are unavailable")
        self.capture()
        cached_link = self.snapshot / "packs/link"
        cached_link.unlink()
        cached_link.symlink_to("different-target")
        clean = self.root / "clean-sdk"
        with self.assertRaises(workload.WorkloadError):
            workload.restore_snapshot(self.snapshot, clean)
        self.assertFalse(clean.exists())

    def test_restore_into_populated_sdk_preserves_identical_file_and_directory_symlinks(self):
        file_link = self.sdk / "packs" / "file-link"
        directory_link = self.sdk / "packs" / "directory-link"
        try:
            file_link.symlink_to("nested/asset.bin")
            directory_link.symlink_to("nested", target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks are unavailable")
        self.capture()
        workload.restore_snapshot(self.snapshot, self.sdk)
        self.assertEqual(file_link.readlink(), Path("nested/asset.bin"))
        self.assertEqual(directory_link.readlink(), Path("nested"))
        self.assertEqual((self.sdk / "packs/nested/asset.bin").read_bytes(), b"asset")

    def test_restore_into_populated_sdk_rejects_conflicting_symlink_target(self):
        link = self.sdk / "packs" / "link"
        try:
            link.symlink_to("nested/asset.bin")
        except (OSError, NotImplementedError):
            self.skipTest("symlinks are unavailable")
        self.capture()
        link.unlink()
        link.symlink_to("different-target")
        with self.assertRaises(workload.WorkloadError):
            workload.restore_snapshot(self.snapshot, self.sdk)
        self.assertEqual(link.readlink(), Path("different-target"))

    def test_missing_snapshot_is_rejected_without_creating_live_sdk(self):
        clean = self.root / "clean-sdk"
        with self.assertRaises(workload.WorkloadError):
            workload.restore_snapshot(self.snapshot, clean)
        self.assertFalse(clean.exists())

    def test_powershell_fallback_handles_missing_snapshot_and_discard_exit_code(self):
        pwsh = shutil.which("pwsh")
        if not pwsh:
            self.skipTest("PowerShell Core is unavailable")
        runner_temp = self.root / "runner-temp"
        snapshot = runner_temp / workload.SNAPSHOT_NAME
        clean = self.root / "clean-sdk"
        script = Path(__file__).resolve().parents[1] / "dotnet-workload.py"
        command = f'''\
python "{script}" --snapshot-operation restore --snapshot-root "$env:SNAPSHOT_ROOT" --install-root "$env:INSTALL_ROOT"
$restoreCode = $LASTEXITCODE
if ($restoreCode -eq 2) {{
  python "{script}" --snapshot-operation discard --snapshot-root "$env:SNAPSHOT_ROOT"
  if ($LASTEXITCODE -ne 0) {{ exit 1 }}
}} elseif ($restoreCode -ne 0) {{ exit $restoreCode }}
'''
        environment = dict(os.environ)
        environment.update({"RUNNER_TEMP": str(runner_temp), "SNAPSHOT_ROOT": str(snapshot), "INSTALL_ROOT": str(clean)})
        result = subprocess.run([pwsh, "-NoProfile", "-Command", command], capture_output=True, text=True, env=environment)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(clean.exists())

    def test_discard_only_removes_owned_snapshot_not_parent_or_sdk(self):
        self.capture()
        workload.discard_snapshot(self.snapshot, self.root / "runner-temp")
        self.assertFalse(self.snapshot.exists())
        self.assertTrue((self.root / "runner-temp").is_dir())
        self.assertTrue(self.sdk.is_dir())
        with self.assertRaises(workload.WorkloadError):
            workload.discard_snapshot(self.sdk, self.root)
        with self.assertRaises(workload.WorkloadError):
            workload.capture_snapshot(self.sdk, self.sdk / "nested-snapshot")

    def test_install_is_unconditional_and_failed_install_cannot_capture_snapshot(self):
        action = (Path(__file__).resolve().parents[3] / ".github/actions/setup-dotnet-workload/action.yml").read_text(encoding="utf-8")
        install = action.index('dotnet workload install "$env:WORKLOAD_ID"')
        capture = action.index("--snapshot-operation capture")
        self.assertLess(install, capture)
        self.assertIn('if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }', action[install:capture])
        self.assertNotIn("dotnet workload repair", action)
        self.assertIn("dotnet workload install", action)
        self.assertIn('if ($env:CACHE_HIT -ne "true") {', action[install:capture])
        self.assertIn("$restoreCode -eq 2", action)
        self.assertIn("$global:LASTEXITCODE = 0", action)


if __name__ == "__main__":
    unittest.main()
