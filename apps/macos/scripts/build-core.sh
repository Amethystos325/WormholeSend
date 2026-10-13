#!/bin/sh
# Builds the Rust core (core-ffi) and copies libwormhole.a to Vendor/lib.
#   --universal  arm64 + x86_64, merged with lipo (needs both rustup targets)
set -e
cd "$(dirname "$0")/.."
ROOT="$(cd ../.. && pwd)"
# The app runs on macOS 14+: compile the C and Rust code of the core for it too.
export MACOSX_DEPLOYMENT_TARGET=14.0
mkdir -p Vendor/lib
if [ "$1" = "--universal" ]; then
    cargo build -p wormhole-ffi --release --manifest-path "$ROOT/Cargo.toml" --target aarch64-apple-darwin
    cargo build -p wormhole-ffi --release --manifest-path "$ROOT/Cargo.toml" --target x86_64-apple-darwin
    lipo -create -output Vendor/lib/libwormhole.a \
        "$ROOT/target/aarch64-apple-darwin/release/libwormhole.a" \
        "$ROOT/target/x86_64-apple-darwin/release/libwormhole.a"
else
    cargo build -p wormhole-ffi --release --manifest-path "$ROOT/Cargo.toml"
    cp "$ROOT/target/release/libwormhole.a" Vendor/lib/
fi
