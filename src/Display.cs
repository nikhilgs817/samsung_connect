using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SamsungCONNECT
{
    // Pure rules can be tested without opening Cast or changing the real display topology.
    static class SafetyPolicy
    {
        internal static bool ValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64) return false;
            foreach (char c in name) if (char.IsControl(c)) return false;
            return true;
        }
        internal static bool Matches(string value, string name)
        {
            return ValidName(name) && !string.IsNullOrEmpty(value) && value.IndexOf(name.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
        internal static DisplayTarget Select(List<DisplayTarget> targets, string name)
        {
            DisplayTarget selected = null;
            foreach (DisplayTarget target in targets)
            {
                if (!target.Wireless || !Matches(target.Name, name)) continue;
                if (selected != null) throw new InvalidOperationException("Multiple TVs match. Save a more specific TV name.");
                selected = target;
            }
            return selected;
        }
        // HDMI (or USB-C/DisplayPort) TV: exactly one connected external display, and it must be wired.
        internal static DisplayTarget SelectWired(List<DisplayTarget> targets)
        {
            DisplayTarget selected = null;
            foreach (DisplayTarget target in targets)
            {
                if (target.Internal) continue;
                if (selected != null) throw new InvalidOperationException("More than one external display is connected. Unplug the others and keep only the TV.");
                selected = target;
            }
            if (selected != null && selected.Wireless)
                throw new InvalidOperationException("A wireless display is connected. Disconnect it in Win+K before using HDMI.");
            return selected;
        }
        internal static bool CanSwitch(List<DisplayTarget> targets, DisplayTarget selected)
        {
            if (selected == null || !selected.Wireless) return false;
            bool found = false;
            foreach (DisplayTarget target in targets)
            {
                if (target.Key == selected.Key) { if (!target.Wireless) return false; found = true; }
                else if (!target.Internal) return false;
            }
            return found;
        }
        internal static bool IsOnlyTv(List<DisplayTarget> targets, string key)
        {
            return targets.Count == 1 && targets[0].Wireless && targets[0].Key == key;
        }
        // Watchdog rule for either connection type: the one verified display is the only active one.
        internal static bool IsOnlyDisplay(List<DisplayTarget> targets, string key)
        {
            return key != null && targets.Count == 1 && !targets[0].Internal && targets[0].Key == key;
        }
    }

    sealed class DisplayTarget
    {
        internal string Name, Key;
        internal bool Wireless, Internal;
        internal Native.PathInfo Path;
    }

    static class CastAutomation
    {
        internal static bool TryConnect(string name)
        {
            AutomationElement pane = FindPane(); if (pane == null) return false;
            try
            {
                List<AutomationElement> candidates = new List<AutomationElement>();
                AutomationElementCollection items = pane.FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                foreach (AutomationElement item in items)
                {
                    string label = item.Current.Name ?? "";
                    if (!SafetyPolicy.Matches(label, name) || !item.Current.IsEnabled || item.Current.IsOffscreen ||
                        label.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    candidates.Add(item);
                }
                if (candidates.Count > 1) throw new InvalidOperationException("More than one cast device matches. Use the TV's full unique name.");
                foreach (AutomationElement item in candidates)
                {
                    object pattern;
                    if (item.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) { ((InvokePattern)pattern).Invoke(); return true; }
                    if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) { ((SelectionItemPattern)pattern).Select(); return true; }
                }
            }
            catch (ElementNotAvailableException) { }
            return false;
        }
        static AutomationElement FindPane()
        {
            try
            {
                AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Cast"), new PropertyCondition(AutomationElement.NameProperty, "Connect"),
                        new PropertyCondition(AutomationElement.NameProperty, "Cast to")));
                string windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
                foreach (AutomationElement window in windows)
                {
                    using (Process process = Process.GetProcessById(window.Current.ProcessId))
                    {
                        string path = System.IO.Path.GetFullPath(process.MainModule.FileName), file = System.IO.Path.GetFileName(path);
                        bool shellApp = (file.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("ShellHost.exe", StringComparison.OrdinalIgnoreCase)) &&
                            path.StartsWith(windowsRoot + "SystemApps\\", StringComparison.OrdinalIgnoreCase);
                        bool systemShell = path.Equals(windowsRoot + "explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                            path.Equals(windowsRoot + "System32\\ShellHost.exe", StringComparison.OrdinalIgnoreCase);
                        if (shellApp || systemShell) return window;
                    }
                }
            }
            catch { /* Unrecognized shell: let the user select the TV manually. */ }
            return null;
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)] internal struct SourceInfo { internal Luid Adapter; internal uint Id, Mode, Status; }
        [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo
        {
            internal Luid Adapter; internal uint Id, Mode, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, Scanline;
            internal int Available; internal uint Status;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { internal SourceInfo Source; internal TargetInfo Target; internal uint Flags; }
        // 16-byte mode header plus 48-byte native union. Mode data is not interpreted here.
        [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct ModeInfo { [FieldOffset(0)] internal uint Type; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceHeader { internal uint Type, Size; internal Luid Adapter; internal uint Id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TargetName
        {
            internal DeviceHeader Header; internal uint Flags, Technology; internal ushort Manufacturer, Product; internal uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DevicePath;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WaveCaps
        {
            internal ushort Manufacturer, Product; internal uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            internal uint Formats; internal ushort Channels, Reserved; internal uint Support;
        }
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, [Out] ModeInfo[] modeArray, IntPtr topology);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)] static extern int GetTargetName(ref TargetName name);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint paths, IntPtr pathArray, uint modes, IntPtr modeArray, uint flags);
        [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")] static extern int SetDisplayPaths(uint paths, [In] PathInfo[] pathArray, uint modes, IntPtr modeArray, uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)] static extern uint waveOutGetDevCaps(UIntPtr device, ref WaveCaps caps, uint size);

        const uint QdcAllPaths = 1, QdcOnlyActive = 2;

        // Active displays only (what is lit up right now).
        internal static List<DisplayTarget> GetTargets() { return Query(QdcOnlyActive); }

        // Every display Windows can see, including a plugged-in HDMI TV that is not currently in use.
        internal static List<DisplayTarget> GetConnectedTargets() { return Query(QdcAllPaths); }

        static List<DisplayTarget> Query(uint flags)
        {
            bool all = flags == QdcAllPaths;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint paths, modes; int result = GetDisplayConfigBufferSizes(flags, out paths, out modes);
                if (result != 0) throw new Win32Exception(result, "Cannot read Windows display configuration.");
                if (paths > (all ? 4096u : 256u) || modes > (all ? 4096u : 1024u)) throw new InvalidOperationException("Unexpected Windows display buffer size.");
                PathInfo[] pathArray = new PathInfo[Math.Max(paths, 1)]; ModeInfo[] modeArray = new ModeInfo[Math.Max(modes, 1)];
                result = QueryDisplayConfig(flags, ref paths, pathArray, ref modes, modeArray, IntPtr.Zero);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception(result, "Cannot query Windows displays.");
                List<DisplayTarget> targets = new List<DisplayTarget>(); HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < paths; i++)
                {
                    TargetInfo target = pathArray[i].Target;
                    if (all && target.Available == 0) continue;
                    TargetName name = new TargetName();
                    name.Header.Type = 2; name.Header.Size = (uint)Marshal.SizeOf(typeof(TargetName)); name.Header.Adapter = target.Adapter; name.Header.Id = target.Id;
                    result = GetTargetName(ref name);
                    if (result != 0)
                    {
                        if (all) continue;
                        throw new Win32Exception(result, "Cannot identify an active display.");
                    }
                    string key = target.Adapter.High + ":" + target.Adapter.Low + ":" + target.Id + ":" + name.DevicePath;
                    if (!seen.Add(key)) continue;
                    uint technology = name.Technology; // Base connector type, including Miracast (15).
                    targets.Add(new DisplayTarget { Name = name.Name ?? "", Key = key, Wireless = technology == 15,
                        Internal = technology == 0x80000000 || technology == 6 || technology == 11 || technology == 13, Path = pathArray[i] });
                }
                return targets;
            }
            throw new InvalidOperationException("Displays are changing. Wait a moment and retry.");
        }
        internal static PathInfo TvPath(DisplayTarget target)
        {
            if (target == null || !target.Wireless) throw new InvalidOperationException("A verified wireless display is required.");
            PathInfo path = target.Path;
            path.Source.Mode = uint.MaxValue; path.Target.Mode = uint.MaxValue; path.Flags = 1;
            return path;
        }
        internal static void SetTv(DisplayTarget target)
        {
            PathInfo[] paths = new PathInfo[] { TvPath(target) };
            // Supply only the verified TV path. Never ask Windows to choose a remembered external monitor.
            int result = SetDisplayPaths(1, paths, 0, IntPtr.Zero, 0x460); // VALIDATE | USE_SUPPLIED | ALLOW_CHANGES
            if (result != 0) throw new Win32Exception(result, "Windows could not validate the TV-only layout.");
            result = SetDisplayPaths(1, paths, 0, IntPtr.Zero, 0x4A0); // APPLY | USE_SUPPLIED | ALLOW_CHANGES
            if (result != 0) throw new Win32Exception(result, "Windows could not apply the TV-only layout.");
        }
        // Same as Win+P > "Second screen only". Only called after exactly one wired external display was verified.
        internal static void SetExternalOnly()
        {
            int result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x88); // TOPOLOGY_EXTERNAL | APPLY
            if (result != 0) throw new Win32Exception(result, "Windows could not switch to the HDMI screen.");
        }
        internal static void RestoreInternal()
        {
            int result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x81); // TOPOLOGY_INTERNAL | APPLY
            if (result != 0) throw new Win32Exception(result, "Windows could not change display mode.");
        }
        internal static bool HasTvAudio(string name)
        {
            uint count = Math.Min(waveOutGetNumDevs(), 256);
            for (uint i = 0; i < count; i++)
            {
                WaveCaps caps = new WaveCaps();
                if (waveOutGetDevCaps(new UIntPtr(i), ref caps, (uint)Marshal.SizeOf(typeof(WaveCaps))) == 0 &&
                    (SafetyPolicy.Matches(caps.Name, name) || SafetyPolicy.Matches(caps.Name, "Samsung") || SafetyPolicy.Matches(caps.Name, "Wireless Display"))) return true;
            }
            return false;
        }
        internal static void OpenCast()
        {
            keybd_event(0x5B, 0, 0, UIntPtr.Zero);
            try { keybd_event(0x4B, 0, 0, UIntPtr.Zero); }
            finally { keybd_event(0x4B, 0, 2, UIntPtr.Zero); keybd_event(0x5B, 0, 2, UIntPtr.Zero); }
        }
    }
}
