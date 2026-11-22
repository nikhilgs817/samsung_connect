# Changelog

## 3.0.0 — November 22, 2026

The utility is now a complete TV app with a two-step flow: connect, then watch.

- **HDMI support** alongside wireless. The app detects exactly one wired external screen and switches to it with the same 15-second keep/revert confirmation and drop-out watchdog as wireless.
- **TV home screen:** full-screen launcher with big tiles for 23 streaming and music services popular in India (Netflix, Prime Video, JioHotstar, YouTube, ZEE5, SonyLIV, JioTV, aha, regional and music services). Includes custom "Add a site" tiles and hide/remove.
- Services open as **full-screen Chrome app windows** using the normal Chrome profile. Opening a new one closes the previous one. Edge and the default browser are fallbacks.
- **Real logos** are downloaded once from each service's own site and cached as re-encoded PNGs. Styled brand badges are used when a download fails.
- **Always awake:** no sleep, screen-off, screen saver or lock while the app runs. In TV mode, **closing the lid does nothing**, with crash-safe restore of the user's lid setting.
- **Neumorphic UI** in light, dark and follow-Windows modes. The UI is rewritten in WPF and scales to the screen, so it reads well on a TV.
- **TV-style navigation:** arrow keys and Enter everywhere, mouse hover moves the same focus, and Esc closes dialogs. Ctrl+Alt+H brings the home screen forward, and Ctrl+Alt+T toggles laptop/TV using the last method.
- Starting the app a second time brings the running window forward. An activity log is written to `%LOCALAPPDATA%\SamsungCONNECT\activity.log`.
- Source split into `src/*.cs`. Tests grew from 26 to 60 checks and now render every screen in both themes.
- Behaviour change: the app no longer auto-connects on every start. Turn on **Connect automatically when the app opens** in Settings. `--no-connect` and `--tray` still work, and `--connect` forces an auto-connect.

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
