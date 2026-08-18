# Architecture

ATL-WSL has three deliberately narrow layers.

```text
ATL-WSL Manager.exe
        │ wsl.exe argument list + JSON envelope v1
        ▼
 /usr/bin/atl-wsl ── app metadata, APK copies, logs
        │ bubblewrap mount namespace + launch options
        ▼
 android-translation-layer ── WSLg Wayland/PulseAudio + Mesa llvmpipe
```

## Windows manager

The WPF manager is self-contained and runs without administrator privileges. `ATLWSL.Core` invokes `wsl.exe` with `ProcessStartInfo.ArgumentList`; paths and user strings are never concatenated into a shell command. All runtime responses use the JSON envelope defined by schema version 1.

The UI is English-only for the initial release and follows the Windows light/dark app preference. It does not own Linux app state; refreshing the library reconstructs the view from the runtime API.

## Runtime control plane

`runtime/atl_wsl.py` stores state under these per-user directories:

```text
~/.local/share/atl-wsl/apps/<app-id>/
├── app.apk
├── data/
└── metadata.json

~/.local/state/atl-wsl/logs/
```

Metadata updates and APK copies are atomic. A process lock serializes library mutations. App IDs are random 128-bit hexadecimal identifiers and are validated before filesystem access.

Removing with retained data deletes `app.apk`, marks the record as removed and leaves `data/`. Re-adding the exact APK SHA-256 restores that record. Removing with data deletion removes the complete app directory.

## Launch environment

Each launch enters a mandatory bubblewrap sandbox with a new user, PID, IPC, UTS, cgroup and mount namespace. System binaries and libraries and the APK are read-only; only the selected app's data directory is writable. `/mnt/wslg` is exposed read-only for display/audio socket access. Windows drives, `/init`, the Linux user's home and other applications' data are absent. The network namespace is shared so ordinary networked applications can still work.

Inside the sandbox, `ANDROID_APP_DATA_DIR` points to the selected app's data directory. Rendering is fixed with `LIBGL_ALWAYS_SOFTWARE=1`, `GALLIUM_DRIVER=llvmpipe` and `MESA_LOADER_DRIVER_OVERRIDE=llvmpipe`. WSLg supplies Wayland and PulseAudio endpoints. ATL flags are passed as environment variables only when their matching per-app switch is enabled.

The doctor command verifies platform plumbing and the presence of bubblewrap, not application compatibility. An ABI match proves only that a packaged native library can target the host architecture.

## Distribution build

The release rootfs begins with a hash-pinned Alpine minirootfs. ATL and three standalone dependencies are built from vendored Alpine APKBUILD recipes at exact commits. The resulting packages are installed into the rootfs, then the runtime and WSL configuration overlay are applied. The release includes a package inventory and SPDX SBOM.
