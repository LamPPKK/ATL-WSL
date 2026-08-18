# Troubleshooting

Start in the manager's **Diagnostics** view and select **Run checks**. The sandbox check is mandatory; ATL-WSL will not fall back to launching an APK directly if bubblewrap is missing. If a launch still fails, select **Export support ZIP** and attach that archive to a bug report. The archive excludes APKs and app data.

## The manager cannot find ATL-WSL

Open Settings and confirm the distribution name. Compare it with:

```powershell
wsl.exe --list --verbose
```

This project's installer does not repair, rename or replace an existing distribution.

## WSL or WSLg check fails

Confirm Windows 11 and WSL 2.4.4 or newer:

```powershell
wsl.exe --version
wsl.exe --update
wsl.exe --shutdown
```

Start ATL-WSL again after `wsl --shutdown`. Corporate policy or a disabled WSLg configuration can prevent GUI sockets from being mounted.

## APK is rejected before import

On x64 Windows, an APK containing native code must include `lib/x86_64/`. On ARM64 Windows it must include `lib/arm64-v8a/`. Java-only APKs pass this check. 32-bit-only APKs are unsupported.

## APK passes inspection but does not launch

Architecture compatibility is not full Android compatibility. ATL may lack an Android API, system service, codec or hardware-backed feature required by the app. Review the newest log in the exported diagnostics archive. Try a simple application before changing experimental flags.

## Graphics are slow

This Alpine release intentionally uses Mesa llvmpipe software rendering. Graphics-heavy applications may be unusably slow. Direct EGL is an experimental ATL option, not a supported GPU acceleration path.

## Audio is missing

Run Diagnostics and check the PulseAudio bridge. Audio is optional for the overall health verdict, because some systems intentionally disable it, but applications that require audio may fail or remain silent.

## Remove ATL-WSL completely

First export or copy any data you need. The following WSL command permanently deletes the entire selected distribution and all of its Linux files:

```powershell
wsl.exe --unregister ATL-WSL
```

The ATL-WSL installer never runs this command automatically.
