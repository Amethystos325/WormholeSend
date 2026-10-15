#!/bin/sh
# Builds the release disk image, build/WormholeSend-<version>-macos-universal.dmg:
# the app for arm64 and x86_64, signed ad-hoc, next to a link to /Applications
# to drag it onto. The version is Info.plist's CFBundleShortVersionString.
#
#     ./scripts/release.sh
#
# Ad-hoc, not with a certificate: an Apple Development signature carries the
# developer's name and email, and isn't trusted on other Macs anyway. Without
# Developer ID and notarization, Gatekeeper blocks the first launch until it is
# allowed in System Settings › Privacy & Security.
set -e
cd "$(dirname "$0")/.."
WHS_SIGN_IDENTITY=- ./scripts/bundle.sh release --universal
APP="build/Wormhole Send.app"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' Resources/Info.plist)"
DMG="build/WormholeSend-$VERSION-macos-universal.dmg"
STAGING="build/dmg"
rm -rf "$STAGING" "$DMG"
mkdir -p "$STAGING"
ditto "$APP" "$STAGING/Wormhole Send.app"
ln -s /Applications "$STAGING/Applications"
hdiutil create -quiet -volname "Wormhole Send $VERSION" -srcfolder "$STAGING" -format UDZO "$DMG"
rm -rf "$STAGING"
echo "$DMG"
shasum -a 256 "$DMG" | awk '{print toupper($1)}'
