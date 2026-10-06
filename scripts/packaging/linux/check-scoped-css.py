#!/usr/bin/env python3
"""Validate CSS isolation scope attributes in a .NET publish output.

Exit 0 success, 1 scope mismatch, 2 structural/input failure.
"""

import argparse
import re
import sys
from enum import Enum, auto
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urlparse

_UI_BUNDLE_RE = re.compile(
    r"^_content/MqttProbe\.UI/MqttProbe\.UI(?:\.[a-z0-9]+)?\.bundle\.scp\.css$"
)
_ISO_RE = re.compile(r"\[b-([a-z0-9]{10})\]")


class Outcome(Enum):
    PASS = auto()
    MISMATCH = auto()
    STRUCTURAL = auto()


class Result:
    __slots__ = ("outcome", "errors")

    def __init__(self, outcome: Outcome, errors: list[str] | None = None):
        self.outcome = outcome
        self.errors = errors or []

    @property
    def ok(self) -> bool:
        return self.outcome == Outcome.PASS


class _StylesheetCollector(HTMLParser):
    def __init__(self):
        super().__init__()
        self.hrefs: list[str] = []

    def handle_starttag(self, tag, attrs):
        if tag != "link":
            return
        d = dict(attrs)
        rels = d.get("rel", "").lower().split()
        if "stylesheet" not in rels:
            return
        raw = d.get("href", "").strip()
        if not raw:
            return
        parsed = urlparse(raw)
        if parsed.scheme or parsed.netloc:
            return
        path = parsed.path
        if ".." in path.split("/"):
            return
        normalized = path.lstrip("/")
        if normalized.startswith("./"):
            normalized = normalized[2:]
        self.hrefs.append(normalized)


def _has_link_to_host(index_html: Path, expected: str) -> bool:
    try:
        text = index_html.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return False
    p = _StylesheetCollector()
    try:
        p.feed(text)
    except Exception:
        return False
    return expected in p.hrefs


def _lex_css(text: str) -> list[tuple[str, str]]:
    """Tokenize CSS into (kind, value) pairs.

    Kinds: 'comment', 'string', 'at-import', 'lbrace', 'rbrace',
           'semicolon', 'other'
    """
    tokens: list[tuple[str, str]] = []
    i = 0
    n = len(text)
    while i < n:
        if text[i] == "/" and i + 1 < n and text[i + 1] == "*":
            end = text.find("*/", i + 2)
            if end == -1:
                tokens.append(("comment", text[i:]))
                break
            tokens.append(("comment", text[i:end + 2]))
            i = end + 2
        elif text[i] in ('"', "'"):
            q = text[i]
            j = i + 1
            while j < n:
                if text[j] == "\\" and j + 1 < n:
                    j += 2
                elif text[j] == q:
                    j += 1
                    break
                else:
                    j += 1
            tokens.append(("string", text[i:j]))
            i = j
        elif text[i] == "@":
            m = re.match(
                r"@import\s+(?:url\(\s*)?['\"]?([^'\")\s;]+)['\"]?\s*\)?\s*;",
                text[i:],
                re.IGNORECASE,
            )
            if m:
                tokens.append(("at-import", m.group(1)))
                i += m.end()
            else:
                m2 = re.match(r"@[a-zA-Z-]+", text[i:])
                if m2:
                    tokens.append(("other", m2.group(0)))
                    i += m2.end()
                else:
                    tokens.append(("other", text[i]))
                    i += 1
        elif text[i] == "{":
            tokens.append(("lbrace", "{"))
            i += 1
        elif text[i] == "}":
            tokens.append(("rbrace", "}"))
            i += 1
        elif text[i] == ";":
            tokens.append(("semicolon", ";"))
            i += 1
        else:
            j = i + 1
            while j < n and text[j] not in '{};"\'/@':
                j += 1
            tokens.append(("other", text[i:j]))
            i = j
    return tokens


def _find_ui_import(styles_css: Path, wwwroot: Path) -> tuple[Path | None, str | None]:
    try:
        raw = styles_css.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None, "cannot read host stylesheet"

    tokens = _lex_css(raw)

    ui_target: Path | None = None
    ui_count = 0
    brace_depth = 0
    preamble_ended = False

    for kind, val in tokens:
        if kind == "lbrace":
            preamble_ended = True
            brace_depth += 1
        elif kind == "rbrace":
            brace_depth = max(0, brace_depth - 1)
        elif kind == "at-import":
            if brace_depth > 0:
                if _UI_BUNDLE_RE.match(val):
                    return None, "UI scoped bundle import nested inside at-rule"
                continue
            if preamble_ended:
                if _UI_BUNDLE_RE.match(val):
                    return None, "UI scoped bundle import after rule body"
                continue
            if not _UI_BUNDLE_RE.match(val):
                continue
            ui_count += 1
            if ui_count > 1:
                return None, "multiple active UI scoped bundle imports"
            target = (styles_css.parent / val).resolve()
            wwwroot_res = wwwroot.resolve()
            try:
                target.relative_to(wwwroot_res)
            except ValueError:
                return None, f"UI import path escapes wwwroot: {val}"
            ui_target = target

    if ui_target is None:
        return None, "no active @import targeting _content/MqttProbe.UI/*.bundle.scp.css"
    if not ui_target.is_file():
        return None, f"UI scoped bundle not found: {ui_target.name}"
    return ui_target, None


def _extract_scoped_selectors(css_text: str) -> set[bytes]:
    tokens = _lex_css(css_text)
    found: set[bytes] = set()
    # Stack entries: [prelude_parts, has_declarations]
    stack: list[list] = []
    pending: list[str] = []

    for kind, val in tokens:
        if kind == "lbrace":
            stack.append([pending, False])
            pending = []
        elif kind == "rbrace":
            if stack:
                prelude, has_decls = stack.pop()
                if has_decls:
                    selector_text = "".join(prelude)
                    for m in _ISO_RE.finditer(selector_text):
                        found.add(m.group(1).encode("ascii"))
            pending = []
        elif kind not in ("comment", "string"):
            pending.append(val)
            if stack and kind == "other" and ":" in val:
                stack[-1][1] = True

    return found


def _is_utf16_ident_at(data: bytes, offset: int) -> bool:
    """Check if the UTF-16-LE code unit starting at *offset* is an ASCII identifier char."""
    if offset < 0 or offset + 1 >= len(data):
        return False
    cp = int.from_bytes(data[offset:offset + 2], "little")
    return cp < 128 and (chr(cp).isalnum() or chr(cp) in "_-")


def _dll_has_scope(dll_bytes: bytes, scope: bytes) -> bool:
    token = b"b-" + scope
    needle16 = token.decode("ascii").encode("utf-16-le")
    idx = dll_bytes.find(needle16)
    while idx != -1:
        before_ok = idx < 2 or not _is_utf16_ident_at(dll_bytes, idx - 2)
        after = idx + len(needle16)
        after_ok = after + 1 >= len(dll_bytes) or not _is_utf16_ident_at(dll_bytes, after)
        if before_ok and after_ok:
            return True
        idx = dll_bytes.find(needle16, idx + 1)
    idx = dll_bytes.find(token)
    while idx != -1:
        before_ok = idx == 0 or not (chr(dll_bytes[idx - 1]).isalnum() or chr(dll_bytes[idx - 1]) in "_-")
        after = idx + len(token)
        after_ok = after >= len(dll_bytes) or not (chr(dll_bytes[after]).isalnum() or chr(dll_bytes[after]) in "_-")
        if before_ok and after_ok:
            return True
        idx = dll_bytes.find(token, idx + 1)
    return False


def check(publish_dir: Path) -> Result:
    wwwroot = publish_dir / "wwwroot"
    if not wwwroot.is_dir():
        return Result(Outcome.STRUCTURAL, [f"wwwroot not found: {wwwroot}"])

    index_html = wwwroot / "index.html"
    if not index_html.is_file():
        return Result(Outcome.STRUCTURAL, [f"index.html not found: {index_html}"])

    styles_css = wwwroot / "MqttProbe.Desktop.styles.css"
    if not styles_css.is_file():
        return Result(Outcome.STRUCTURAL, [f"host stylesheet not found: {styles_css}"])

    expected_href = "MqttProbe.Desktop.styles.css"
    if not _has_link_to_host(index_html, expected_href):
        return Result(Outcome.STRUCTURAL, [
            f"index.html missing <link rel=stylesheet href=\"{expected_href}\">"
        ])

    ui_target, err = _find_ui_import(styles_css, wwwroot)
    if err:
        return Result(Outcome.STRUCTURAL, [err])

    assert ui_target is not None
    try:
        bundle_text = ui_target.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as e:
        return Result(Outcome.STRUCTURAL, [f"cannot read bundle: {e}"])

    if not _ISO_RE.search(bundle_text):
        return Result(Outcome.STRUCTURAL, [
            f"no isolation selectors [b-XXXXXXXXXX] in {ui_target.name}"
        ])

    css_scopes = _extract_scoped_selectors(bundle_text)
    if not css_scopes:
        return Result(Outcome.STRUCTURAL, [
            f"zero effective scoped rules in {ui_target.name}"
        ])

    dll_path = publish_dir / "MqttProbe.UI.dll"
    if not dll_path.is_file():
        return Result(Outcome.STRUCTURAL, [f"MqttProbe.UI.dll not found: {dll_path}"])

    try:
        dll_bytes = dll_path.read_bytes()
    except OSError as e:
        return Result(Outcome.STRUCTURAL, [f"cannot read MqttProbe.UI.dll: {e}"])

    missing = sorted(s.decode("ascii") for s in css_scopes if not _dll_has_scope(dll_bytes, s))
    if missing:
        return Result(Outcome.MISMATCH, [
            f"CSS isolation scopes missing from assembly ({len(missing)}): "
            + ", ".join(missing)
        ])

    return Result(Outcome.PASS)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-dir", required=True)
    args = parser.parse_args()

    publish_dir = Path(args.publish_dir)
    if not publish_dir.is_dir():
        print(f"ERROR: --publish-dir not found: {publish_dir}", file=sys.stderr)
        return 2

    result = check(publish_dir)
    if result.ok:
        print("PASS: CSS isolation scopes consistent with assembly")
        return 0
    for e in result.errors:
        print(f"FAIL: {e}", file=sys.stderr)
    return 1 if result.outcome == Outcome.MISMATCH else 2


if __name__ == "__main__":
    sys.exit(main())