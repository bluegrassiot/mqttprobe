"""Tests for check-scoped-css.py."""

import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]
LINUX_DIR = REPO_ROOT / "scripts" / "packaging" / "linux"
CHECKER_PATH = LINUX_DIR / "check-scoped-css.py"

if str(LINUX_DIR) not in sys.path:
    sys.path.insert(0, str(LINUX_DIR))

_spec = importlib.util.spec_from_file_location("csc", CHECKER_PATH)
assert _spec is not None and _spec.loader is not None
_c = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_c)


def _dll(*scopes: str) -> bytes:
    return b"\x00\x00".join(f"b-{s}".encode("utf-16-le") for s in scopes)


def _mk(files: dict[str, str], dll_scopes: list[str] | None = None,
        dll_bytes: bytes | None = None) -> Path:
    d = Path(tempfile.mkdtemp())
    w = d / "wwwroot"
    w.mkdir()
    for rel, content in files.items():
        p = w / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(content, encoding="utf-8")
    if dll_bytes is not None:
        (d / "MqttProbe.UI.dll").write_bytes(dll_bytes)
    elif dll_scopes is not None:
        (d / "MqttProbe.UI.dll").write_bytes(_dll(*dll_scopes))
    return d


_HOST = "MqttProbe.Desktop.styles.css"
_UI_BUNDLE = "_content/MqttProbe.UI/MqttProbe.UI.a1b2c3d4e5.bundle.scp.css"
_IDX_6LINKS = (
    '<!DOCTYPE html><html><head>'
    '<link rel="icon" href="favicon.svg" type="image/svg+xml" />'
    '<link href="_content/MqttProbe.UI/css/fonts.css" rel="stylesheet" />'
    '<link href="css/app.css" rel="stylesheet" />'
    '<link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />'
    '<link href="_content/MqttProbe.UI/css/components.css" rel="stylesheet" />'
    f'<link href="{_HOST}" rel="stylesheet" />'
    '<link rel="stylesheet" href="_content/Blazor-ApexCharts/css/apexcharts.css" />'
    '</head></html>'
)
_IMP = f"@import '{_UI_BUNDLE}';"


def _pass(files: dict[str, str], dll_scopes: list[str]) -> bool:
    d = _mk(files, dll_scopes=dll_scopes)
    return _c.check(d).ok


def _fail(files: dict[str, str], dll_scopes: list[str] | None = None,
          dll_bytes: bytes | None = None) -> list[str]:
    d = _mk(files, dll_scopes=dll_scopes, dll_bytes=dll_bytes)
    return _c.check(d).errors


class TestLoadingChain(unittest.TestCase):
    def test_missing_wwwroot(self):
        d = Path(tempfile.mkdtemp())
        self.assertEqual(_c.check(d).outcome, _c.Outcome.STRUCTURAL)

    def test_missing_index(self):
        r = _c.check(_mk({}))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)
        self.assertIn("index.html", r.errors[0])

    def test_missing_styles_css(self):
        r = _c.check(_mk({"index.html": _IDX_6LINKS}))
        self.assertIn("host stylesheet", r.errors[0])

    def test_no_stylesheet_link(self):
        r = _c.check(_mk({"index.html": "<html></html>", _HOST: _IMP}))
        self.assertIn("<link rel=stylesheet", r.errors[0])

    def test_commented_link_ignored(self):
        html = f'<html><!-- <link href="{_HOST}" rel="stylesheet" /> --></html>'
        r = _c.check(_mk({"index.html": html, _HOST: _IMP,
                           _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}, dll_scopes=["aaaaaaaaaa"]))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)

    def test_six_links_penultimate_host(self):
        files = {"index.html": _IDX_6LINKS, _HOST: _IMP,
                 _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}
        self.assertTrue(_pass(files, ["aaaaaaaaaa"]))

    def test_link_with_query_stripped(self):
        html = f'<html><head><link rel="stylesheet" href="{_HOST}?v=1" /></head></html>'
        files = {"index.html": html, _HOST: _IMP,
                 _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}
        self.assertTrue(_pass(files, ["aaaaaaaaaa"]))

    def test_link_dot_slash(self):
        html = f'<html><head><link rel="stylesheet" href="./{_HOST}" /></head></html>'
        files = {"index.html": html, _HOST: _IMP,
                 _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}
        self.assertTrue(_pass(files, ["aaaaaaaaaa"]))

    def test_link_leading_slash(self):
        html = f'<html><head><link rel="stylesheet" href="/{_HOST}" /></head></html>'
        files = {"index.html": html, _HOST: _IMP,
                 _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}
        self.assertTrue(_pass(files, ["aaaaaaaaaa"]))

    def test_link_dotdot_rejected(self):
        html = f'<html><head><link rel="stylesheet" href="../{_HOST}" /></head></html>'
        r = _c.check(_mk({"index.html": html, _HOST: _IMP}))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)

    def test_link_remote_rejected(self):
        html = '<html><head><link rel="stylesheet" href="https://cdn.example.com/x.css" /></head></html>'
        r = _c.check(_mk({"index.html": html, _HOST: _IMP}))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)


class TestImportScanning(unittest.TestCase):
    def test_no_ui_import(self):
        r = _c.check(_mk({"index.html": _IDX_6LINKS, _HOST: "@import 'x.css';"}))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)
        self.assertIn("no active @import", r.errors[0])

    def test_unrelated_then_ui_passes(self):
        css = f"@import '_content/Other/lib.css';\n{_IMP}"
        files = {"index.html": _IDX_6LINKS, _HOST: css,
                 "_content/Other/lib.css": "body{}",
                 _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}
        self.assertTrue(_pass(files, ["aaaaaaaaaa"]))

    def test_commented_import_ignored(self):
        active = "_content/MqttProbe.UI/MqttProbe.UI.a1.bundle.scp.css"
        css = f"/* @import '{active}'; */\n@import '{active}';"
        files = {"index.html": _IDX_6LINKS, _HOST: css,
                 active: ".n[b-aaaaaaaaaa]{color:red}"}
        self.assertTrue(_pass(files, ["aaaaaaaaaa"]))

    def test_commented_matching_masks_active_mismatch(self):
        ok = "_content/MqttProbe.UI/MqttProbe.UI.ok.bundle.scp.css"
        bad = "_content/MqttProbe.UI/MqttProbe.UI.bad.bundle.scp.css"
        css = f"/* @import '{ok}'; */\n@import '{bad}';"
        files = {"index.html": _IDX_6LINKS, _HOST: css,
                 ok: ".n[b-aaaaaaaaaa]{color:red}",
                 bad: ".n[b-rrv9egykja]{color:red}"}
        errors = _fail(files, dll_scopes=["aaaaaaaaaa"])
        self.assertTrue(any("rrv9egykja" in e for e in errors))

    def test_string_import_ignored(self):
        active = "_content/MqttProbe.UI/MqttProbe.UI.a1.bundle.scp.css"
        css = f"@import '{active}';\nbody {{ background: url('{active}'); }}"
        files = {"index.html": _IDX_6LINKS, _HOST: css,
                 active: ".n[b-aaaaaaaaaa]{color:red}"}
        self.assertTrue(_pass(files, ["aaaaaaaaaa"]))

    def test_late_import_after_rule_structural(self):
        active = "_content/MqttProbe.UI/MqttProbe.UI.a1.bundle.scp.css"
        css = f"body {{ color: red; }}\n@import '{active}';"
        r = _c.check(_mk({"index.html": _IDX_6LINKS, _HOST: css,
                           active: ".n[b-aaaaaaaaaa]{color:red}"},
                          dll_scopes=["aaaaaaaaaa"]))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)
        self.assertIn("after rule", r.errors[0])

    def test_multiple_ui_imports_structural(self):
        a1 = "_content/MqttProbe.UI/MqttProbe.UI.a1.bundle.scp.css"
        a2 = "_content/MqttProbe.UI/MqttProbe.UI.a2.bundle.scp.css"
        css = f"@import '{a1}';\n@import '{a2}';"
        files = {"index.html": _IDX_6LINKS, _HOST: css,
                 a1: ".n[b-aaaaaaaaaa]{color:red}",
                 a2: ".n[b-bbbbbbbbbb]{color:red}"}
        r = _c.check(_mk(files, dll_scopes=["aaaaaaaaaa", "bbbbbbbbbb"]))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)
        self.assertIn("multiple", r.errors[0])

    def test_nested_ui_import_structural(self):
        css = f"@media screen {{\n@import '{_UI_BUNDLE}';\n}}"
        files = {"index.html": _IDX_6LINKS, _HOST: css,
                 _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}
        r = _c.check(_mk(files, dll_scopes=["aaaaaaaaaa"]))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)
        self.assertIn("nested", r.errors[0])

    def test_non_bundle_rejected(self):
        css = "@import '_content/MqttProbe.UI/styles.css';"
        r = _c.check(_mk({"index.html": _IDX_6LINKS, _HOST: css}))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)

    def test_path_escape_rejected(self):
        css = "@import '_content/MqttProbe.UI/../../etc/passwd';"
        r = _c.check(_mk({"index.html": _IDX_6LINKS, _HOST: css}))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)


class TestScopeExtraction(unittest.TestCase):
    def test_basic_selector(self):
        self.assertEqual(_c._extract_scoped_selectors(".nav[b-aaaaaaaaaa]{display:flex}"),
                         {b"aaaaaaaaaa"})

    def test_empty_comment_only_skipped(self):
        self.assertEqual(_c._extract_scoped_selectors(".nav[b-aaaaaaaaaa]{/* comment */}"), set())

    def test_empty_braces_skipped(self):
        self.assertEqual(_c._extract_scoped_selectors(".nav[b-aaaaaaaaaa]{}"), set())

    def test_semicolon_only_skipped(self):
        self.assertEqual(_c._extract_scoped_selectors(".x[b-aaaaaaaaaa]{;}"), set())

    def test_nested_media(self):
        css = "@media(max-width:599px){.nav[b-aaaaaaaaaa]{display:none}}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa"})

    def test_grouped_selector(self):
        css = ".a[b-aaaaaaaaaa],.b[b-bbbbbbbbbb]{color:red}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa", b"bbbbbbbbbb"})

    def test_css_class_not_detected(self):
        css = "[b-aaaaaaaaaa].mud-tab-active{color:red}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa"})

    def test_scope_in_value_ignored(self):
        css = ".x{content:'[b-aaaaaaaaaa]'}"
        self.assertEqual(_c._extract_scoped_selectors(css), set())

    def test_scope_in_double_quoted_value_ignored(self):
        css = '.x{content:"[b-aaaaaaaaaa]"}'
        self.assertEqual(_c._extract_scoped_selectors(css), set())

    def test_comment_in_block_preserves_rule(self):
        css = ".nav[b-aaaaaaaaaa]{/* note */color:red}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa"})

    def test_escaped_quote_in_string(self):
        css = '.nav[b-aaaaaaaaaa]{background:url("a\\"b");color:red}'
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa"})

    def test_content_empty_string_nonempty(self):
        css = ".nav[b-aaaaaaaaaa]{content:''}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa"})

    def test_supports_rule(self):
        css = "@supports(display:grid){.nav[b-aaaaaaaaaa]{display:grid}}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa"})

    def test_scope_in_value_does_not_attach_next_rule(self):
        css = ".x{content:'[b-aaaaaaaaaa]'}\n.y[b-bbbbbbbbbb]{color:red}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"bbbbbbbbbb"})

    def test_media_with_multiple_rules(self):
        css = "@media(min-width:600px){.a[b-aaaaaaaaaa]{display:flex}.b[b-bbbbbbbbbb]{display:none}}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa", b"bbbbbbbbbb"})

    def test_deeply_nested_supports_media(self):
        css = "@supports(display:grid){@media(min-width:600px){.nav[b-aaaaaaaaaa]{display:grid}}}"
        self.assertEqual(_c._extract_scoped_selectors(css), {b"aaaaaaaaaa"})


class TestDllMatching(unittest.TestCase):
    def test_utf16le(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=_dll("aaaaaaaaaa"))
        self.assertTrue(_c.check(d).ok)

    def test_ascii_token(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=b"some b-aaaaaaaaaa text")
        self.assertTrue(_c.check(d).ok)

    def test_bare_suffix_no_match(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=b"aaaaaaaaaa")
        self.assertEqual(_c.check(d).outcome, _c.Outcome.MISMATCH)

    def test_longer_utf16_no_match(self):
        longer = "b-aaaaaaaaaax"
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=longer.encode("utf-16-le"))
        self.assertEqual(_c.check(d).outcome, _c.Outcome.MISMATCH)

    def test_longer_ascii_no_match(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=b"b-aaaaaaaaaax")
        self.assertEqual(_c.check(d).outcome, _c.Outcome.MISMATCH)

    def test_embedded_valid(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=b"<div b-aaaaaaaaaa>")
        self.assertTrue(_c.check(d).ok)

    def test_xb_prefix_no_match(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=b"xb-aaaaaaaaaa")
        self.assertEqual(_c.check(d).outcome, _c.Outcome.MISMATCH)

    def test_utf16_boundary_before(self):
        xb = "xb-aaaaaaaaaa"
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=xb.encode("utf-16-le"))
        self.assertEqual(_c.check(d).outcome, _c.Outcome.MISMATCH)

    def test_missing_dll(self):
        r = _c.check(_mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                           _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)
        self.assertIn("MqttProbe.UI.dll", r.errors[0])

    def test_underscore_suffix_no_match(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=b"b-aaaaaaaaaa_extra")
        self.assertEqual(_c.check(d).outcome, _c.Outcome.MISMATCH)

    def test_dash_suffix_no_match(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes=b"b-aaaaaaaaaa-extra")
        self.assertEqual(_c.check(d).outcome, _c.Outcome.MISMATCH)

    def test_underscore_suffix_utf16_no_match(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"},
                 dll_bytes="b-aaaaaaaaaa_extra".encode("utf-16-le"))
        self.assertEqual(_c.check(d).outcome, _c.Outcome.MISMATCH)


class TestUnrelatedBundles(unittest.TestCase):
    def test_unrelated_matching_no_mask(self):
        files = {"index.html": _IDX_6LINKS, _HOST: _IMP,
                 _UI_BUNDLE: ".n[b-rrv9egykja]{color:red}",
                 "_content/X/y.bundle.scp.css": ".n[b-aaaaaaaaaa]{color:red}"}
        errors = _fail(files, dll_scopes=["aaaaaaaaaa"])
        self.assertTrue(any("rrv9egykja" in e for e in errors))

    def test_unrelated_mismatched_ignored(self):
        files = {"index.html": _IDX_6LINKS, _HOST: _IMP,
                 _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}",
                 "_content/X/y.bundle.scp.css": ".n[b-rrv9egykja]{color:red}"}
        self.assertTrue(_pass(files, ["aaaaaaaaaa"]))


class TestResultOutcome(unittest.TestCase):
    def test_pass(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}, dll_scopes=["aaaaaaaaaa"])
        r = _c.check(d)
        self.assertEqual(r.outcome, _c.Outcome.PASS)
        self.assertTrue(r.ok)
        self.assertEqual(r.errors, [])

    def test_mismatch(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-rrv9egykja]{color:red}"}, dll_scopes=["aaaaaaaaaa"])
        r = _c.check(d)
        self.assertEqual(r.outcome, _c.Outcome.MISMATCH)
        self.assertFalse(r.ok)
        self.assertTrue(len(r.errors) == 1)
        self.assertIn("rrv9egykja", r.errors[0])

    def test_structural(self):
        r = _c.check(Path(tempfile.mkdtemp()))
        self.assertEqual(r.outcome, _c.Outcome.STRUCTURAL)


class TestCLIReturnCodes(unittest.TestCase):
    def _run(self, publish_dir: Path) -> subprocess.CompletedProcess:
        return subprocess.run(
            [sys.executable, str(CHECKER_PATH), "--publish-dir", str(publish_dir)],
            capture_output=True, text=True
        )

    def test_pass_returns_0(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-aaaaaaaaaa]{color:red}"}, dll_scopes=["aaaaaaaaaa"])
        r = self._run(d)
        self.assertEqual(r.returncode, 0)
        self.assertIn("PASS", r.stdout)

    def test_mismatch_returns_1(self):
        d = _mk({"index.html": _IDX_6LINKS, _HOST: _IMP,
                  _UI_BUNDLE: ".n[b-rrv9egykja]{color:red}"}, dll_scopes=["aaaaaaaaaa"])
        r = self._run(d)
        self.assertEqual(r.returncode, 1)
        self.assertIn("rrv9egykja", r.stderr)

    def test_structural_returns_2(self):
        d = Path(tempfile.mkdtemp())
        r = self._run(d)
        self.assertEqual(r.returncode, 2)

    def test_missing_dir_returns_2(self):
        r = self._run(Path("/nonexistent"))
        self.assertEqual(r.returncode, 2)


if __name__ == "__main__":
    unittest.main()