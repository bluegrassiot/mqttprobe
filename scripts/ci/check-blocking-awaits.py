#!/usr/bin/env python3
"""Flags synchronous blocking on async chains.

Blocking a thread on a Task deadlocks when the awaited chain captures a
SynchronizationContext, because the continuation is posted back to the thread
that is already blocked. MA0004 cannot catch this: it flags every missing
ConfigureAwait equally and says nothing about which chains are actually blocked
on, which is why it is only advisory here (see .editorconfig).

So the invariant is enforced from the other end. Every blocking call site is
listed below with the reason it is safe. Adding one fails this check until
somebody writes down which of the three escapes applies:

  1. the chain has no awaits at all, or
  2. it carries ConfigureAwait(false) throughout, or
  3. it provably runs where no SynchronizationContext is installed.

Counts rather than line numbers, so ordinary edits do not churn the list.
"""

import re
import sys
from pathlib import Path, PurePosixPath

def find_repo_root() -> Path:
    here = Path(__file__).resolve().parent
    for d in [here, *here.parents]:
        if (d / "MqttProbe.slnx").is_file():
            return d
    raise SystemExit("Could not find repo root (MqttProbe.slnx)")


ROOT = find_repo_root()

# path -> (call sites, why blocking there is safe)
REVIEWED = {
    "src/MqttProbe.Desktop/Program.cs": (
        3,
        "InitializeStorage runs after Build() and before Run(), so Photino has not "
        "installed a context yet. LoadAsync and CertificateStoreCleanup.RunAsync also "
        "carry ConfigureAwait(false) throughout, because they are Shared code MAUI can "
        "call. DesktopSecretKeyProtector.InitializeAsync does not and does not need to: "
        "it is Desktop-only and unreachable from a context-bearing host.",
    ),
    "src/MqttProbe.Core/Services/Emulation/EmulationService.cs": (
        1,
        "Dispose blocks on _publishLoop, which carries ConfigureAwait(false) throughout.",
    ),
    "src/MqttProbe.Core/Services/Mqtt/MessageStoreManager.cs": (
        1,
        "Dispose blocks on Stop(), which returns Task.CompletedTask and never awaits.",
    ),
}

PATTERNS = ("GetAwaiter().GetResult()", ".Wait()")

# Word-bounded so ConnectResult.ResultCode and friends do not match.
BLOCKING_RESULT = re.compile(r"\.Result\b")

RELEVANT_EXTENSIONS = {".cs", ".razor"}


def is_code(line: str) -> bool:
    stripped = line.strip()
    return not (stripped.startswith("//") or stripped.startswith("*"))


def blocking_sites(text: str) -> list[tuple[int, str]]:
    hits = []
    for n, line in enumerate(text.splitlines(), 1):
        if not is_code(line):
            continue
        if any(p in line for p in PATTERNS):
            hits.append((n, line.strip()))
            continue
        # .Result is the same hazard, but `await x.Result` is an ordinary await
        # of a property (MudBlazor DialogReference), not a blocking read.
        if BLOCKING_RESULT.search(line) and "await " not in line:
            hits.append((n, line.strip()))
    return hits


def main() -> int:
    print("\n=== Blocking awaits ===")

    found: dict[str, list[tuple[int, str]]] = {}
    for path in sorted((ROOT / "src").rglob("*")):
        if path.suffix.lower() not in RELEVANT_EXTENSIONS:
            continue
        rel = PurePosixPath(path.relative_to(ROOT).as_posix())
        if "obj" in rel.parts or "bin" in rel.parts:
            continue
        hits = blocking_sites(path.read_text(encoding="utf-8-sig"))
        if hits:
            found[str(rel)] = hits

    violations = []
    for rel, hits in found.items():
        expected = REVIEWED.get(rel, (0, ""))[0]
        if len(hits) != expected:
            violations.append((rel, hits, expected))

    stale = [rel for rel in REVIEWED if rel not in found]

    if violations:
        for rel, hits, expected in violations:
            print(f"  FAIL  {rel}: {len(hits)} blocking call site(s), {expected} reviewed")
            for n, snippet in hits:
                print(f"          {rel}:{n}  {snippet}")
        print(
            "\nBlocking on a Task deadlocks if the chain captures a SynchronizationContext.\n"
            "Confirm the chain is safe, then record it in REVIEWED in\n"
            "scripts/ci/check-blocking-awaits.py with the reason."
        )
        return 1

    if stale:
        for rel in stale:
            print(f"  FAIL  {rel}: listed in REVIEWED but has no blocking call sites")
        print("\nRemove the stale entry so the list keeps meaning something.")
        return 1

    total = sum(len(h) for h in found.values())
    print(f"  OK  {total} reviewed blocking call site(s) in {len(found)} file(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
