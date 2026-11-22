# SamsungCONNECT security and reliability review

Review date: October 22, 2026. Scope: the supplied Kimi-generated C# source and 25,600-byte executable, followed by the revised version 2.0 source and local build. The original source is retained at [review/original/SamsungCONNECT.cs](../review/original/SamsungCONNECT.cs).

## Assessment

No obvious credential theft, telemetry, downloader, application-owned network client, or destructive file operation was found in the original source. The original executable matched a fresh local compilation after normalizing only normal build metadata (the PE timestamp and module GUID). A Microsoft Defender custom scan of the original repository copy completed with no SamsungCONNECT detection reported.

Those observations are not a certification of safety. The original app had meaningful privacy and availability defects and should not be treated as a reliably verified TV-only utility. Version 2.0 addresses the specific issues below. Live pairing, display changes, rollback, and loss-of-connection recovery still require testing on the actual TV and drivers.

## Findings and changes

### High: unrelated cast-device fallback can disclose the desktop

**Original:** `FindTvElement`, lines 318–334, picks the first qualifying list item or button when the configured TV name is absent. A different nearby cast receiver could therefore be activated. A generic default name also cannot establish physical device identity.

**Changed:** `CastAutomation.TryConnect` accepts only matching, enabled, visible list items/buttons, rejects multiple candidates including identical labels, and has no arbitrary-device or physical-click fallback. It accepts panes only from recognized Windows shell executable locations. If the shell cannot be identified or automated, the user must choose the TV in Windows Cast.

**Remaining:** A receiver can advertise a misleading name. Name matching is not cryptographic authentication. Confirm the intended physical TV during pairing and avoid automatic sharing with untrusted receivers.

### High: screen switching bypasses connection verification

**Original:** `Toggle` at lines 478–481 and the TV-only menu handler at lines 493–495 call external mode directly. `ConnectFlow`, line 150, treats any second display as the intended TV. A wired monitor or unrelated indirect display can satisfy that shortcut.

**Changed:** Every app-controlled TV switch runs the same guarded connection path. The selected target must be active, match the saved name, and be identified by Windows as Miracast. The target is rechecked immediately before switching. Other active external displays cause refusal. The revised app supplies only the verified TV path to `SetDisplayConfig`, validates the request, checks the return value, then verifies the resulting active target list. It does not ask Windows to select a remembered external monitor.

### High availability: watchdog can misclassify TV-only mode

**Original:** `WatchdogTick`, lines 419–434, assumes at least two active displays or an adapter string containing `Indirect`/`Mira`. In TV-only mode one display is expected; the adapter description need not contain those substrings. This can cause unwanted rollback. Another display can also mask the intended TV disappearing.

**Changed:** The watchdog tracks the verified target key (adapter/target identity plus monitor device path) and expects exactly that TV to remain active. Two consecutive missing, changed, or failed queries trigger recovery. A worker-side 15-second confirmation deadline also restores the laptop unless the user keeps the new layout. Recovery does not depend on the UI countdown timer.

### High availability: entering TV mode can crash on the tray tooltip

**Original:** The `SetMode` tooltip at lines 463–465 exceeds .NET Framework's 63-character limit and can throw after switching displays. The `Ui` catch then retries the same failing action, potentially on the wrong thread.

**Changed:** Both tray tooltips are short. The original exception was reproduced in the regression harness without running the original app or switching displays. The program attempts recovery before reporting an unhandled application exception.

Reference: [Microsoft's NotifyIcon.Text limits](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon.text?view=netframework-4.8.1).

### Medium: executable lookup depends on the launch environment

**Original:** `WifiLooksConnected`, line 387, launches `netsh` without an absolute executable path. A hostile launch environment can redirect unqualified process lookup to an unintended executable. This is a local attack precondition, not evidence of remote code execution or malicious intent.

**Changed:** The revised app launches no external commands. Display queries/switches use Windows APIs; the fragile `netsh` text check is removed. Native DLL lookup is restricted to System32. It neither requests elevation nor weakens Windows security settings.

References: [ProcessStartInfo.UseShellExecute](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-diagnostics-processstartinfo-useshellexecute), [Windows process creation rules](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw).

### Medium availability: unsafe UI dispatch and overlapping operations

**Original:** The UI marshaler changes when menus are rebuilt (`RebuildMenu`, lines 528–534). A control without a created handle can incorrectly report that no invocation is required. `Ui`, lines 588–596, executes the action on the caller after an exception. `flowRunning` is a non-atomic check/set, disconnect is not tracked as a busy flow, and the recovery hotkey is ignored during connection.

**Changed:** A stable form handle is created on the UI thread before workers start. Background UI updates use that handle and are never retried on a worker after shutdown. An atomic connection guard prevents duplicate attempts. Connection cancellation sources are synchronized and disposed. Topology changes and emergency restore share a lock; cancellation is checked inside the lock immediately before applying TV mode. Recovery remains available during connection and disconnection first cancels the attempt.

Reference: [Control.InvokeRequired and controls without handles](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.invokerequired?view=netframework-4.8.1).

### Medium reliability: audio detection overclaims and native layout mismatch

**Original:** The comments describe confirmed TV speakers, but the code only searches legacy output-device names and never sets or checks the default output. `WAVEOUTCAPS.szPname`, line 738, uses 64 characters; native `WAVEOUTCAPSW` uses 32. The device ID parameter also needs pointer-sized marshaling on 64-bit Windows.

**Changed:** The revised structure is 84 bytes with a 32-character name, and the API receives a `UIntPtr` device ID. UI and documentation explicitly call audio detection advisory and explain manual selection. Missing audio never bypasses display verification, but does not prevent using the TV as a display.

References: [WAVEOUTCAPSW](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/ns-mmeapi-waveoutcapsw), [Microsoft-generated native bindings showing the 32-character field](https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/Media/Audio/struct.WAVEOUTCAPSW.html).

### Additional corrected reliability issues

- The original Wi-Fi question uses OK/Cancel but checks for `DialogResult.No`; Cancel therefore does not stop the connection flow. The revised app removes that unreliable English-text Wi-Fi probe and provides a working cancellation action throughout.
- The original connection path does not wait for display-switch process completion or verify the resulting topology. The revised synchronous API calls check status and independently inspect the result.
- The original drawn icons retain native handles and bitmaps. Revised icon creation clones the icon and releases the native handle and temporary bitmap.
- The original disconnect operation clicks a generic Disconnect button. Version 2.0 restores the laptop and asks the user to select the intended receiver in Windows' own pane.
- TV-name settings now reject empty, oversized, and control-character values. Registry write failures are visible in the activity box.

## Revised application permissions and data handling

- Runs with the current user's normal privileges; no administrator manifest, service, scheduled task, driver installation, or firewall changes.
- Registers Ctrl+Alt+T, draws a tray icon, reads display/audio information, and uses Windows UI Automation and Win+K input simulation.
- May change the active Windows display layout when a user launches/connects or enables automatic sign-in connection. Windows manages the wireless connection and any pairing records.
- Writes only its `TVName` preference and an optional quoted, per-user startup entry, as described in the README. The activity log is memory-only.
- Source contains no web client, secret/API key, credential access, analytics, or auto-update mechanism.

## Validation and limits

The C# 5/.NET Framework build passed. The included automated harness checks safety decisions, native structure sizes, read-only display/audio enumeration, the original tooltip regression, and ready/active/error/scaled/confirmation UI rendering. These are not end-to-end Miracast tests or simulated controller tests.

The published executable is unsigned. Checksums identify the published bytes but cannot replace publisher authentication if both a file and its checksum are compromised. The original local files were not modified.

The original source hash, original binary hash, and binary comparison details are in [BUILDING.md](BUILDING.md). The root [SHA256SUMS.txt](../SHA256SUMS.txt) identifies the revised distribution. Defender results for the final build are recorded in [VALIDATION.md](VALIDATION.md).

Remaining limitations:

- A name and Windows-reported device path are not proof of the receiver's identity.
- Windows shell automation is version/language dependent, and UI Automation providers may block. Manual Cast remains available; recovery runs separately from discovery.
- Drivers must expose a matching name and Miracast base connector type. Otherwise the app refuses automatic TV-only mode.
- The app does not intercept or sandbox Windows' initial casting behavior; Windows may initially mirror or extend during pairing.
- A process kill, OS crash, graphics-driver hang, closed/unavailable internal panel, or a failed Windows restore call can defeat software recovery. Use Win+P > PC screen only and test recovery on your hardware.
- Physical TV pairing, successful live TV-only application, timeout rollback, watchdog behavior during a real link loss, and actual speaker playback remain unverified.

The outcome is a more constrained and testable implementation, with the remaining hardware and operating-system assumptions stated explicitly.

## Version 3.0 addendum (November 22, 2026)

Version 3.0 adds new attack surface. Each new surface is listed with the limits that apply to it:

- **Network (logo download):** HTTPS only, including after redirects (at most 5). Pages are capped at 1.5 MB, manifests at 300 KB and images at 2 MB. Requests time out after 12 seconds. Images are decoded by Windows' own codecs, size-checked (≤4096 px, near-square), scaled to ≤256 px and **re-encoded as PNG** before caching. Cached files are named by a hash of the service id, never by remote names. No cookies, accounts or user data are sent. Failed sites are retried at most every 3 days.
- **Launching the browser:** URLs come from the built-in catalog or from a user-typed address. A typed address must parse as an absolute `http`/`https` URI with a dotted host. Only `Uri.AbsoluteUri` (percent-encoded, so it can't contain a `"`) is placed in the `--app="…"` argument, so a tile can't inject extra Chrome switches (tested). The browser path comes from the `App Paths` registry key or the standard install folder.
- **Window automation:** after launch, the app looks only for *new* visible `Chrome_WidgetWin_1` windows owned by the chosen browser's process. It sends F11 only if that window is in the foreground and not yet full screen. Opening the next tile posts `WM_CLOSE` to the previous app window only.
- **Power settings:** `SetThreadExecutionState` is released by Windows when the process exits. The screen saver is paused with `SystemParametersInfo` without `SPIF_UPDATEINIFILE`, so it is session-only. The lid action is written to the **active power plan only while in TV mode**, after saving the original AC/DC values and plan GUID to `HKCU\Software\SamsungCONNECT`. It is restored on returning to the laptop, on exit, on sign-out, and at the next start after a crash.
- **Privileges:** unchanged. The app runs as the invoking user (`asInvoker`) with no elevation.
- **Residual risks:** a malicious or compromised streaming website could serve a hostile image to the Windows imaging codecs. That is the same exposure as a browser tab, but outside the browser sandbox. Use **Refresh logos** only when you want new icons, or delete the logo cache to stop showing them. On machines that use a corporate "machine inactivity limit" policy, the lock is enforced by policy and is not prevented.
