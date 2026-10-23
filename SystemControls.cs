using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace KnobControl;

/// <summary>Built-in display brightness through WMI (laptop panels; external monitors are not covered).</summary>
internal sealed class Brightness
{
    readonly object _gate = new();
    int? _pending;
    bool _writing;

    public int? Get()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementBaseObject o in searcher.Get())
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (Exception ex) when (ex is ManagementException or COMException)
        {
            Debug.WriteLine(ex);
        }
        return null;
    }

    /// <summary>
    /// WMI writes take tens of milliseconds, too slow for the input thread, so they run on a
    /// worker that only ever applies the newest value.
    /// </summary>
    public void Set(int percent)
    {
        lock (_gate)
        {
            _pending = percent;
            if (_writing) return;
            _writing = true;
        }
        Task.Run(WriteLatest);
    }

    void WriteLatest()
    {
        while (true)
        {
            int value;
            lock (_gate)
            {
                if (_pending is null) { _writing = false; return; }
                value = _pending.Value;
                _pending = null;
            }
            try
            {
                using var methods = new ManagementClass(@"root\WMI", "WmiMonitorBrightnessMethods", null);
                foreach (ManagementObject o in methods.GetInstances())
                    using (o) o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)value });
            }
            catch (Exception ex) when (ex is ManagementException or COMException)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}

/// <summary>Master volume of the default playback device through Core Audio.</summary>
internal static class Volume
{
    public static int Get()
    {
        var v = Endpoint();
        Marshal.ThrowExceptionForHR(v.GetMasterVolumeLevelScalar(out float level));
        return (int)Math.Round(level * 100);
    }

    public static void Set(int percent)
    {
        var v = Endpoint();
        var ctx = Guid.Empty;
        Marshal.ThrowExceptionForHR(v.SetMasterVolumeLevelScalar(percent / 100f, ref ctx));
        if (percent > 0) v.SetMute(false, ref ctx);
    }

    static IAudioEndpointVolume Endpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o));
        return (IAudioEndpointVolume)o;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, ref Guid context);
        int SetMasterVolumeLevelScalar(float level, ref Guid context);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

internal static class AppLauncher
{
    /// <summary>Brings the app's most recent window to the front, or starts the app if it has none.</summary>
    public static void Launch(MenuItemConfig item)
    {
        IntPtr hwnd = FindAppWindow(item);
        // A packaged app that refuses the focus is safe to relaunch: it is single-instance and
        // activates its own window. Anything else would open a second window, so leave it be.
        if (hwnd != IntPtr.Zero && (Focus(hwnd) || PackageFamily(item) is null))
            return;

        ProcessStartInfo start;
        if (!string.IsNullOrWhiteSpace(item.AppId))
            start = new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + item.AppId);
        else if (!string.IsNullOrWhiteSpace(item.Path))
            start = new ProcessStartInfo(item.Path) { UseShellExecute = true };
        else
            throw new InvalidOperationException($"\"{item.Name}\" has no appId or path.");

        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// The app's topmost real window: visible (minimized counts), not cloaked, unowned, titled.
    /// EnumWindows walks in z-order, so the first hit is the one used most recently.
    /// </summary>
    static IntPtr FindAppWindow(MenuItemConfig item)
    {
        var pids = AppProcessIds(item);
        if (pids.Count == 0) return IntPtr.Zero;

        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero
                || Native.GetWindowTextLength(hwnd) == 0)
                return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains(pid)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Processes belonging to the item: by package family for Store apps (process names are
    /// ambiguous, e.g. "claude" is also the Claude Code CLI), else by process name or exe path.
    /// </summary>
    static HashSet<uint> AppProcessIds(MenuItemConfig item)
    {
        var ids = new HashSet<uint>();
        string? family = PackageFamily(item);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                bool match = family != null ? ProcessPackageFamily((uint)p.Id) == family
                    : !string.IsNullOrWhiteSpace(item.Process) ? p.ProcessName.Equals(item.Process, StringComparison.OrdinalIgnoreCase)
                    : !string.IsNullOrWhiteSpace(item.Path) && string.Equals(ProcessPath((uint)p.Id), item.Path, StringComparison.OrdinalIgnoreCase);
                if (match) ids.Add((uint)p.Id);
            }
        }
        return ids;
    }

    static string? PackageFamily(MenuItemConfig item)
    {
        int bang = item.AppId?.IndexOf('!') ?? -1;
        return bang > 0 ? item.AppId![..bang] : null;
    }

    static string? ProcessPackageFamily(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 0;
            if (Native.GetPackageFamilyName(h, ref len, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */) return null;
            var name = new StringBuilder((int)len);
            return Native.GetPackageFamilyName(h, ref len, name) == 0 ? name.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    static string? ProcessPath(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            uint size = (uint)path.Capacity;
            return Native.QueryFullProcessImageName(h, 0, path, ref size) ? path.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    static bool Focus(IntPtr hwnd)
    {
        Native.ShowWindow(hwnd, Native.IsIconic(hwnd) ? Native.SW_RESTORE : Native.SW_SHOW);

        // Windows only lets the process that sent the last input take the foreground. Tap an
        // unassigned key so that is us, without triggering anything in the current app.
        Native.INPUT Key(uint flags) => new()
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = Native.VK_UNASSIGNED, dwFlags = flags } },
        };
        var tap = new[] { Key(0), Key(Native.KEYEVENTF_KEYUP) };
        Native.SendInput((uint)tap.Length, tap, Marshal.SizeOf<Native.INPUT>());
        Native.SetForegroundWindow(hwnd);
        if (Native.GetForegroundWindow() == hwnd) return true;

        // Fallback: share the foreground thread's input state for the duration of the switch.
        uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        uint self = Native.GetCurrentThreadId();
        Native.AttachThreadInput(self, foregroundThread, true);
        Native.BringWindowToTop(hwnd);
        Native.SetForegroundWindow(hwnd);
        Native.AttachThreadInput(self, foregroundThread, false);
        return Native.GetForegroundWindow() == hwnd;
    }
}
