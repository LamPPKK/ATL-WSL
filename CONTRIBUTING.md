# Contributing

Keep changes within ATL-WSL's stated scope: Windows 11, WSLg, Alpine 3.24, x64 and ARM64, and software rendering. Do not add proprietary APKs, Google components or claims of broad Android compatibility.

Before opening a pull request, run the checks in `docs/BUILDING.md`. Changes to upstream pins must update `config/versions.env`, the relevant APKBUILD source/checksum, documentation and release metadata together. UI changes should preserve keyboard access, Windows light/dark behavior and clear labeling for experimental ATL options.
