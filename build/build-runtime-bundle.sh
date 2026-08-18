#!/usr/bin/env bash

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
# shellcheck disable=SC1091
. "$SCRIPT_DIR/lib/common.sh"

require_alpine_324
arch="$(normalize_arch "${1:-$(uname -m)}")"
artifact_arch="$(windows_arch "$arch")"
package_repo="${2:-$PROJECT_ROOT/artifacts/packages/$arch/repository/$arch}"
output_dir="${3:-$PROJECT_ROOT/artifacts/release}"
work="$PROJECT_ROOT/.build/runtime-$arch"
staging="$work/bundle"
output="$output_dir/ATL-WSL-Runtime-$ATL_WSL_VERSION-$artifact_arch.tar.gz"

safe_remove_project_tree "$work"
install -d "$staging/packages" "$staging/overlay/usr/lib/atl-wsl" \
    "$staging/overlay/usr/bin" "$staging/overlay/usr/libexec" "$output_dir"
find "$package_repo" -maxdepth 1 -name '*.apk' -type f -exec cp -p '{}' "$staging/packages/" \;
cp -a "$PROJECT_ROOT/rootfs/overlay/." "$staging/overlay/"
install -Dm0755 "$PROJECT_ROOT/runtime/atl_wsl.py" "$staging/overlay/usr/lib/atl-wsl/atl_wsl.py"
(
    cd "$staging"
    find packages overlay -type f -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS
    tar --numeric-owner --sort=name --mtime="@${SOURCE_DATE_EPOCH:-0}" \
        -czf "$output" SHA256SUMS packages overlay
)
sha256sum "$output" > "$output.sha256"
log "Runtime update bundle ready: $output"
