# Changelog

## 2.0.0 — October 22, 2026

- Added a desktop control window with connection stages, an activity box, editable TV settings, startup controls, and persistent laptop/cancel and exit actions.
- Kept single-file executable distribution, framework-only dependencies, a tray menu, and Ctrl+Alt+T.
- Removed arbitrary cast-device selection, physical click fallback, and external command launching.
- Added named Miracast-target verification and a direct request for only the verified display path, rather than a generic external-monitor switch.
- Added a 15-second keep/revert confirmation and target-specific connection monitoring.
- Corrected UI-thread dispatch, connection synchronization, cancellation, native audio layouts, tray tooltip lengths, and icon cleanup.
- Made complete disconnection an explicit choice in Windows Cast after restoring the laptop.
- Added installation/usage/build documentation, security findings, validation limits, original-source provenance, and safety regression checks.

## Original Kimi build

The supplied original source is preserved in `review/original/SamsungCONNECT.cs`. Original local source and executable files remain unchanged. The repository root executable is the revised version.
