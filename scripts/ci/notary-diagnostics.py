#!/usr/bin/env python3
"""Reports what Apple's notary service recorded around a Velopack pack.

Prints allowlisted summary and issue fields only. The raw notary log carries
signed download URLs and a failed call can echo credentials, so neither is
printed, and stderr is never printed at all. The keychain is queried as the pack
step left it.

Run the tests with: python -m unittest discover -s scripts/ci/tests
"""

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone

PROFILE = "velopack-profile"
TIMEOUT = 25
MAX_CANDIDATES = 3
MAX_ISSUES = 10
TERMINAL = {"Accepted", "Invalid", "Rejected"}
SUMMARY_FIELDS = ("id", "name", "status", "statusCode", "statusSummary", "createdDate")
ISSUE_FIELDS = ("severity", "code", "message", "path")


def parse_stamp(value):
    """Parses a notarytool date as an aware datetime, or returns None."""
    if not isinstance(value, str):
        return None
    text = value.strip()
    if text.endswith(("Z", "z")):
        text = text[:-1] + "+00:00"
    try:
        stamp = datetime.fromisoformat(text)
    except ValueError:
        return None
    return stamp if stamp.tzinfo else stamp.replace(tzinfo=timezone.utc)


def run(args, label):
    """Runs one notarytool call, returning its JSON or None. Never prints stderr."""
    try:
        done = subprocess.run(args, capture_output=True, text=True, timeout=TIMEOUT)
    except subprocess.TimeoutExpired:
        print(f"  {label}: timed out after {TIMEOUT}s")
        return None
    except OSError as error:
        print(f"  {label}: could not run notarytool ({type(error).__name__})")
        return None
    if done.returncode != 0:
        print(f"  {label}: exited {done.returncode}")
        return None
    try:
        return json.loads(done.stdout)
    except ValueError:
        print(f"  {label}: output was not JSON")
        return None


def submissions(payload):
    """The list of submissions in a history response, or None if unrecognised."""
    if isinstance(payload, dict) and isinstance(payload.get("history"), list):
        return payload["history"]
    print(f"  history: no submission list in the response (top level: "
          f"{sorted(payload) if isinstance(payload, dict) else type(payload).__name__})")
    return None


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Report Apple's view of notarization submissions after a pack."
    )
    parser.add_argument("--pack-start", required=True, help="UTC time vpk pack started")
    parser.add_argument("--keychain", required=True, help="CI signing keychain path")
    parser.add_argument("--expected-name", action="append", default=None,
                        help="only report submissions with this artifact name; repeatable")
    args = parser.parse_args(argv)

    start = parse_stamp(args.pack_start)
    if start is None:
        print(f"Diagnostics skipped: unreadable pack start {args.pack_start!r}.")
        return 0

    def call(subcommand, *extra):
        return run(
            ["xcrun", "notarytool", subcommand, *extra,
             "--keychain-profile", PROFILE, "--keychain", args.keychain,
             "--output-format", "json"],
            " ".join((subcommand, *extra)),
        )

    payload = call("history")
    entries = submissions(payload) if payload is not None else None
    if entries is None:
        print("Submission history unavailable, so no status can be reported.")
        return 0

    wanted = set(args.expected_name or ())
    criteria = f" named {' or '.join(sorted(wanted))}" if wanted else ""
    print(f"Candidate submissions created at or after pack start {start.isoformat()}{criteria}:")
    rows = [row for row in entries if isinstance(row, dict)]
    if len(rows) != len(entries):
        print(f"  skipped {len(entries) - len(rows)} history entries that were not objects.")
    dated = [(parse_stamp(row.get("createdDate")), row) for row in rows]
    unreadable = sum(1 for stamp, _ in dated if stamp is None)
    if unreadable:
        print(f"  {unreadable} of {len(rows)} entries had an unreadable createdDate, "
              "so they could not be matched against the pack start.")
    # Name is matched before the cap so a busier team cannot push this run's
    # own submissions out of the report. A malformed non-string name is skipped
    # rather than compared, since it cannot be a member of the expected set.
    fresh = [
        (stamp, row)
        for stamp, row in dated
        if stamp is not None
        and stamp >= start
        and (not wanted
             or (isinstance(row.get("name"), str) and row.get("name") in wanted))
    ]
    fresh.sort(reverse=True, key=lambda pair: pair[0])

    if not fresh:
        print("  None matched that search. This is not proof the upload never completed:\n"
              "  history can lag behind an accepted upload, or the upload never left the\n"
              "  runner. Check the pack step output for the notarytool error.")
        return 0

    candidates = fresh[:MAX_CANDIDATES]
    print(f"  {len(candidates)} shown, capped at {MAX_CANDIDATES}. History lists every submission\n"
          "  on the team, so these are candidates only: a matching time and name do not prove\n"
          "  this run created them. The app bundle is submitted under a generic zip name, so a\n"
          "  concurrent build of the same app would match too.")
    for index, (_, entry) in enumerate(candidates, 1):
        submission_id = entry.get("id")
        print(f"  [{index}] id={submission_id} created={entry.get('createdDate')} "
              f"status={entry.get('status')} name={entry.get('name')}")
        if not isinstance(submission_id, str) or not submission_id:
            print("    no submission id, cannot query further")
            continue

        info = call("info", submission_id)
        # A failed, timed out or non-object response must stay unknown rather
        # than read as a clean submission.
        if not isinstance(info, dict):
            print(f"    info {submission_id} unavailable; status unknown")
            continue
        for field in SUMMARY_FIELDS:
            if info.get(field):
                print(f"    {field}: {info[field]}")

        # Fresh status from info, so no log is fetched while Apple is still working.
        status = info.get("status")
        if not isinstance(status, str):
            print("    status unknown (missing or not a string), log not fetched")
            continue
        if status not in TERMINAL:
            print(f"    status {status!r} is not terminal, log not fetched")
            continue

        # log repeats statusCode and statusSummary, which info may not carry.
        log = call("log", submission_id)
        if not isinstance(log, dict):
            print(f"    log {submission_id} unavailable; issues unknown")
            continue
        for field in ("statusCode", "statusSummary"):
            if log.get(field) is not None:
                print(f"    log {field}: {log[field]}")
        issues = log.get("issues")
        # An Accepted submission carries no issues at all, which is genuinely
        # clean. Only a present value of the wrong shape means we cannot tell.
        if issues is None or issues == []:
            print(f"    log {submission_id}: no issues reported")
        elif not isinstance(issues, list):
            print(f"    log {submission_id}: issues unknown (unexpected issues field)")
        else:
            for issue in issues[:MAX_ISSUES]:
                if isinstance(issue, dict):
                    print("    issue: " + "; ".join(
                        f"{field}={issue[field]}" for field in ISSUE_FIELDS if issue.get(field)))
            if len(issues) > MAX_ISSUES:
                print(f"    ({len(issues) - MAX_ISSUES} further issues not shown)")
    return 0


if __name__ == "__main__":
    sys.exit(main())