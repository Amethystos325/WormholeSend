#!/bin/sh
# Builds the app and wraps it into "build/Wormhole Send.app", signed with
# $WHS_SIGN_IDENTITY, else the first valid Apple Development certificate in the
# keychain, else ad-hoc. A certificate keeps the app the same to macOS across
# rebuilds, so Full Disk Access and Local Network stay granted; an ad-hoc
# signature is new with every build.
#
#     ./scripts/bundle.sh [debug|release] [--universal] [--install]
#
# --universal builds for arm64 and x86_64 in one binary (needs both rustup
# targets); otherwise it's for this Mac's architecture only.
# --install then copies it to /Applications, quitting the running app first
# and starting the installed one again.
set -e
cd "$(dirname "$0")/.."
CONFIG=release
INSTALL=
UNIVERSAL=
ARCHS=
for arg in "$@"; do
  case "$arg" in
    --install) INSTALL=1 ;;
    --universal) UNIVERSAL=--universal; ARCHS="--arch arm64 --arch x86_64" ;;
    *) CONFIG="$arg" ;;
  esac
done
./scripts/build-core.sh $UNIVERSAL
swift build -c "$CONFIG" $ARCHS
APP="build/Wormhole Send.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$(swift build -c "$CONFIG" $ARCHS --show-bin-path)/WormholeSend" "$APP/Contents/MacOS/"
cp Resources/Info.plist "$APP/Contents/"
cp Resources/AppIcon.icns ../../assets/sounds/*.wav "$APP/Contents/Resources/"
# One folder per language: macOS picks the app's language from these (Language
# in L10n.swift follows it), and reads the Local Network prompt's text there.
cp -R Resources/*.lproj "$APP/Contents/Resources/"
IDENTITY="${WHS_SIGN_IDENTITY:-$(security find-identity -v -p codesigning | awk '/"Apple Development: /{print $2; exit}')}"
codesign --force --sign "${IDENTITY:--}" "$APP" >/dev/null
echo "$APP"
if [ -n "$INSTALL" ]; then
  INSTALLED="/Applications/Wormhole Send.app"
  RUNNING=
  if pkill -x WormholeSend; then
    RUNNING=1
    while pgrep -qx WormholeSend; do sleep 0.2; done
  fi
  rm -rf "$INSTALLED"
  ditto "$APP" "$INSTALLED"
  echo "$INSTALLED"
  if [ -n "$RUNNING" ]; then open "$INSTALLED"; fi
fi
