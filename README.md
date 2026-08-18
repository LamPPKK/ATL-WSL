# ATL-WSL

ATL-WSL 0.2 packages the [Android Translation Layer](https://gitlab.com/android_translation_layer/android_translation_layer) in a reproducible Alpine Linux 3.24.1 WSL distribution, with a native Windows lifecycle and APK manager.

ATL is a compatibility layer rather than a complete Android system. Application support depends on the APK ABI and the Android APIs implemented upstream, so runtime compatibility is reported separately from the stable installer, update, rollback, and data-preservation guarantees.

## Stable platform scope

- Windows 11 x64 and ARM64 with Store WSL 2.4.4 or newer and WSLg.
- Native Alpine, ATL packages, runtime bundle, and self-contained .NET 10 manager artifacts for both architectures.
- Pinned ATL, ART, bionic translation, and OpenSL ES revisions. They are advanced together only after both architectures pass the build, sandbox, APK, and launch gates.
- Mesa llvmpipe is the supported renderer. Hardware acceleration is not claimed.
- Windows 10 and 32-bit APK ABIs are outside the stable support tier.

No APK is bundled. Install only applications you are authorized to use.

## Manager and CLI

The English WPF manager provides Overview, Library, Diagnostics, and Settings pages. It supports preflight, install, versioned update, repair, APK inspection and launch, diagnostics export, and safe uninstall. It follows the Windows theme and accent, high contrast, keyboard navigation, and screen-reader labels. No telemetry is collected.

The equivalent machine-readable runtime interface is available inside the distribution:

```sh
atl-wsl --json system status
atl-wsl --json system version
atl-wsl --json doctor
atl-wsl --json inspect /mnt/c/Users/me/Downloads/example.apk
atl-wsl --json library add /mnt/c/Users/me/Downloads/example.apk
atl-wsl --json library list
atl-wsl --json launch APP_ID
atl-wsl --json logs export /mnt/c/Users/me/Desktop/atl-wsl-diagnostics.zip
```

Responses use the v0.2 envelope `{schemaVersion, product, command, ok, data, warnings, error}`. The v0.1 command and library fields remain compatible; v0.2.x changes are additive.

## Verified installation and updates

The manager and headless PowerShell frontend consume the same release manifest and lifecycle rules. A stable release contains `release-manifest.json`, its detached Ed25519 signature, the embedded public-key ring, Authenticode-signed manager archives, architecture-specific rootfs and update bundles, SHA256SUMS, SPDX SBOMs, and corresponding source.

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\install-atl-wsl.ps1
```

The bootstrap first validates the manager's Authenticode signature, then the manager validates the raw manifest's Ed25519 signature. The selected x64 or ARM64 artifact is subsequently checked by exact size and SHA-256.

Updates save package inventory, current recovery packages, and overlay metadata before applying a versioned bundle. A failed doctor check restores the previous runtime. APKs and per-app data under `~/.local/share/atl-wsl/apps` are never modified by update or repair.

Normal uninstall removes manager integration while retaining the distro and application data. Permanent unregister is a separate option that requires typing the exact distro name and can export a VHDX backup first.

## Build and release

See [Building](docs/BUILDING.md), [Architecture](docs/ARCHITECTURE.md), and [Troubleshooting](docs/TROUBLESHOOTING.md). Version pins live in [config/versions.env](config/versions.env).

The CI matrix builds on native `ubuntu-24.04` / `ubuntu-24.04-arm` and `windows-2025` / `windows-11-arm` workers with .NET SDK 10.0.400 and runtime 10.0.11. Stable publication requires release-key material, SignPath Foundation Authenticode signing, native lab acceptance, and non-placeholder artifact hashes. CI creates draft releases only; tag publication and WinGet submission remain manual authorized actions.

## License

ATL-WSL is licensed under `GPL-3.0-only`. Packaged upstream components retain their own licenses and are recorded in release SBOM and source artifacts.
