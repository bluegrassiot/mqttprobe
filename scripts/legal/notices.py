"""Command line entry point for the offline legal notice pipeline."""

from __future__ import annotations

import argparse
import sys

import catalogue
import profiles
import refresh


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("generate", help="write catalogue.json, texts and NOTICES")
    subparsers.add_parser("check", help="fail if generated output is stale")
    subparser = subparsers.add_parser("verify", help="check one profile against the committed catalogue")
    subparser.add_argument("--scenario", required=True)
    for name in ("profiles-export", "profiles-check"):
        subparser = subparsers.add_parser(name)
        subparser.add_argument("--scenario", action="append", dest="scenarios")
    subparser = subparsers.add_parser("refresh")
    subparser.add_argument("--scenario", action="append", dest="scenarios")
    subparser.add_argument("--all", action="store_true", dest="all_scenarios")
    args = parser.parse_args(argv)

    if args.command == "generate":
        return catalogue.generate()
    if args.command == "check":
        return catalogue.check()
    if args.command == "verify":
        return catalogue.verify(args.scenario)
    if args.command.startswith("profiles-"):
        results = profiles.export(args.scenarios, check=args.command == "profiles-check")
        for name, result in results.items():
            print(f"{name}: {len(result['packages']) if isinstance(result, dict) else result}")
        return 0
    return refresh.refresh(args.scenarios, args.all_scenarios)


if __name__ == "__main__":
    sys.exit(main())
