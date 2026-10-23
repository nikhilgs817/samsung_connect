# Validation record

Date: October 22, 2026 (America/New_York).

## Completed

- Original source reviewed for privacy, command execution, persistence, UI Automation, display-switch safety, audio interop, and background/UI-thread handling.
- Original 25,600-byte executable confirmed as a Windows PE. Fresh framework compilation of the supplied source produced the same complete file after normalizing only the parsed PE timestamp and sole 16-byte .NET module GUID. Resource and relocation sections matched as supplied.
- Original and revised executable custom scans completed through Microsoft Defender. Antivirus and real-time protection were enabled; no matching SamsungCONNECT threat detection was reported. This records a local scan result, not a guarantee that the application is harmless.
- Revised source compiled successfully with the Windows .NET Framework compiler, C# 5, AnyCPU, optimization, framework-only references, and the embedded manifest.
- Automated harness: **26 checks passed**. This includes safety policy cases, selected TV path construction, native structure sizes, read-only Windows display enumeration, audio enumeration, and reproduction of the original tray-tooltip exception.
- Ready, TV-mode, error, scaled-bound, and confirmation screens rendered from real WinForms controls. Ready, active, scaled, and confirmation previews were visually inspected. No physical TV was used for the screenshots.
- Production download is a single **41,472-byte EXE**. Source and build files are included for inspection and rebuilding; they are not runtime sidecars.
- The binary is **not Authenticode-signed**. Published checksums are in [SHA256SUMS.txt](../SHA256SUMS.txt).

## Not performed

- Physical Samsung TV pairing or automatic selection on the user's installed Windows Cast pane.
- Live application of the selected TV-only path, physical laptop-panel power verification, or actual TV speaker playback.
- Real connection-loss recovery, timeout rollback, startup-at-sign-in, or hotkey-conflict testing on the user's hardware.
- A complete controller simulation, fuzzing, penetration test, independent review, or exhaustive malware analysis.
- A full accessibility audit or testing of every DPI/Windows-language configuration.

The automated harness never calls the display-switching API. The implementation contains cancellation, post-switch verification, a confirmation deadline, and a watchdog, but their end-to-end hardware behavior remains unverified. Follow the [hardware validation steps](BUILDING.md#hardware-validation-before-relying-on-tv-mode) before relying on unattended switching.
