# Build and test SamsungCONNECT

## Prerequisites

Use Windows with the .NET Framework 4.x compiler and framework UI Automation assemblies. No Visual Studio, .NET SDK, NuGet packages, or installer is needed.

The script first looks for:

```text
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

It falls back to the corresponding `Framework` directory when `Framework64` is unavailable. The UI Automation references come from that framework directory's `WPF` subdirectory.

## Build

Open PowerShell in the repository folder:

```powershell
.\build.ps1
```

If local PowerShell policy blocks a downloaded script, review it first. You can invoke a temporary process without changing machine-wide policy:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Output: `build\SamsungCONNECT.exe`. Building does not replace the published executable at the repository root. After reviewing and validating a build, copy it to the root intentionally and update checksums before distributing it.

The script invokes the framework compiler with C# 5, `-target:winexe`, `-platform:anycpu`, `-optimize+`, the embedded manifest, and these framework references:

- `System.dll`
- `System.Drawing.dll`
- `System.Windows.Forms.dll`
- `WPF\UIAutomationClient.dll`
- `WPF\UIAutomationTypes.dll`
- `WPF\WindowsBase.dll`

The manifest is embedded inside the executable, so it is not a runtime sidecar. The app restricts native DLL resolution to System32 and requests `asInvoker` privileges.

## Automated checks

```powershell
.\build.ps1 -Test
```

The test build selects a separate entry point. It does not run the production controller, change display topology, open Windows Cast, connect to a TV, write app settings, or register a global hotkey. It reads the current display/audio device list and renders transparent, offscreen test windows to PNG files under `build\`.

Checks cover unrelated/wired device rejection, duplicate TV names, active external-monitor conflicts, missing/changed TV identity, the single-active-TV state, native structure sizes, selected-path construction, the original tooltip regression, read-only Windows display enumeration, and UI rendering.

The tests require an interactive Windows display session. Headless runners or Remote Desktop configurations can fail the native display check without indicating a build failure in the application.

The images are render checks, not evidence of a working TV connection. The scaled preview exercises larger control bounds, not every per-monitor DPI or accessibility configuration.

## Compare builds

Framework compiler output contains a changing PE timestamp and module GUID. Rebuilding the same source need not give the same raw SHA-256. A published file's checksum identifies that exact file; it does not independently establish publisher identity.

For the supplied original, a fresh compilation used the original requested framework references and options. The two complete PE files matched after normalizing only the parsed PE `TimeDateStamp` field and the sole 16-byte metadata `#GUID` heap. The native resources and relocation sections matched without normalization.

The original binary SHA-256 was:

```text
061c9d2aa4cefe190d454219bf4040b6b5afcf0253e29db5d17f5aa94dfec8f4
```

The original source SHA-256 was:

```text
1a1ef89c55f2ba6dbcef18139d268e441d8aed54862426c5ffb3ce229a0eba68
```

The original EXE is preserved at its original local location, not redistributed as the current download.

## Hardware validation before relying on TV mode

These steps require the real laptop and TV and remain unverified in the build environment:

1. With the laptop panel open, establish a connection manually through Win+K. Verify picture and audio.
2. Start the revised app with `--no-connect`, save the exact TV name, then connect through the app.
3. Confirm only the intended physical TV receives the desktop. Check a nearby unrelated cast device is never selected.
4. Let the 15-second confirmation expire once. Verify the laptop panel returns.
5. Connect again and keep TV mode. Verify the laptop is no longer an active display and check the physical panel behavior.
6. Press Ctrl+Alt+T while active, while searching, and while waiting for pairing. Verify restoration/cancellation. If Windows finishes pairing after cancellation, disconnect in Win+K.
7. Connect and keep TV mode, then turn the TV off or drop its wireless connection. Verify restoration after roughly two watchdog samples, allowing for driver delays.
8. Check TV sound output manually. Device-name detection alone is insufficient.
9. Test another external monitor, two similar TV names, missing TV permissions, and a TV name that does not match. The app should refuse an unsafe/ambiguous automatic switch.
10. Test exit, sign-in startup, hotkey conflicts, normal/reduced screen sizes, and your actual display scaling settings.

Use Win+P > PC screen only if the app cannot recover. No application timer can guarantee recovery from forced process termination, an OS/graphics-driver failure, or an unavailable internal panel.
