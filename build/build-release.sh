#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
# shellcheck disable=SC1091
. "$SCRIPT_DIR/lib/common.sh"

arch="$(normalize_arch "${1:-$(uname -m)}")"
"$SCRIPT_DIR/build-packages.sh" "$arch"
"$SCRIPT_DIR/build-runtime-bundle.sh" "$arch"
"$SCRIPT_DIR/build-rootfs.sh" "$arch"
