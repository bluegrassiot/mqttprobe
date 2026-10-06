"""Tests for source archive collection, dpkg owner querying, and Launchpad fallback.

Covers exact-path precedence over fallback, relocation for native shared
libs, .dsc parsing/validation, and fetch_source.py helpers.
"""

import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import MagicMock, patch, call
from urllib.parse import urlsplit, urlunsplit

REPO_ROOT = Path(__file__).resolve().parents[5]
LINUX_DIR = REPO_ROOT / "scripts" / "packaging" / "linux"

if str(LINUX_DIR) not in sys.path:
    sys.path.insert(0, str(LINUX_DIR))

from manifest import (
    _query_dpkg_owner,
    _system_path_candidates,
    _query_dpkg_owner_with_relocation,
    _is_dotnet_runtime_file,
    generate_manifest,
    BundledFile,
    SystemPackage,
)

from fetch_source import (
    parse_dsc,
    verify_dsc_identity,
    fetch_source_from_launchpad,
    try_apt_source,
    _launchpad_dsc_url,
    _validate_safe,
    _validate_downloaded_package,
    _download,
    _check_redirect_url,
    _HttpsOnlyRedirectHandler,
    _ALLOWED_HOSTS,
    _SAFE_NAME_RE,
    _SAFE_VERSION_RE,
    _SAFE_FILENAME_RE,
    _MAX_DSC_BYTES,
    _MAX_TOTAL_BYTES,
    _dsc_filename,
    apt_config_options,
    write_apt_bootstrap_conf,
    fetch_source,
    DscInfo,
)


def _owned_sources(td: str, name: str = "owned.sources") -> Path:
    """Write a minimal owned, Signed-By deb822 sources file under a temp dir.

    Placed in its own subdirectory so tests can assert exactly what a fetch
    left behind in the destination without this fixture appearing in it.
    """
    cfg_dir = Path(td) / "aptcfg"
    cfg_dir.mkdir(parents=True, exist_ok=True)
    path = cfg_dir / name
    path.write_text(
        "Types: deb-src\n"
        "URIs: https://archive.ubuntu.com/ubuntu/\n"
        "Suites: jammy\n"
        "Components: main restricted universe multiverse\n"
        "Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n",
        encoding="utf-8",
    )
    return path


# -- Helpers -------------------------------------------------------------------

def _valid_dsc_content(
    source: str = "openssl",
    version: str = "3.0.2-0ubuntu1.29",
    payloads: list[tuple[str, int, bytes]] | None = None,
) -> tuple[str, list[tuple[str, int, bytes, str]]]:
    """Generate a valid .dsc string and payload fixtures.

    Returns (dsc_text, [(filename, size, content, sha256), ...]).
    """
    if payloads is None:
        payloads = [
            (f"{source}_{version.split(':', 1)[-1]}.orig.tar.gz", 100, b"A" * 100),
            (f"{source}_{version.replace(':', '_')}.debian.tar.xz", 200, b"B" * 200),
        ]
    checksum_lines = []
    payload_records = []
    for fname, size, content in payloads:
        sha256 = hashlib.sha256(content).hexdigest()
        checksum_lines.append(f" {sha256} {size} {fname}")
        payload_records.append((fname, size, content, sha256))

    dsc_text = (
        f"Format: 3.0 (quilt)\n"
        f"Source: {source}\n"
        f"Binary: libssl3, openssl\n"
        f"Architecture: any amd64\n"
        f"Version: {version}\n"
        f"Checksums-Sha256:\n"
        + "\n".join(checksum_lines) + "\n"
    )
    return dsc_text, payload_records


def _write_fixtures(dest: Path, dsc_text: str,
                    payloads: list[tuple[str, int, bytes, str]]) -> None:
    """Write .dsc and payload fixtures to dest."""
    dest.mkdir(parents=True, exist_ok=True)
    for fname, size, content, sha256 in payloads:
        (dest / fname).write_bytes(content)


# -- Test _query_dpkg_owner: exact-only, no glob fallback --------------------

class TestDpkgOwnerExactOnly(unittest.TestCase):
    """_query_dpkg_owner must only accept exact path matches."""

    @patch("manifest.subprocess.run")
    def test_exact_path_match(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\n"),
            MagicMock(returncode=0, stdout="3.24.33-1ubuntu2\tgtk+3.0\t3.24.33-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libgtk-3-0")
        first_call_args = mock_run.call_args_list[0][0][0]
        self.assertEqual(first_call_args, ["dpkg-query", "-S", "/usr/lib/x86_64-linux-gnu/libgtk-3.so.0"])

    @patch("manifest.subprocess.run")
    def test_different_path_returned_rejects(self, mock_run):
        """If dpkg returns a different path, it's not an exact match."""
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout="libpulse0:amd64: /usr/lib/x86_64-linux-gnu/pulseaudio/libpulsecommon-15.99.so\n",
        )
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libpulsecommon-15.99.so")
        self.assertIsNone(result)

    @patch("manifest.subprocess.run")
    def test_no_glob_fallback_attempted(self, mock_run):
        """Must NOT try */basename glob patterns."""
        mock_run.return_value = MagicMock(returncode=1, stdout="")
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so.1")
        self.assertIsNone(result)
        self.assertEqual(mock_run.call_count, 1)
        args = mock_run.call_args[0][0]
        self.assertNotIn("*/libfoo.so.1", args)

    @patch("manifest.subprocess.run")
    def test_diverted_returns_none(self, mock_run):
        """dpkg reports diversions as annotation lines, never as owners."""
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout="diversion by libbar from: /usr/lib/x86_64-linux-gnu/libfoo.so\n",
        )
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIsNone(result)

    @patch("manifest.subprocess.run")
    def test_local_diversion_returns_none(self, mock_run):
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout="local diversion from /usr/lib/x86_64-linux-gnu/libfoo.so\n",
        )
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIsNone(result)

    @patch("manifest.subprocess.run")
    def test_multiline_uses_first_line(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libgtk-3-0:amd64: /usr/lib/x86_64-linux-gnu/libgtk-3.so.0\nlibother: /some/path\n"),
            MagicMock(returncode=0, stdout="3.24.33-1ubuntu2\tgtk+3.0\t3.24.33-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libgtk-3-0")

    @patch("manifest.subprocess.run")
    def test_timeout_returns_none(self, mock_run):
        import subprocess
        mock_run.side_effect = subprocess.TimeoutExpired(cmd="dpkg-query", timeout=5)
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIsNone(result)


# -- Test _system_path_candidates: /lib counterparts included ----------------

class TestSystemPathCandidatesWithLib(unittest.TestCase):
    """_system_path_candidates must include /lib counterparts for /usr/lib paths."""

    def test_collapsed_lib_has_lib_counterpart(self):
        candidates = _system_path_candidates("usr/lib/libpulsecommon-15.99.so")
        self.assertEqual(candidates, [
            "/usr/lib/x86_64-linux-gnu/libpulsecommon-15.99.so",
            "/lib/x86_64-linux-gnu/libpulsecommon-15.99.so",
            "/usr/lib/libpulsecommon-15.99.so",
            "/lib/libpulsecommon-15.99.so",
        ])

    def test_multiarch_preserved(self):
        """Preserved multiarch keeps its /lib counterpart for usr-merge."""
        candidates = _system_path_candidates("usr/lib/x86_64-linux-gnu/libgtk-3.so.0")
        self.assertEqual(candidates, [
            "/usr/lib/x86_64-linux-gnu/libgtk-3.so.0",
            "/lib/x86_64-linux-gnu/libgtk-3.so.0",
        ])

    def test_usr_bin_path(self):
        candidates = _system_path_candidates("usr/bin/Photino.Native.so")
        self.assertEqual(candidates, ["/usr/bin/Photino.Native.so"])

    def test_empty_for_non_system(self):
        self.assertEqual(_system_path_candidates("AppRun"), [])


# -- Test _query_dpkg_owner_with_relocation: PulseAudio case -----------------

class TestDpkgOwnerRelocation(unittest.TestCase):
    """Relocation fallback for native shared libs in subdirectories."""

    @patch("manifest._query_dpkg_owner")
    def test_relocation_finds_pulseaudio(self, mock_owner):
        """A native staged lib relocates to its owner in the host subdirectory."""
        pulseaudio_pkg = SystemPackage(
            binary_package="libpulse0",
            binary_version="15.99.1-0ubuntu3",
            source_package="pulseaudio",
            source_version="15.99.1-0ubuntu3",
            license_hint="",
            bundled_files=[],
        )
        mock_owner.side_effect = [None, None, None, None, pulseaudio_pkg]
        with tempfile.TemporaryDirectory() as td:
            search_dir = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            search_dir.mkdir(parents=True)
            (search_dir / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF")
            with patch("manifest.os.walk") as mock_walk:
                mock_walk.side_effect = [
                    [(str(search_dir), [], ["libpulsecommon-15.99.so"])],
                    [],
                ]
                result = _query_dpkg_owner_with_relocation(
                    "usr/lib/libpulsecommon-15.99.so",
                    staged_is_native_lib=True)
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libpulse0")

    @patch("manifest._query_dpkg_owner")
    def test_relocation_ambiguous_same_basename_rejects(self, mock_owner):
        """Two genuinely distinct host files with one basename stay rejected."""
        mock_owner.side_effect = [None, None, None, None]
        with tempfile.TemporaryDirectory() as td:
            dir1 = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            dir1.mkdir(parents=True)
            (dir1 / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF")
            dir2 = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "other"
            dir2.mkdir(parents=True)
            (dir2 / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF")
            with patch("manifest.os.walk") as mock_walk:
                mock_walk.return_value = [
                    (str(dir1), [], ["libpulsecommon-15.99.so"]),
                    (str(dir2), [], ["libpulsecommon-15.99.so"]),
                ]
                result = _query_dpkg_owner_with_relocation(
                    "usr/lib/libpulsecommon-15.99.so",
                    staged_is_native_lib=True)
        self.assertIsNone(result)

    @patch("manifest._query_dpkg_owner")
    def test_relocation_skipped_for_staged_nonnative(self, mock_owner):
        """A staged resource never relocates, even onto a same-named host ELF.

        The gate is the staged file's own classification, not the host
        candidate's bytes: a bundled resource must not be claimed by a native
        library that happens to share its basename.
        """
        mock_owner.side_effect = [None, None, None, None]
        with tempfile.TemporaryDirectory() as td:
            search_dir = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            search_dir.mkdir(parents=True)
            # Host file really is an ELF; the staged file is not.
            (search_dir / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF")
            with patch("manifest.os.walk") as mock_walk:
                mock_walk.return_value = [
                    (str(search_dir), [], ["libpulsecommon-15.99.so"]),
                ]
                result = _query_dpkg_owner_with_relocation(
                    "usr/lib/libpulsecommon-15.99.so",
                    staged_is_native_lib=False)
        self.assertIsNone(result)
        # Only the four exact-path candidates were consulted.
        self.assertEqual(mock_owner.call_count, 4)

    @patch("manifest._query_dpkg_owner")
    def test_no_relocation_for_non_usr_lib(self, mock_owner):
        mock_owner.return_value = None
        result = _query_dpkg_owner_with_relocation("usr/bin/libfoo.so")
        self.assertIsNone(result)
        mock_owner.assert_called_once()

    @patch("manifest._query_dpkg_owner")
    def test_no_relocation_for_non_so(self, mock_owner):
        mock_owner.return_value = None
        result = _query_dpkg_owner_with_relocation("usr/lib/libfoo.txt")
        self.assertIsNone(result)


# -- Test .NET classification takes precedence --------------------------------

class TestDotnetPrecedence(unittest.TestCase):
    """Dotnet runtime files are classified as vendor, not attributed to host."""

    def test_dotnet_lib_in_usr_lib_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/lib/libcoreclr.so"))

    def test_dotnet_lib_in_usr_bin_is_dotnet(self):
        self.assertTrue(_is_dotnet_runtime_file("usr/bin/libcoreclr.so"))

    def test_photino_is_not_dotnet(self):
        self.assertFalse(_is_dotnet_runtime_file("usr/bin/Photino.Native.so"))


# -- Test source metadata preserves epochs ------------------------------------

class TestSourceMetadataEpochs(unittest.TestCase):
    """Source version metadata must preserve epochs."""

    @patch("manifest.subprocess.run")
    def test_epoch_preserved_in_source_version(self, mock_run):
        """A source epoch survives into source_version, binary version is its own."""
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libssl3:amd64: /usr/lib/x86_64-linux-gnu/libssl.so.3\n"),
            MagicMock(returncode=0, stdout="3.0.2-0ubuntu1.30\topenssl\t1:3.0.2-0ubuntu1.29\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libssl.so.3")
        self.assertIsNotNone(result)
        self.assertEqual(result.source_version, "1:3.0.2-0ubuntu1.29")
        self.assertEqual(result.binary_version, "3.0.2-0ubuntu1.30")
        self.assertEqual(result.source_package, "openssl")

    @patch("manifest.subprocess.run")
    def test_binary_source_version_differs(self, mock_run):
        mock_run.side_effect = [
            MagicMock(returncode=0, stdout="libfoo:amd64: /usr/lib/x86_64-linux-gnu/libfoo.so\n"),
            MagicMock(returncode=0, stdout="1.2.3-1ubuntu1\tfoo-src\t1.2.3-1ubuntu2\n"),
        ]
        result = _query_dpkg_owner("/usr/lib/x86_64-linux-gnu/libfoo.so")
        self.assertIsNotNone(result)
        self.assertEqual(result.binary_version, "1.2.3-1ubuntu1")
        self.assertEqual(result.source_version, "1.2.3-1ubuntu2")


# -- Test .dsc parsing --------------------------------------------------------

class TestDscParsing(unittest.TestCase):
    def test_parse_valid_dsc(self):
        sha_orig = hashlib.sha256(b"orig-bytes").hexdigest()
        sha_debian = hashlib.sha256(b"debian-bytes").hexdigest()
        dsc_content = f"""Format: 3.0 (quilt)
Source: openssl
Binary: libssl3, openssl
Architecture: any amd64
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 {sha_orig} 1234 openssl_3.0.2.orig.tar.gz
 {sha_debian} 5678 openssl_3.0.2-0ubuntu1.29.debian.tar.xz
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "openssl_3.0.2-0ubuntu1.29.dsc"
            dsc_path.write_text(dsc_content)
            dsc = parse_dsc(dsc_path)
        self.assertEqual(dsc.source, "openssl")
        self.assertEqual(dsc.version, "3.0.2-0ubuntu1.29")
        self.assertEqual(len(dsc.files), 2)
        self.assertEqual(dsc.files[0][0], "openssl_3.0.2.orig.tar.gz")
        self.assertEqual(dsc.files[0][1], 1234)
        self.assertEqual(dsc.files[0][2], sha_orig)

    def test_parse_dsc_with_gpg_signature(self):
        dsc_content = """-----BEGIN PGP SIGNED MESSAGE-----
Hash: SHA256

Format: 3.0 (quilt)
Source: openssl
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 abc123def456789012345678901234567890123456789012345678901234abcd 1234 openssl_3.0.2.orig.tar.gz

-----BEGIN PGP SIGNATURE-----
invalid
-----END PGP SIGNATURE-----
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "test.dsc"
            dsc_path.write_text(dsc_content)
            dsc = parse_dsc(dsc_path)
        self.assertEqual(dsc.source, "openssl")

    def test_parse_dsc_missing_source(self):
        dsc_content = """Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 abc123def456789012345678901234567890123456789012345678901234abcd 1234 test.tar.gz
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "test.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("Source", str(ctx.exception))

    def test_parse_dsc_missing_version(self):
        dsc_content = """Source: openssl
Checksums-Sha256:
 abc123def456789012345678901234567890123456789012345678901234abcd 1234 test.tar.gz
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "test.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("Version", str(ctx.exception))

    def test_parse_dsc_no_checksums(self):
        dsc_content = """Source: openssl
Version: 3.0.2-0ubuntu1.29
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "test.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("Checksums", str(ctx.exception))

    def test_parse_dsc_invalid_sha256(self):
        dsc_content = """Source: openssl
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 tooshorthash 1234 test.tar.gz
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "test.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("SHA256", str(ctx.exception))

    def test_parse_dsc_unsafe_filename(self):
        dsc_content = """Source: openssl
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 abc123def456789012345678901234567890123456789012345678901234abcd 1234 ../../../etc/passwd
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "test.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("Unsafe", str(ctx.exception))

    def test_parse_dsc_negative_size_rejected(self):
        """Negative size in checksums stanza must raise ValueError."""
        dsc_content = """Source: openssl
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 abc123def456789012345678901234567890123456789012345678901234abcd -1 test.tar.gz
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "test.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("Negative size", str(ctx.exception))

    def test_parse_dsc_duplicate_filename_rejected(self):
        """Duplicate filename in checksums stanza must raise ValueError."""
        digest_a = hashlib.sha256(b"first").hexdigest()
        digest_b = hashlib.sha256(b"second").hexdigest()
        dsc_content = f"""Source: openssl
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 {digest_a} 100 test.tar.gz
 {digest_b} 200 test.tar.gz
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "test.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("Duplicate", str(ctx.exception))

    def test_parse_dsc_self_reference_rejected(self):
        """A .dsc referencing itself in checksums must raise ValueError."""
        digest_self = hashlib.sha256(b"self").hexdigest()
        dsc_content = f"""Source: openssl
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 {digest_self} 100 openssl_3.0.2-0ubuntu1.29.dsc
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "openssl_3.0.2-0ubuntu1.29.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("must not reference itself", str(ctx.exception))

    def test_parse_dsc_malformed_row_after_valid_row_rejected(self):
        """A short continuation row must fail the whole descriptor.

        Silently dropping the row would shrink the verified file set while the
        descriptor still looked well-formed.
        """
        digest_a = hashlib.sha256(b"first").hexdigest()
        dsc_content = f"""Source: openssl
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 {digest_a} 100 openssl_3.0.2.orig.tar.gz
 truncated-row.tar.gz
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "openssl_3.0.2-0ubuntu1.29.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("Malformed checksum row", str(ctx.exception))

    def test_parse_dsc_extra_field_row_rejected(self):
        """A continuation row with more than three fields must be rejected."""
        digest_a = hashlib.sha256(b"first").hexdigest()
        dsc_content = f"""Source: openssl
Version: 3.0.2-0ubuntu1.29
Checksums-Sha256:
 {digest_a} 100 openssl_3.0.2.orig.tar.gz
 {digest_a} 200 extra-field.tar.gz extra
"""
        with tempfile.TemporaryDirectory() as td:
            dsc_path = Path(td) / "openssl_3.0.2-0ubuntu1.29.dsc"
            dsc_path.write_text(dsc_content)
            with self.assertRaises(ValueError) as ctx:
                parse_dsc(dsc_path)
            self.assertIn("Malformed checksum row", str(ctx.exception))


# -- Test identity validation -------------------------------------------------

class TestDscIdentity(unittest.TestCase):
    def test_matching_identity_passes(self):
        dsc = DscInfo(source="openssl", version="3.0.2-0ubuntu1.29", files=[])
        verify_dsc_identity(dsc, "openssl", "3.0.2-0ubuntu1.29")

    def test_source_mismatch_raises(self):
        dsc = DscInfo(source="wrong", version="3.0.2-0ubuntu1.29", files=[])
        with self.assertRaises(ValueError) as ctx:
            verify_dsc_identity(dsc, "openssl", "3.0.2-0ubuntu1.29")
        self.assertIn("Source mismatch", str(ctx.exception))

    def test_version_mismatch_raises(self):
        dsc = DscInfo(source="openssl", version="3.0.2-0ubuntu1.30", files=[])
        with self.assertRaises(ValueError) as ctx:
            verify_dsc_identity(dsc, "openssl", "3.0.2-0ubuntu1.29")
        self.assertIn("Version mismatch", str(ctx.exception))


# -- Test Launchpad URL construction -------------------------------------------

class TestLaunchpadUrl(unittest.TestCase):
    def test_url_format(self):
        url = _launchpad_dsc_url("openssl", "3.0.2-0ubuntu1.29")
        self.assertEqual(
            url,
            "https://launchpad.net/ubuntu/+archive/primary/+sourcefiles"
            "/openssl/3.0.2-0ubuntu1.29/openssl_3.0.2-0ubuntu1.29.dsc",
        )

    def test_url_with_epoch(self):
        """Epoch is percent-encoded in the path, underscored in the filename.

        Launchpad's publisher path carries the full version, so an epoch colon
        must be quoted to stay a legal path segment, while the stored .dsc
        name replaces the colon with an underscore.
        """
        url = _launchpad_dsc_url("foo", "1:2.3.4-1ubuntu1")
        self.assertIn("/foo/1%3A2.3.4-1ubuntu1/", url)
        # Filename drops the epoch entirely (Debian convention), it does not
        # keep it as an underscore.
        self.assertTrue(url.endswith("/foo_2.3.4-1ubuntu1.dsc"))
        self.assertNotIn("1_2.3.4", url)
        # Identity check keeps the colon: the descriptor must still declare 1:
        dsc_text, _ = _valid_dsc_content(source="foo", version="1:2.3.4-1ubuntu1")
        with tempfile.TemporaryDirectory() as td:
            p = Path(td) / "foo_2.3.4-1ubuntu1.dsc"
            p.write_text(dsc_text)
            self.assertEqual(parse_dsc(p).version, "1:2.3.4-1ubuntu1")

    def test_dsc_filename_drops_epoch(self):
        """The descriptor is named after the upstream version."""
        self.assertEqual(
            _dsc_filename("openssl", "1:3.0.2-0ubuntu1.29"),
            "openssl_3.0.2-0ubuntu1.29.dsc",
        )

    def test_dsc_filename_no_epoch_unchanged(self):
        self.assertEqual(
            _dsc_filename("openssl", "3.0.2-0ubuntu1.29"),
            "openssl_3.0.2-0ubuntu1.29.dsc",
        )

    def test_dsc_filename_keeps_only_first_colon_split(self):
        """Only the first colon separates the epoch."""
        self.assertEqual(
            _dsc_filename("foo", "2:1:2.3.4"), "foo_1:2.3.4.dsc")


# -- Test safety validation ---------------------------------------------------

class TestSafetyValidation(unittest.TestCase):
    def test_valid_name_passes(self):
        _validate_safe("openssl", _SAFE_NAME_RE, "name")

    def test_unsafe_name_with_slash_raises(self):
        with self.assertRaises(ValueError):
            _validate_safe("../etc/passwd", _SAFE_NAME_RE, "name")

    def test_unsafe_name_with_space_raises(self):
        with self.assertRaises(ValueError):
            _validate_safe("open ssl", _SAFE_NAME_RE, "name")

    def test_unsafe_version_with_shell_raises(self):
        with self.assertRaises(ValueError):
            _validate_safe("$(whoami)", _SAFE_VERSION_RE, "version")

    def test_safe_filename_with_epoch_replacement(self):
        """Descriptor/payload filenames with the upstream version are safe."""
        _validate_safe("foo_2.3.4-1ubuntu1.dsc", _SAFE_FILENAME_RE, "filename")

    def test_path_traversal_in_filename_rejected(self):
        with self.assertRaises(ValueError):
            _validate_safe("../../etc/passwd", _SAFE_FILENAME_RE, "filename")


# -- Test _validate_downloaded_package ----------------------------------------

class TestValidateDownloadedPackage(unittest.TestCase):
    def test_valid_package_passes(self):
        dsc_text, payloads = _valid_dsc_content()
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            dsc_path = dest / "openssl_3.0.2-0ubuntu1.29.dsc"
            dsc_path.write_text(dsc_text)
            for fname, size, content, sha256 in payloads:
                (dest / fname).write_bytes(content)
            result = _validate_downloaded_package(
                dsc_path, dest, "openssl", "3.0.2-0ubuntu1.29")
        self.assertEqual(len(result), 3)  # .dsc + 2 payloads

    def test_empty_dsc_raises(self):
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            dsc_path = dest / "test.dsc"
            dsc_path.write_text("")
            with self.assertRaises(ValueError) as ctx:
                _validate_downloaded_package(dsc_path, dest, "test", "1.0")
            self.assertIn("empty", str(ctx.exception))

    def test_wrong_source_raises(self):
        dsc_text, payloads = _valid_dsc_content(source="wrong")
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            dsc_path = dest / "test.dsc"
            dsc_path.write_text(dsc_text)
            for fname, size, content, sha256 in payloads:
                (dest / fname).write_bytes(content)
            with self.assertRaises(ValueError) as ctx:
                _validate_downloaded_package(dsc_path, dest, "openssl", "3.0.2-0ubuntu1.29")
            self.assertIn("Source mismatch", str(ctx.exception))

    def test_wrong_version_raises(self):
        dsc_text, payloads = _valid_dsc_content(version="3.0.2-0ubuntu1.30")
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            dsc_path = dest / "test.dsc"
            dsc_path.write_text(dsc_text)
            for fname, size, content, sha256 in payloads:
                (dest / fname).write_bytes(content)
            with self.assertRaises(ValueError) as ctx:
                _validate_downloaded_package(dsc_path, dest, "openssl", "3.0.2-0ubuntu1.29")
            self.assertIn("Version mismatch", str(ctx.exception))

    def test_missing_payload_raises(self):
        dsc_text, payloads = _valid_dsc_content()
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            dsc_path = dest / "test.dsc"
            dsc_path.write_text(dsc_text)
            # Only write first payload, skip second
            fname, size, content, sha256 = payloads[0]
            (dest / fname).write_bytes(content)
            with self.assertRaises(ValueError) as ctx:
                _validate_downloaded_package(dsc_path, dest, "openssl", "3.0.2-0ubuntu1.29")
            self.assertIn("missing", str(ctx.exception))

    def test_bad_payload_sha256_raises(self):
        """Equal-length but different bytes must fail on SHA256, not size."""
        dsc_text, payloads = _valid_dsc_content()
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            dsc_path = dest / "test.dsc"
            dsc_path.write_text(dsc_text)
            for fname, size, content, sha256 in payloads:
                # Same length, different bytes, so size still matches and the
                # SHA256 comparison is what must catch it.
                (dest / fname).write_bytes(bytes(b ^ 0xFF for b in content))
                self.assertEqual((dest / fname).stat().st_size, size)
            with self.assertRaises(ValueError) as ctx:
                _validate_downloaded_package(dsc_path, dest, "openssl", "3.0.2-0ubuntu1.29")
            self.assertIn("SHA256 mismatch", str(ctx.exception))

    def test_bad_payload_size_raises(self):
        dsc_text, payloads = _valid_dsc_content()
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            dsc_path = dest / "test.dsc"
            dsc_path.write_text(dsc_text)
            for fname, size, content, sha256 in payloads:
                # Write correct content truncated
                (dest / fname).write_bytes(content[:10])
            with self.assertRaises(ValueError) as ctx:
                _validate_downloaded_package(dsc_path, dest, "openssl", "3.0.2-0ubuntu1.29")
            self.assertIn("Size mismatch", str(ctx.exception))


# -- Test HTTPS redirect handler ----------------------------------------------

def _tls_downgrade(url: str) -> str:
    """Return ``url`` with its scheme forced to plain http.

    Built from an otherwise-acceptable HTTPS URL so each negative test proves
    the gate rejects exactly the downgrade of a URL it would have followed,
    rather than an arbitrary hostile string.
    """
    parts = urlsplit(url)
    return urlunsplit(("http", parts.netloc, parts.path, parts.query, ""))


# A URL the gate is expected to accept, used as the downgrade baseline.
_ACCEPTED_DSC_URL = (
    "https://launchpad.net/ubuntu/+archive/primary/+sourcefiles/openssl/"
    "3.0.2-0ubuntu1.29/openssl_3.0.2-0ubuntu1.29.dsc"
)


class _FakeResponse:
    """Minimal response that returns one chunk per read.

    Each read advances a fake clock by ``tick`` seconds, modelling a peer that
    trickles data in small pieces.
    """

    def __init__(self, chunks, clock, tick):
        self.url = _ACCEPTED_DSC_URL
        self._chunks = list(chunks)
        self._clock = clock
        self._tick = tick
        self.reads = 0

    def _next(self):
        self.reads += 1
        if not self._chunks:
            self._clock[0] += self._tick
            return b""
        chunk = self._chunks.pop(0)
        self._clock[0] += self._tick
        return chunk

    # http.client.HTTPResponse exposes both; read1 returns after a single
    # underlying socket read, read() aggregates until n bytes are buffered.
    def read1(self, _size):
        return self._next()

    def read(self, _size):
        return self._next()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class _BufferedDripResponse:
    """Realistic slow drip over a buffering HTTPResponse.

    ``read(n)`` behaves like the stdlib: it keeps pulling underlying socket
    reads until ``n`` bytes are buffered or the body ends, so on a trickling
    peer a single call consumes a large slice of wall clock.  ``read1(n)``
    returns after one underlying read.  Both advance the same fake clock, which
    is what makes the two distinguishable.
    """

    def __init__(self, chunks, clock, tick):
        self.url = _ACCEPTED_DSC_URL
        self._chunks = list(chunks)
        self._clock = clock
        self._tick = tick
        self.read_calls = 0
        self.underlying_reads = 0

    def _under(self):
        self.underlying_reads += 1
        self._clock[0] += self._tick
        return self._chunks.pop(0) if self._chunks else b""

    def read1(self, size):
        self.read_calls += 1
        return self._under()

    def read(self, size):
        self.read_calls += 1
        buf = b""
        while len(buf) < size:
            piece = self._under()
            if not piece:
                break
            buf += piece
        return buf

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class _FakeOpener:
    def __init__(self, resp):
        self.resp = resp

    def open(self, *_a, **_k):
        return self.resp


class TestBufferedSlowDripUsesRead1(unittest.TestCase):
    """A buffering response must not let one read swallow the whole budget.

    ``read(n)`` aggregates underlying socket reads until n bytes are buffered,
    so against a trickling peer a single call can run far past the deadline
    before any check executes. ``read1`` returns after one underlying read, so
    the post-read deadline check trips promptly. The regression is asserted on
    *overshoot*: how far past the deadline the transfer ran.
    """

    DEADLINE = 10.0
    TICK = 0.5
    CHUNKS = 400

    def _run_with_patched_read(self, patched: bool):
        import fetch_source as fs

        clock = [0.0]
        resp = _BufferedDripResponse([b"x" * 8] * self.CHUNKS, clock, self.TICK)
        opener = _FakeOpener(resp)
        if patched:
            # Simulate the old behaviour: read() aggregates until 64 KiB.
            resp.read1 = resp.read

        real_opener, real_time = fs._opener, fs.time
        fs._opener = opener
        fs.time = type("T", (), {"monotonic": staticmethod(lambda: clock[0])})
        try:
            with tempfile.TemporaryDirectory() as td:
                dest = Path(td) / "payload"
                outcome = None
                try:
                    fs._download(_ACCEPTED_DSC_URL, dest, timeout=60,
                                 deadline=self.DEADLINE)
                except OSError as exc:
                    outcome = str(exc)
                return outcome, clock[0], resp.read_calls, resp.underlying_reads
        finally:
            fs._opener, fs.time = real_opener, real_time

    def test_read1_bounds_overshoot(self):
        outcome, clock, _read_calls, underlying = self._run_with_patched_read(False)
        self.assertIsNotNone(outcome, "drip should have been cut off")
        self.assertIn("deadline", outcome.lower())
        overshoot = clock - self.DEADLINE
        # Each read1 returns after one small underlying read, so the overshoot
        # is at most about one tick.
        self.assertLessEqual(overshoot, self.TICK,
                             f"read1 overshot the deadline by {overshoot}s")
        # And it stopped early rather than draining the body.
        self.assertLess(underlying, self.CHUNKS)

    def test_aggregating_read_overshoots_badly(self):
        """Control: the old read() path blows far past the deadline.

        This is the behaviour the read1 change exists to prevent; if it ever
        stops overshooting, the regression above is no longer meaningful.
        """
        outcome, clock, _read_calls, underlying = self._run_with_patched_read(True)
        self.assertIsNotNone(outcome, "drip should have been cut off")
        overshoot = clock - self.DEADLINE
        self.assertGreater(overshoot, 10 * self.TICK,
                           f"aggregating read unexpectedly stayed near the "
                           f"deadline (overshoot {overshoot}s); the read1 "
                           f"regression would then be vacuous")
        # One aggregating call consumed the whole buffered body (plus the
        # trailing EOF probe the stdlib loop makes to detect the end).
        self.assertGreaterEqual(underlying, self.CHUNKS)


class TestDownloadDeadlineInReadLoop(unittest.TestCase):
    """The absolute deadline must be enforced during reads, not only before.

    A host that dribbles one chunk per socket-timeout period must still be cut
    off at the total deadline, otherwise a slow drip bypasses the budget.
    """

    def test_slow_drip_aborted_at_deadline(self):
        # 100 chunks x 2s each = 200s of drip against a 10s deadline.
        response = _FakeResponse([b"x" * 8] * 100, clock=[0.0], tick=2.0)
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td) / "payload"
            with patch("fetch_source._opener") as mock_opener, \
                 patch("fetch_source.time.monotonic",
                       side_effect=lambda: response._clock[0]):
                mock_opener.open.return_value = response
                with self.assertRaises(OSError) as ctx:
                    _download(_ACCEPTED_DSC_URL, dest, timeout=5,
                              deadline=10.0)
            self.assertIn("deadline", str(ctx.exception).lower())
            # Cut off well before the 100-chunk drip could finish.
            self.assertLess(response.reads, 100)
            # The partial file must not be left behind as if it were complete.
            self.assertFalse(dest.exists())

    def test_deadline_shared_across_descriptor_and_payloads(self):
        """fetch_source_from_launchpad passes its single deadline downward."""
        version = "3.0.2-0ubuntu1.29"
        dsc_text, payloads = _valid_dsc_content()

        def download_side_effect(url, dest, timeout=60, max_bytes=None,
                                 deadline=None):
            captured.append(deadline)
            if url.endswith(".dsc"):
                dest.write_text(dsc_text)
            else:
                for fname, size, content, _digest in payloads:
                    if url.endswith(fname):
                        dest.write_bytes(content)
                        return

        captured = []
        with patch("fetch_source._download", side_effect=download_side_effect):
            with tempfile.TemporaryDirectory() as td:
                fetch_source_from_launchpad("openssl", version, Path(td))

        self.assertEqual(len(captured), 3)  # .dsc + 2 payloads
        self.assertTrue(all(d is not None for d in captured))
        # Every request shares one absolute deadline, not a fresh budget.
        self.assertEqual(len(set(captured)), 1)

    def test_exhausted_deadline_rejected_before_request(self):
        response = _FakeResponse([b"data"], clock=[100.0], tick=0.0)
        with tempfile.TemporaryDirectory() as td:
            with patch("fetch_source._opener") as mock_opener, \
                 patch("fetch_source.time.monotonic", return_value=100.0):
                mock_opener.open.return_value = response
                with self.assertRaises(OSError) as ctx:
                    _download(_ACCEPTED_DSC_URL, Path(td) / "d", timeout=5,
                              deadline=50.0)
        self.assertIn("deadline", str(ctx.exception).lower())
        mock_opener.open.assert_not_called()


class TestHttpsRedirectHandler(unittest.TestCase):
    """Redirects must stay HTTPS on an allowlisted Launchpad host.

    The gate is asserted directly (before urllib would follow) so the
    https-to-http downgrade and untrusted-host cases are proven rejected
    rather than merely logged.
    """

    def test_allows_launchpad_net(self):
        _check_redirect_url(
            "https://launchpad.net/ubuntu/+archive/primary/+sourcefiles/openssl/"
            "3.0.2-0ubuntu1.29/openssl_3.0.2-0ubuntu1.29.dsc")

    def test_allows_launchpadlibrarian(self):
        """Official descriptor downloads are served by launchpadlibrarian.net."""
        _check_redirect_url("https://launchpadlibrarian.net/12345/openssl_3.0.2.orig.tar.gz")

    def test_rejects_https_to_http_downgrade(self):
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            _check_redirect_url(_tls_downgrade(_ACCEPTED_DSC_URL))
        self.assertIn("https", str(ctx.exception).lower())

    def test_rejects_untrusted_https_host(self):
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            _check_redirect_url("https://evil.example.com/steal")
        self.assertIn("allowlist", str(ctx.exception))

    def test_rejects_lookalike_host(self):
        """A suffix-match trick must not pass the exact allowlist."""
        with self.assertRaises(urllib.error.HTTPError):
            _check_redirect_url("https://launchpad.net.evil.example/steal")

    def test_rejects_non_https_scheme(self):
        for url in ("ftp://launchpad.net/f", "file:///etc/passwd"):
            with self.subTest(url=url):
                with self.assertRaises(urllib.error.HTTPError):
                    _check_redirect_url(url)

    def test_allowlist_is_exactly_official_hosts(self):
        self.assertEqual(
            _ALLOWED_HOSTS,
            frozenset({"launchpad.net", "www.launchpad.net",
                       "launchpadlibrarian.net"}),
        )

    def test_handler_uses_gate_before_following(self):
        """The handler rejects a downgrade rather than following it."""
        handler = _HttpsOnlyRedirectHandler()
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            handler.redirect_request(
                MagicMock(), MagicMock(), 302, "Found", {},
                _tls_downgrade(_ACCEPTED_DSC_URL))
        self.assertIn("https", str(ctx.exception).lower())


# -- Test download URL enforcement --------------------------------------------

class TestDownloadUrlEnforcement(unittest.TestCase):
    """_download must reject non-HTTPS and non-allowlisted URLs."""

    def test_rejects_http_url(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError) as ctx:
                _download(_tls_downgrade(_ACCEPTED_DSC_URL),
                          Path(td) / "test.dsc")
            self.assertIn("HTTPS", str(ctx.exception))

    def test_rejects_untrusted_host(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError) as ctx:
                _download("https://evil.com/test.dsc",
                          Path(td) / "test.dsc")
            self.assertIn("not in allowed", str(ctx.exception))

    def test_rejects_ftp_scheme(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError) as ctx:
                _download("ftp://launchpad.net/test.dsc",
                          Path(td) / "test.dsc")
            self.assertIn("HTTPS", str(ctx.exception))


# -- Test apt-get source validation -------------------------------------------

class TestAptSourceArgs(unittest.TestCase):
    @patch("fetch_source.subprocess.run")
    def test_apt_source_command_format(self, mock_run):
        mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="")
        with tempfile.TemporaryDirectory() as td:
            owned = _owned_sources(td)
            try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                           apt_sources=owned)
        args = mock_run.call_args[0][0]
        # Global options must precede the subcommand for apt to accept them.
        self.assertEqual(args[0], "apt-get")
        subcmd = args.index("source")
        self.assertEqual(args[subcmd:subcmd + 3],
                         ["source", "--download-only", "--only-source"])
        # Source only: never accept binary packages as "sources"
        self.assertIn("openssl=3.0.2-0ubuntu1.29", args)
        # Origin pinning options all precede the subcommand.
        self.assertEqual([a for a in args[1:subcmd:2]], ["-o"] * 4)
        opts = args[2:subcmd:2]
        self.assertIn(f"Dir::Etc::sourcelist={owned}", opts)

    @patch("fetch_source.subprocess.run")
    def test_apt_pins_origin_and_ignores_ambient_config(self, mock_run):
        """Every extra apt config dir is redirected to /dev/null."""
        mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="")
        with tempfile.TemporaryDirectory() as td:
            owned = _owned_sources(td)
            try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                           apt_sources=owned)
        args = mock_run.call_args[0][0]
        subcmd = args.index("source")
        opts = set(args[2:subcmd:2])
        self.assertIn(f"Dir::Etc::sourcelist={owned}", opts)
        # No third-party .list/.sources, no apt.conf.d, no main apt.conf.
        self.assertIn("Dir::Etc::sourceparts=/dev/null", opts)
        self.assertIn("Dir::Etc::Parts=/dev/null", opts)
        self.assertIn("Dir::Etc::main=/dev/null", opts)
        # Inherited APT_CONFIG must name our bootstrap, never a bare /dev/null.
        env = mock_run.call_args[1]["env"]
        conf = env["APT_CONFIG"]
        self.assertNotEqual(conf, "/dev/null")
        self.assertTrue(conf.endswith("apt-bootstrap.conf"), conf)

    @patch("fetch_source.subprocess.run")
    def test_no_owned_sources_skips_apt_entirely(self, mock_run):
        """Without --apt-sources the helper must not consult ambient apt.

        A standalone run cannot assume the host's apt configuration is ours,
        so it goes straight to the official Launchpad archive instead.
        """
        with tempfile.TemporaryDirectory() as td:
            self.assertIsNone(
                try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td)))
        mock_run.assert_not_called()

    @patch("fetch_source.subprocess.run")
    def test_relative_apt_sources_rejected(self, mock_run):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError) as ctx:
                try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                               apt_sources="relative/owned.sources")
        self.assertIn("absolute", str(ctx.exception))
        mock_run.assert_not_called()

    @patch("fetch_source.subprocess.run")
    def test_caller_timeout_reaches_apt(self, mock_run):
        """The caller's budget must be honoured instead of a fixed 120s."""
        mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="")
        with tempfile.TemporaryDirectory() as td:
            owned = _owned_sources(td)
            try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                           timeout=10, apt_sources=owned)
            fetch_source("openssl", "3.0.2-0ubuntu1.29", Path(td) / "dest",
                         timeout=10, apt_sources=owned)
        self.assertEqual(mock_run.call_args[1]["timeout"], 10)

    def test_apt_config_options_match_expected_pairs(self):
        opts = apt_config_options("/etc/apt/sources.list.d/owned.sources")
        self.assertEqual(opts[0::2], ["-o"] * 4)
        self.assertEqual(opts[1::2], [
            "Dir::Etc::sourcelist=/etc/apt/sources.list.d/owned.sources",
            "Dir::Etc::sourceparts=/dev/null",
            "Dir::Etc::Parts=/dev/null",
            "Dir::Etc::main=/dev/null",
        ])

    def test_bootstrap_conf_disables_parts_and_main(self):
        """The early APT_CONFIG must redirect both ambient config dirs."""
        with tempfile.TemporaryDirectory() as td:
            path = write_apt_bootstrap_conf(Path(td))
            text = path.read_text(encoding="utf-8")
        self.assertIn('Dir::Etc::Parts "/dev/null";', text)
        self.assertIn('Dir::Etc::main "/dev/null";', text)

    def test_bootstrap_conf_is_owner_only(self):
        with tempfile.TemporaryDirectory() as td:
            path = write_apt_bootstrap_conf(Path(td))
            mode = path.stat().st_mode & 0o777
        self.assertEqual(mode, 0o600, f"bootstrap mode was {oct(mode)}")

    @patch("fetch_source.subprocess.run")
    def test_apt_env_points_at_bootstrap_not_devnull(self, mock_run):
        """APT_CONFIG must name the real bootstrap file, never /dev/null."""
        mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="")
        with tempfile.TemporaryDirectory() as td:
            owned = _owned_sources(td)
            try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                           apt_sources=owned)
        conf = mock_run.call_args[1]["env"]["APT_CONFIG"]
        self.assertNotEqual(conf, "/dev/null")
        self.assertTrue(conf.endswith("apt-bootstrap.conf"), conf)

    @patch("fetch_source.subprocess.run")
    def test_bootstrap_removed_with_work_dir(self, mock_run):
        """The bootstrap lives only as long as the apt subprocess."""
        mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="")
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            owned = _owned_sources(td)
            try_apt_source("openssl", "3.0.2-0ubuntu1.29", dest,
                           apt_sources=owned)
            self.assertEqual(
                [p.name for p in dest.iterdir() if p.is_file()], [],
                "apt work dir and its bootstrap must not survive")

    def test_bootstrap_suppresses_ambient_apt_config_with_real_apt(self):
        """Real apt startup: ambient config must not be loaded.

        Runs ``apt-config dump`` only (no network, no apt-get update, no writes
        anywhere under /etc). The host's own ambient apt.conf.d settings are the
        sentinel: with ``APT_CONFIG=/dev/null`` they are still loaded, while with
        APT_CONFIG pointing at our bootstrap they must disappear, because the
        bootstrap redirects Parts/main before apt resolves its defaults.
        """
        apt_config_bin = shutil.which("apt-config")
        if not apt_config_bin:
            self.skipTest("apt-config not available")

        # A setting Ubuntu/Debian always ship in apt.conf.d.
        ambient_keys = ("APT::Periodic::Unattended-Upgrade", "DPkg::Post-Invoke")

        def dump(apt_config_value, owned):
            env = {**os.environ, "APT_CONFIG": apt_config_value}
            proc = subprocess.run(
                [apt_config_bin,
                 "-o", f"Dir::Etc::sourcelist={owned}",
                 "-o", "Dir::Etc::sourceparts=/dev/null",
                 "dump"],
                capture_output=True, text=True, timeout=30, env=env)
            return proc.stdout

        with tempfile.TemporaryDirectory() as td:
            owned = _owned_sources(td)
            bootstrap = write_apt_bootstrap_conf(Path(td))

            # Control: APT_CONFIG=/dev/null alone does not stop ambient loading.
            control = dump("/dev/null", owned)
            if not any(k in control for k in ambient_keys):
                self.skipTest(
                    "no ambient apt.conf.d settings found to use as a sentinel")

            with_bootstrap = dump(str(bootstrap), owned)
            for key in ambient_keys:
                self.assertNotIn(
                    key, with_bootstrap,
                    f"ambient {key} survived our APT_CONFIG bootstrap")
            # The bootstrap's redirects must be the resolved values.
            self.assertIn('Dir::Etc::parts "/dev/null";', with_bootstrap)
            self.assertIn('Dir::Etc::main "/dev/null";', with_bootstrap)
            # The owned sources file is still the only one configured.
            self.assertIn(f'Dir::Etc::sourcelist "{owned}";', with_bootstrap)

    @patch("fetch_source.subprocess.run")
    def test_apt_source_valid_dsc_returns_validated_files(self, mock_run):
        """Valid .dsc with matching identity and checksums returns file list."""
        dsc_text, payloads = _valid_dsc_content()

        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", args[0] if len(args) > 1 else "."))
            dsc_path = dest / "openssl_3.0.2-0ubuntu1.29.dsc"
            dsc_path.write_text(dsc_text)
            for fname, size, content, sha256 in payloads:
                (dest / fname).write_bytes(content)
            return MagicMock(returncode=0, stdout="", stderr="")
        mock_run.side_effect = side_effect

        with tempfile.TemporaryDirectory() as td:
            result = try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                              apt_sources=_owned_sources(td))
            self.assertIsNotNone(result)
            self.assertEqual(len(result), 3)  # .dsc + 2 payloads
            for p in result:
                self.assertTrue(p.exists(), f"verified file missing: {p}")
                self.assertTrue(p.is_file())

    @patch("fetch_source.subprocess.run")
    def test_apt_source_epoch_bearing_version_uses_upstream_filename(self, mock_run):
        """apt success with an epoch version and an epoch-free filename.

        Debian names the descriptor after the upstream version, so the file on
        disk is ``foo_2.3.4-1ubuntu1.dsc`` while its ``Version`` field and the
        requested version both keep the ``1:`` epoch.
        """
        version = "1:2.3.4-1ubuntu1"
        dsc_text, payloads = _valid_dsc_content(source="foo", version=version)

        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", "."))
            (dest / "foo_2.3.4-1ubuntu1.dsc").write_text(dsc_text)
            for fname, size, content, digest in payloads:
                (dest / fname).write_bytes(content)
            return MagicMock(returncode=0, stdout="", stderr="")

        mock_run.side_effect = side_effect
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            result = try_apt_source("foo", version, dest, apt_sources=_owned_sources(td))
            self.assertIsNotNone(result)
            names = sorted(p.name for p in result)
            self.assertIn("foo_2.3.4-1ubuntu1.dsc", names)
            # No underscored-epoch filename should have been invented.
            self.assertNotIn("foo_1_2.3.4-1ubuntu1.dsc", names)
            self.assertEqual(sorted(p.name for p in dest.iterdir() if p.is_file()), names)

    @patch("fetch_source.subprocess.run")
    def test_apt_source_epoch_underscore_filename_rejected(self, mock_run):
        """A descriptor named with an underscored epoch is not the real one."""
        version = "1:2.3.4-1ubuntu1"
        dsc_text, payloads = _valid_dsc_content(source="foo", version=version)

        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", "."))
            # Wrong name: epoch kept as an underscore instead of dropped.
            (dest / "foo_1_2.3.4-1ubuntu1.dsc").write_text(dsc_text)
            for fname, size, content, digest in payloads:
                (dest / fname).write_bytes(content)
            return MagicMock(returncode=0, stdout="", stderr="")

        mock_run.side_effect = side_effect
        with tempfile.TemporaryDirectory() as td:
            self.assertIsNone(try_apt_source("foo", version, Path(td),
                                             apt_sources=_owned_sources(td)))

    @patch("fetch_source.subprocess.run")
    def test_apt_source_malformed_checksum_row_rejected(self, mock_run):
        """A malformed row makes apt results unusable, even beside a valid row."""
        version = "3.0.2-0ubuntu1.29"
        good = hashlib.sha256(b"first").hexdigest()
        dsc_text = f"""Source: openssl
Version: {version}
Checksums-Sha256:
 {good} 100 openssl_3.0.2.orig.tar.gz
 truncated.tar.gz
"""

        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", "."))
            (dest / f"openssl_{version}.dsc").write_text(dsc_text)
            (dest / "openssl_3.0.2.orig.tar.gz").write_bytes(b"A" * 100)
            return MagicMock(returncode=0, stdout="", stderr="")

        mock_run.side_effect = side_effect
        with tempfile.TemporaryDirectory() as td:
            self.assertIsNone(try_apt_source("openssl", version, Path(td),
                                             apt_sources=_owned_sources(td)))

    @patch("fetch_source.subprocess.run")
    def test_apt_source_isolates_failed_downloads(self, mock_run):
        """A failed apt run must leave nothing behind in dest_dir."""
        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", "."))
            # Partial junk that is not the requested version
            (dest / "openssl_3.0.2-0ubuntu1.30.dsc").write_text("junk")
            (dest / "openssl_9.9.9.orig.tar.gz").write_bytes(b"junk")
            return MagicMock(returncode=1, stdout="", stderr="E: no such source")

        mock_run.side_effect = side_effect
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            # Pre-existing unrelated file must survive untouched
            (dest / "keepme.txt").write_text("unrelated")
            self.assertIsNone(
                try_apt_source("openssl", "3.0.2-0ubuntu1.29", dest,
                              apt_sources=_owned_sources(td)))
            leftovers = sorted(p.name for p in dest.iterdir() if p.is_file())
            self.assertEqual(leftovers, ["keepme.txt"],
                             f"failed download leaked files: {leftovers}")

    @patch("fetch_source.subprocess.run")
    def test_apt_source_wrong_version_dsc_rejected(self, mock_run):
        """apt-get producing .dsc for wrong version must return None."""
        dsc_text, payloads = _valid_dsc_content(version="3.0.2-0ubuntu1.30")

        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", args[0] if len(args) > 1 else "."))
            # apt downloads the file with version in name matching dsc content
            dsc_path = dest / "openssl_3.0.2-0ubuntu1.30.dsc"
            dsc_path.write_text(dsc_text)
            for fname, size, content, sha256 in payloads:
                (dest / fname).write_bytes(content)
            return MagicMock(returncode=0, stdout="", stderr="")
        mock_run.side_effect = side_effect

        with tempfile.TemporaryDirectory() as td:
            result = try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                              apt_sources=_owned_sources(td))
        self.assertIsNone(result)

    @patch("fetch_source.subprocess.run")
    def test_apt_source_empty_dsc_rejected(self, mock_run):
        """Empty .dsc file must return None."""
        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", args[0] if len(args) > 1 else "."))
            (dest / "openssl_3.0.2-0ubuntu1.29.dsc").write_text("")
            return MagicMock(returncode=0, stdout="", stderr="")
        mock_run.side_effect = side_effect

        with tempfile.TemporaryDirectory() as td:
            result = try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                              apt_sources=_owned_sources(td))
        self.assertIsNone(result)

    @patch("fetch_source.subprocess.run")
    def test_apt_source_missing_payload_rejected(self, mock_run):
        """Valid .dsc but missing payload file must return None."""
        dsc_text, payloads = _valid_dsc_content()

        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", args[0] if len(args) > 1 else "."))
            (dest / "openssl_3.0.2-0ubuntu1.29.dsc").write_text(dsc_text)
            # Don't write payloads
            return MagicMock(returncode=0, stdout="", stderr="")
        mock_run.side_effect = side_effect

        with tempfile.TemporaryDirectory() as td:
            result = try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                              apt_sources=_owned_sources(td))
        self.assertIsNone(result)

    @patch("fetch_source.subprocess.run")
    def test_apt_source_failure_returns_none(self, mock_run):
        mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="")
        with tempfile.TemporaryDirectory() as td:
            result = try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                              apt_sources=_owned_sources(td))
        self.assertIsNone(result)

    @patch("fetch_source.subprocess.run")
    def test_apt_source_timeout_returns_none(self, mock_run):
        import subprocess
        mock_run.side_effect = subprocess.TimeoutExpired(cmd="apt-get", timeout=120)
        with tempfile.TemporaryDirectory() as td:
            result = try_apt_source("openssl", "3.0.2-0ubuntu1.29", Path(td),
                              apt_sources=_owned_sources(td))
        self.assertIsNone(result)

    @patch("fetch_source.subprocess.run")
    def test_apt_source_cleans_up_work_dir(self, mock_run):
        """Isolated work directory must be cleaned up after success."""
        dsc_text, payloads = _valid_dsc_content()

        def side_effect(*args, **kwargs):
            dest = Path(kwargs.get("cwd", args[0] if len(args) > 1 else "."))
            (dest / "openssl_3.0.2-0ubuntu1.29.dsc").write_text(dsc_text)
            for fname, size, content, sha256 in payloads:
                (dest / fname).write_bytes(content)
            return MagicMock(returncode=0, stdout="", stderr="")
        mock_run.side_effect = side_effect

        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            try_apt_source("openssl", "3.0.2-0ubuntu1.29", dest,
                              apt_sources=_owned_sources(td))
            # No .apt-work-* directories should remain
            work_dirs = list(dest.glob(".apt-work-*"))
            self.assertEqual(work_dirs, [],
                             f"Work dir not cleaned up: {work_dirs}")

    @patch("fetch_source.subprocess.run")
    def test_apt_source_cleans_up_work_dir_on_failure(self, mock_run):
        """Isolated work directory must be cleaned up after failure too."""
        mock_run.return_value = MagicMock(returncode=1, stdout="", stderr="")
        with tempfile.TemporaryDirectory() as td:
            dest = Path(td)
            try_apt_source("openssl", "3.0.2-0ubuntu1.29", dest,
                              apt_sources=_owned_sources(td))
            work_dirs = list(dest.glob(".apt-work-*"))
            self.assertEqual(work_dirs, [],
                             f"Work dir not cleaned up on failure: {work_dirs}")


# -- Test fetch_source_from_launchpad: full flow with mocks -------------------

class TestFetchSourceLaunchpad(unittest.TestCase):
    @patch("fetch_source._download")
    def test_full_flow_success(self, mock_download):
        dsc_text, payloads = _valid_dsc_content()

        def download_side_effect(url, dest, timeout=60, max_bytes=None,
                                 deadline=None):
            if url.endswith(".dsc"):
                dest.write_text(dsc_text)
            else:
                for fname, size, content, sha256 in payloads:
                    if url.endswith(fname):
                        dest.write_bytes(content)
                        return
        mock_download.side_effect = download_side_effect

        with tempfile.TemporaryDirectory() as td:
            files = fetch_source_from_launchpad("openssl", "3.0.2-0ubuntu1.29", Path(td))
        self.assertEqual(len(files), 3)  # .dsc + 2 payload

    @patch("fetch_source._download")
    def test_source_mismatch_raises(self, mock_download):
        dsc_text, _ = _valid_dsc_content(source="wrong")

        def download_side_effect(url, dest, timeout=60, max_bytes=None,
                                 deadline=None):
            dest.write_text(dsc_text)
        mock_download.side_effect = download_side_effect

        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError) as ctx:
                fetch_source_from_launchpad("openssl", "3.0.2-0ubuntu1.29", Path(td))
            self.assertIn("Source mismatch", str(ctx.exception))

    @patch("fetch_source._download")
    def test_version_mismatch_raises(self, mock_download):
        dsc_text, _ = _valid_dsc_content(version="3.0.2-0ubuntu1.30")

        def download_side_effect(url, dest, timeout=60, max_bytes=None,
                                 deadline=None):
            dest.write_text(dsc_text)
        mock_download.side_effect = download_side_effect

        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError) as ctx:
                fetch_source_from_launchpad("openssl", "3.0.2-0ubuntu1.29", Path(td))
            self.assertIn("Version mismatch", str(ctx.exception))

    def test_unsafe_source_name_raises(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError):
                fetch_source_from_launchpad("../etc", "1.0", Path(td))

    def test_unsafe_version_raises(self):
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError):
                fetch_source_from_launchpad("openssl", "$(whoami)", Path(td))

    @patch("fetch_source._download")
    def test_network_failure_raises(self, mock_download):
        mock_download.side_effect = OSError("Connection refused")
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(OSError) as ctx:
                fetch_source_from_launchpad("openssl", "3.0.2-0ubuntu1.29", Path(td), retries=0)
            self.assertIn("Failed to download .dsc", str(ctx.exception))

    @patch("fetch_source._download")
    def test_payload_checksum_mismatch_raises(self, mock_download):
        dsc_text, _ = _valid_dsc_content()

        def download_side_effect(url, dest, timeout=60, max_bytes=None,
                                 deadline=None):
            if url.endswith(".dsc"):
                dest.write_text(dsc_text)
            else:
                dest.write_bytes(b"A" * 100)  # Won't match checksums
        mock_download.side_effect = download_side_effect

        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError) as ctx:
                fetch_source_from_launchpad("openssl", "3.0.2-0ubuntu1.29", Path(td), retries=0)
            self.assertIn("Size mismatch", str(ctx.exception))

    @patch("fetch_source._download")
    def test_dsc_size_limit_enforced(self, mock_download):
        """Download must pass max_bytes=_MAX_DSC_BYTES for .dsc."""
        dsc_text, payloads = _valid_dsc_content()
        captured_max_bytes = []

        def download_side_effect(url, dest, timeout=60, max_bytes=None,
                                 deadline=None):
            captured_max_bytes.append(max_bytes)
            if url.endswith(".dsc"):
                dest.write_text(dsc_text)
            else:
                for fname, size, content, sha256 in payloads:
                    if url.endswith(fname):
                        dest.write_bytes(content)
                        return
        mock_download.side_effect = download_side_effect

        with tempfile.TemporaryDirectory() as td:
            fetch_source_from_launchpad("openssl", "3.0.2-0ubuntu1.29", Path(td))
        # First call is .dsc download with _MAX_DSC_BYTES
        self.assertEqual(captured_max_bytes[0], _MAX_DSC_BYTES)

    @patch("fetch_source._download")
    def test_launchpad_malformed_checksum_row_rejected(self, mock_download):
        """A malformed row aborts the Launchpad fetch before any payload."""
        version = "3.0.2-0ubuntu1.29"
        good = hashlib.sha256(b"first").hexdigest()
        dsc_text = f"""Source: openssl
Version: {version}
Checksums-Sha256:
 {good} 100 openssl_3.0.2.orig.tar.gz
 truncated.tar.gz
"""

        def download_side_effect(url, dest, timeout=60, max_bytes=None,
                                 deadline=None):
            dest.write_text(dsc_text)

        mock_download.side_effect = download_side_effect
        with tempfile.TemporaryDirectory() as td:
            with self.assertRaises(ValueError) as ctx:
                fetch_source_from_launchpad("openssl", version, Path(td))
            self.assertIn("Malformed checksum row", str(ctx.exception))
        # Only the descriptor was requested; no payload fetch was attempted.
        self.assertEqual(mock_download.call_count, 1)

    @patch("fetch_source._download")
    def test_timeout_propagated(self, mock_download):
        """Custom timeout must be passed to _download calls."""
        dsc_text, payloads = _valid_dsc_content()
        captured_timeouts = []

        def download_side_effect(url, dest, timeout=60, max_bytes=None,
                                 deadline=None):
            captured_timeouts.append(timeout)
            if url.endswith(".dsc"):
                dest.write_text(dsc_text)
            else:
                for fname, size, content, sha256 in payloads:
                    if url.endswith(fname):
                        dest.write_bytes(content)
                        return
        mock_download.side_effect = download_side_effect

        with tempfile.TemporaryDirectory() as td:
            fetch_source_from_launchpad(
                "openssl", "3.0.2-0ubuntu1.29", Path(td), timeout=30)
        for t in captured_timeouts:
            self.assertEqual(t, 30)


# -- Test CLI --timeout wiring ------------------------------------------------

class TestCliTimeoutWiring(unittest.TestCase):
    """The --timeout CLI argument must actually be used for network calls."""

    def test_cli_timeout_passed_to_fetch(self):
        """fetch_source() must pass timeout to fetch_source_from_launchpad."""
        with patch("fetch_source.try_apt_source", return_value=None), \
             patch("fetch_source.fetch_source_from_launchpad") as mock_lp:
            mock_lp.return_value = [Path("/fake/file.dsc")]
            from fetch_source import fetch_source
            try:
                fetch_source("openssl", "3.0.2-0ubuntu1.29",
                             Path("/tmp"), timeout=42)
            except Exception:
                pass
            if mock_lp.called:
                _, kwargs = mock_lp.call_args
                self.assertEqual(kwargs.get("timeout"), 42)


# -- Test relocated PulseAudio success (integration) --------------------------

class TestRelocatedPulseAudio(unittest.TestCase):
    """Integration: relocated PulseAudio native lib finds correct owner."""

    @patch("manifest.subprocess.run")
    def test_pulseaudio_relocated_lib_found(self, mock_run):
        def dpkg_side_effect(args, **kwargs):
            path = args[-1] if len(args) > 1 else ""
            if path == "/usr/lib/x86_64-linux-gnu/libpulsecommon-15.99.so":
                return MagicMock(returncode=1, stdout="")
            if path == "/lib/x86_64-linux-gnu/libpulsecommon-15.99.so":
                return MagicMock(returncode=1, stdout="")
            if path == "/usr/lib/libpulsecommon-15.99.so":
                return MagicMock(returncode=1, stdout="")
            if path == "/lib/libpulsecommon-15.99.so":
                return MagicMock(returncode=1, stdout="")
            if "pulseaudio/libpulsecommon-15.99.so" in path:
                return MagicMock(
                    returncode=0,
                    stdout=f"libpulse0:amd64: {path}\n",
                )
            if "-f=${Version}" in str(args):
                return MagicMock(
                    returncode=0,
                    stdout="15.99.1-0ubuntu3\tpulseaudio\t15.99.1-0ubuntu3\n",
                )
            return MagicMock(returncode=1, stdout="")

        mock_run.side_effect = dpkg_side_effect

        with tempfile.TemporaryDirectory() as td:
            relocated = Path(td) / "usr" / "lib" / "x86_64-linux-gnu" / "pulseaudio"
            relocated.mkdir(parents=True)
            (relocated / "libpulsecommon-15.99.so").write_bytes(b"\x7fELF" + b"\x00" * 60)

            with patch("manifest.os.walk") as mock_walk:
                mock_walk.side_effect = [
                    [(str(relocated), [], ["libpulsecommon-15.99.so"])],
                    [],
                ]
                with patch("manifest._is_elf", return_value=True):
                    result = _query_dpkg_owner_with_relocation("usr/lib/libpulsecommon-15.99.so")

        self.assertIsNotNone(result)
        self.assertEqual(result.binary_package, "libpulse0")
        self.assertEqual(result.source_package, "pulseaudio")


if __name__ == "__main__":
    unittest.main()