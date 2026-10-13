#!/bin/sh
# Builds the app and wraps it into "build/Wormhole Send.app" (ad-hoc signed).
set -e
cd "$(dirname "$0")/.."
CONFIG="${1:-release}"
./scripts/build-core.sh
swift build -c "$CONFIG"
APP="build/Wormhole Send.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$(swift build -c "$CONFIG" --show-bin-path)/WormholeSend" "$APP/Contents/MacOS/"
cp Resources/Info.plist "$APP/Contents/"
cp Resources/AppIcon.icns ../../assets/sounds/*.wav "$APP/Contents/Resources/"
# One folder per language: macOS picks the app's language from these (Language
# in L10n.swift follows it), and reads the Local Network prompt's text there.
cp -R Resources/*.lproj "$APP/Contents/Resources/"
codesign --force --sign - "$APP" >/dev/null
echo "$APP"
