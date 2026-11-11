#!/usr/bin/env python3
"""Derives the Apple build number for an automated release run.

The number combines the workflow run and attempt so a failed job rerun gets a
distinct build without the release coordinator precomputing one. Apple caps the
first CFBundleVersion integer at four digits, so the result must stay at or
below 9999 and the attempt digit is limited to 1..9.
"""

from __future__ import annotations

import argparse
import sys

ATTEMPT_MULTIPLIER = 10
MIN_RUN_NUMBER = 1
MAX_RUN_ATTEMPT = 9
MAX_BUILD_NUMBER = 9999


class BuildNumberError(ValueError):
    pass


def derive(run_number: int, run_attempt: int) -> int:
    if run_number < MIN_RUN_NUMBER:
        raise BuildNumberError(
            f"Run number {run_number} is not valid; GitHub numbers runs from "
            f"{MIN_RUN_NUMBER} upwards."
        )
    if run_attempt < 1:
        raise BuildNumberError(
            f"Run attempt {run_attempt} is not valid; the first attempt of a run is 1."
        )
    if run_attempt > MAX_RUN_ATTEMPT:
        raise BuildNumberError(
            f"Run attempt {run_attempt} is out of range: each run owns attempts "
            f"1-{MAX_RUN_ATTEMPT}, so attempt {ATTEMPT_MULTIPLIER} is the boundary "
            f"between runs and attempt {ATTEMPT_MULTIPLIER + 1} collides with the next "
            "run's first attempt. Automatic releases stop here until the build-number "
            "policy changes; uploading a build by hand does not unblock the release."
        )

    value = run_number * ATTEMPT_MULTIPLIER + run_attempt
    if value > MAX_BUILD_NUMBER:
        highest_run = (MAX_BUILD_NUMBER - MAX_RUN_ATTEMPT) // ATTEMPT_MULTIPLIER
        raise BuildNumberError(
            f"Derived build number {value} exceeds Apple's four-digit maximum "
            f"({MAX_BUILD_NUMBER}). Numbers are run x {ATTEMPT_MULTIPLIER} + attempt, so "
            f"automatic releases stop at run {highest_run} attempt {MAX_RUN_ATTEMPT}. "
            "Releasing again needs a deliberate change to the build-number policy; "
            "uploading a build by hand does not unblock the release."
        )
    return value


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--run-number", required=True, type=int)
    parser.add_argument("--run-attempt", required=True, type=int)
    args = parser.parse_args(argv)

    try:
        value = derive(args.run_number, args.run_attempt)
    except BuildNumberError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    print(value)
    return 0


if __name__ == "__main__":
    sys.exit(main())
