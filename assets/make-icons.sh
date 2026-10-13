#!/bin/sh
# Renders app-icon.svg into the platform icons, which are committed:
#   apps/macos/Resources/AppIcon.icns  the 1024 canvas, macOS margin included
#   assets/app-icon.ico                cropped to the plate, 16 to 256 px
# Rasterizing SVG (masks, gradients) needs a browser engine: headless Google
# Chrome, with a throwaway profile. Run it again after changing the SVG.
set -e
cd "$(dirname "$0")"
CHROME="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# <svg> <png>: a 1024 × 1024 render with a transparent background.
render() {
    "$CHROME" --headless=new --disable-gpu --hide-scrollbars --user-data-dir="$TMP/profile" \
        --default-background-color=00000000 --window-size=1024,1024 \
        --screenshot="$2" "file://$1" >/dev/null 2>&1
}

# <png> <size> <out>
scale() {
    sips -z "$2" "$2" "$1" --out "$3" >/dev/null
}

# macOS: an .iconset of every size, then iconutil.
render "$PWD/app-icon.svg" "$TMP/mac.png"
SET="$TMP/AppIcon.iconset"
mkdir "$SET"
for size in 16 32 128 256 512; do
    scale "$TMP/mac.png" $size "$SET/icon_${size}x${size}.png"
    scale "$TMP/mac.png" $((size * 2)) "$SET/icon_${size}x${size}@2x.png"
done
iconutil -c icns -o ../apps/macos/Resources/AppIcon.icns "$SET"

# Windows: no margin around the plate.
sed 's/viewBox="0 0 1024 1024"/viewBox="100 100 824 824"/' app-icon.svg > "$TMP/windows.svg"
render "$TMP/windows.svg" "$TMP/windows.png"
SIZES="16 20 24 32 40 48 64 256"
for size in $SIZES; do
    scale "$TMP/windows.png" $size "$TMP/win-$size.png"
done
# An .ico is a directory of PNG images.
python3 - "$TMP" app-icon.ico $SIZES <<'PY'
import struct, sys
tmp, out, sizes = sys.argv[1], sys.argv[2], [int(s) for s in sys.argv[3:]]
images = [open(f"{tmp}/win-{s}.png", "rb").read() for s in sizes]
offset = 6 + 16 * len(sizes)
data = struct.pack("<HHH", 0, 1, len(sizes))
for size, png in zip(sizes, images):
    side = 0 if size >= 256 else size
    data += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(png), offset)
    offset += len(png)
open(out, "wb").write(data + b"".join(images))
PY
echo "apps/macos/Resources/AppIcon.icns, assets/app-icon.ico"
