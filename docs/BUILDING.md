# Building ATL-WSL

Release artifacts are built from pinned upstream source revisions in disposable native environments. x64 is built on an x86_64 Alpine 3.24 worker and ARM64 on an aarch64 Alpine 3.24 worker.

## Inputs

The single version ledger is `config/versions.env`. It pins:

- ATL-WSL and minimum WSL versions.
- Alpine release branch and minirootfs SHA-256 for both architectures.
- Alpine aports reference used to vendor the APKBUILD recipes.
- Exact commits for ATL and its standalone runtime dependencies.
- .NET SDK used for the Windows manager.

Changing a pin is a source change and should be reviewed with the associated APKBUILD checksum. The build intentionally does not follow a moving Git branch.

Alpine runtime dependencies resolve from the supported `v3.24` repositories so security fixes are included. Their exact resolved versions are recorded in each release's `.packages.txt` and SPDX files; for that reason, rebuilding at a later date is source-reproducible but not guaranteed to be bit-for-bit identical.

## Build the Linux artifact

Start an Alpine 3.24 environment matching the target architecture, mount the repository at `/work`, and run as root:

```sh
apk add --no-cache bash
cd /work
bash build/build-release.sh x86_64   # or aarch64
```

`build-packages.sh` creates an ephemeral abuild key, builds the four vendored packages in dependency order and constructs a local APK repository. `build-rootfs.sh` verifies the Alpine minirootfs hash, installs the local ATL package and runtime dependencies, applies the WSL overlay, emits an SPDX document and creates the `.wsl` tarball.

Outputs are written beneath `artifacts/`:

```text
artifacts/
├── packages/<arch>/
└── release/
    ├── ATL-WSL-<version>-<windows-arch>.wsl
    ├── *.sha256
    ├── *.packages.txt
    └── *.spdx.json
```

The script refuses cross-architecture assembly. Use the CI ARM64 runner or a native ARM64 Alpine machine for `aarch64`.

## Build the Windows manager

On Windows with the .NET SDK selected by `global.json` and PowerShell 7:

```powershell
dotnet build .\ATLWSL.slnx -c Release
.\scripts\publish-manager.ps1
```

The publish script produces self-contained `win-x64` and `win-arm64` ZIP files in `artifacts/release`.

## Assemble a manifest

After all four public artifacts are in the same directory:

```sh
python3 tools/create-release-manifest.py \
  --directory artifacts/release \
  --base-url https://github.com/LamPPKK/ATL-WSL/releases/download/v0.2.0 \
  --output artifacts/release/release-manifest.json
```

Validate the manifest against `config/release-manifest.schema.json`, then publish it beside the artifacts and `scripts/install-atl-wsl.ps1`.

The release workflow also runs `build/create-source-bundle.sh`. That bundle contains the tagged ATL-WSL source plus hash-verified source archives for every ATL component built into the distribution.

Run the **Release** workflow manually to exercise both native-architecture builders without publishing. Pushing a matching `v<version>` tag performs the same builds and then creates the public GitHub release.

## Local checks

```sh
python3 -m unittest discover -s tests -v
python3 -m py_compile runtime/atl_wsl.py tools/*.py
shellcheck build/*.sh build/lib/*.sh rootfs/overlay/usr/bin/atl-wsl rootfs/overlay/usr/libexec/atl-wsl-oobe
```

On Windows, also run:

```powershell
dotnet build .\ATLWSL.slnx -c Release
dotnet run --project .\tests\ATLWSL.Core.Smoke\ATLWSL.Core.Smoke.csproj -c Release
```
