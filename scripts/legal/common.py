"""Shared helpers for the legal notice pipeline."""

from __future__ import annotations

import hashlib
import json
import re
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LEGAL = ROOT / "legal"
PROFILES = LEGAL / "profiles"
SUPPLEMENTS = LEGAL / "supplements"
GENERATED = LEGAL / "generated"
TEXTS = GENERATED / "texts"


class LegalError(RuntimeError):
    pass


def read_json(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise LegalError(f"missing file: {relative(path)}") from error
    except json.JSONDecodeError as error:
        raise LegalError(f"invalid JSON in {relative(path)}: {error}") from error


def write_text(path, text):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    data = text.encode("utf-8")
    if path.is_file() and path.read_bytes() == data:
        return False
    path.write_bytes(data)
    return True


def write_json(path, value):
    return write_text(path, json.dumps(value, indent=2, ensure_ascii=False) + "\n")


def relative(path):
    try:
        return Path(path).resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return str(path)


def normalize_text(value, *, html=False):
    if isinstance(value, bytes):
        value = value.decode("utf-8-sig", errors="replace")
    value = value.replace("\ufeff", "")
    if html:
        value = html_to_text(value)
    value = value.replace("\r\n", "\n").replace("\r", "\n")
    lines = [line.rstrip() for line in value.split("\n")]
    value = "\n".join(lines).strip("\n")
    return value + "\n" if value else ""


def html_to_text(value):
    from html.parser import HTMLParser

    class Extractor(HTMLParser):
        def __init__(self):
            super().__init__(convert_charrefs=True)
            self.parts = []
            self.skip = 0

        def handle_starttag(self, tag, attrs):
            if tag in ("script", "style"):
                self.skip += 1
            elif tag in ("p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "pre"):
                self.parts.append("\n")

        def handle_endtag(self, tag):
            if tag in ("script", "style") and self.skip:
                self.skip -= 1
            elif tag in ("p", "div", "li", "tr", "h1", "h2", "h3", "h4", "pre"):
                self.parts.append("\n")

        def handle_data(self, data):
            if not self.skip:
                self.parts.append(data)

    extractor = Extractor()
    extractor.feed(value)
    return "".join(extractor.parts)


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha256_text(text):
    return sha256_bytes(normalize_text(text).encode("utf-8"))


def sha256_file(path):
    return sha256_bytes(Path(path).read_bytes())


def slug(text, limit=64):
    normalized = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode("ascii")
    normalized = re.sub(r"[^a-zA-Z0-9]+", "-", normalized).strip("-").lower()
    return normalized[:limit].strip("-") or "document"


def safe_relative(path):
    raw = str(path).replace("\\", "/")
    if raw.startswith("/") or raw.startswith("//") or re.match(r"^[A-Za-z]:", raw):
        raise LegalError(f"unsafe relative path: {path}")
    candidate = Path(raw)
    if candidate.is_absolute() or ".." in candidate.parts or not candidate.parts:
        raise LegalError(f"unsafe relative path: {path}")
    return candidate


def package_cache_dirs(assets):
    folders = []
    for value in assets.get("packageFolders", {}):
        folder = Path(value)
        if folder.is_dir() and folder not in folders:
            folders.append(folder)
    return folders


def package_directory(package_id, version, folders):
    for folder in folders:
        candidate = folder / package_id.lower() / version
        if candidate.is_dir():
            return candidate
    return None
