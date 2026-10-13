#!/bin/sh
# Builds the app and wraps it into "build/Wormhole Send.app" (ad-hoc signed).
set -e
cd "$(dirname "$0")/.."
CONFIG="${1:-release}"
swift build -c "$CONFIG"
APP="build/Wormhole Send.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$(swift build -c "$CONFIG" --show-bin-path)/WormholeSend" "$APP/Contents/MacOS/"
cp Resources/Info.plist "$APP/Contents/"
codesign --force --sign - "$APP" >/dev/null
echo "$APP"
