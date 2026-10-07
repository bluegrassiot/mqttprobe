#!/usr/bin/env python3
"""Tests for scripts/ci/notary-diagnostics.py.

Every notarytool call goes through subprocess.run, so the whole path runs against
a fake that raises on any call a test did not plan. "No log for In Progress" and
the candidate cap are therefore checked by the calls not happening.

Run: python -m unittest discover -s scripts/ci/tests
"""

import contextlib
import importlib.util
import io
import json
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

_SPEC = importlib.util.spec_from_file_location(
    "ci_notary_diagnostics", Path(__file__).resolve().parents[1] / "notary-diagnostics.py"
)
if _SPEC is None or _SPEC.loader is None:
    raise SystemExit("Cannot load scripts/ci/notary-diagnostics.py")
notary = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(notary)

PACK_START = "2026-01-30T12:34:56Z"
KEYCHAIN = "/tmp/runner/signing.keychain-db"
NOISE = "https://appstoreconnect.apple.com/notary/download/deadbeef"

# The two names vpk actually submits, as seen on the macOS runner.
APP_NAME = "notarize.zip"
PKG_NAME = "MQTTProbe-osx-Setup.pkg"
OTHER_NAME = "SomeOtherApp-2.0.0.pkg"


def ok(payload, returncode=0):
    return subprocess.CompletedProcess(
        args=[], returncode=returncode, stdout=json.dumps(payload), stderr=""
    )


def fail(returncode=1, stdout="", stderr=""):
    return subprocess.CompletedProcess(args=[], returncode=returncode, stdout=stdout, stderr=stderr)


def entry(submission_id, created, status, name=APP_NAME):
    return {"id": submission_id, "createdDate": created, "status": status, "name": name}


def history(*entries):
    """The documented `notarytool history -f json` envelope."""
    return ok({"history": list(entries), "message": "Successfully received"})


class Fake:
    """Answers calls from a plan keyed by 'history', 'info <id>', 'log <id>'."""

    def __init__(self, plan):
        self.plan, self.commands, self.timeouts = plan, [], []

    def __call__(self, command, **kwargs):
        self.commands.append(list(command))
        self.timeouts.append(kwargs.get("timeout"))
        key = self._key(command)
        if key not in self.plan:
            raise AssertionError(f"unexpected notarytool call: {key}")
        answer = self.plan[key]
        if isinstance(answer, BaseException):
            raise answer
        return answer

    @staticmethod
    def _key(command):
        sub = command[2]
        return f"{sub} {command[3]}" if sub in ("info", "log") else sub

    def called(self):
        return [self._key(command) for command in self.commands]


class Tests(unittest.TestCase):
    def test_script_starts_in_isolated_mode(self) -> None:
        # The mocked tests load the module by path, so they never put
        # scripts/ci on sys.path and cannot see the inspect.py in that
        # directory shadow the stdlib one. That killed argparse on the
        # Python 3.14 runner, so run the script for real, from that cwd, the
        # way the workflow does: `python3 -I scripts/ci/notary-diagnostics.py`.
        script = Path(__file__).resolve().parents[1] / "notary-diagnostics.py"
        done = subprocess.run(
            [sys.executable, "-I", str(script), "--help"],
            cwd=script.parent,
            capture_output=True,
            text=True,
            timeout=15,
        )
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("usage:", done.stdout)
        self.assertIn("--pack-start", done.stdout)

        workflow = script.parents[2] / ".github/workflows/build-macos-desktop.yml"
        self.assertIn("-I scripts/ci/notary-diagnostics.py", workflow.read_text(encoding="utf-8"))

    def run_helper(self, plan, pack_start=PACK_START, names=None):
        fake = Fake(plan)
        output = io.StringIO()
        argv = ["--pack-start", pack_start, "--keychain", KEYCHAIN]
        for name in names or ():
            argv += ["--expected-name", name]
        with mock.patch.object(subprocess, "run", fake):
            with contextlib.redirect_stdout(output):
                code = notary.main(argv)
        self.assertEqual(code, 0, "diagnostics must never fail the step")
        return output.getvalue(), fake

    def test_candidates_need_created_after_the_pack_start(self) -> None:
        # 07:30-05:00 is 12:30Z (before the pack), 07:35-05:00 is 12:35Z (after).
        output, fake = self.run_helper({
            "history": history(
                entry("before", "2026-01-30T07:30:00-05:00", "Invalid"),
                entry("after", "2026-01-30T07:35:00-05:00", "Invalid"),
            ),
            "info after": ok({"id": "after", "status": "Invalid"}),
            "log after": ok({"issues": []}),
        })
        self.assertIn("id=after", output)
        self.assertNotIn("id=before", output)
        self.assertEqual(fake.called(), ["history", "info after", "log after"])

    def test_no_match_is_not_proof_the_upload_never_happened(self) -> None:
        output, fake = self.run_helper({"history": history(entry("old", "2026-01-30T11:00:00Z", "Accepted"))})
        self.assertIn("not proof the upload never completed", output)
        self.assertNotIn("old", output)
        self.assertEqual(fake.called(), ["history"])

    def test_unreadable_created_date_is_counted(self) -> None:
        output, _ = self.run_helper({"history": history(entry("bad", "whenever", "Invalid"))})
        self.assertIn("1 of 1 entries had an unreadable createdDate", output)
        self.assertIn("None matched that search", output)

    def test_unrelated_newer_submissions_do_not_hide_this_run(self) -> None:
        # Four unrelated submissions are newer than both of ours, so a cap
        # applied before the name filter would report only those.
        rows = [entry(f"other-{n}", f"2026-01-30T14:0{n}:00Z", "Accepted", OTHER_NAME)
                for n in range(4)]
        rows += [entry("app-1", "2026-01-30T12:40:00Z", "Accepted", APP_NAME),
                 entry("pkg-1", "2026-01-30T12:41:00Z", "Accepted", PKG_NAME)]
        plan = {"history": history(*rows)}
        for key, name in (("app-1", APP_NAME), ("pkg-1", PKG_NAME)):
            plan[f"info {key}"] = ok({"id": key, "status": "Accepted", "name": name})
            plan[f"log {key}"] = ok({"issues": []})
        output, fake = self.run_helper(plan, names=[APP_NAME, PKG_NAME])

        self.assertEqual(fake.called(),
                         ["history", "info pkg-1", "log pkg-1", "info app-1", "log app-1"])
        self.assertNotIn("other-", output)
        self.assertIn(APP_NAME, output.splitlines()[0])
        self.assertIn(PKG_NAME, output.splitlines()[0])

    def test_cap_and_pre_start_apply_within_matching_names(self) -> None:
        rows = [entry(f"app-{n}", f"2026-01-30T12:{40 + n}:00Z", "Accepted", APP_NAME)
                for n in range(5)]
        rows.append(entry("old-app", "2026-01-30T11:00:00Z", "Accepted", APP_NAME))
        plan = {"history": history(*rows)}
        for n in range(5):
            key = f"app-{n}"
            plan[f"info {key}"] = ok({"id": key, "status": "Accepted"})
            plan[f"log {key}"] = ok({"issues": []})
        output, fake = self.run_helper(plan, names=[APP_NAME])

        self.assertIn("capped at 3", output)
        self.assertNotIn("id=old-app", output)
        self.assertEqual([k for k in fake.called() if k.startswith("info")],
                         ["info app-4", "info app-3", "info app-2"])

    def test_no_matching_name_reports_the_search_not_a_failed_upload(self) -> None:
        # A malformed non-string name must be skipped, not compared into the set.
        broken = entry("broken", "2026-01-30T12:45:00Z", "Accepted")
        broken["name"] = {"unexpected": True}
        output, fake = self.run_helper(
            {"history": history(entry("x", "2026-01-30T12:40:00Z", "Accepted", OTHER_NAME), broken)},
            names=[APP_NAME, PKG_NAME],
        )
        self.assertIn("None matched that search", output)
        self.assertIn("not proof the upload never completed", output)
        self.assertNotIn("id=x", output)
        self.assertNotIn("id=broken", output)
        self.assertEqual(fake.called(), ["history"])

    def test_unavailable_info_reports_status_unknown_and_skips_the_log(self) -> None:
        for label, answer in [("nonzero", fail(1, stderr=f"key {NOISE}")),
                              ("timeout", subprocess.TimeoutExpired(cmd=["xcrun"], timeout=notary.TIMEOUT)),
                              ("nonjson", fail(stdout="not json", returncode=0)),
                              ("list", ok(["not", "an", "object"])),
                              ("text", ok("text"))]:
            with self.subTest(answer=label):
                output, fake = self.run_helper({
                    "history": history(entry("s", "2026-01-30T12:40:00Z", "Accepted")),
                    "info s": answer,
                })
                self.assertIn("status unknown", output)
                self.assertNotIn("no issues reported", output)
                self.assertNotIn(NOISE, output)
                self.assertEqual(fake.called(), ["history", "info s"])

    def test_missing_or_malformed_status_is_unknown_not_pending(self) -> None:
        for label, payload in [("absent", {"id": "s"}),
                               ("null", {"id": "s", "status": None}),
                               ("object", {"id": "s", "status": {}})]:
            with self.subTest(status=label):
                output, fake = self.run_helper({
                    "history": history(entry("s", "2026-01-30T12:40:00Z", "Accepted")),
                    "info s": ok(payload),
                })
                self.assertIn("status unknown", output)
                self.assertEqual(fake.called(), ["history", "info s"])

    def test_unavailable_log_is_never_reported_as_no_issues(self) -> None:
        for label, answer in [("nonzero", fail(4)),
                              ("timeout", subprocess.TimeoutExpired(cmd=["xcrun"], timeout=notary.TIMEOUT)),
                              ("nonjson", fail(stdout="{oops", returncode=0)),
                              ("list", ok(["nope"])), ("null", ok(None))]:
            with self.subTest(answer=label):
                output, _ = self.run_helper({
                    "history": history(entry("s", "2026-01-30T12:40:00Z", "Accepted")),
                    "info s": ok({"id": "s", "status": "Accepted"}),
                    "log s": answer,
                })
                self.assertIn("issues unknown", output)
                self.assertNotIn("no issues reported", output)

    def test_malformed_issues_field_is_unknown_not_clean(self) -> None:
        output, _ = self.run_helper({
            "history": history(entry("s", "2026-01-30T12:40:00Z", "Invalid")),
            "info s": ok({"id": "s", "status": "Invalid"}),
            "log s": ok({"issues": {"unexpected": True}}),
        })
        self.assertIn("issues unknown (unexpected issues field)", output)
        self.assertNotIn("no issues reported", output)

    def test_accepted_log_without_issues_is_still_clean(self) -> None:
        # The real Accepted response carries no issues key at all.
        for label, answer in [("absent", ok({})), ("null", ok({"issues": None})),
                              ("empty", ok({"issues": []}))]:
            with self.subTest(issues=label):
                output, _ = self.run_helper({
                    "history": history(entry("s", "2026-01-30T12:40:00Z", "Accepted")),
                    "info s": ok({"id": "s", "status": "Accepted"}),
                    "log s": answer,
                })
                self.assertIn("no issues reported", output)

    def test_candidates_are_capped_at_three_newest_and_labelled_unproven(self) -> None:
        plan = {"history": history(*[entry(f"sub-{i}", f"2026-01-30T13:0{i}:00Z", "Invalid") for i in range(5)])}
        for i in range(5):
            plan[f"info sub-{i}"], plan[f"log sub-{i}"] = ok({"id": f"sub-{i}", "status": "Invalid"}), ok({})
        output, fake = self.run_helper(plan)
        self.assertIn("capped at 3", output)
        self.assertIn("do not prove", output)
        self.assertEqual([k for k in fake.called() if k.startswith("info")],
                         ["info sub-4", "info sub-3", "info sub-2"])

    def test_info_status_decides_whether_a_log_is_fetched(self) -> None:
        # History claims Invalid in every case; fresh info status governs.
        for status, logged in [("In Progress", False), ("Accepted", True),
                               ("Invalid", True), ("Rejected", True), (None, False),
                               ({}, False), ([], False)]:
            with self.subTest(status=status):
                output, fake = self.run_helper({
                    "history": history(entry("sub-1", "2026-01-30T12:40:00Z", "Invalid")),
                    "info sub-1": ok({"id": "sub-1", "status": status}),
                    "log sub-1": ok({"issues": []}),
                })
                self.assertEqual(fake.called(),
                                 ["history", "info sub-1"] + (["log sub-1"] if logged else []))
                if logged:
                    self.assertNotIn("log not fetched", output)
                else:
                    self.assertIn("status unknown" if not isinstance(status, str)
                                  else "is not terminal, log not fetched", output)

    def test_non_object_history_entries_are_skipped(self) -> None:
        output, fake = self.run_helper({
            "history": ok({"history": ["junk", None, entry("s", "2026-01-30T12:40:00Z", "Invalid")]}),
            "info s": ok({"id": "s", "status": "Invalid"}),
            "log s": ok({}),
        })
        self.assertIn("skipped 2 history entries that were not objects", output)
        self.assertEqual(fake.called(), ["history", "info s", "log s"])

    def test_issue_list_is_capped_and_reports_truncation(self) -> None:
        issues = [{"code": f"ITMS-{n:05d}"} for n in range(15)]
        for answer, expected in [(ok({"issues": issues}), "5 further issues not shown"),
                                 (ok({"issues": "none"}), "issues unknown (unexpected issues field)"),
                                 (ok({}), "no issues reported"),
                                 (ok({"issues": []}), "no issues reported")]:
            with self.subTest(expected=expected):
                output, _ = self.run_helper({
                    "history": history(entry("sub-1", "2026-01-30T12:40:00Z", "Invalid")),
                    "info sub-1": ok({"id": "sub-1", "status": "Invalid"}),
                    "log sub-1": answer,
                })
                self.assertIn(expected, output)
                if expected.startswith("5"):
                    self.assertEqual(output.count("    issue: "), notary.MAX_ISSUES)

    def test_log_prints_allowlisted_fields_only(self) -> None:
        output, _ = self.run_helper({
            "history": history(entry("sub-9", "2026-01-30T12:40:00Z", "Invalid")),
            "info sub-9": ok({"id": "sub-9", "status": "Invalid"}),
            "log sub-9": ok({
                "statusCode": 0,
                "statusSummary": "Submission Unsuccessful",
                "developerLogPath": NOISE,
                "logArchiveUrl": NOISE,
                "issues": [
                    {"severity": "error", "code": "ITMS-90949", "path": "MQTTProbe.app",
                     "message": "must be signed", "requestId": "req-secret"},
                    {"severity": "error", "code": "ITMS-90062", "message": "not signed",
                     "path": "MQTTProbe.app/Contents/MacOS/MqttProbe.Desktop"},
                ],
            }),
        })
        self.assertIn("log statusCode: 0", output)
        self.assertIn("log statusSummary: Submission Unsuccessful", output)
        self.assertIn("code=ITMS-90949", output)
        self.assertIn("path=MQTTProbe.app/Contents/MacOS/MqttProbe.Desktop", output)
        self.assertNotIn(NOISE, output)
        self.assertNotIn("req-secret", output)

    def test_failures_stop_the_escalation_and_hide_stderr(self) -> None:
        cases = [
            ({"history": history(entry("s", "2026-01-30T12:40:00Z", "Invalid")),
              "info s": fail(1, stderr=f"API key {NOISE} rejected")},
             PACK_START, "exited 1", ["history", "info s"]),
            ({"history": subprocess.TimeoutExpired(cmd=["xcrun"], timeout=notary.TIMEOUT)},
             PACK_START, "timed out after 25s", ["history"]),
            ({"history": fail(3, stderr=f"key {NOISE}")},
             PACK_START, "exited 3", ["history"]),
            ({"history": fail(stdout="not json", returncode=0)},
             PACK_START, "was not JSON", ["history"]),
            ({"history": ok({"submissionsV2": [], "message": NOISE})},
             PACK_START, "no submission list", ["history"]),
            ({"history": ok([entry("s", "2026-01-30T12:40:00Z", "Invalid")])},
             PACK_START, "no submission list", ["history"]),
            ({}, "not-a-timestamp", "unreadable pack start", []),
        ]
        for plan, start, expected, expected_calls in cases:
            with self.subTest(expected=expected):
                output, fake = self.run_helper(plan, start)
                self.assertIn(expected, output)
                self.assertNotIn(NOISE, output)
                self.assertEqual(fake.called(), expected_calls)

    def test_calls_are_bounded_and_carry_only_profile_credentials(self) -> None:
        fake = Fake({
            "history": history(entry("sub-1", "2026-01-30T12:40:00Z", "Invalid")),
            "info sub-1": ok({"id": "sub-1", "status": "Invalid"}),
            "log sub-1": ok({"issues": [{"code": "ITMS-90062"}]}),
        })
        with mock.patch.object(subprocess, "run", fake):
            with contextlib.redirect_stdout(io.StringIO()):
                notary.main(["--pack-start", PACK_START, "--keychain", KEYCHAIN])
        self.assertEqual(set(fake.timeouts), {notary.TIMEOUT})
        for command in fake.commands:
            self.assertEqual(command[:3], ["xcrun", "notarytool", command[2]])
            self.assertIn("--output-format", command)
            self.assertIn(notary.PROFILE, command)
            self.assertIn(KEYCHAIN, command)
            for forbidden in ("--key", "--key-id", "--issuer", "unlock-keychain"):
                self.assertNotIn(forbidden, command)


if __name__ == "__main__":
    unittest.main()