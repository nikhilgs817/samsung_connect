# Validation record

## Version 3.0 — November 22, 2026

### Completed

- Source compiled with the Windows .NET Framework compiler (C# 5, AnyCPU, optimized, framework-only references, embedded manifest).
- Automated harness: **60 checks passed**. They cover the wireless and HDMI safety rules, the watchdog rule, native layouts, the streaming catalog, custom-tile address validation, saved-tile round trips, read-only display enumeration, and rendering of all five screens in light and dark mode. The rendered screens were inspected visually.
- Logo download check (`build.ps1 -Logos`) from the build machine: 15 of 23 services returned a usable icon. ZEE5, SonyLIV and JioTV refuse automated or non-Indian requests, and a few others returned no usable icon. Those tiles show styled brand badges, and results on an Indian connection may differ.
- Smoke run of the real executable with `--no-connect`: it started, created the tray icon and hotkeys, wrote the activity log, and a second launch handed off to the first instance and exited. The screen-saver flag was checked before and after. No display, power or startup setting was changed.
- Production download is a single **111,616-byte EXE**, and it is **not Authenticode-signed**. Checksums are in [SHA256SUMS.txt](../SHA256SUMS.txt).

### Not performed

- Physical Samsung TV pairing (wireless) or HDMI switching on the user's laptop. The build machine is a desktop with three wired monitors and no laptop panel.
- Live lid-close behaviour, power-plan restore after a real crash, or screen-lock prevention over long idle periods.
- Chrome full-screen behaviour on the user's machine, including the F11 fallback when Chrome ignores `--start-fullscreen` for an already-running browser.
- A full accessibility audit or every DPI and language configuration.

Follow the [hardware validation steps](BUILDING.md#hardware-validation-before-relying-on-tv-mode) before relying on unattended switching.

## Version 2.0 — October 22, 2026

- Original source reviewed for privacy, command execution, persistence, UI Automation, display-switch safety, audio interop, and background/UI-thread handling.
- Original 25,600-byte executable confirmed as a Windows PE matching a fresh compilation of the supplied source after normalizing only the PE timestamp and module GUID.
- Original and revised executables scanned with Microsoft Defender; no detection was reported. This is a local scan result, not a guarantee.
- Automated harness: 26 checks passed. Physical TV pairing, live switching, connection-loss recovery and startup were not tested on the user's hardware.
