#!/usr/bin/env bash
# Build firmware variants and pack each into a zip file for a release.
#
#   firmware/build-release.sh <version> <output folder> [xiao-wifi|xiao-uvc|xiao-uvc-multimode ...]
#
# Without a variant, it builds all three. The build folders stay in <output folder>/build/<variant>.
#
# CI runs this in the ESP-IDF image pinned in .github/workflows/build.yml and firmware.yml. Run it in the same image to
# get the same files byte for byte, wherever the repository is checked out.
set -euo pipefail

usage="usage: $0 <version> <output folder> [xiao-wifi|xiao-uvc|xiao-uvc-multimode ...]"
if [ $# -lt 2 ]; then
  echo "$usage" >&2
  exit 2
fi
version=$1
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
mkdir -p "$2"
output=$(cd "$2" && pwd)
shift 2
variants=("$@")
if [ ${#variants[@]} -eq 0 ]; then
  variants=(xiao-wifi xiao-uvc xiao-uvc-multimode)
fi
if ! command -v idf.py >/dev/null; then
  . "$IDF_PATH/export.sh" >/dev/null
fi
# Zip entries get the commit time, so that a rebuild of the same commit packs the same bytes.
export SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git -c safe.directory='*' -C "$root" log -1 --format=%ct)}

# package <project> <package> <app image> [extra sdkconfig defaults, relative to the project]
package() {
  local project=$1 name=mouthtrap-firmware-$2-$version app=$3 defaults="sdkconfig.defaults${4:+;$4}"
  local build=$output/build/$2 stage=$output/stage/$2
  rm -rf "$build" "$stage"
  idf.py -C "$root/firmware/$project" -B "$build" -D SDKCONFIG="$build/sdkconfig" \
    -D SDKCONFIG_DEFAULTS="$defaults" -D PROJECT_VER="$version" build
  mkdir -p "$stage/bootloader" "$stage/partition_table"
  cp "$build/bootloader/bootloader.bin" "$stage/bootloader/"
  cp "$build/partition_table/partition-table.bin" "$stage/partition_table/"
  cp "$build/$app.bin" "$build/flash_args" "$root/firmware/requirements.txt" "$root/LICENSE" "$stage/"
  if [ "$project" = xiao-uvc ]; then
    cp "$root/firmware/xiao-uvc/LICENSE" "$stage/LICENSE-Apache-2.0"
  fi
  if [ "$project" = xiao-wifi ]; then
    cp "$root/firmware/xiao-wifi/provision.py" "$stage/"
  fi
  python "$root/tools/release/pack.py" "$stage" "$output/$name.zip" --prefix "$name"
  rm -rf "$stage"
}

for variant in "${variants[@]}"; do
  case $variant in
    xiao-wifi) package xiao-wifi xiao-wifi mouthtrap_xiao_wifi ;;
    xiao-uvc) package xiao-uvc xiao-uvc mouthtrap_xiao_uvc ;;
    xiao-uvc-multimode) package xiao-uvc xiao-uvc-multimode mouthtrap_xiao_uvc sdkconfig.quality ;;
    *) echo "unknown variant: $variant" >&2; echo "$usage" >&2; exit 2 ;;
  esac
done
rmdir "$output/stage" 2>/dev/null || true
