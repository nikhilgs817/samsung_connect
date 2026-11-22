using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SamsungCONNECT
{
    // Keeps the laptop awake and unlocked for as long as the app runs.
    // Nothing here is persisted: if the app crashes, Windows drops the request automatically.
    static class KeepAwake
    {
        const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;
        const uint SpiGetScreenSaveActive = 0x10, SpiSetScreenSaveActive = 0x11;
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] static extern bool SystemParametersSet(uint action, uint param, IntPtr value, uint winIni);
        static bool active, screenSaverWasOn;

        // Must be called on the UI thread: the request lives as long as the calling thread.
        internal static void Start()
        {
            if (active) return;
            SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
            int on = 0;
            if (SystemParametersInfo(SpiGetScreenSaveActive, 0, ref on, 0) && on != 0)
            {
                // Session-only change (no SPIF_UPDATEINIFILE): the screen saver, and its lock, pause while the app runs.
                screenSaverWasOn = SystemParametersSet(SpiSetScreenSaveActive, 0, IntPtr.Zero, 0);
            }
            active = true;
        }
        // Re-assert periodically; some drivers and power plans drop long-held requests.
        internal static void Refresh()
        {
            if (active) SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
        }
        internal static void Stop()
        {
            if (!active) return;
            SetThreadExecutionState(EsContinuous);
            if (screenSaverWasOn) SystemParametersSet(SpiSetScreenSaveActive, 1, IntPtr.Zero, 0);
            screenSaverWasOn = false; active = false;
        }
    }

    // While the TV is in use, closing the lid does nothing (instead of sleeping).
    // The original values are written to the registry first, so a crash or power cut is undone on next start.
    static class LidPolicy
    {
        static Guid subButtons = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        static Guid lidAction = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        static readonly object gate = new object();

        static Guid ActiveScheme()
        {
            IntPtr pointer;
            uint result = PowerGetActiveScheme(IntPtr.Zero, out pointer);
            if (result != 0) throw new InvalidOperationException("Cannot read the active power plan (" + result + ").");
            try { return (Guid)Marshal.PtrToStructure(pointer, typeof(Guid)); }
            finally { LocalFree(pointer); }
        }

        // Returns a message for the activity log.
        internal static string Apply()
        {
            lock (gate)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Settings.Path))
                {
                    if (key.GetValue("LidScheme") != null) return "Lid already set to 'do nothing'.";
                    Guid scheme = ActiveScheme();
                    uint ac, dc;
                    if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subButtons, ref lidAction, out ac) != 0 ||
                        PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subButtons, ref lidAction, out dc) != 0)
                        return "This PC has no lid setting; nothing changed.";
                    if (ac == 0 && dc == 0) return "Lid is already set to 'do nothing'.";
                    // Save first, change second.
                    key.SetValue("LidAC", (int)ac, RegistryValueKind.DWord);
                    key.SetValue("LidDC", (int)dc, RegistryValueKind.DWord);
                    key.SetValue("LidScheme", scheme.ToString());
                    if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subButtons, ref lidAction, 0) != 0 ||
                        PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subButtons, ref lidAction, 0) != 0)
                    {
                        Restore();
                        return "Windows did not allow changing the lid action. Keep the lid open.";
                    }
                    PowerSetActiveScheme(IntPtr.Zero, ref scheme);
                    return "You can close the lid: it does nothing while the TV is in use.";
                }
            }
        }

        // Safe to call any time; does nothing unless a saved original exists.
        internal static string Restore()
        {
            lock (gate)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Settings.Path, true))
                    {
                        string saved = key == null ? null : key.GetValue("LidScheme") as string;
                        if (saved == null) return null;
                        Guid scheme = new Guid(saved);
                        uint ac = (uint)(int)key.GetValue("LidAC", 1), dc = (uint)(int)key.GetValue("LidDC", 1);
                        PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subButtons, ref lidAction, ac);
                        PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subButtons, ref lidAction, dc);
                        try { Guid active = ActiveScheme(); if (active == scheme) PowerSetActiveScheme(IntPtr.Zero, ref scheme); }
                        catch { /* The values are written; Windows applies them when the plan is next activated. */ }
                        key.DeleteValue("LidScheme", false); key.DeleteValue("LidAC", false); key.DeleteValue("LidDC", false);
                        return "Lid action restored to your normal setting.";
                    }
                }
                catch (Exception ex) { return "Could not restore the lid action: " + ex.Message; }
            }
        }
    }
}
