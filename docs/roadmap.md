# Roadmap

What's planned but not built yet.

## macOS

- A settings window. For now, settings live in the menu bar menu.
- A pairing step and animation in the first-run welcome.
- Storing the private key in the Keychain instead of `identity.pem`.
- Developer ID signing and notarization, so the app runs on other Macs without Gatekeeper warnings. `scripts/release.sh` signs ad-hoc for now.

## Windows

- Code-signing the installer (`apps/windows/installer`), so SmartScreen doesn't block it.
- Protecting the private key in `identity.pem` with DPAPI.
- A "sending" hint when you drag onto the island while a send is in progress. For now, that drag is ignored.
- Testing on real hardware with a second monitor, at 150% scaling, with monitors at different scaling, and with a Mac on the other end.

## Core

- Sending to LocalSend devices that require a PIN.
