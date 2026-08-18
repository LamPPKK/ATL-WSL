#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest


PROJECT_ROOT = pathlib.Path(__file__).resolve().parents[1]


class BuildToolTests(unittest.TestCase):
    def test_release_manifest_hashes_all_public_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = pathlib.Path(temporary)
            version = (PROJECT_ROOT / "VERSION").read_text(encoding="utf-8").strip()
            names = (
                f"ATL-WSL-{version}-x64.wsl",
                f"ATL-WSL-{version}-arm64.wsl",
                f"ATL-WSL-Manager-{version}-win-x64.zip",
                f"ATL-WSL-Manager-{version}-win-arm64.zip",
                f"ATL-WSL-Runtime-{version}-x64.tar.gz",
                f"ATL-WSL-Runtime-{version}-arm64.tar.gz",
            )
            for index, name in enumerate(names):
                target = directory / "signpath-output" / name if "Manager" in name else directory / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(f"artifact-{index}".encode())

            output = directory / "release-manifest.json"
            subprocess.run(
                [
                    sys.executable,
                    str(PROJECT_ROOT / "tools" / "create-release-manifest.py"),
                    "--directory",
                    str(directory),
                    "--base-url",
                    "https://example.invalid/releases/v0.1.0/",
                    "--output",
                    str(output),
                ],
                check=True,
            )
            manifest = json.loads(output.read_text(encoding="utf-8"))

            self.assertEqual(manifest["schemaVersion"], 2)
            self.assertEqual(manifest["minimumWslVersion"], "2.4.4")
            self.assertEqual(manifest["product"], "atl-wsl")
            for descriptor in manifest["artifacts"]:
                artifact = directory / pathlib.Path(descriptor["url"]).name
                self.assertEqual(descriptor["sha256"], hashlib.sha256(artifact.read_bytes()).hexdigest())
                self.assertEqual(descriptor["sizeBytes"], artifact.stat().st_size)

    def test_sbom_reads_alpine_package_database(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = pathlib.Path(temporary)
            rootfs = directory / "rootfs"
            database = rootfs / "lib" / "apk" / "db" / "installed"
            database.parent.mkdir(parents=True)
            database.write_text(
                "P:android-translation-layer\n"
                "V:0_git20260106-r0\n"
                "L:GPL-3.0-only\n"
                "U:https://gitlab.com/android_translation_layer/android_translation_layer\n\n"
                "P:mesa\n"
                "V:26.1.2-r0\n"
                "L:MIT\n\n",
                encoding="utf-8",
            )
            output = directory / "sbom.spdx.json"

            subprocess.run(
                [
                    sys.executable,
                    str(PROJECT_ROOT / "tools" / "create-sbom.py"),
                    "--rootfs",
                    str(rootfs),
                    "--name",
                    "ATL-WSL-x64",
                    "--version",
                    "0.1.0",
                    "--output",
                    str(output),
                ],
                check=True,
            )
            sbom = json.loads(output.read_text(encoding="utf-8"))

            packages = {package["name"]: package for package in sbom["packages"]}
            self.assertEqual(sbom["spdxVersion"], "SPDX-2.3")
            self.assertEqual(packages["android-translation-layer"]["licenseDeclared"], "GPL-3.0-only")
            self.assertEqual(packages["mesa"]["versionInfo"], "26.1.2-r0")
            self.assertEqual(len(sbom["relationships"]), 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
