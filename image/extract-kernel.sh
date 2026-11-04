#!/bin/sh
# extract-kernel.sh <vmlinuz> <output>: writes the uncompressed kernel image Virtualization.framework boots
# (raw ARM64 Image). Handles raw images, plain gzip, and EFI zboot images (gzip or zstd payload).
set -eu
in="$1"; out="$2"

le32() { od -A n -t u4 -j "$2" -N 4 "$1" | tr -d ' '; }

magic="$(dd if="$in" bs=1 skip=4 count=4 2>/dev/null)"
if [ "$magic" = "zimg" ]; then
  offset="$(le32 "$in" 8)"; size="$(le32 "$in" 12)"
  comp="$(dd if="$in" bs=1 skip=24 count=8 2>/dev/null | tr -d '\0')"
  tail -c +"$((offset + 1))" "$in" | head -c "$size" > "$out.payload"
  case "$comp" in
    gzip) gzip -dc "$out.payload" > "$out" ;;
    zstd) zstd -qdc "$out.payload" > "$out" ;;
    *) echo "unsupported zboot compression: $comp" >&2; exit 1 ;;
  esac
  rm -f "$out.payload"
elif [ "$(od -A n -t x1 -N 2 "$in" | tr -d ' ')" = "1f8b" ]; then
  gzip -dc "$in" > "$out"
else
  cp "$in" "$out"
fi
