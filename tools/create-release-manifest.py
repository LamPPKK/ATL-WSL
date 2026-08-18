#!/usr/bin/env python3
"""Build a populated ATL-WSL manifest v2 from native release artifacts."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import shutil


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def stage_unique(directory: pathlib.Path, filename: str) -> pathlib.Path:
    destination = directory / filename
    matches = [path for path in directory.rglob(filename) if path.is_file()]
    if len(matches) != 1:
        raise SystemExit(f"expected exactly one {filename} below {directory}, found {len(matches)}")
    source = matches[0]
    if source.resolve() != destination.resolve():
        shutil.copy2(source, destination)
    return destination


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=pathlib.Path, required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--signing-key-id", default="release-2026")
    args = parser.parse_args()
    if not args.base_url.startswith("https://"):
        parser.error("--base-url must use HTTPS")

    project = pathlib.Path(__file__).resolve().parents[1]
    manifest = json.loads((project / "config/release-manifest.template.json").read_text(encoding="utf-8"))
    manifest.pop("$schema", None)
    manifest["channel"] = "stable"
    manifest["publishedAtUtc"] = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    manifest["signingKeyId"] = args.signing_key_id
    for descriptor in manifest["artifacts"]:
        path = stage_unique(args.directory, descriptor["fileName"])
        descriptor.update({
            "url": args.base_url.rstrip("/") + "/" + path.name,
            "sha256": sha256(path),
            "sizeBytes": path.stat().st_size,
        })
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
