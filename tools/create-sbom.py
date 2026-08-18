#!/usr/bin/env python3
"""Create a compact SPDX 2.3 JSON inventory from an Alpine rootfs."""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import uuid


def parse_installed(path: pathlib.Path) -> list[dict[str, str]]:
    packages: list[dict[str, str]] = []
    current: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines() + [""]:
        if not line:
            if current.get("P") and current.get("V"):
                packages.append(current)
            current = {}
            continue
        if len(line) > 2 and line[1] == ":":
            current[line[0]] = line[2:]
    return packages


def package_spdx_id(name: str) -> str:
    safe = "".join(char if char.isalnum() or char in ".-" else "-" for char in name)
    return f"SPDXRef-Package-{safe}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rootfs", type=pathlib.Path, required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()

    installed = args.rootfs / "lib/apk/db/installed"
    packages = parse_installed(installed)
    namespace_seed = hashlib.sha256(
        args.name.encode("utf-8") + b"\0" + args.version.encode("utf-8") + b"\0" + installed.read_bytes()
    ).hexdigest()
    document_id = f"urn:uuid:{uuid.uuid5(uuid.NAMESPACE_URL, namespace_seed)}"
    document_spdx_id = "SPDXRef-DOCUMENT"
    root_spdx_id = "SPDXRef-Package-ATL-WSL"

    output = {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": document_spdx_id,
        "name": args.name,
        "documentNamespace": document_id,
        "creationInfo": {
            "creators": ["Tool: ATL-WSL create-sbom.py"],
            "created": "1970-01-01T00:00:00Z",
        },
        "packages": [
            {
                "SPDXID": root_spdx_id,
                "name": args.name,
                "versionInfo": args.version,
                "downloadLocation": "NOASSERTION",
                "filesAnalyzed": False,
                "licenseConcluded": "GPL-3.0-only",
                "licenseDeclared": "GPL-3.0-only",
                "copyrightText": "NOASSERTION",
            }
        ],
        "relationships": [],
    }

    for package in packages:
        spdx_id = package_spdx_id(package["P"])
        output["packages"].append(
            {
                "SPDXID": spdx_id,
                "name": package["P"],
                "versionInfo": package["V"],
                "downloadLocation": package.get("U", "NOASSERTION"),
                "filesAnalyzed": False,
                "licenseConcluded": "NOASSERTION",
                "licenseDeclared": package.get("L", "NOASSERTION"),
                "copyrightText": "NOASSERTION",
                "supplier": "Organization: Alpine Linux",
            }
        )
        output["relationships"].append(
            {
                "spdxElementId": root_spdx_id,
                "relationshipType": "CONTAINS",
                "relatedSpdxElement": spdx_id,
            }
        )

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
