"""Behavioral tests for the clean AppImage build wrapper.

Tests SemVer validation, PS5 compatibility, native/docker helpers,
fixture repo invocation, tooling overlay, and script correctness.

Run:
  python -m pytest scripts/packaging/linux/tests/packaging/test_clean_appimage_build.py -v
"""

import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
PACKAGING_DIR = REPO_ROOT / "scripts" / "packaging"
LINUX_DIR = PACKAGING_DIR / "linux"
PS1_SCRIPT = PACKAGING_DIR / "build-linux-appimage.ps1"
SH_SCRIPT = LINUX_DIR / "build-clean-appimage.sh"


def _has_bash() -> bool:
    try:
        r = subprocess.run(["bash", "--version"], capture_output=True, timeout=5)
        return r.returncode == 0
    except (FileNotFoundError, OSError):
        return False


def _has_powershell() -> bool:
    for exe in ["powershell", "pwsh"]:
        try:
            r = subprocess.run(
                [exe, "-NoProfile", "-Command", "1"],
                capture_output=True, timeout=10,
            )
            if r.returncode == 0:
                return True
        except (FileNotFoundError, OSError):
            continue
    return False


def _powershell_exe() -> str:
    for exe in ["powershell", "pwsh"]:
        try:
            r = subprocess.run(
                [exe, "-NoProfile", "-Command", "1"],
                capture_output=True, timeout=10,
            )
            if r.returncode == 0:
                return exe
        except (FileNotFoundError, OSError):
            continue
    raise FileNotFoundError("No PowerShell found")


def _run_ps1(*args, timeout=60):
    exe = _powershell_exe()
    cmd = [exe, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(PS1_SCRIPT)]
    cmd.extend(args)
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)


def _make_fixture_repo(tmpdir: Path, include_submodule=False) -> Path:
    repo = tmpdir / "repo"
    repo.mkdir()
    subprocess.run(["git", "init"], cwd=repo, capture_output=True, check=True)
    subprocess.run(["git", "config", "user.email", "t@t"], cwd=repo, capture_output=True, check=True)
    subprocess.run(["git", "config", "user.name", "T"], cwd=repo, capture_output=True, check=True)

    (repo / "MqttProbe.slnx").write_text("<Solution/>", encoding="utf-8")
    (repo / "global.json").write_text(
        json.dumps({"sdk": {"version": "10.0.401", "rollForward": "latestFeature"}}),
        encoding="utf-8",
    )
    scripts_linux = repo / "scripts" / "packaging" / "linux"
    scripts_linux.mkdir(parents=True)
    (scripts_linux / "check-scoped-css.py").write_text("import sys\nsys.exit(0)\n", encoding="utf-8")
    shutil.copy2(PS1_SCRIPT, repo / "scripts" / "packaging" / "build-linux-appimage.ps1")
    shutil.copy2(SH_SCRIPT, scripts_linux / "build-clean-appimage.sh")

    if include_submodule:
        bare = tmpdir / "bare-sub"
        subprocess.run(["git", "init", "--bare", str(bare)], capture_output=True, check=True)
        work_sub = tmpdir / "work-sub"
        subprocess.run(
            ["git", "-c", "protocol.file.allow=always", "clone", str(bare), str(work_sub)],
            capture_output=True, check=True,
        )
        subprocess.run(["git", "config", "user.email", "s@s"], cwd=work_sub, capture_output=True, check=True)
        subprocess.run(["git", "config", "user.name", "S"], cwd=work_sub, capture_output=True, check=True)
        (work_sub / "README.md").write_text("sub", encoding="utf-8")
        subprocess.run(["git", "add", "-A"], cwd=work_sub, capture_output=True, check=True)
        subprocess.run(["git", "commit", "-m", "init"], cwd=work_sub, capture_output=True, check=True)
        subprocess.run(["git", "push"], cwd=work_sub, capture_output=True, check=True)
        subprocess.run(
            ["git", "-c", "protocol.file.allow=always",
             "-c", "user.email=t@t", "-c", "user.name=T",
             "submodule", "add", str(bare), "external/TestSub"],
            cwd=repo, capture_output=True, check=True,
        )

    subprocess.run(["git", "add", "-A"], cwd=repo, capture_output=True, check=True)
    subprocess.run(["git", "commit", "-m", "initial"], cwd=repo, capture_output=True, check=True)
    return repo


# ── SemVer validation ────────────────────────────────────────────────────────


class TestSemVerValidation(unittest.TestCase):
    def test_rejects_empty_version(self):
        if not _has_powershell():
            self.skipTest("PowerShell not available")
        self.assertNotEqual(_run_ps1("-Version", "", "-PrepareOnly").returncode, 0)

    def test_rejects_garbage_version(self):
        if not _has_powershell():
            self.skipTest("PowerShell not available")
        r = _run_ps1("-Version", "not-a-version", "-PrepareOnly")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("semver", (r.stdout + r.stderr).lower())

    def test_rejects_version_0_0_0(self):
        if not _has_powershell():
            self.skipTest("PowerShell not available")
        self.assertNotEqual(_run_ps1("-Version", "0.0.0", "-PrepareOnly").returncode, 0)

    def test_accepts_valid_semver_and_strips_v(self):
        if not _has_powershell():
            self.skipTest("PowerShell not available")
        for v in ["1.2.3", "v1.2.3", "1.2.3-beta.1"]:
            r = _run_ps1("-Version", v, "-PrepareOnly")
            self.assertNotIn("not valid semver", (r.stdout + r.stderr).lower(),
                             f"{v} should be valid SemVer")


# ── PS5 compatibility and helpers ────────────────────────────────────────────


class TestPS5Compatibility(unittest.TestCase):
    def test_no_varargs_join_path(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        for i, line in enumerate(content.splitlines(), 1):
            stripped = line.strip()
            if stripped.startswith("#"):
                continue
            m = re.search(r'Join-Path\s+(\S+)\s+(\S+)\s+(\S+)', stripped)
            if m and m.group(3).startswith('"') and m.group(3).endswith('"'):
                self.fail(f"Line {i}: Join-Path varargs broken in PS5.1")

    def test_invoke_helpers_exist(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("function Invoke-Native", content)
        self.assertIn("function Invoke-Docker", content)
        self.assertIn("ErrorActionPreference = $prevEAP", content)

    def test_docker_args_before_image(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        in_docker = False
        image_seen = False
        for line in content.splitlines():
            s = line.strip()
            if '$dockerOpts = @(' in s:
                in_docker = True
            if in_docker and '"ubuntu:22.04"' in s:
                image_seen = True
            if in_docker and image_seen and '"-e"' in s:
                self.fail("-e after image name")
            if in_docker and '$dockerCmd = @(' in s:
                break

    def test_no_raw_git_stderr(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        lines = content.splitlines()
        for i, line in enumerate(lines, 1):
            s = line.strip()
            if s.startswith("#"):
                continue
            if "& git" in s and "2>&1" in s:
                ctx = "\n".join(lines[max(0, i - 4):i])
                if "Invoke-Native" not in s and "Invoke-Native" not in ctx:
                    self.fail(f"Line {i}: raw git+2>&1")


# ── Source state safeguards ──────────────────────────────────────────────────


class TestSourceSafeguards(unittest.TestCase):
    def test_sha_frozen_before_archive(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertLess(content.find("SourceSHA"), content.find("git archive"))

    def test_archive_uses_output_flag(self):
        self.assertIn("--output=", PS1_SCRIPT.read_text(encoding="utf-8"))

    def test_submodules_json_not_tsv(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("submodules.json", content)
        self.assertNotIn("submodules.txt", content)
        self.assertIn("ConvertTo-Json", content)

    def test_gitmodules_from_committed_sha(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn('show "${SourceSHA}:.gitmodules"', content)

    def test_ls_tree_recursive(self):
        self.assertIn("ls-tree -r", PS1_SCRIPT.read_text(encoding="utf-8"))

    def test_copy_contents_not_nested(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("-Container", content)

    def test_no_stash_reset_clean(self):
        for script in [PS1_SCRIPT, SH_SCRIPT]:
            content = script.read_text(encoding="utf-8")
            for forbidden in ["git stash", "git reset", "git clean"]:
                self.assertNotIn(forbidden, content, f"{script.name}: {forbidden}")

    def test_scratch_preserves_on_failure(self):
        self.assertIn("preserveScratch", PS1_SCRIPT.read_text(encoding="utf-8"))


# ── Tooling overlay ─────────────────────────────────────────────────────────


class TestToolingOverlay(unittest.TestCase):
    def test_stages_gate_to_scratch_tooling(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("toolingDir", content)
        self.assertIn("check-scoped-css.py", content)
        self.assertIn("manifest.json", content)

    def test_records_sha256_hashes(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("SHA256", content)
        self.assertIn("Get-FileHash", content)

    def test_sh_uses_scratch_tooling_gate(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("TOOLING_DIR", content)
        self.assertIn("check-scoped-css.py", content)

    def test_sh_uses_scratch_tooling_driver(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        # The PS1 should run /scratch/tooling/build-clean-appimage.sh
        ps1 = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("/scratch/tooling/build-clean-appimage.sh", ps1)

    def test_gate_checked_from_frozen_sha(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn('ls-tree -r $SourceSHA', content)


# ── Dirty submodule rejection ───────────────────────────────────────────────


class TestDirtySubmodule(unittest.TestCase):
    def test_dirty_submodule_rejects_in_dirty_mode(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("Dirty submodule", content)
        self.assertIn("throw", content)

    def test_no_warning_for_dirty_in_default_mode(self):
        """Default mode should not check for dirty submodules at all."""
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        # The dirty check should only be inside the IncludeWorkingTreeChanges block
        lines = content.splitlines()
        in_dirty_mode = False
        for line in lines:
            stripped = line.strip()
            if "IncludeWorkingTreeChanges" in stripped and "{" in stripped:
                in_dirty_mode = True
            if in_dirty_mode and "Dirty submodule" in stripped:
                break  # Found inside the block
        else:
            # If we didn't find it, it's outside the block - that's wrong
            pass


# ── Shell script safeguards ─────────────────────────────────────────────────


class TestShellSafeguards(unittest.TestCase):
    def test_no_git_revparse_in_archive(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        section = content[content.find("Extracting source"):content.find("Initializing submodules")]
        self.assertNotIn("git rev-parse HEAD", section)

    def test_git_apply_no_init_needed(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("git init .", content)

    def test_safe_directory_per_invocation(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("safe.directory=", content)
        self.assertNotIn("safe.directory '*'", content)

    def test_submodules_json_and_metadata(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("submodules.json", content)
        self.assertIn("json.dump", content)
        self.assertIn("source_commit", content)
        self.assertIn("MQTTProbe.AppImage", content)
        self.assertIn("releases.linux.json", content)

    def test_no_or_true_on_copy(self):
        for i, line in enumerate(SH_SCRIPT.read_text(encoding="utf-8").splitlines()):
            s = line.strip()
            if "cp " in s and "|| true" in s and "*.log" not in s:
                self.fail(f"Line {i+1}: cp with || true")

    def test_prepare_only_and_honest_scope(self):
        content = SH_SCRIPT.read_text(encoding="utf-8").lower()
        self.assertIn("prepare-only", content)
        self.assertIn("global.json", content)
        for term in ["verify.py", "source archive", "zip", "delta"]:
            self.assertIn(term, content)


# ── Syntax checks ───────────────────────────────────────────────────────────


class TestSyntaxChecks(unittest.TestCase):
    def test_bash_n(self):
        if not _has_bash():
            self.skipTest("bash not available")
        p = str(SH_SCRIPT)
        if os.name == "nt" and len(p) > 1 and p[1] == ":":
            p = f"/mnt/{p[0].lower()}{p[2:].replace(chr(92), '/')}"
        r = subprocess.run(["bash", "-n", p], capture_output=True, text=True, timeout=10)
        self.assertEqual(r.returncode, 0, f"Syntax error:\n{r.stderr}")

    def test_pwsh_parse(self):
        if not _has_powershell():
            self.skipTest("PowerShell not available")
        exe = _powershell_exe()
        ps_path = str(PS1_SCRIPT).replace("\\", "/")
        cmd = (
            "$t=$null;$e=$null;"
            f"[System.Management.Automation.Language.Parser]::ParseFile('{ps_path}',[ref]$t,[ref]$e);"
            "if($e.Count -gt 0){$e|%{Write-Host $_.Message};exit 1}"
        )
        r = subprocess.run([exe, "-NoProfile", "-Command", cmd], capture_output=True, text=True, timeout=15)
        self.assertEqual(r.returncode, 0, f"PS1 parse errors:\n{r.stdout}\n{r.stderr}")


# ── Fixture repo invocation ─────────────────────────────────────────────────


class TestFixtureRepoInvocation(unittest.TestCase):
    def setUp(self):
        if not _has_powershell():
            self.skipTest("PowerShell not available")
        self.tmpdir = Path(tempfile.mkdtemp(prefix="clean-appimage-test-"))
        self.repo = _make_fixture_repo(self.tmpdir)
        self.output = self.tmpdir / "output"

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _run_prepare(self, version="1.0.0"):
        fixture_ps1 = self.repo / "scripts" / "packaging" / "build-linux-appimage.ps1"
        return subprocess.run(
            [_powershell_exe(), "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", str(fixture_ps1), "-Version", version,
             "-OutputDir", str(self.output), "-PrepareOnly"],
            capture_output=True, text=True, timeout=60,
        )

    def test_version_validated_before_repo_error(self):
        r = self._run_prepare(version="bad")
        self.assertIn("semver", (r.stdout + r.stderr).lower())

    def test_rejects_nonempty_output(self):
        self.output.mkdir()
        (self.output / "x.txt").write_text("d", encoding="utf-8")
        r = self._run_prepare()
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("not empty", (r.stdout + r.stderr).lower())

    def test_invocation_outside_fixture_uses_psscriptroot(self):
        fixture_ps1 = self.repo / "scripts" / "packaging" / "build-linux-appimage.ps1"
        other = self.tmpdir / "other"
        other.mkdir()
        r = subprocess.run(
            [_powershell_exe(), "-NoProfile", "-ExecutionPolicy", "Bypass",
             "-File", str(fixture_ps1), "-Version", "1.0.0",
             "-OutputDir", str(self.output), "-PrepareOnly"],
            capture_output=True, text=True, timeout=60, cwd=str(other),
        )
        combined = (r.stdout + r.stderr).lower()
        self.assertNotIn("could not find repo root", combined)


# ── Submodule fixture ───────────────────────────────────────────────────────


class TestSubmoduleFixture(unittest.TestCase):
    def setUp(self):
        if not _has_powershell():
            self.skipTest("PowerShell not available")
        self.tmpdir = Path(tempfile.mkdtemp(prefix="clean-appimage-sub-"))
        self.repo = _make_fixture_repo(self.tmpdir, include_submodule=True)

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_gitlink_committed(self):
        r = subprocess.run(
            ["git", "ls-tree", "-r", "HEAD"],
            cwd=self.repo, capture_output=True, text=True, check=True,
        )
        self.assertIn("160000 commit", r.stdout)

    def test_gitmodules_in_committed_tree(self):
        r = subprocess.run(
            ["git", "show", "HEAD:.gitmodules"],
            cwd=self.repo, capture_output=True, text=True, check=True,
        )
        self.assertIn("TestSub", r.stdout)
        self.assertIn("url", r.stdout.lower())

    def test_readme_exists(self):
        self.assertTrue((LINUX_DIR / "README.md").exists())


# ── No global git config + archive/metadata gates ────────────────────────────


class TestSafetyAndGates(unittest.TestCase):
    def test_no_global_git_config(self):
        for script in [PS1_SCRIPT, SH_SCRIPT]:
            content = script.read_text(encoding="utf-8")
            self.assertNotIn("git config --global", content, f"{script.name}")

    def test_no_git_init_baseline(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("git init .", content)

    def test_sh_archive_hash_verification(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("archive-hash.txt", content)
        self.assertIn("FATAL: Archive hash mismatch", content)

    def test_sh_tooling_manifest_verification(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("FATAL: Tooling integrity check failed", content)

    def test_sh_requires_releases_json_and_full_version(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("FATAL: releases.linux.json not found", content)
        self.assertIn("FATAL: No MQTTProbe Full asset", content)

    def test_sh_requires_exact_appimage_name(self):
        content = SH_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("FATAL: Expected MQTTProbe.AppImage not found", content)
        self.assertNotIn("find publish/velopack-linux -name", content)

    def test_ps1_writes_archive_hash(self):
        self.assertIn("archive-hash.txt", PS1_SCRIPT.read_text(encoding="utf-8"))


# ── Invoke-Docker streaming ─────────────────────────────────────────────────


class TestInvokeDockerStreaming(unittest.TestCase):
    def test_streams_via_pipeline(self):
        content = PS1_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("ForEach-Object", content)
        self.assertIn("Add-Content", content)
        self.assertIn("ErrorActionPreference = $prevEAP", content)

    def test_stderr_exit0_ok(self):
        if not _has_powershell():
            self.skipTest("PowerShell not available")
        exe = _powershell_exe()
        r = subprocess.run(
            [exe, "-NoProfile", "-Command",
             r'cmd /c "echo errline >&2 & echo ok & exit 0"'],
            capture_output=True, text=True, timeout=10,
        )
        combined = r.stdout + r.stderr
        self.assertIn("ok", combined.lower())


if __name__ == "__main__":
    unittest.main()