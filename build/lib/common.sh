#!/usr/bin/env bash

set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=../../config/versions.env
# shellcheck disable=SC1091
. "$PROJECT_ROOT/config/versions.env"

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

log() {
    printf '\n==> %s\n' "$*"
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

safe_remove_project_tree() {
    local requested="$1"
    local target
    local project
    require_command realpath
    target="$(realpath -m -- "$requested")"
    project="$(realpath -m -- "$PROJECT_ROOT")"
    case "$target" in
        "$project"/*) rm -rf -- "$target" ;;
        *) die "refusing to remove a path outside the project: $target" ;;
    esac
}

normalize_arch() {
    case "$1" in
        x86_64|amd64|AMD64) printf 'x86_64\n' ;;
        aarch64|arm64|ARM64) printf 'aarch64\n' ;;
        *) die "unsupported architecture: $1" ;;
    esac
}

windows_arch() {
    case "$1" in
        x86_64) printf 'x64\n' ;;
        aarch64) printf 'arm64\n' ;;
        *) die "unsupported architecture: $1" ;;
    esac
}

sha256_file() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

verify_sha256() {
    local path="$1"
    local expected="$2"
    local actual
    actual="$(sha256_file "$path")"
    [[ "$actual" == "$expected" ]] || die "SHA-256 mismatch for $path: expected $expected, got $actual"
}

require_alpine_324() {
    [[ -f /etc/alpine-release ]] || die 'this build step must run inside Alpine Linux'
    local version
    version="$(cut -d. -f1,2 /etc/alpine-release)"
    [[ "$version" == '3.24' ]] || die "Alpine 3.24 is required; found $(cat /etc/alpine-release)"
}
