#!/usr/bin/env bash

set -euo pipefail
[[ "$#" -eq 4 ]] || { echo 'usage: verify-manifest-signature.sh MANIFEST SIGNATURE PRIVATE_KEY PUBLIC_KEY_BASE64' >&2; exit 2; }
manifest=$1
signature=$2
private_key=$3
public_key_base64=$4
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
openssl pkey -in "$private_key" -pubout -out "$work/public.pem"
openssl pkey -in "$private_key" -pubout -outform DER | tail -c 32 > "$work/derived.raw"
printf '%s' "$public_key_base64" | openssl base64 -d -A > "$work/expected.raw"
[[ $(wc -c < "$work/expected.raw") -eq 32 ]] || { echo 'release public key must contain 32 raw Ed25519 bytes' >&2; exit 1; }
cmp "$work/derived.raw" "$work/expected.raw" || { echo 'release private and public keys do not match' >&2; exit 1; }
openssl base64 -d -A -in "$signature" -out "$work/signature.bin"
openssl pkeyutl -verify -rawin -pubin -inkey "$work/public.pem" -sigfile "$work/signature.bin" -in "$manifest" >/dev/null
echo 'Detached Ed25519 manifest signature verified.'
