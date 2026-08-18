#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
import zipfile
from unittest import mock


PROJECT_ROOT = pathlib.Path(__file__).resolve().parents[1]
MODULE_PATH = PROJECT_ROOT / "runtime" / "atl_wsl.py"
SPEC = importlib.util.spec_from_file_location("atl_wsl", MODULE_PATH)
assert SPEC and SPEC.loader
atl_wsl = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(atl_wsl)


def make_apk(path: pathlib.Path, abis: tuple[str, ...] = ()) -> pathlib.Path:
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("AndroidManifest.xml", b"manifest")
        archive.writestr("classes.dex", b"dex")
        for abi in abis:
            archive.writestr(f"lib/{abi}/libfixture.so", b"elf")
    return path


class RuntimeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.temp = pathlib.Path(self.temporary.name)
        self.fake_atl = self.temp / "fake-atl"
        self.fake_atl.write_text(
            "#!/bin/sh\n"
            "printf 'apk=%s\\nrenderer=%s\\ndata=%s\\n' \"$1\" \"$GALLIUM_DRIVER\" \"$ANDROID_APP_DATA_DIR\"\n",
            encoding="utf-8",
        )
        self.fake_atl.chmod(0o755)
        self.paths = atl_wsl.RuntimePaths(
            root=self.temp / "state",
            apps=self.temp / "state" / "apps",
            logs=self.temp / "logs",
            lock=self.temp / "state" / ".lock",
            release=self.temp / "release.json",
        )
        self.paths.release.write_text(
            json.dumps(
                {
                    "version": "test",
                    "architecture": "x86_64",
                    "renderer": "llvmpipe",
                }
            ),
            encoding="utf-8",
        )
        self.environment = mock.patch.dict(
            os.environ,
            {
                "ATL_WSL_TEST_ARCH": "x86_64",
                "ATL_WSL_TEST_WSLG": "1",
                "ATL_WSL_ATL_BINARY": str(self.fake_atl),
                "ATL_WSL_TEST_LAUNCH_FOREGROUND": "1",
                "ATL_WSL_TEST_DISABLE_SANDBOX": "1",
                "DBUS_SESSION_BUS_ADDRESS": "unix:path=/tmp/atl-wsl-test-bus",
            },
            clear=False,
        )
        self.environment.start()

    def tearDown(self) -> None:
        self.environment.stop()
        self.temporary.cleanup()

    def test_boolean_options_have_positive_and_negative_forms(self) -> None:
        parser = atl_wsl.build_parser()
        enabled = parser.parse_args(["library", "configure", "a" * 32, "--fullscreen"])
        disabled = parser.parse_args(["library", "configure", "a" * 32, "--no-fullscreen"])
        self.assertTrue(enabled.fullscreen)
        self.assertFalse(disabled.fullscreen)

    def test_inspect_accepts_java_only_and_matching_native_apks(self) -> None:
        java_apk = make_apk(self.temp / "java.apk")
        native_apk = make_apk(self.temp / "native.apk", ("x86_64", "arm64-v8a"))

        self.assertTrue(atl_wsl.inspect_apk(str(java_apk))["compatible"])
        inspection = atl_wsl.inspect_apk(str(native_apk))
        self.assertTrue(inspection["compatible"])
        self.assertEqual(inspection["requiredAbi"], "x86_64")

    def test_inspect_rejects_incompatible_native_apk(self) -> None:
        apk = make_apk(self.temp / "arm-only.apk", ("arm64-v8a",))

        with self.assertRaisesRegex(atl_wsl.CliError, "requires x86_64") as captured:
            atl_wsl.add_app(self.paths, str(apk))

        self.assertEqual(captured.exception.code, "incompatible_abi")

    def test_library_lifecycle_retains_and_then_deletes_data(self) -> None:
        apk = make_apk(self.temp / "Example App.apk", ("x86_64",))
        added = atl_wsl.add_app(self.paths, str(apk))
        app_id = added["app"]["id"]
        data_file = self.paths.apps / app_id / "data" / "save.dat"
        data_file.write_text("retained", encoding="utf-8")

        args = atl_wsl.build_parser().parse_args(
            [
                "library",
                "configure",
                app_id,
                "--display-name",
                "Configured App",
                "--width",
                "1280",
                "--height",
                "720",
                "--fullscreen",
                "--no-validate-certificates",
            ]
        )
        configured = atl_wsl.configure_app(self.paths, args)
        self.assertEqual(configured["displayName"], "Configured App")
        self.assertEqual(configured["launchOptions"]["width"], 1280)
        self.assertTrue(configured["launchOptions"]["fullscreen"])
        self.assertFalse(configured["launchOptions"]["validateCertificates"])

        removed = atl_wsl.remove_app(self.paths, app_id, delete_data=False)
        self.assertTrue(removed["retained"])
        self.assertTrue(data_file.is_file())
        self.assertEqual(atl_wsl.all_apps(self.paths), [])

        restored = atl_wsl.add_app(self.paths, str(apk))
        self.assertTrue(restored["restored"])
        self.assertEqual(restored["app"]["id"], app_id)
        self.assertEqual(data_file.read_text(encoding="utf-8"), "retained")

        deleted = atl_wsl.remove_app(self.paths, app_id, delete_data=True)
        self.assertTrue(deleted["dataDeleted"])
        self.assertFalse((self.paths.apps / app_id).exists())

    def test_launch_uses_software_renderer_and_writes_log(self) -> None:
        apk = make_apk(self.temp / "launch.apk")
        app_id = atl_wsl.add_app(self.paths, str(apk))["app"]["id"]

        result = atl_wsl.launch_app(self.paths, app_id)

        log = pathlib.Path(result["logPath"]).read_text(encoding="utf-8")
        self.assertIsNone(result["pid"])
        self.assertEqual(result["renderer"], "llvmpipe")
        self.assertIn("renderer=llvmpipe", log)
        self.assertIn(f"data={self.paths.apps / app_id / 'data'}", log)

    def test_sandbox_hides_windows_mounts_and_only_binds_app_data_writable(self) -> None:
        app_dir = self.temp / "sandbox-app"
        (app_dir / "data").mkdir(parents=True)
        (app_dir / "app.apk").write_bytes(b"apk")
        environment = {
            "GDK_BACKEND": "wayland,x11",
            "LIBGL_ALWAYS_SOFTWARE": "1",
            "GALLIUM_DRIVER": "llvmpipe",
            "MESA_LOADER_DRIVER_OVERRIDE": "llvmpipe",
            "ATL_VALIDATE_CERTS": "1",
            "SECRET_FROM_HOST": "must-not-pass",
        }

        with mock.patch.object(atl_wsl.shutil, "which", side_effect=lambda name: f"/usr/bin/{name}"):
            command = atl_wsl.sandbox_command(app_dir, "/usr/bin/android-translation-layer", [], environment)

        rendered = "\0".join(command)
        self.assertIn("--clearenv", command)
        self.assertIn(f"--ro-bind\0{app_dir / 'app.apk'}\0/app/app.apk", rendered)
        self.assertIn(f"--bind\0{app_dir / 'data'}\0/app/data", rendered)
        self.assertNotIn("/mnt/c", rendered)
        self.assertNotIn("SECRET_FROM_HOST", rendered)
        self.assertIn("ATL_VALIDATE_CERTS", command)

    def test_diagnostic_export_contains_health_metadata_and_logs(self) -> None:
        apk = make_apk(self.temp / "diagnostic.apk")
        app_id = atl_wsl.add_app(self.paths, str(apk))["app"]["id"]
        (self.paths.logs / "fixture.log").write_text(
            f"fixture {self.paths.root} C:\\Users\\private-user\\secret.apk", encoding="utf-8"
        )

        result = atl_wsl.export_logs(self.paths, str(self.temp / "diagnostics"))

        output = pathlib.Path(result["path"])
        self.assertEqual(output.suffix, ".zip")
        with zipfile.ZipFile(output) as archive:
            self.assertIn("doctor.json", archive.namelist())
            self.assertIn(f"apps/{app_id}.json", archive.namelist())
            self.assertIn("logs/fixture.log", archive.namelist())
            doctor = json.loads(archive.read("doctor.json"))
            combined = b"\n".join(archive.read(name) for name in archive.namelist()).decode("utf-8")
        self.assertNotIn(str(self.paths.root), combined)
        self.assertNotIn("private-user", combined)
        self.assertTrue(doctor["healthy"])
        self.assertTrue(doctor["checks"]["sandboxBinary"])
        self.assertEqual(doctor["renderer"], "llvmpipe")


if __name__ == "__main__":
    unittest.main(verbosity=2)
