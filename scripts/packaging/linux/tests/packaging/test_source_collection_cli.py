"""Production entry-point smoke tests for collect-sources.sh.

Tests the actual collect-sources.sh entry point with stubbed external
commands (git, apt-get source) to verify archive layout, error handling,
copyright processing, and manifest parsing without network access.

Must run on Linux (Ubuntu) with bash and tar available.  On non-Linux
hosts every test is skipped (the script is bash-only).

Test inventory:
  TestCollectorArchiveLayout   — 5  (archive tree structure + fetch_source inclusion)
  TestCollectorCopyright       — 3  (arch stripping, fail on missing)
  TestCollectorErrorHandling   — 5  (missing manifest, bad versions, apt update fatal)
  TestCollectorManifestParsing — 3  (section parsing, empty manifest)
  TestNoticesCliRejection      — 2  (unknown ELF rejection via CLI)
"""

import hashlib
import os
import shutil
import subprocess
import tarfile
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
LINUX_DIR = REPO_ROOT / "scripts" / "packaging" / "linux"


def _is_linux() -> bool:
    try:
        return os.name == "posix" and "linux" in os.uname().sysname.lower()
    except (AttributeError, OSError):
        return False


def _skip_unless_linux() -> None:
    if not _is_linux():
        raise unittest.SkipTest("Linux-only: requires bash + tar")


def _make_manifest(path: Path, lines: list[str]) -> None:
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def _make_stubs(tmpdir: Path, apt_update_fails: bool = False) -> Path:
    """Create stub executables for git, apt-get, sudo, tee, and sed.

    git:      clone/fetch/checkout/rev-parse → minimal Photino-like tree
    apt-get:  source --download-only         → valid .dsc/.orig/.debian fixtures
              update                         → no-op (or fail if apt_update_fails)
    sudo:     pass-through (execute the command that follows)
    tee:      write stdin to the given file
    sed:      no-op (deb-src enablement)
    """
    stubs = tmpdir / "stubs"
    stubs.mkdir(parents=True)

    (stubs / "git").write_text(textwrap.dedent("""\
        #!/usr/bin/env bash
        case "$1" in
            clone)
                dest="${!#}"
                mkdir -p "$dest/Photino.Native"
                echo "stub photino source" > "$dest/Photino.Native/Photino.Linux.cpp"
                ;;
            fetch|checkout) ;;
            rev-parse) echo "fixture-source-revision" ;;
        esac
    """), encoding="utf-8")
    (stubs / "git").chmod(0o755)

    apt_update_rc = "exit 1" if apt_update_fails else "exit 0"

    # Built with a placeholder instead of an f-string so the bash body needs
    # no brace escaping and every line keeps the same indent (dedent-safe).
    apt_stub = textwrap.dedent("""\
        #!/usr/bin/env bash
        set -u
        if [ "${1:-}" = "source" ]; then
            # Find the name=version spec by position, not a fixed index:
            # flags such as --download-only/--only-source may come first.
            spec=""
            for arg in "$@"; do
                case "$arg" in
                    *=*) spec="$arg"; break ;;
                esac
            done
            if [ -z "$spec" ]; then echo "E: no source spec" >&2; exit 1; fi
            name="${spec%%=*}"
            ver="${spec#*=}"
            # Descriptor name uses the upstream version (epoch dropped), while
            # the Version field keeps the epoch.
            file_ver="${ver#*:}"
            orig_name="${name}_${file_ver}.orig.tar.gz"
            deb_name="${name}_${file_ver}.debian.tar.xz"
            # Real payload bytes so fetch_source.py SHA256/size checks pass.
            printf 'orig' > "$orig_name"
            printf 'debian' > "$deb_name"
            {
                printf 'Format: 3.0 (quilt)\\n'
                printf 'Source: %s\\n' "$name"
                printf 'Version: %s\\n' "$ver"
                printf 'Checksums-Sha256:\\n'
                printf ' %s 4 %s\\n' "$(printf 'orig' | sha256sum | cut -d' ' -f1)" "$orig_name"
                printf ' %s 6 %s\\n' "$(printf 'debian' | sha256sum | cut -d' ' -f1)" "$deb_name"
            } > "${name}_${file_ver}.dsc"
            exit 0
        fi
        if [ "${1:-}" = "update" ]; then
            __APT_UPDATE_RC__
        fi
        exit 0
    """).replace("__APT_UPDATE_RC__", apt_update_rc)
    (stubs / "apt-get").write_text(apt_stub, encoding="utf-8")
    (stubs / "apt-get").chmod(0o755)

    # sudo: pass-through — execute the command after "sudo"
    (stubs / "sudo").write_text(textwrap.dedent("""\
        #!/usr/bin/env bash
        exec "$@"
    """), encoding="utf-8")
    (stubs / "sudo").chmod(0o755)

    # tee: write stdin to the file argument (for deb-src config creation)
    (stubs / "tee").write_text(textwrap.dedent("""\
        #!/usr/bin/env bash
        # Find the file argument (after any flags)
        target=""
        for arg in "$@"; do
            case "$arg" in
                -*) ;;
                *) target="$arg"; break ;;
            esac
        done
        if [[ -n "$target" ]]; then
            mkdir -p "$(dirname "$target")"
            cat > "$target"
        else
            cat > /dev/null
        fi
    """), encoding="utf-8")
    (stubs / "tee").chmod(0o755)

    (stubs / "sed").write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
    (stubs / "sed").chmod(0o755)

    return stubs


def _run_collector(
    manifest: Path, output: Path, stubs: Path,
    apt_sources_dir: Path | None = None,
    extra_env: dict | None = None,
) -> subprocess.CompletedProcess:
    # Redirect the script-owned apt config into a temp directory so tests
    # never read or write the real /etc/apt tree, and stub apt-get so no
    # network fetch or privileged package operation ever happens.  Defaults
    # to a directory beside --output-dir, which every test owns and cleans up.
    env = {**os.environ, "PATH": f"{stubs}:{os.environ.get('PATH', '')}"}
    apt_dir = apt_sources_dir or (output.parent / "apt-sources.d")
    apt_dir.mkdir(parents=True, exist_ok=True)
    env["APT_SOURCES_DIR"] = str(apt_dir)
    env["APT_KEYRING"] = str(apt_dir / "ubuntu-archive-keyring.gpg")
    if extra_env:
        env.update(extra_env)
    return subprocess.run(
        [
            "bash",
            str(LINUX_DIR / "collect-sources.sh"),
            "--version", "1.0.6",
            "--output-dir", str(output),
            "--manifest", str(manifest),
        ],
        capture_output=True, text=True, timeout=60, env=env,
    )


def _get_real_pkg_version(pkg: str) -> str | None:
    """Query dpkg for an installed package version (Linux only)."""
    try:
        r = subprocess.run(
            ["dpkg-query", "-W", "-f=${Version}", pkg],
            capture_output=True, text=True, timeout=5,
        )
        if r.returncode == 0 and r.stdout.strip():
            return r.stdout.strip()
    except (OSError, subprocess.TimeoutExpired, FileNotFoundError):
        pass
    return None


# ── TestCollectorArchiveLayout ───────────────────────────────────────────────


class TestCollectorArchiveLayout(unittest.TestCase):
    """Verify the fixed archive layout with build-recipes/ tree."""

    def setUp(self) -> None:
        _skip_unless_linux()
        self.tmpdir = Path(tempfile.mkdtemp(prefix="collect-layout-"))
        self.manifest = self.tmpdir / "package-manifest.txt"
        self.output = self.tmpdir / "output"
        self.output.mkdir()
        self.stubs = _make_stubs(self.tmpdir)

    def tearDown(self) -> None:
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _run_with_real_pkg(self, pkg: str = "libc6"):
        ver = _get_real_pkg_version(pkg) or "2.35-0ubuntu3"
        _make_manifest(self.manifest, [
            "# Test manifest",
            "## System Packages",
            "",
            f"{pkg} | {ver} | glibc | {ver} | 1",
        ])
        result = _run_collector(self.manifest, self.output, self.stubs)
        tar_path = self.output / "mqttprobe-third-party-sources-v1.0.6.tar.gz"
        return result, tar_path

    def _tar_names(self, tar_path: Path) -> list[str]:
        with tarfile.open(tar_path) as tf:
            return tf.getnames()

    def test_archive_contains_build_recipes_tree(self):
        """build-recipes/scripts/packaging/linux/appdir.sh must exist."""
        r, tar_path = self._run_with_real_pkg()
        self.assertEqual(r.returncode, 0, f"Script failed:\n{r.stderr}\n{r.stdout}")
        self.assertTrue(tar_path.exists(), "Archive not created")
        names = self._tar_names(tar_path)
        self.assertTrue(
            any("build-recipes/scripts/packaging/linux/appdir.sh" in n for n in names),
            f"appdir.sh not at expected path. First entries: {names[:20]}",
        )

    def test_archive_contains_fetch_source_in_recipes(self):
        """fetch_source.py must be under build-recipes/scripts/packaging/linux/."""
        r, tar_path = self._run_with_real_pkg()
        self.assertEqual(r.returncode, 0, f"Script failed:\n{r.stderr}\n{r.stdout}")
        names = self._tar_names(tar_path)
        self.assertTrue(
            any("build-recipes/scripts/packaging/linux/fetch_source.py" in n for n in names),
            f"fetch_source.py not in build-recipes. Entries: {names[:20]}",
        )

    def test_archive_contains_vendor_under_recipes(self):
        """Vendor files must be under build-recipes/.../vendor/."""
        r, tar_path = self._run_with_real_pkg()
        self.assertEqual(r.returncode, 0, r.stderr)
        names = self._tar_names(tar_path)
        self.assertTrue(
            any("build-recipes/scripts/packaging/linux/vendor/NOTICE.md" in n for n in names),
            f"vendor/NOTICE.md not under build-recipes. Entries: {names[:20]}",
        )

    def test_archive_contains_workflow_under_recipes(self):
        """Workflow must be under build-recipes/.github/workflows/."""
        r, tar_path = self._run_with_real_pkg()
        self.assertEqual(r.returncode, 0, r.stderr)
        names = self._tar_names(tar_path)
        self.assertTrue(
            any("build-recipes/.github/workflows/build-linux-desktop.yml" in n for n in names),
            f"Workflow not under build-recipes. Entries: {names[:20]}",
        )

    def test_no_flat_vendor_or_build_scripts(self):
        """Old flat vendor-scripts/ and build-scripts/ must not appear."""
        r, tar_path = self._run_with_real_pkg()
        self.assertEqual(r.returncode, 0, r.stderr)
        names = self._tar_names(tar_path)
        self.assertFalse(
            any("/vendor-scripts/" in n for n in names),
            f"Old flat vendor-scripts/ found: {[n for n in names if 'vendor-scripts' in n]}",
        )
        self.assertFalse(
            any("/build-scripts/" in n for n in names),
            f"Old flat build-scripts/ found: {[n for n in names if 'build-scripts' in n]}",
        )
        self.assertFalse(
            any("/ci-workflow/" in n for n in names),
            f"Old flat ci-workflow/ found: {[n for n in names if 'ci-workflow' in n]}",
        )


# ── TestCollectorCopyright ──────────────────────────────────────────────────


class TestCollectorCopyright(unittest.TestCase):
    """Copyright handling: arch stripping and fail on missing."""

    def setUp(self) -> None:
        _skip_unless_linux()
        self.tmpdir = Path(tempfile.mkdtemp(prefix="collect-copyr-"))
        self.manifest = self.tmpdir / "package-manifest.txt"
        self.output = self.tmpdir / "output"
        self.output.mkdir()
        self.stubs = _make_stubs(self.tmpdir)

    def tearDown(self) -> None:
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_copyright_strips_arch_suffix(self):
        """libc6:amd64 must look up /usr/share/doc/libc6/copyright."""
        ver = _get_real_pkg_version("libc6") or "2.35-0ubuntu3"
        _make_manifest(self.manifest, [
            "# Test", "## System Packages", "",
            f"libc6:amd64 | {ver} | glibc | {ver} | 1",
        ])
        r = _run_collector(self.manifest, self.output, self.stubs)
        self.assertEqual(r.returncode, 0, f"Failed:\n{r.stderr}\n{r.stdout}")
        tar_path = self.output / "mqttprobe-third-party-sources-v1.0.6.tar.gz"
        with tarfile.open(tar_path) as tf:
            names = tf.getnames()
        self.assertTrue(
            any("copyright/libc6:amd64.copyright" in n for n in names),
            f"Copyright with arch suffix not found. Entries: {names[:20]}",
        )

    def test_missing_copyright_fails_not_warns(self):
        """Missing required copyright must exit non-zero (FATAL)."""
        _make_manifest(self.manifest, [
            "# Test", "## System Packages", "",
            "nonexistent-pkg | 1.0 | nonexistent-src | 1.0 | 1",
        ])
        r = _run_collector(self.manifest, self.output, self.stubs)
        self.assertNotEqual(r.returncode, 0, "Should fail for missing copyright")
        combined = (r.stdout + r.stderr).lower()
        self.assertIn("copyright", combined, "Error should mention copyright")
        self.assertIn("fatal", combined, "Should be FATAL not WARNING")

    def test_common_licenses_not_fabricated(self):
        """Common licenses section copies available ones, doesn't fail for missing."""
        ver = _get_real_pkg_version("libc6") or "2.35-0ubuntu3"
        _make_manifest(self.manifest, [
            "# Test", "## System Packages", "",
            f"libc6 | {ver} | glibc | {ver} | 1",
        ])
        r = _run_collector(self.manifest, self.output, self.stubs)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("common license", r.stdout.lower())


# ── TestCollectorErrorHandling ──────────────────────────────────────────────


class TestCollectorErrorHandling(unittest.TestCase):
    """Error conditions: missing manifest, bad versions, apt update failure."""

    def setUp(self) -> None:
        _skip_unless_linux()
        self.tmpdir = Path(tempfile.mkdtemp(prefix="collect-err-"))
        self.output = self.tmpdir / "output"
        self.output.mkdir()
        self.stubs = _make_stubs(self.tmpdir)

    def tearDown(self) -> None:
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _env(self) -> dict:
        """Isolated env: stubbed tools plus a temp APT_SOURCES_DIR."""
        apt_dir = self.tmpdir / "apt-sources.d"
        apt_dir.mkdir(parents=True, exist_ok=True)
        return {
            **os.environ,
            "PATH": f"{self.stubs}:{os.environ.get('PATH', '')}",
            "APT_SOURCES_DIR": str(apt_dir),
            "APT_KEYRING": str(apt_dir / "ubuntu-archive-keyring.gpg"),
        }

    def test_missing_manifest_flag_exits(self):
        """--manifest required; must exit before git operations."""
        env = self._env()
        r = subprocess.run(
            [
                "bash", str(LINUX_DIR / "collect-sources.sh"),
                "--version", "1.0.6", "--output-dir", str(self.output),
            ],
            capture_output=True, text=True, timeout=10, env=env,
        )
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("--manifest", r.stdout + r.stderr)

    def test_nonexistent_manifest_file_exits(self):
        """Non-existent manifest path must exit with error."""
        env = self._env()
        r = subprocess.run(
            [
                "bash", str(LINUX_DIR / "collect-sources.sh"),
                "--version", "1.0.6", "--output-dir", str(self.output),
                "--manifest", str(self.tmpdir / "no-such-file.txt"),
            ],
            capture_output=True, text=True, timeout=10, env=env,
        )
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("not found", (r.stdout + r.stderr).lower())

    def test_malformed_unknown_version_fails(self):
        """Source version 'unknown' in manifest must fail."""
        manifest = self.tmpdir / "manifest.txt"
        _make_manifest(manifest, [
            "# Test", "## System Packages", "",
            "bad-pkg | 1.0 | bad-src | unknown | 1",
        ])
        r = _run_collector(manifest, self.output, self.stubs)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("source version", (r.stdout + r.stderr).lower())

    def test_conflicting_source_versions_fails(self):
        """Same source package with different versions must fail."""
        manifest = self.tmpdir / "manifest.txt"
        _make_manifest(manifest, [
            "# Test", "## System Packages", "",
            "pkg1 | 1.0 | shared-src | 1.0 | 1",
            "pkg2 | 2.0 | shared-src | 2.0 | 1",
        ])
        r = _run_collector(manifest, self.output, self.stubs)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("conflicting", (r.stdout + r.stderr).lower())

    def test_apt_update_failure_is_fatal(self):
        """apt-get update failure must be fatal, not a warning."""
        # Use stubs where apt-get update fails
        stubs = _make_stubs(self.tmpdir / "fail-stubs", apt_update_fails=True)
        manifest = self.tmpdir / "manifest.txt"
        ver = _get_real_pkg_version("libc6") or "2.35-0ubuntu3"
        _make_manifest(manifest, [
            "# Test", "## System Packages", "",
            f"libc6 | {ver} | glibc | {ver} | 1",
        ])
        r = _run_collector(manifest, self.output, stubs)
        self.assertNotEqual(r.returncode, 0,
                            "apt-get update failure must be fatal")
        combined = (r.stdout + r.stderr).lower()
        self.assertIn("fatal", combined,
                      "Must say FATAL not WARNING for apt update failure")


# ── TestCollectorManifestParsing ────────────────────────────────────────────


class TestCollectorManifestParsing(unittest.TestCase):
    """Manifest section parsing: empty, vendor-only, vendor+system."""

    def setUp(self) -> None:
        _skip_unless_linux()
        self.tmpdir = Path(tempfile.mkdtemp(prefix="collect-parse-"))
        self.manifest = self.tmpdir / "manifest.txt"
        self.output = self.tmpdir / "output"
        self.output.mkdir()
        self.stubs = _make_stubs(self.tmpdir)

    def tearDown(self) -> None:
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_empty_system_packages_fails(self):
        """No packages between ## System Packages and next section → fail."""
        _make_manifest(self.manifest, [
            "# Test", "## System Packages", "",
            "## Vendor Ingredients",
        ])
        r = _run_collector(self.manifest, self.output, self.stubs)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("no packages", (r.stdout + r.stderr).lower())

    def test_ignores_vendor_ingredients_section(self):
        """Vendor Ingredients lines must not be parsed as system packages."""
        ver = _get_real_pkg_version("libc6") or "2.35-0ubuntu3"
        _make_manifest(self.manifest, [
            "# Test", "## System Packages", "",
            f"libc6 | {ver} | glibc | {ver} | 1",
            "", "## Vendor Ingredients", "",
            "Photino.Native 4.0.22 (Apache-2.0) - test",
        ])
        r = _run_collector(self.manifest, self.output, self.stubs)
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_record_line_format_parsed_correctly(self):
        """Manifest record fields (bin|binver|src|srcver|count) must parse."""
        ver = _get_real_pkg_version("libc6") or "2.35-0ubuntu3"
        _make_manifest(self.manifest, [
            "# Test", "## System Packages", "",
            f"libc6 | {ver} | glibc | {ver} | 42",
        ])
        r = _run_collector(self.manifest, self.output, self.stubs)
        self.assertEqual(r.returncode, 0, r.stderr)
        tar_path = self.output / "mqttprobe-third-party-sources-v1.0.6.tar.gz"
        self.assertTrue(tar_path.exists())
        with tarfile.open(tar_path) as tf:
            names = tf.getnames()
        self.assertTrue(
            any("package-versions.txt" in n for n in names),
            "package-versions.txt not in archive",
        )


# ── TestNoticesCliRejection ─────────────────────────────────────────────────


class TestNoticesCliRejection(unittest.TestCase):
    """notices.py --strict-unknown ELF rejection via CLI."""

    def setUp(self) -> None:
        _skip_unless_linux()
        self.tmpdir = Path(tempfile.mkdtemp(prefix="notices-cli-"))

    def tearDown(self) -> None:
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _make_fake_appdir(self, with_unknown_elf: bool = True) -> Path:
        """Create a minimal AppDir for notices.py testing."""
        appdir = self.tmpdir / "AppDir"
        bindir = appdir / "usr" / "bin"
        bindir.mkdir(parents=True)

        (bindir / "MqttProbe.Desktop").write_bytes(b"\x7fELF" + b"\x00" * 64)
        (bindir / "MqttProbe.Desktop").chmod(0o755)

        if with_unknown_elf:
            (bindir / "libunknown-foo.so").write_bytes(b"\x7fELF" + b"\x00" * 64)

        return appdir

    def test_strict_unknown_rejects_unknown_elf(self):
        """--strict-unknown must exit non-zero when unknown ELFs are present."""
        appdir = self._make_fake_appdir(with_unknown_elf=True)
        outdir = self.tmpdir / "output"
        outdir.mkdir()

        r = subprocess.run(
            [
                "python3", str(LINUX_DIR / "notices.py"),
                "--appdir", str(appdir),
                "--version", "1.0.6",
                "--source-archive", "test.tar.gz",
                "--output-dir", str(outdir),
                "--strict-unknown",
            ],
            capture_output=True, text=True, timeout=30,
        )
        self.assertNotEqual(r.returncode, 0, "Should reject unknown ELF")
        self.assertIn("unknown", (r.stdout + r.stderr).lower())

    def test_not_strict_allows_unknown_elf(self):
        """Without --strict-unknown, unknown ELFs are allowed."""
        appdir = self._make_fake_appdir(with_unknown_elf=True)
        outdir = self.tmpdir / "output"
        outdir.mkdir()

        r = subprocess.run(
            [
                "python3", str(LINUX_DIR / "notices.py"),
                "--appdir", str(appdir),
                "--version", "1.0.6",
                "--source-archive", "test.tar.gz",
                "--output-dir", str(outdir),
            ],
            capture_output=True, text=True, timeout=30,
        )
        self.assertEqual(r.returncode, 0, f"Should succeed:\n{r.stderr}")


if __name__ == "__main__":
    unittest.main()