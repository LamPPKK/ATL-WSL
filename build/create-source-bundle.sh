#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
# shellcheck disable=SC1091
. "$SCRIPT_DIR/lib/common.sh"

for command in curl git sha512sum tar; do
    require_command "$command"
done

output_dir="${1:-$PROJECT_ROOT/artifacts/release}"
work_root="$PROJECT_ROOT/.build/source-bundle"
stage="$work_root/ATL-WSL-$ATL_WSL_VERSION-sources"
artifact="$output_dir/ATL-WSL-$ATL_WSL_VERSION-sources.tar.gz"

safe_remove_project_tree "$work_root"
install -d "$stage/project" "$stage/upstream" "$output_dir"

log 'Archiving ATL-WSL project source'
git -C "$PROJECT_ROOT" archive --format=tar HEAD | tar -xf - -C "$stage/project"

fetch_source() {
    local name="$1"
    local url="$2"
    local expected="$3"
    local destination="$stage/upstream/$name"
    curl --fail --location --retry 3 --output "$destination" "$url"
    printf '%s  %s\n' "$expected" "$destination" | sha512sum -c -
}

log 'Downloading and verifying corresponding upstream sources'
fetch_source \
    "android_translation_layer-$ATL_COMMIT.tar.gz" \
    "https://gitlab.com/android_translation_layer/android_translation_layer/-/archive/$ATL_COMMIT/android_translation_layer-$ATL_COMMIT.tar.gz" \
    "3b80154bee9eeacd7c0f06d0aa306a91b27c5aa8bc744cfdf36b97cd59f7d910d464e24ea4d66c219a506991931d3bec7a53bb0437526d71278a13d7160ae37f"
fetch_source \
    "art_standalone-$ART_STANDALONE_COMMIT.tar.gz" \
    "https://gitlab.com/android_translation_layer/art_standalone/-/archive/$ART_STANDALONE_COMMIT/art_standalone-$ART_STANDALONE_COMMIT.tar.gz" \
    "75ef56d63dfc7661a7928191441d4672d612b6a8d27c3957764d324e4f622a42c132c34540f6a89556b1971964df8be2eced0b8a599d5a793ab3039cfb9c48a2"
fetch_source \
    "bionic_translation-$BIONIC_TRANSLATION_COMMIT.tar.gz" \
    "https://gitlab.com/android_translation_layer/bionic_translation/-/archive/$BIONIC_TRANSLATION_COMMIT/bionic_translation-$BIONIC_TRANSLATION_COMMIT.tar.gz" \
    "bf6878d49f711692322c82aaf0acab47138fe850ccab56fe4ac70e6c0878bccc40ef1d1fcca561b7d15c137cbb0c5012bebdfb0baf87e103c97927778f3ec129"
fetch_source \
    "libopensles-standalone-$LIBOPENSLES_STANDALONE_COMMIT.tar.gz" \
    "https://gitlab.com/android_translation_layer/libopensles-standalone/-/archive/$LIBOPENSLES_STANDALONE_COMMIT/libopensles-standalone-$LIBOPENSLES_STANDALONE_COMMIT.tar.gz" \
    "fed4a95ffb4f099d21e2d8d9463b4fb1ee5a6324c278a857e4344a693fdb6c2c62657637ae216ca4d6c86562db6583c39a0384fe85cd8957c502eef40a3ce029"

cp "$PROJECT_ROOT/config/versions.env" "$stage/SOURCE-VERSIONS"
(
    cd "$stage"
    find . -type f -print0 | sort -z | xargs -0 sha256sum
) > "$work_root/SHA256SUMS"
mv "$work_root/SHA256SUMS" "$stage/SHA256SUMS"

source_date_epoch="${SOURCE_DATE_EPOCH:-$(git -C "$PROJECT_ROOT" log -1 --format=%ct)}"
tar --numeric-owner --owner=0 --group=0 --sort=name --mtime="@$source_date_epoch" --clamp-mtime \
    -C "$work_root" -czf "$artifact" "$(basename "$stage")"
sha256sum "$artifact" > "$artifact.sha256"
log "Source bundle ready: $artifact"
