#!/usr/bin/env python3

from __future__ import annotations

import argparse
import base64
import json
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--key-id", required=True)
    parser.add_argument("--public-key-base64", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    raw = base64.b64decode(args.public_key_base64, validate=True)
    if len(raw) != 32 or not any(raw):
        raise SystemExit("release public key must be a non-zero 32-byte Ed25519 raw key")
    payload = {
        "schemaVersion": 1,
        "keys": [{
            "keyId": args.key_id,
            "algorithm": "Ed25519",
            "publicKeyBase64": args.public_key_base64,
        }],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
