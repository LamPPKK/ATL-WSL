#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
# shellcheck disable=SC1091
. "$SCRIPT_DIR/lib/common.sh"

require_alpine_324
[[ "$(id -u)" -eq 0 ]] || die 'build-rootfs.sh must run as root inside the disposable build environment'

requested_arch="${1:-$(uname -m)}"
arch="$(normalize_arch "$requested_arch")"
host_arch="$(normalize_arch "$(uname -m)")"
[[ "$arch" == "$host_arch" ]] || die "native rootfs assembly required: requested $arch on $host_arch"

case "$arch" in
    x86_64) minirootfs_sha="$ALPINE_X86_64_MINIROOTFS_SHA256" ;;
    aarch64) minirootfs_sha="$ALPINE_AARCH64_MINIROOTFS_SHA256" ;;
esac

package_root="${2:-$PROJECT_ROOT/artifacts/packages/$arch}"
package_repo="$package_root/repository/$arch"
output_dir="${3:-$PROJECT_ROOT/artifacts/release}"
work_root="$PROJECT_ROOT/.build/rootfs-$arch"
rootfs="$work_root/rootfs"
minirootfs_name="alpine-minirootfs-$ALPINE_VERSION-$arch.tar.gz"
minirootfs_url="https://dl-cdn.alpinelinux.org/alpine/$ALPINE_BRANCH/releases/$arch/$minirootfs_name"
minirootfs_cache="$PROJECT_ROOT/.cache/$minirootfs_name"
artifact_arch="$(windows_arch "$arch")"
artifact="$output_dir/ATL-WSL-$ATL_WSL_VERSION-$artifact_arch.wsl"

[[ -f "$package_repo/APKINDEX.tar.gz" ]] || die "local ATL package repository not found: $package_repo"
apk add --no-cache bash ca-certificates coreutils curl findutils gzip python3 rsvg-convert tar

install -d "$PROJECT_ROOT/.cache" "$output_dir"
if [[ ! -f "$minirootfs_cache" ]]; then
    log "Downloading $minirootfs_name"
    curl --fail --location --retry 3 --output "$minirootfs_cache" "$minirootfs_url"
fi
verify_sha256 "$minirootfs_cache" "$minirootfs_sha"

safe_remove_project_tree "$work_root"
install -d "$rootfs"
tar -xzf "$minirootfs_cache" -C "$rootfs"
rm -f "$rootfs/etc/resolv.conf"

cat > "$rootfs/etc/apk/repositories" <<EOF
https://dl-cdn.alpinelinux.org/alpine/$ALPINE_BRANCH/main
https://dl-cdn.alpinelinux.org/alpine/$ALPINE_BRANCH/community
EOF

log "Installing ATL-WSL runtime packages"
apk --root "$rootfs" --arch "$arch" --update-cache --no-progress add \
    --repository "$package_repo" \
    --allow-untrusted \
    alpine-base \
    android-translation-layer \
    alsa-lib \
    alsa-plugins-pulse \
    bash \
    bubblewrap \
    ca-certificates \
    coreutils \
    dbus \
    font-noto \
    font-noto-emoji \
    mesa-dri-gallium \
    mesa-egl \
    mesa-gl \
    mesa-gles \
    mesa-vulkan-swrast \
    libpulse \
    python3 \
    shadow \
    tzdata \
    unzip \
    vulkan-loader \
    xdg-desktop-portal \
    xdg-desktop-portal-gtk

cp -a "$PROJECT_ROOT/rootfs/overlay/." "$rootfs/"
install -Dm0755 "$PROJECT_ROOT/runtime/atl_wsl.py" "$rootfs/usr/lib/atl-wsl/atl_wsl.py"
install -Dm0644 "$PROJECT_ROOT/LICENSE" "$rootfs/usr/share/licenses/atl-wsl/LICENSE"
install -Dm0644 "$PROJECT_ROOT/assets/atl-wsl.svg" "$rootfs/usr/share/atl-wsl/atl-wsl.svg"
install -d "$rootfs/usr/share/atl-wsl/recovery-packages"
find "$package_repo" -maxdepth 1 -name '*.apk' -type f \
    -exec cp -p '{}' "$rootfs/usr/share/atl-wsl/recovery-packages/" \;
install -Dm0755 "$PROJECT_ROOT/rootfs/overlay/usr/libexec/atl-wsl-system" \
    "$rootfs/usr/libexec/atl-wsl-system"

rsvg-convert -w 256 -h 256 -o "$work_root/atl-wsl.png" "$PROJECT_ROOT/assets/atl-wsl.svg"
python3 - "$work_root/atl-wsl.png" "$rootfs/usr/share/atl-wsl/atl-wsl.ico" <<'PY'
import pathlib
import struct
import sys

png = pathlib.Path(sys.argv[1]).read_bytes()
header = struct.pack("<HHH", 0, 1, 1)
entry = struct.pack("<BBBBHHII", 0, 0, 0, 0, 1, 32, len(png), 22)
pathlib.Path(sys.argv[2]).write_bytes(header + entry + png)
PY

cat > "$rootfs/etc/atl-wsl-release" <<EOF
{
  "version": "$ATL_WSL_VERSION",
  "alpineVersion": "$ALPINE_VERSION",
  "architecture": "$arch",
  "atlCommit": "$ATL_COMMIT",
  "renderer": "llvmpipe"
}
EOF

chmod 0755 \
    "$rootfs/usr/bin/atl-wsl" \
    "$rootfs/usr/lib/atl-wsl/atl_wsl.py" \
    "$rootfs/usr/libexec/atl-wsl-oobe" \
    "$rootfs/usr/libexec/atl-wsl-system"
chmod 0644 \
    "$rootfs/etc/asound.conf" \
    "$rootfs/etc/skel/.ashrc" \
    "$rootfs/etc/skel/.profile" \
    "$rootfs/etc/wsl.conf" \
    "$rootfs/etc/wsl-distribution.conf" \
    "$rootfs/etc/profile.d/atl-wsl.sh" \
    "$rootfs/usr/share/atl-wsl/terminal-profile.json" \
    "$rootfs/var/lib/atl-wsl/README"
chown -R 0:0 \
    "$rootfs/etc/asound.conf" \
    "$rootfs/etc/profile.d/atl-wsl.sh" \
    "$rootfs/etc/skel/.ashrc" \
    "$rootfs/etc/skel/.profile" \
    "$rootfs/etc/wsl.conf" \
    "$rootfs/etc/wsl-distribution.conf" \
    "$rootfs/usr/bin/atl-wsl" \
    "$rootfs/usr/lib/atl-wsl" \
    "$rootfs/usr/libexec/atl-wsl-oobe" \
    "$rootfs/usr/share/atl-wsl" \
    "$rootfs/var/lib/atl-wsl"
rm -rf "$rootfs/var/cache/apk/"* "$rootfs/tmp/"*
rm -f "$rootfs/etc/resolv.conf"

apk --root "$rootfs" info -vv | sort > "$rootfs/usr/share/atl-wsl/package-inventory.txt"
sha256sum "$rootfs/usr/share/atl-wsl/package-inventory.txt" | awk '{print $1}' \
    > "$rootfs/usr/share/atl-wsl/package-inventory.sha256"

python3 "$PROJECT_ROOT/tools/create-sbom.py" \
    --rootfs "$rootfs" \
    --name "ATL-WSL-$artifact_arch" \
    --version "$ATL_WSL_VERSION" \
    --output "$output_dir/ATL-WSL-$ATL_WSL_VERSION-$artifact_arch.spdx.json"

log "Creating $artifact"
source_date_epoch="${SOURCE_DATE_EPOCH:-$(git -C "$PROJECT_ROOT" log -1 --format=%ct 2>/dev/null || date +%s)}"
tar --numeric-owner --sort=name --mtime="@$source_date_epoch" --clamp-mtime \
    -C "$rootfs" -czf "$artifact" .
sha256sum "$artifact" > "$artifact.sha256"

apk --root "$rootfs" info -vv | sort > "$output_dir/ATL-WSL-$ATL_WSL_VERSION-$artifact_arch.packages.txt"
log "WSL artifact ready: $artifact"
