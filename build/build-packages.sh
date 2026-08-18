#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
# shellcheck disable=SC1091
. "$SCRIPT_DIR/lib/common.sh"

require_alpine_324
[[ "$(id -u)" -eq 0 ]] || die 'build-packages.sh must start as root inside the disposable build environment'

requested_arch="${1:-$(uname -m)}"
arch="$(normalize_arch "$requested_arch")"
host_arch="$(normalize_arch "$(uname -m)")"
[[ "$arch" == "$host_arch" ]] || die "native build required: requested $arch on $host_arch"

output_root="${2:-$PROJECT_ROOT/artifacts/packages/$arch}"
work_root="$PROJECT_ROOT/.build/packages-$arch"
builder=atlbuilder
repository_dir="$output_root/repository/$arch"

log "Installing Alpine package build tooling for $arch"
apk add --no-cache alpine-sdk doas bash curl git meson samurai

if ! id "$builder" >/dev/null 2>&1; then
    adduser -D -s /bin/ash "$builder"
fi
addgroup "$builder" abuild 2>/dev/null || true
install -d -m 0750 /etc/doas.d
printf 'permit nopass :abuild\n' > /etc/doas.d/abuild.conf

safe_remove_project_tree "$work_root"
safe_remove_project_tree "$output_root"
install -d -o "$builder" -g "$builder" "$work_root/aports" "$output_root" "$repository_dir"
cp -R "$PROJECT_ROOT/packaging/aports/." "$work_root/aports/"
chown -R "$builder:$builder" "$work_root" "$output_root"

builder_home="$(getent passwd "$builder" | cut -d: -f6)"
install -d -o "$builder" -g "$builder" "$builder_home/.abuild"

if ! find "$builder_home/.abuild" -maxdepth 1 -name '*.rsa' -print -quit | grep -q .; then
    su "$builder" -s /bin/ash -c 'abuild-keygen -a -n'
fi
private_key="$(find "$builder_home/.abuild" -maxdepth 1 -name '*.rsa' -print -quit)"
[[ -n "$private_key" ]] || die 'abuild signing key was not created'
cp "$private_key.pub" /etc/apk/keys/

cat > "$builder_home/.abuild/abuild.conf" <<EOF
PACKAGER="ATL-WSL build <noreply@example.invalid>"
PACKAGER_PRIVKEY="$private_key"
REPODEST="$output_root"
JOBS="${JOBS:-$(getconf _NPROCESSORS_ONLN)}"
EOF
chown "$builder:$builder" "$builder_home/.abuild/abuild.conf"
chmod 0600 "$builder_home/.abuild/abuild.conf" "$private_key"

refresh_repository() {
    find "$output_root" -type f -name '*.apk' ! -path "$repository_dir/*" -exec cp -f {} "$repository_dir/" \;
    if find "$repository_dir" -maxdepth 1 -name '*.apk' -print -quit | grep -q .; then
        apk index -o "$repository_dir/APKINDEX.tar.gz" "$repository_dir"/*.apk
        abuild-sign -k "$private_key" "$repository_dir/APKINDEX.tar.gz"
    fi
}

if ! grep -Fqx "$repository_dir" /etc/apk/repositories; then
    printf '%s\n' "$repository_dir" >> /etc/apk/repositories
fi

build_one() {
    local package="$1"
    log "Building $package for $arch"
    su "$builder" -s /bin/ash -c "cd '$work_root/aports/$package' && abuild -r"
    refresh_repository
}

build_one bionic_translation
build_one art_standalone
build_one libopensles-standalone
build_one android-translation-layer

find "$output_root" -type f -name '*.apk' -exec sha256sum {} + | sort -k2 > "$output_root/SHA256SUMS"
printf '%s\n' "$arch" > "$output_root/ARCH"
log "Package repository ready at $repository_dir"
