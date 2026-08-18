#!/usr/bin/env python3
"""ATL-WSL runtime control plane.

The Windows manager treats this CLI's JSON output as a versioned public API.
Human-readable output is intentionally small; use --json for automation.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import fcntl
import hashlib
import json
import os
import pathlib
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
import zipfile
from dataclasses import dataclass
from typing import Any, Iterator


SCHEMA_VERSION = 1
METADATA_VERSION = 1
WINDOWS_PATH_RE = re.compile(r"^([A-Za-z]):[\\/](.*)$")
ACTIVITY_RE = re.compile(r"^[A-Za-z0-9_.$/]+$")
SUPPORTED_ABI = {"x86_64": "x86_64", "aarch64": "arm64-v8a"}
DEFAULT_OPTIONS: dict[str, Any] = {
    "width": None,
    "height": None,
    "activity": None,
    "fullscreen": False,
    "webView": False,
    "validateCertificates": True,
    "directEgl": False,
    "location": False,
}


class CliError(Exception):
    def __init__(self, code: str, message: str, *, details: Any = None, exit_code: int = 2) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.details = details
        self.exit_code = exit_code


@dataclass(frozen=True)
class RuntimePaths:
    root: pathlib.Path
    apps: pathlib.Path
    logs: pathlib.Path
    lock: pathlib.Path
    release: pathlib.Path

    @classmethod
    def load(cls) -> "RuntimePaths":
        root = pathlib.Path(
            os.environ.get("ATL_WSL_STATE_ROOT", "~/.local/share/atl-wsl")
        ).expanduser()
        return cls(
            root=root,
            apps=root / "apps",
            logs=pathlib.Path(
                os.environ.get("ATL_WSL_LOG_ROOT", "~/.local/state/atl-wsl/logs")
            ).expanduser(),
            lock=root / ".lock",
            release=pathlib.Path(
                os.environ.get("ATL_WSL_RELEASE_FILE", "/etc/atl-wsl-release")
            ),
        )

    def ensure(self) -> None:
        self.apps.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.logs.mkdir(parents=True, exist_ok=True, mode=0o700)


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def atomic_json(path: pathlib.Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = pathlib.Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(value, handle, indent=2, sort_keys=True)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)
    finally:
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()


@contextlib.contextmanager
def state_lock(paths: RuntimePaths) -> Iterator[None]:
    paths.ensure()
    with paths.lock.open("a+", encoding="utf-8") as handle:
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


def resolve_input_path(raw: str) -> pathlib.Path:
    match = WINDOWS_PATH_RE.match(raw)
    if match:
        drive, remainder = match.groups()
        normalized = remainder.replace("\\", "/")
        return pathlib.Path(f"/mnt/{drive.lower()}/{normalized}")
    if raw.startswith("\\\\"):
        raise CliError(
            "unsupported_windows_path",
            "UNC paths are not supported. Copy the APK to a local drive or pass its /mnt path.",
        )
    return pathlib.Path(raw).expanduser()


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def host_architecture() -> str:
    value = os.environ.get("ATL_WSL_TEST_ARCH", platform.machine()).lower()
    aliases = {"amd64": "x86_64", "arm64": "aarch64"}
    value = aliases.get(value, value)
    if value not in SUPPORTED_ABI:
        raise CliError("unsupported_host_arch", f"Unsupported ATL-WSL architecture: {value}")
    return value


def inspect_apk(raw_path: str) -> dict[str, Any]:
    path = resolve_input_path(raw_path)
    if not path.is_file():
        raise CliError("apk_not_found", f"APK not found: {raw_path}")
    if path.suffix.lower() != ".apk":
        raise CliError("not_an_apk", "The selected file must use the .apk extension.")
    try:
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
    except (zipfile.BadZipFile, OSError) as exc:
        raise CliError("invalid_apk", "The selected file is not a readable APK archive.") from exc
    if "AndroidManifest.xml" not in names:
        raise CliError("invalid_apk", "The archive does not contain AndroidManifest.xml.")

    abis = sorted(
        {
            parts[1]
            for name in names
            if len(parts := name.split("/")) >= 3 and parts[0] == "lib" and parts[1]
        }
    )
    arch = host_architecture()
    required_abi = SUPPORTED_ABI[arch]
    compatible = not abis or required_abi in abis
    reason = (
        "Java-only APK"
        if not abis
        else f"Contains native {required_abi} libraries"
        if compatible
        else f"Requires one of {', '.join(abis)}; this distro requires {required_abi}"
    )
    return {
        "path": str(path),
        "fileName": path.name,
        "displayName": path.stem[:128],
        "sha256": sha256_file(path),
        "size": path.stat().st_size,
        "nativeAbis": abis,
        "hostArchitecture": arch,
        "requiredAbi": required_abi,
        "compatible": compatible,
        "compatibilityReason": reason,
    }


def metadata_path(app_dir: pathlib.Path) -> pathlib.Path:
    return app_dir / "metadata.json"


def read_metadata(app_dir: pathlib.Path) -> dict[str, Any]:
    try:
        value = json.loads(metadata_path(app_dir).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise CliError("invalid_app_metadata", f"Invalid app metadata in {app_dir.name}.") from exc
    if value.get("schemaVersion") != METADATA_VERSION or value.get("id") != app_dir.name:
        raise CliError("invalid_app_metadata", f"Unsupported app metadata in {app_dir.name}.")
    value["apkPresent"] = (app_dir / "app.apk").is_file()
    value["dataPath"] = str(app_dir / "data")
    return value


def all_apps(paths: RuntimePaths, *, include_removed: bool = False) -> list[dict[str, Any]]:
    paths.ensure()
    apps: list[dict[str, Any]] = []
    for app_dir in sorted(paths.apps.iterdir()):
        if not app_dir.is_dir() or not metadata_path(app_dir).is_file():
            continue
        metadata = read_metadata(app_dir)
        if include_removed or not metadata.get("removedAt"):
            apps.append(metadata)
    return sorted(apps, key=lambda item: str(item.get("displayName", "")).casefold())


def get_app(paths: RuntimePaths, app_id: str, *, require_apk: bool = True) -> tuple[pathlib.Path, dict[str, Any]]:
    if not re.fullmatch(r"[0-9a-f]{32}", app_id):
        raise CliError("invalid_app_id", "The app ID is invalid.")
    app_dir = paths.apps / app_id
    if not app_dir.is_dir():
        raise CliError("app_not_found", f"App not found: {app_id}")
    metadata = read_metadata(app_dir)
    if require_apk and not (app_dir / "app.apk").is_file():
        raise CliError("app_removed", "This app has retained data but no installed APK.")
    return app_dir, metadata


def add_app(paths: RuntimePaths, raw_path: str) -> dict[str, Any]:
    inspection = inspect_apk(raw_path)
    if not inspection["compatible"]:
        raise CliError("incompatible_abi", inspection["compatibilityReason"], details=inspection)

    source = pathlib.Path(inspection["path"])
    free = shutil.disk_usage(paths.root.parent if paths.root.parent.exists() else pathlib.Path.home()).free
    required = inspection["size"] * 2 + 256 * 1024 * 1024
    if free < required:
        raise CliError(
            "insufficient_space",
            "Not enough free Linux filesystem space to add this APK safely.",
            details={"required": required, "available": free},
        )

    with state_lock(paths):
        for existing in all_apps(paths, include_removed=True):
            if existing.get("sha256") != inspection["sha256"]:
                continue
            app_dir = paths.apps / existing["id"]
            if existing.get("removedAt"):
                copy_apk(source, app_dir / "app.apk", inspection["sha256"])
                existing["removedAt"] = None
                existing["restoredAt"] = utc_now()
                existing.pop("apkPresent", None)
                existing.pop("dataPath", None)
                atomic_json(metadata_path(app_dir), existing)
                return {"app": read_metadata(app_dir), "restored": True, "alreadyInstalled": False}
            return {"app": existing, "restored": False, "alreadyInstalled": True}

        app_id = uuid.uuid4().hex
        app_dir = paths.apps / app_id
        app_dir.mkdir(mode=0o700)
        (app_dir / "data").mkdir(mode=0o700)
        copy_apk(source, app_dir / "app.apk", inspection["sha256"])
        metadata = {
            "schemaVersion": METADATA_VERSION,
            "id": app_id,
            "displayName": inspection["displayName"],
            "sourceFileName": inspection["fileName"],
            "sha256": inspection["sha256"],
            "size": inspection["size"],
            "nativeAbis": inspection["nativeAbis"],
            "hostArchitecture": inspection["hostArchitecture"],
            "compatibilityReason": inspection["compatibilityReason"],
            "installedAt": utc_now(),
            "removedAt": None,
            "launchOptions": DEFAULT_OPTIONS.copy(),
        }
        atomic_json(metadata_path(app_dir), metadata)
        return {"app": read_metadata(app_dir), "restored": False, "alreadyInstalled": False}


def copy_apk(source: pathlib.Path, destination: pathlib.Path, expected_sha: str) -> None:
    temporary = destination.with_suffix(".apk.partial")
    try:
        shutil.copyfile(source, temporary)
        if sha256_file(temporary) != expected_sha:
            raise CliError("copy_verification_failed", "The APK changed while it was being copied.")
        os.chmod(temporary, 0o600)
        os.replace(temporary, destination)
    finally:
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()


def configure_app(paths: RuntimePaths, args: argparse.Namespace) -> dict[str, Any]:
    with state_lock(paths):
        app_dir, metadata = get_app(paths, args.app_id)
        options = dict(DEFAULT_OPTIONS)
        options.update(metadata.get("launchOptions", {}))

        for cli_name, json_name in (
            ("width", "width"),
            ("height", "height"),
            ("fullscreen", "fullscreen"),
            ("web_view", "webView"),
            ("validate_certificates", "validateCertificates"),
            ("direct_egl", "directEgl"),
            ("location", "location"),
        ):
            value = getattr(args, cli_name)
            if value is not None:
                options[json_name] = value

        if args.clear_resolution:
            options["width"] = None
            options["height"] = None
        if (options["width"] is None) != (options["height"] is None):
            raise CliError("invalid_resolution", "Width and height must be set or cleared together.")
        if options["width"] is not None:
            if not 64 <= options["width"] <= 8192 or not 64 <= options["height"] <= 8192:
                raise CliError("invalid_resolution", "Width and height must be between 64 and 8192.")

        if args.clear_activity:
            options["activity"] = None
        elif args.activity is not None:
            if args.activity and not ACTIVITY_RE.fullmatch(args.activity):
                raise CliError("invalid_activity", "The activity class contains unsupported characters.")
            options["activity"] = args.activity or None

        if args.display_name is not None:
            name = args.display_name.strip()
            if not name or len(name) > 128:
                raise CliError("invalid_display_name", "Display name must contain 1 to 128 characters.")
            metadata["displayName"] = name

        metadata["launchOptions"] = options
        metadata["configuredAt"] = utc_now()
        metadata.pop("apkPresent", None)
        metadata.pop("dataPath", None)
        atomic_json(metadata_path(app_dir), metadata)
        return read_metadata(app_dir)


def remove_app(paths: RuntimePaths, app_id: str, delete_data: bool) -> dict[str, Any]:
    with state_lock(paths):
        app_dir, metadata = get_app(paths, app_id, require_apk=False)
        if delete_data:
            shutil.rmtree(app_dir)
            return {"id": app_id, "dataDeleted": True, "retained": False}
        with contextlib.suppress(FileNotFoundError):
            (app_dir / "app.apk").unlink()
        metadata["removedAt"] = utc_now()
        metadata.pop("apkPresent", None)
        metadata.pop("dataPath", None)
        atomic_json(metadata_path(app_dir), metadata)
        return {"id": app_id, "dataDeleted": False, "retained": True, "dataPath": str(app_dir / "data")}


def release_info(paths: RuntimePaths) -> dict[str, Any]:
    try:
        return json.loads(paths.release.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {"version": "development", "renderer": "llvmpipe", "architecture": host_architecture()}


def system_status(paths: RuntimePaths) -> dict[str, Any]:
    expected_path = pathlib.Path("/usr/share/atl-wsl/package-inventory.sha256")
    expected = expected_path.read_text(encoding="utf-8").strip() if expected_path.is_file() else "unknown"
    try:
        inventory = subprocess.run(
            ["apk", "info", "-vv"], check=True, capture_output=True, text=True
        ).stdout.splitlines()
        actual = hashlib.sha256(("\n".join(sorted(inventory)) + "\n").encode()).hexdigest()
    except (OSError, subprocess.CalledProcessError):
        actual = "unavailable"
    release = release_info(paths)
    components = release.get("components", {}) if isinstance(release.get("components"), dict) else {}
    return {
        "runtimeVersion": release.get("version", "unknown"),
        "alpineVersion": release.get("alpineVersion", components.get("alpine", "unknown")),
        "architecture": host_architecture(),
        "expectedPackageInventorySha256": expected,
        "actualPackageInventorySha256": actual,
        "drift": expected != actual,
        "appsRoot": str(paths.apps),
        "transactionPending": pathlib.Path("/var/lib/atl-wsl/update-transaction.json").is_file(),
    }


def _socket_from_environment(variable: str) -> pathlib.Path | None:
    value = os.environ.get(variable)
    if not value:
        return None
    if value.startswith("unix:"):
        value = value[5:]
    return pathlib.Path(value)


def doctor(paths: RuntimePaths) -> dict[str, Any]:
    fake_wslg = os.environ.get("ATL_WSL_TEST_WSLG") == "1"
    runtime_dir = pathlib.Path(os.environ.get("XDG_RUNTIME_DIR", "/mnt/wslg/runtime-dir"))
    wayland_name = os.environ.get("WAYLAND_DISPLAY", "wayland-0")
    wayland_path = runtime_dir / wayland_name
    pulse_path = _socket_from_environment("PULSE_SERVER") or pathlib.Path("/mnt/wslg/PulseServer")
    atl_binary = os.environ.get("ATL_WSL_ATL_BINARY", "android-translation-layer")
    atl_path = shutil.which(atl_binary) if os.path.sep not in atl_binary else atl_binary
    sandbox_path = shutil.which("bwrap")
    checks = {
        "wslgMount": fake_wslg or pathlib.Path("/mnt/wslg").is_dir(),
        "wayland": fake_wslg or wayland_path.exists(),
        "pulseAudio": fake_wslg or pulse_path.exists(),
        "atlBinary": bool(atl_path and pathlib.Path(atl_path).exists()),
        "sandboxBinary": fake_wslg or bool(sandbox_path and pathlib.Path(sandbox_path).exists()),
        "softwareRenderer": fake_wslg
        or pathlib.Path("/usr/lib/dri/swrast_dri.so").exists()
        or pathlib.Path("/usr/lib/dri/libgallium_dri.so").exists(),
    }
    required = ("wslgMount", "wayland", "atlBinary", "sandboxBinary", "softwareRenderer")
    return {
        "healthy": all(checks[name] for name in required),
        "checks": checks,
        "release": release_info(paths),
        "architecture": host_architecture(),
        "requiredAbi": SUPPORTED_ABI[host_architecture()],
        "renderer": "llvmpipe",
        "user": os.environ.get("USER", "unknown"),
        "home": str(pathlib.Path.home()),
        "stateRoot": str(paths.root),
        "warnings": [
            "ATL implements only a subset of Android APIs; ABI compatibility does not guarantee that an app will run.",
            "ATL-WSL uses software rendering on Alpine 3.24.",
            "The filesystem sandbox reduces host exposure but does not reproduce Android's complete security model.",
        ],
    }


def sandbox_command(
    app_dir: pathlib.Path,
    atl_binary: str,
    atl_arguments: list[str],
    environment: dict[str, str],
) -> list[str]:
    sandbox = shutil.which("bwrap")
    if not sandbox:
        raise CliError("sandbox_unavailable", "The required bubblewrap sandbox is not installed.")

    data_dir = app_dir / "data"
    (data_dir / "home").mkdir(parents=True, exist_ok=True, mode=0o700)
    (data_dir / "android").mkdir(parents=True, exist_ok=True, mode=0o700)
    command = [
        sandbox,
        "--die-with-parent",
        "--new-session",
        "--unshare-all",
        "--share-net",
        "--cap-drop",
        "ALL",
        "--clearenv",
        "--proc",
        "/proc",
        "--dev",
        "/dev",
        "--tmpfs",
        "/dev/shm",
        "--tmpfs",
        "/tmp",
        "--tmpfs",
        "/run",
    ]
    for source in ("/bin", "/etc", "/lib", "/lib64", "/sbin", "/sys", "/usr"):
        if pathlib.Path(source).exists():
            command.extend(["--ro-bind", source, source])
    command.extend(
        [
            "--dir",
            "/app",
            "--ro-bind",
            str(app_dir / "app.apk"),
            "/app/app.apk",
            "--bind",
            str(data_dir),
            "/app/data",
        ]
    )
    if pathlib.Path("/mnt/wslg").exists():
        command.extend(["--dir", "/mnt", "--ro-bind", "/mnt/wslg", "/mnt/wslg"])

    sandbox_environment = {
        "ANDROID_APP_DATA_DIR": "/app/data/android",
        "GDK_BACKEND": environment["GDK_BACKEND"],
        "HOME": "/app/data/home",
        "LANG": environment.get("LANG", "C.UTF-8"),
        "LIBGL_ALWAYS_SOFTWARE": "1",
        "LOGNAME": "atl-app",
        "MESA_LOADER_DRIVER_OVERRIDE": "llvmpipe",
        "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        "TMPDIR": "/tmp",
        "USER": "atl-app",
        "XDG_CACHE_HOME": "/app/data/.cache",
        "XDG_CONFIG_HOME": "/app/data/.config",
        "XDG_DATA_HOME": "/app/data/.local/share",
        "GALLIUM_DRIVER": "llvmpipe",
    }
    for name in (
        "ATL_DIRECT_EGL",
        "ATL_FORCE_FULLSCREEN",
        "ATL_UGLY_ENABLE_LOCATION",
        "ATL_UGLY_ENABLE_WEBVIEW",
        "ATL_VALIDATE_CERTS",
        "DISPLAY",
        "PULSE_SERVER",
        "WAYLAND_DISPLAY",
        "XDG_RUNTIME_DIR",
    ):
        if value := environment.get(name):
            sandbox_environment[name] = value
    for name, value in sandbox_environment.items():
        command.extend(["--setenv", name, value])

    command.extend(["--chdir", "/app/data", "--hostname", "atl-wsl-app", "--"])
    if shutil.which("dbus-run-session"):
        command.extend(["/usr/bin/dbus-run-session", "--"])
    command.extend([atl_binary, "/app/app.apk", *atl_arguments])
    return command


def launch_app(paths: RuntimePaths, app_id: str) -> dict[str, Any]:
    health = doctor(paths)
    if not health["healthy"]:
        raise CliError("preflight_failed", "ATL-WSL preflight checks failed.", details=health)
    app_dir, metadata = get_app(paths, app_id)
    options = dict(DEFAULT_OPTIONS)
    options.update(metadata.get("launchOptions", {}))

    atl_binary = os.environ.get("ATL_WSL_ATL_BINARY", "/usr/bin/android-translation-layer")
    atl_arguments: list[str] = []
    if options["activity"]:
        atl_arguments.extend(["-l", options["activity"]])
    if options["width"] and options["height"]:
        atl_arguments.extend(["-w", str(options["width"]), "-h", str(options["height"])])

    environment = os.environ.copy()
    environment.update(
        {
            "ANDROID_APP_DATA_DIR": str(app_dir / "data"),
            "LIBGL_ALWAYS_SOFTWARE": "1",
            "GALLIUM_DRIVER": "llvmpipe",
            "MESA_LOADER_DRIVER_OVERRIDE": "llvmpipe",
            "GDK_BACKEND": "wayland,x11",
        }
    )
    if pathlib.Path("/mnt/wslg/runtime-dir/wayland-0").exists():
        environment.setdefault("XDG_RUNTIME_DIR", "/mnt/wslg/runtime-dir")
        environment.setdefault("WAYLAND_DISPLAY", "wayland-0")
    if pathlib.Path("/mnt/wslg/PulseServer").exists():
        environment.setdefault("PULSE_SERVER", "unix:/mnt/wslg/PulseServer")
    if pathlib.Path("/mnt/wslg/.X11-unix").exists():
        environment.setdefault("DISPLAY", ":0")
    toggles = {
        "fullscreen": "ATL_FORCE_FULLSCREEN",
        "webView": "ATL_UGLY_ENABLE_WEBVIEW",
        "validateCertificates": "ATL_VALIDATE_CERTS",
        "directEgl": "ATL_DIRECT_EGL",
        "location": "ATL_UGLY_ENABLE_LOCATION",
    }
    for option, variable in toggles.items():
        if options[option]:
            environment[variable] = "1"
        else:
            environment.pop(variable, None)

    if os.environ.get("ATL_WSL_TEST_DISABLE_SANDBOX") == "1":
        command = [atl_binary, str(app_dir / "app.apk"), *atl_arguments]
        if not environment.get("DBUS_SESSION_BUS_ADDRESS") and shutil.which("dbus-run-session"):
            command = ["dbus-run-session", "--", *command]
    else:
        command = sandbox_command(app_dir, atl_binary, atl_arguments, environment)

    paths.logs.mkdir(parents=True, exist_ok=True, mode=0o700)
    log_path = paths.logs / f"{app_id}-{dt.datetime.now().strftime('%Y%m%d-%H%M%S')}.log"
    with log_path.open("ab", buffering=0) as log_handle:
        if os.environ.get("ATL_WSL_TEST_LAUNCH_FOREGROUND") == "1":
            result = subprocess.run(command, env=environment, stdout=log_handle, stderr=subprocess.STDOUT, check=False)
            if result.returncode != 0:
                raise CliError(
                    "launch_failed",
                    f"ATL exited with code {result.returncode}.",
                    details={"exitCode": result.returncode, "logPath": str(log_path)},
                )
            pid = None
        else:
            process = subprocess.Popen(
                command,
                env=environment,
                stdin=subprocess.DEVNULL,
                stdout=log_handle,
                stderr=subprocess.STDOUT,
                close_fds=True,
                start_new_session=True,
            )
            pid = process.pid
    return {"id": app_id, "pid": pid, "logPath": str(log_path), "renderer": "llvmpipe"}


def export_logs(paths: RuntimePaths, raw_output: str) -> dict[str, Any]:
    output = resolve_input_path(raw_output)
    if output.suffix.lower() != ".zip":
        output = output.with_suffix(".zip")
    output.parent.mkdir(parents=True, exist_ok=True)
    paths.ensure()

    replacements = {
        str(pathlib.Path.home()): "<HOME>",
        str(paths.root): "<ATL_STATE>",
        str(paths.logs): "<ATL_LOGS>",
    }

    def redact(value: str) -> str:
        for sensitive, replacement in sorted(replacements.items(), key=lambda item: len(item[0]), reverse=True):
            if sensitive and sensitive != ".":
                value = value.replace(sensitive, replacement)
        value = re.sub(r"(?i)(?:[A-Z]:[\\/]|/mnt/[a-z]/)Users[\\/][^\\/\s]+", "<WINDOWS_USER>", value)
        return value

    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("doctor.json", redact(json.dumps(doctor(paths), indent=2) + "\n"))
        archive.writestr("release.json", redact(json.dumps(release_info(paths), indent=2) + "\n"))
        for app in all_apps(paths, include_removed=True):
            archive.writestr(f"apps/{app['id']}.json", redact(json.dumps(app, indent=2) + "\n"))
        total = 0
        for log_path in sorted(paths.logs.glob("*.log"), key=lambda item: item.stat().st_mtime, reverse=True)[:10]:
            size = log_path.stat().st_size
            if total + size > 5 * 1024 * 1024:
                break
            archive.writestr(f"logs/{log_path.name}", redact(log_path.read_text(encoding="utf-8", errors="replace")))
            total += size
    return {"path": str(output), "size": output.stat().st_size}


def add_boolean_option(parser: argparse.ArgumentParser, name: str, *, destination: str | None = None) -> None:
    parser.add_argument(
        f"--{name}",
        dest=destination or name.replace("-", "_"),
        action=argparse.BooleanOptionalAction,
        default=None,
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="atl-wsl")
    parser.add_argument("--json", action="store_true", help="emit the versioned JSON response envelope")
    commands = parser.add_subparsers(dest="command", required=True)

    commands.add_parser("doctor")
    commands.add_parser("version")
    system = commands.add_parser("system")
    system_commands = system.add_subparsers(dest="system_command", required=True)
    system_commands.add_parser("status")
    inspect_parser = commands.add_parser("inspect")
    inspect_parser.add_argument("apk")

    library = commands.add_parser("library")
    library_commands = library.add_subparsers(dest="library_command", required=True)
    library_commands.add_parser("list")
    add_parser = library_commands.add_parser("add")
    add_parser.add_argument("apk")
    configure_parser = library_commands.add_parser("configure")
    configure_parser.add_argument("app_id")
    configure_parser.add_argument("--display-name")
    configure_parser.add_argument("--width", type=int)
    configure_parser.add_argument("--height", type=int)
    configure_parser.add_argument("--clear-resolution", action="store_true")
    configure_parser.add_argument("--activity")
    configure_parser.add_argument("--clear-activity", action="store_true")
    add_boolean_option(configure_parser, "fullscreen")
    add_boolean_option(configure_parser, "web-view")
    add_boolean_option(configure_parser, "validate-certificates")
    add_boolean_option(configure_parser, "direct-egl")
    add_boolean_option(configure_parser, "location")
    remove_parser = library_commands.add_parser("remove")
    remove_parser.add_argument("app_id")
    remove_parser.add_argument("--delete-data", action="store_true")

    launch_parser = commands.add_parser("launch")
    launch_parser.add_argument("app_id")

    logs = commands.add_parser("logs")
    log_commands = logs.add_subparsers(dest="logs_command", required=True)
    export_parser = log_commands.add_parser("export")
    export_parser.add_argument("output")
    return parser


def execute(args: argparse.Namespace, paths: RuntimePaths) -> Any:
    if args.command == "doctor":
        return doctor(paths)
    if args.command == "version":
        return release_info(paths)
    if args.command == "system" and args.system_command == "status":
        return system_status(paths)
    if args.command == "inspect":
        return inspect_apk(args.apk)
    if args.command == "launch":
        return launch_app(paths, args.app_id)
    if args.command == "library":
        if args.library_command == "list":
            return {"apps": all_apps(paths)}
        if args.library_command == "add":
            return add_app(paths, args.apk)
        if args.library_command == "configure":
            return {"app": configure_app(paths, args)}
        if args.library_command == "remove":
            return remove_app(paths, args.app_id, args.delete_data)
    if args.command == "logs" and args.logs_command == "export":
        return export_logs(paths, args.output)
    raise CliError("unsupported_command", "Unsupported command.")


def response(command: str, *, data: Any = None, error: CliError | None = None) -> dict[str, Any]:
    value: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "product": "atl-wsl",
        "ok": error is None,
        "command": command,
        "data": data if error is None else None,
        "warnings": [],
        "error": None,
    }
    if error:
        value["error"] = {"code": error.code, "message": error.message, "details": error.details}
    return value


def print_human(command: str, data: Any) -> None:
    if command == "library" and isinstance(data, dict) and "apps" in data:
        apps = data["apps"]
        if not apps:
            print("No APKs are installed.")
        for app in apps:
            print(f"{app['id']}  {app['displayName']}  {app['compatibilityReason']}")
        return
    print(json.dumps(data, indent=2))


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    paths = RuntimePaths.load()
    try:
        data = execute(args, paths)
    except CliError as error:
        if args.json:
            print(json.dumps(response(args.command, error=error), separators=(",", ":")))
        else:
            print(f"error: {error.message}", file=sys.stderr)
        return error.exit_code
    except Exception as exc:  # defensive boundary for the manager protocol
        error = CliError("internal_error", "ATL-WSL encountered an unexpected error.", details=str(exc), exit_code=1)
        if args.json:
            print(json.dumps(response(args.command, error=error), separators=(",", ":")))
        else:
            print(f"error: {error.message}: {exc}", file=sys.stderr)
        return error.exit_code

    if args.json:
        print(json.dumps(response(args.command, data=data), separators=(",", ":")))
    else:
        print_human(args.command, data)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
