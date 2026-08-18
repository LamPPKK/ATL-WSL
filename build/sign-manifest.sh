#!/usr/bin/env bash

set -euo pipefail
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=build/lib/common.sh
source "${SCRIPT_DIR}/lib/common.sh"

[ "$#" -eq 3 ] || die "Usage: $0 <manifest.json> <ed25519-private-key.pem> <signature.txt>"
MANIFEST=$1
PRIVATE_KEY=$2
OUTPUT=$3
require_command openssl
require_command base64
[ -s "${MANIFEST}" ] || die "Manifest is missing"
[ -s "${PRIVATE_KEY}" ] || die "Ed25519 signing key is missing"

signature_bin=$(mktemp)
trap 'rm -f "${signature_bin}"' EXIT
openssl pkeyutl -sign -rawin -inkey "${PRIVATE_KEY}" -in "${MANIFEST}" -out "${signature_bin}"
base64 < "${signature_bin}" | tr -d '\r\n' > "${OUTPUT}"
printf '\n' >> "${OUTPUT}"
log "Detached Ed25519 signature written to ${OUTPUT}"
