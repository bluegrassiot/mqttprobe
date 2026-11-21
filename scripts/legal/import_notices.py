"""Split the legacy NOTICES file into reviewable supplement source texts.

This is a migration helper. The generated supplement manifest is the reviewed
input; run this again only when re-importing an unchanged legacy NOTICES file.
"""

from __future__ import annotations

import argparse
import re

from common import (
    ROOT,
    SUPPLEMENTS,
    LegalError,
    normalize_text,
    read_json,
    sha256_text,
    slug,
    write_json,
    write_text,
)

SEPARATOR = "=" * 78
HEADING_UNDERLINE = re.compile(r"^-{3,}$")


def is_heading(line, following):
    return bool(line.strip()) and not line.startswith((" ", "\t")) and bool(HEADING_UNDERLINE.match(following or ""))


def heading_index(lines, start=0):
    for index in range(start, len(lines) - 1):
        if is_heading(lines[index], lines[index + 1]):
            return index
    return None


def trim(lines):
    while lines and not lines[0].strip():
        lines = lines[1:]
    while lines and not lines[-1].strip():
        lines = lines[:-1]
    return lines


def blocks(lines, start):
    separators = [index for index in range(start, len(lines)) if lines[index] == SEPARATOR]
    result = []
    for position, begin in enumerate(separators):
        end = separators[position + 1] if position + 1 < len(separators) else len(lines)
        content = trim(lines[begin + 1:end])
        if content:
            result.append(content)
    return result


def parse_legacy(text):
    lines = text.split("\n")
    try:
        marker = lines.index("LICENCE TEXTS")
    except ValueError as error:
        raise LegalError("NOTICES has no LICENCE TEXTS section") from error

    documents = []
    start = marker
    while start > 0 and lines[start] != SEPARATOR:
        start -= 1
    for content in blocks(lines, start):
        if content[0].strip() == "PROVENANCE NOTES":
            documents.append(("provenance", content))
            continue
        first_heading = heading_index(content)
        if content[0].strip() == "LICENCE TEXTS":
            if first_heading is None:
                continue
            content = content[first_heading:]
        documents.append(("document", content))

    merged = []
    pending = None
    for kind, content in documents:
        if kind == "document" and len([line for line in content if line.strip()]) == 1:
            pending = content[0].strip()
            continue
        if pending and kind == "document":
            content = [pending, "", *content]
            pending = None
        merged.append((kind, content))
    if pending:
        merged.append(("document", [pending]))
    return merged


def document_title(content):
    index = heading_index(content)
    if index is not None:
        return content[index].strip()
    for line in content:
        if line.strip():
            return line.strip()
    return "Untitled document"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", default=str(ROOT / "NOTICES"))
    args = parser.parse_args(argv)

    text = open(args.source, encoding="utf-8").read()
    parsed = parse_legacy(text)
    existing = {}
    manifest_path = SUPPLEMENTS / "documents.json"
    if manifest_path.is_file():
        existing = {entry["key"]: entry for entry in read_json(manifest_path)["documents"]}

    texts_dir = SUPPLEMENTS / "texts"
    entries = []
    used = set()
    provenance = ""
    for kind, content in parsed:
        body = normalize_text("\n".join(content))
        if kind == "provenance":
            provenance = body
            write_text(SUPPLEMENTS / "provenance.txt", body)
            continue
        title = document_title(content)
        key = slug(title)
        suffix = 2
        while key in used:
            key = f"{slug(title)}-{suffix}"
            suffix += 1
        used.add(key)
        path = texts_dir / f"{key}.txt"
        write_text(path, body)
        previous = existing.get(key, {})
        entries.append(
            {
                "key": key,
                "title": title,
                "file": f"texts/{key}.txt",
                "sha256": sha256_text(body),
                "kind": "license" if "licen" in title.lower() or "license" in title.lower() else "notice",
                "sourceUrls": sorted(previous.get("sourceUrls", [])),
            }
        )

    write_json(
        manifest_path,
        {
            "schemaVersion": 1,
            "documents": entries,
        },
    )
    print(f"imported {len(entries)} documents and provenance from {args.source}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
