"""Download exact Ubuntu source packages from Launchpad historical archive.

Provides a fallback for apt-get source --download-only when a specific
source version has been removed from the current Ubuntu pool (superseded).
Downloads the .dsc descriptor, validates identity/checksums, then fetches
referenced payload files.

Only official HTTPS Launchpad URLs are accepted.  No arbitrary metadata
sources, no persistent cache, no upgrade logic.

Not a standalone script; called by collect-sources.sh.
"""

import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import urllib.error
from dataclasses import dataclass
from email.message import Message
from pathlib import Path
from urllib.parse import urlparse, quote

LAUNCHPAD_URL_BASE = (
    "https://launchpad.net/ubuntu/+archive/primary/+sourcefiles"
)

# Only these characters are safe in source package names, versions, filenames
_SAFE_NAME_RE = re.compile(r"^[a-z0-9][a-z0-9+\-.]+$")
_SAFE_VERSION_RE = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9+.~:\-]+$")
_SAFE_FILENAME_RE = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9+.~_:\-]+$")

# Hosts allowed for direct requests and redirects (HTTPS only)
_ALLOWED_HOSTS = frozenset({"launchpad.net", "www.launchpad.net",
                            "launchpadlibrarian.net"})

# Descriptor and payload size limits
_MAX_DSC_BYTES = 512 * 1024        # 512 KiB — generous upper bound for .dsc
_MAX_PAYLOAD_BYTES = 512 * 1024 * 1024  # 512 MiB per payload file
_MAX_TOTAL_BYTES = 1024 * 1024 * 1024   # 1 GiB total download budget

# Wall-clock budget for an entire fetch (all requests + retries), so a slow
# or stalled host cannot hang the collector indefinitely.
_TOTAL_DEADLINE_SECONDS = 15 * 60


@dataclass
class DscInfo:
    """Parsed .dsc descriptor fields."""
    source: str
    version: str
    files: list[tuple[str, int, str]]  # (filename, size, sha256)


# -- HTTPS-only redirect handler -----------------------------------------------

def _check_redirect_url(newurl: str, code: int = 302) -> None:
    """Reject any redirect that is not HTTPS to an official Launchpad host.

    Runs before urllib follows the redirect, so an https-to-http downgrade or
    a hop to an untrusted host never issues the second request.  Official
    Launchpad serves descriptor downloads through launchpadlibrarian.net, so
    that host is on the allowlist alongside launchpad.net.
    """
    parsed = urlparse(newurl)
    if parsed.scheme != "https":
        raise urllib.error.HTTPError(
            newurl, code,
            f"Redirect rejected: scheme must be https, got {parsed.scheme}",
            Message(), None,
        )
    host = parsed.hostname or ""
    if host not in _ALLOWED_HOSTS:
        raise urllib.error.HTTPError(
            newurl, code,
            f"Redirect rejected: host {host!r} not in allowlist",
            Message(), None,
        )


class _HttpsOnlyRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Follow redirects only to HTTPS official Launchpad hosts.

    Rejects any redirect to HTTP or to an untrusted host, so TLS can never be
    downgraded and the payload origin cannot be swapped out from under us.
    """

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        _check_redirect_url(newurl, code)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def _build_opener():
    """Build a urllib opener that enforces HTTPS and allowlisted hosts."""
    https_handler = urllib.request.HTTPSHandler()
    return urllib.request.build_opener(
        _HttpsOnlyRedirectHandler,
        https_handler,
    )


_opener = _build_opener()


# -- Safety helpers -------------------------------------------------------------

def _validate_safe(value: str, pattern: re.Pattern, label: str) -> None:
    """Reject unsafe values that could cause path traversal or injection."""
    if not pattern.match(value):
        raise ValueError(f"Unsafe {label}: {value!r}")


def _socket_of(resp):
    """Best-effort access to the underlying socket, or None."""
    try:
        return resp.fp.raw._sock
    except AttributeError:
        return None


def _download(url: str, dest: Path, timeout: int = 60,
              max_bytes: int | None = None,
              deadline: float | None = None) -> None:
    """Download a URL to a local file with bounded timeout, size, and deadline.

    Uses the custom opener that enforces HTTPS-only and allowlisted hosts on
    both the initial request and every redirect.

    ``deadline`` is an absolute ``time.monotonic()`` value bounding the whole
    transfer.  It is enforced before every read and again immediately after
    each one, so neither a stalled peer nor a host that dribbles a few bytes at
    a time can run past the budget: reads use ``read1`` (one underlying socket
    read each) and are bounded by the remaining time.
    """
    parsed = urlparse(url)
    if parsed.scheme != "https":
        raise ValueError(f"URL must use HTTPS: {url}")
    host = parsed.hostname or ""
    if host not in _ALLOWED_HOSTS:
        raise ValueError(f"Host {host!r} not in allowed list")

    if deadline is not None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise OSError("Download deadline already exhausted before request")
        timeout = max(1, min(timeout, int(remaining) + 1))

    req = urllib.request.Request(
        url, headers={"User-Agent": "mqttprobe-source-collector/1.0"},
    )
    with _opener.open(req, timeout=timeout) as resp:
        # Verify the final URL is still HTTPS on an allowed host
        final_url = resp.url
        final_parsed = urlparse(final_url)
        if final_parsed.scheme != "https":
            raise ValueError(
                f"Final URL scheme is not HTTPS: {final_url}")
        final_host = final_parsed.hostname or ""
        if final_host not in _ALLOWED_HOSTS:
            raise ValueError(
                f"Final host {final_host!r} not in allowed list")

        sock = _socket_of(resp)
        with open(dest, "wb") as f:
            total = 0
            while True:
                if deadline is not None:
                    left = deadline - time.monotonic()
                    if left <= 0:
                        f.close()
                        dest.unlink(missing_ok=True)
                        raise OSError(
                            "Download deadline exceeded after "
                            f"{total} bytes: {url}")
                    # Bound this read by what is left of the budget so a
                    # stalled peer cannot block past the deadline.
                    if sock is not None:
                        try:
                            sock.settimeout(max(0.001, left))
                        except OSError:
                            pass

                # read1() returns after a single underlying socket read.
                # read() would keep aggregating reads until the whole 64 KiB
                # buffer filled, so a host dribbling a few bytes at a time
                # could block here long past the deadline before any check ran.
                chunk = resp.read1(65536)

                # Re-check immediately: the read itself consumed budget, and an
                # over-deadline read must be refused before the bytes are
                # written or an EOF is mistaken for a clean finish.
                if deadline is not None and time.monotonic() >= deadline:
                    f.close()
                    dest.unlink(missing_ok=True)
                    raise OSError(
                        "Download deadline exceeded after "
                        f"{total} bytes: {url}")

                if not chunk:
                    break
                total += len(chunk)
                if max_bytes is not None and total > max_bytes:
                    f.close()
                    dest.unlink(missing_ok=True)
                    raise ValueError(
                        f"Download exceeded {max_bytes} byte limit "
                        f"(got {total} bytes): {url}")
                f.write(chunk)


def _sha256_file(path: Path) -> str:
    """Compute SHA256 hex digest of a file."""
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            chunk = f.read(65536)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


# -- .dsc parsing --------------------------------------------------------------

def parse_dsc(dsc_path: Path) -> DscInfo:
    """Parse a signed .dsc file extracting Source, Version, and Checksums-Sha256.

    Uses manual parsing compatible with dpkg-source signed .dsc format.
    Handles GPG-signed .dsc by stripping signature blocks.

    Validates:
    - Source and Version fields are present and non-empty
    - Checksums-Sha256 stanza exists with at least one entry
    - Each checksum line has valid SHA256 (64 hex chars), positive integer size
    - Each filename passes the safe-filename regex
    - No duplicate filenames
    - The .dsc file itself is not listed in its own Checksums-Sha256
    """
    text = dsc_path.read_text(encoding="utf-8", errors="replace")

    # Strip GPG signature if present
    if "-----BEGIN PGP SIGNED MESSAGE-----" in text:
        parts = text.split("\n\n", 1)
        if len(parts) > 1:
            text = parts[1]
        if "-----BEGIN PGP SIGNATURE-----" in text:
            text = text.split("-----BEGIN PGP SIGNATURE-----")[0]

    source = ""
    version = ""
    files: list[tuple[str, int, str]] = []
    seen_filenames: set[str] = set()

    in_checksums = False
    for raw_line in text.splitlines():
        # Check continuation lines BEFORE stripping (they start with whitespace)
        if in_checksums and (raw_line.startswith(" ") or raw_line.startswith("\t")):
            parts = raw_line.split()
            # A truncated or padded continuation row is a malformed
            # descriptor. Ignoring it would silently shrink the set of files
            # this fetch verifies, so reject the whole descriptor instead.
            if len(parts) != 3:
                raise ValueError(
                    f"Malformed checksum row (expected 3 fields, got "
                    f"{len(parts)}): {raw_line.strip()!r}"
                )
            sha256, size_str, filename = parts
            try:
                size = int(size_str)
            except ValueError:
                raise ValueError(f"Invalid size in .dsc: {size_str}")
            if size < 0:
                raise ValueError(f"Negative size in .dsc: {size}")
            if not re.match(r"^[a-f0-9]{64}$", sha256):
                raise ValueError(f"Invalid SHA256 in .dsc: {sha256}")
            _validate_safe(filename, _SAFE_FILENAME_RE, "filename")
            # Reject .dsc self-reference
            if filename.endswith(".dsc"):
                raise ValueError(
                    f".dsc file must not reference itself: {filename}")
            # Reject duplicate filenames
            if filename in seen_filenames:
                raise ValueError(f"Duplicate filename in .dsc: {filename}")
            seen_filenames.add(filename)
            files.append((filename, size, sha256))
            continue

        line = raw_line.strip()

        # Source: <name>
        if line.startswith("Source:"):
            source = line.split(":", 1)[1].strip()
            continue

        # Version: <version>
        if line.startswith("Version:"):
            version = line.split(":", 1)[1].strip()
            continue

        # Checksums-Sha256: stanza
        if line.startswith("Checksums-Sha256:"):
            in_checksums = True
            continue

        # End of checksums stanza (non-continuation line after checksums)
        if in_checksums and line:
            in_checksums = False

    if not source:
        raise ValueError("Missing Source field in .dsc")
    if not version:
        raise ValueError("Missing Version field in .dsc")
    if not files:
        raise ValueError("No Checksums-Sha256 stanza in .dsc")

    return DscInfo(source=source, version=version, files=files)


def verify_dsc_identity(dsc: DscInfo, expected_source: str,
                        expected_version: str) -> None:
    """Verify .dsc Source and Version match the request."""
    if dsc.source != expected_source:
        raise ValueError(
            f"Source mismatch: requested {expected_source!r}, got {dsc.source!r}"
        )
    if dsc.version != expected_version:
        raise ValueError(
            f"Version mismatch: requested {expected_version!r}, got {dsc.version!r}"
        )


# -- Launchpad URL construction -----------------------------------------------

def _dsc_filename(source: str, version: str) -> str:
    """Build the .dsc filename, dropping any epoch prefix.

    Debian/Ubuntu name the descriptor after the *upstream* version, so an
    epoch-bearing ``1:3.0.2-0ubuntu1.29`` is stored as
    ``<source>_3.0.2-0ubuntu1.29.dsc``.  Only the part after the first colon is
    used; a version without an epoch is used unchanged.
    """
    return f"{source}_{version.split(':', 1)[-1]}.dsc"


def _launchpad_dsc_url(source: str, version: str) -> str:
    """Build the Launchpad historical-archive URL for a .dsc descriptor.

    Launchpad serves ``{source}/{version}/{source}_{upstream-version}.dsc``:
    the publisher path carries the full version (epoch included,
    percent-encoded so the ``:`` is legal in a path segment) while the stored
    descriptor name drops the epoch.  The descriptor's own ``Version`` field
    still declares the epoch, so identity verification compares the full
    version.

    Verified against the real archive entry for openssl 3.0.2-0ubuntu1.29:
    https://launchpad.net/ubuntu/+archive/primary/+sourcefiles/openssl/
    3.0.2-0ubuntu1.29/openssl_3.0.2-0ubuntu1.29.dsc
    """
    fname = _dsc_filename(source, version)
    return f"{LAUNCHPAD_URL_BASE}/{quote(source)}/{quote(version, safe='')}/{fname}"


# -- Shared validation for downloaded .dsc + payloads -------------------------

def _validate_downloaded_package(
    dsc_path: Path,
    dest_dir: Path,
    expected_source: str,
    expected_version: str,
) -> list[Path]:
    """Validate a downloaded .dsc and all referenced payload files.

    Checks:
    - .dsc is a real file with content (not empty)
    - .dsc parses and identity matches
    - Every referenced payload exists and matches its declared size + SHA256
    - No payload filename collides with the descriptor itself

    Returns sorted list of validated file paths (.dsc plus payloads).

    Raises ValueError on any mismatch.
    """
    if not dsc_path.exists() or dsc_path.stat().st_size == 0:
        raise ValueError(f".dsc file missing or empty: {dsc_path}")

    dsc = parse_dsc(dsc_path)
    verify_dsc_identity(dsc, expected_source, expected_version)

    validated = [dsc_path]

    for filename, expected_size, expected_sha256 in dsc.files:
        _validate_safe(filename, _SAFE_FILENAME_RE, "payload filename")
        if filename == dsc_path.name:
            raise ValueError(
                f"Descriptor collides with referenced payload: {filename}")
        payload_path = dest_dir / filename
        # Defence in depth: the safe-name pattern already forbids separators,
        # so the resolved payload must stay a direct child of dest_dir.
        if payload_path.parent != dest_dir:
            raise ValueError(f"Unsafe payload filename: {filename!r}")

        if not payload_path.is_file():
            raise ValueError(f"Referenced payload missing: {filename}")

        actual_size = payload_path.stat().st_size
        if actual_size != expected_size:
            raise ValueError(
                f"Size mismatch for {filename}: "
                f"expected {expected_size}, got {actual_size}")

        actual_sha256 = _sha256_file(payload_path)
        if actual_sha256 != expected_sha256:
            raise ValueError(
                f"SHA256 mismatch for {filename}: "
                f"expected {expected_sha256}, got {actual_sha256}")

        validated.append(payload_path)

    return sorted(validated)


# -- apt-get source path -------------------------------------------------------

# Directories apt would otherwise read for extra configuration. Each is
# redirected to /dev/null so a host's ambient apt setup cannot contribute
# sources to a fetch that is supposed to come from one owned file.
_APT_CONFIG_KEYS = (
    "Dir::Etc::sourceparts",  # other *.list / *.sources in the list dir
    "Dir::Etc::Parts",        # apt.conf.d/*.conf
    "Dir::Etc::main",         # /etc/apt/apt.conf
)

# Early bootstrap configuration, passed via APT_CONFIG so it is read before apt
# resolves its own defaults. Setting Parts/main here is what actually stops
# ambient apt.conf.d and apt.conf from being loaded; the command-line -o flags
# below only override values apt has already read.
# Fixed contents, no caller data interpolated.
_APT_BOOTSTRAP_CONF = (
    'Dir::Etc::Parts "/dev/null";\n'
    'Dir::Etc::main "/dev/null";\n'
)


def write_apt_bootstrap_conf(directory: Path) -> Path:
    """Write the early apt bootstrap config into ``directory``.

    Owner-only permissions; it holds no secrets but nothing else needs to read
    it. The caller owns the lifetime and should delete it with the directory.
    """
    path = Path(directory) / "apt-bootstrap.conf"
    path.write_text(_APT_BOOTSTRAP_CONF, encoding="utf-8")
    path.chmod(0o600)
    return path


def apt_config_options(apt_sources: Path | str) -> list[str]:
    """Build the apt options that pin a fetch to one owned, signed sources file.

    Returns a flat ``["-o", "Key=Value", ...]`` list suitable for both
    ``apt-get update`` and ``apt-get source`` so the two always agree on origin.

    ``sourceparts``/``Parts``/``main`` pointing at /dev/null is what makes the
    owned file authoritative: apt reads no other sources file, no apt.conf.d
    drop-in, and no main apt.conf. The archive still authenticates with its
    ``Signed-By`` keyring, so this narrows origin rather than weakening
    signature checking.
    """
    path = Path(apt_sources)
    if not path.is_absolute():
        raise ValueError(
            f"apt sources path must be absolute, got {apt_sources!r}")
    opts = [f"Dir::Etc::sourcelist={path}"]
    opts += [f"{key}=/dev/null" for key in _APT_CONFIG_KEYS]
    flat: list[str] = []
    for opt in opts:
        flat.extend(["-o", opt])
    return flat


def apt_env(bootstrap_conf: Path | str) -> dict[str, str]:
    """Environment for apt subprocesses with an early, owned APT_CONFIG.

    ``APT_CONFIG`` points at the bootstrap file rather than ``/dev/null``, so
    apt reads our Parts/main redirects as its first configuration and never
    opens the ambient ``apt.conf`` or ``apt.conf.d``. An inherited APT_CONFIG
    is overwritten so nothing from the host environment survives.
    """
    env = dict(os.environ)
    env["APT_CONFIG"] = str(bootstrap_conf)
    return env


def try_apt_source(
    source: str,
    version: str,
    dest_dir: Path,
    timeout: int = 120,
    apt_sources: Path | str | None = None,
) -> list[Path] | None:
    """Try ``apt-get source --only-source`` for the exact requested version.

    apt is asked for source only (no binary packages) at the exact
    ``source=version``.  The download happens in an isolated temporary
    directory so a failed or partial apt run cannot leave files in
    ``dest_dir`` that would later be mistaken for a good download.

    ``apt_sources`` must point at the caller's owned, Signed-By sources file.
    When it is ``None`` this returns ``None`` without invoking apt at all: a
    standalone run must never silently source from whatever ambient apt
    configuration the host happens to have, so the caller falls back to the
    official Launchpad archive instead.

    Returns the verified file list on success, ``None`` on any failure
    (no owned config, non-zero exit, timeout, missing/incorrect .dsc, checksum
    mismatch).
    """
    _validate_safe(source, _SAFE_NAME_RE, "source package name")
    _validate_safe(version, _SAFE_VERSION_RE, "version")

    # No owned sources file means no apt. Fall back to Launchpad rather than
    # trusting ambient configuration.
    if apt_sources is None:
        return None

    dest_dir.mkdir(parents=True, exist_ok=True)
    apt_opts = apt_config_options(apt_sources)

    # Isolate apt output so failed leftovers never reach dest_dir.
    work_dir = Path(tempfile.mkdtemp(prefix=f"apt-{source}-", dir=str(dest_dir)))
    bootstrap = write_apt_bootstrap_conf(work_dir)
    try:
        result = subprocess.run(
            [
                "apt-get",
                *apt_opts,
                "source", "--download-only", "--only-source",
                f"{source}={version}",
            ],
            capture_output=True, text=True, timeout=timeout,
            cwd=str(work_dir), env=apt_env(bootstrap),
        )
        if result.returncode != 0:
            return None

        # Require the exact descriptor for the exact requested version.
        # A .dsc for any other version is a failure, not a partial success.
        dsc_path = work_dir / _dsc_filename(source, version)
        if not dsc_path.is_file() or dsc_path.stat().st_size == 0:
            return None

        # Rejects wrong Source/Version, missing payloads, and any
        # size/SHA256 mismatch against the descriptor.
        validated = _validate_downloaded_package(
            dsc_path, work_dir, source, version,
        )

        # Only verified files are promoted into dest_dir.
        for path in validated:
            path.replace(dest_dir / path.name)
        return sorted(dest_dir / p.name for p in validated)

    except (OSError, subprocess.TimeoutExpired, ValueError):
        return None
    finally:
        shutil.rmtree(work_dir, ignore_errors=True)


# -- Launchpad archive path ----------------------------------------------------

def fetch_source_from_launchpad(
    source: str,
    version: str,
    dest_dir: Path,
    timeout: int = 60,
    retries: int = 2,
) -> list[Path]:
    """Fetch exact source package from Launchpad historical archive.

    Downloads the .dsc descriptor, validates identity/checksums, then
    downloads all referenced payload files.  Returns list of downloaded
    file paths.

    Args:
        source: Exact source package name (e.g., "openssl")
        version: Exact source version (e.g., "3.0.2-0ubuntu1.29")
        dest_dir: Directory to download files into
        timeout: Network timeout per request in seconds
        retries: Number of retry attempts per download

    Returns:
        List of downloaded file paths (.dsc + payload files)

    Raises:
        ValueError: On identity/checksum/safety validation failure
        OSError: On network failure after retries
    """
    _validate_safe(source, _SAFE_NAME_RE, "source package name")
    _validate_safe(version, _SAFE_VERSION_RE, "version")
    dest_dir.mkdir(parents=True, exist_ok=True)

    # Total wall-clock budget shared by every request in this fetch.
    deadline = time.monotonic() + _TOTAL_DEADLINE_SECONDS

    def _budget() -> int:
        """Per-request timeout clamped by what remains of the total budget."""
        left = deadline - time.monotonic()
        if left <= 0:
            raise OSError(
                f"Total download deadline of {_TOTAL_DEADLINE_SECONDS}s exceeded")
        return max(1, min(timeout, int(left)))

    dsc_url = _launchpad_dsc_url(source, version)
    dsc_name = _dsc_filename(source, version)
    dsc_path = dest_dir / dsc_name

    # Download .dsc with retries and size limit
    last_err = None
    for attempt in range(retries + 1):
        try:
            _download(dsc_url, dsc_path, timeout=_budget(),
                      max_bytes=_MAX_DSC_BYTES, deadline=deadline)
            break
        except Exception as e:
            last_err = e
            if attempt < retries:
                continue
            raise OSError(
                f"Failed to download .dsc from {dsc_url} "
                f"after {retries + 1} attempts: {e}"
            ) from e

    # Parse and validate .dsc
    dsc = parse_dsc(dsc_path)
    verify_dsc_identity(dsc, source, version)

    # Early abort before any payload transfer: refuse an over-budget total.
    total_expected = sum(size for _, size, _ in dsc.files)
    if total_expected > _MAX_TOTAL_BYTES:
        raise ValueError(
            f"Total expected payload size {total_expected} exceeds "
            f"{_MAX_TOTAL_BYTES} byte budget")

    # Download referenced payload files
    downloaded = [dsc_path]
    total_downloaded = dsc_path.stat().st_size
    for filename, expected_size, expected_sha256 in dsc.files:
        _validate_safe(filename, _SAFE_FILENAME_RE, "payload filename")
        payload_url = (
            f"{LAUNCHPAD_URL_BASE}/{quote(source)}/"
            f"{quote(version, safe='')}/{filename}")
        payload_path = dest_dir / filename

        # Abort as soon as a payload outgrows its declared size: never let an
        # unverified stream be buffered past its expected footprint.
        per_file_limit = min(expected_size + 1, _MAX_PAYLOAD_BYTES)

        last_err = None
        for attempt in range(retries + 1):
            try:
                _download(payload_url, payload_path, timeout=_budget(),
                          max_bytes=per_file_limit, deadline=deadline)
                break
            except Exception as e:
                last_err = e
                if attempt < retries:
                    continue
                raise OSError(
                    f"Failed to download {filename} "
                    f"from {payload_url}: {e}"
                ) from e

        # Validate size
        actual_size = payload_path.stat().st_size
        if actual_size != expected_size:
            payload_path.unlink(missing_ok=True)
            raise ValueError(
                f"Size mismatch for {filename}: "
                f"expected {expected_size}, got {actual_size}")

        # Validate SHA256
        actual_sha256 = _sha256_file(payload_path)
        if actual_sha256 != expected_sha256:
            payload_path.unlink(missing_ok=True)
            raise ValueError(
                f"SHA256 mismatch for {filename}: "
                f"expected {expected_sha256}, got {actual_sha256}")

        total_downloaded += actual_size
        if total_downloaded > _MAX_TOTAL_BYTES:
            payload_path.unlink(missing_ok=True)
            raise ValueError(
                f"Total download exceeded {_MAX_TOTAL_BYTES} byte budget")

        downloaded.append(payload_path)

    return downloaded


# -- Top-level fetch -----------------------------------------------------------

def fetch_source(
    source: str,
    version: str,
    dest_dir: Path,
    timeout: int = 120,
    apt_sources: Path | str | None = None,
) -> list[Path]:
    """Fetch exact source package: try apt-get first, then Launchpad fallback.

    ``timeout`` is the caller's overall budget and is passed to both paths.
    ``apt_sources`` is the owned sources file; without it apt is skipped and
    the Launchpad archive is used.

    Returns list of downloaded file paths, or raises on failure.
    """
    apt_result = try_apt_source(
        source, version, dest_dir, timeout=timeout, apt_sources=apt_sources)
    if apt_result is not None:
        return apt_result

    # Fallback to Launchpad historical archive
    print(
        f"  apt-get source failed for {source}={version}, "
        f"trying Launchpad archive...",
        file=sys.stderr,
    )
    return fetch_source_from_launchpad(
        source, version, dest_dir, timeout=timeout)


# -- CLI entry point -----------------------------------------------------------

if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(
        description=(
            "Fetch exact Ubuntu source package "
            "(apt-get or Launchpad fallback)."
        ),
    )
    parser.add_argument("source", help="Source package name")
    parser.add_argument("version", help="Exact source version")
    parser.add_argument("--dest", required=True, help="Destination directory")
    parser.add_argument(
        "--timeout", type=int, default=120,
        help="Budget in seconds, applied to apt and to network reads",
    )
    parser.add_argument(
        "--apt-sources",
        help=(
            "Absolute path to this project's owned, Signed-By deb822 "
            "sources file. Required for apt; without it the official "
            "Launchpad archive is used instead."
        ),
    )
    args = parser.parse_args()

    dest = Path(args.dest)
    try:
        files = fetch_source(
            args.source, args.version, dest, timeout=args.timeout,
            apt_sources=args.apt_sources,
        )
        for f in files:
            print(f"  {f.name} ({f.stat().st_size} bytes)")
        print(f"Downloaded {len(files)} file(s) to {dest}")
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        sys.exit(1)