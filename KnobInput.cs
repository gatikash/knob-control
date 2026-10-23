using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace KnobControl;

internal enum KnobEvent { Left, Right, Press }

/// <summary>
/// Turns the Kreo knob into <see cref="KnobEvent"/>s.
///
/// The knob sends consumer-control reports (Volume Increment/Decrement/Mute) that Windows
/// turns into VK_VOLUME_* keystrokes. The keyboard hook sees those keystrokes but not which
/// device sent them; Raw Input sees the device but cannot block anything. So the hook blocks
/// every volume keystroke, Raw Input tells us which ones came from the Kreo, and any keystroke
/// no Kreo report claims within a short window (laptop Fn keys, other keyboards) is replayed.
/// </summary>
internal sealed class KnobInput : IDisposable
{
    static readonly IntPtr ReplayTag = new(0x4B4E4F42); // "KNOB": marks keystrokes we replay ourselves
    const long ReplayAfterMs = 80;
    const long CreditLifetimeMs = 250;

    readonly Native.LowLevelKeyboardProc _proc;
    readonly IntPtr _hook;
    readonly HwndSource _sink;
    readonly DispatcherTimer _timer;
    readonly Dictionary<IntPtr, bool> _isKnobDevice = new();
    readonly List<(uint Vk, long At)> _unclaimedKeys = new();   // blocked keystrokes waiting for a Kreo report
    readonly List<(uint Vk, long At)> _unmatchedReports = new(); // Kreo reports whose keystroke has not arrived yet

    public event Action<KnobEvent>? Knob;
    public bool Enabled { get; set; } = true;
    public string DeviceMatch { get; set; }

    public KnobInput(string deviceMatch)
    {
        DeviceMatch = deviceMatch;

        // Message-only window that receives WM_INPUT even when we are in the background.
        _sink = new HwndSource(new HwndSourceParameters("KnobControlRawInput") { ParentWindow = new IntPtr(-3) });
        _sink.AddHook(SinkProc);
        var devices = new[]
        {
            new Native.RAWINPUTDEVICE { UsagePage = 0x0C, Usage = 0x01, Flags = Native.RIDEV_INPUTSINK, Target = _sink.Handle },
        };
        if (!Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            throw new InvalidOperationException($"RegisterRawInputDevices failed ({Marshal.GetLastWin32Error()})");

        _proc = HookProc; // keep the delegate alive for as long as the hook is installed
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed ({Marshal.GetLastWin32Error()})");

        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        _timer.Tick += (_, _) => Sweep();
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (IsVolumeKey(k.vkCode) && k.dwExtraInfo != ReplayTag)
            {
                int msg = (int)wParam;
                if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                    OnVolumeKeyDown(k.vkCode);
                return (IntPtr)1; // swallow; key-ups too, replays send their own
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void OnVolumeKeyDown(uint vk)
    {
        int i = _unmatchedReports.FindIndex(r => r.Vk == vk);
        if (i >= 0) _unmatchedReports.RemoveAt(i);          // already handled as a knob event
        else _unclaimedKeys.Add((vk, Environment.TickCount64));
        _timer.Start();
    }

    IntPtr SinkProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_INPUT && Enabled)
            ReadRawInput(lParam);
        return IntPtr.Zero; // let DefWindowProc release the raw input buffer
    }

    void ReadRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        uint size = 0;
        Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, buffer, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buffer);
            if (header.Type != Native.RIM_TYPEHID || !IsKnobDevice(header.Device)) return;

            int offset = (int)headerSize;
            int reportSize = Marshal.ReadInt32(buffer, offset);
            int count = Marshal.ReadInt32(buffer, offset + 4);
            for (int n = 0; n < count; n++)
            {
                int report = offset + 8 + n * reportSize;
                // Report layout seen from the Kreo Hive: [report id][usage lo][usage hi]
                int usage = reportSize >= 3
                    ? Marshal.ReadByte(buffer, report + 1) | Marshal.ReadByte(buffer, report + 2) << 8
                    : Marshal.ReadByte(buffer, report);
                if (UsageToEvent(usage) is { } e)
                    OnKnobReport(e.Vk, e.Event);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    void OnKnobReport(uint vk, KnobEvent e)
    {
        int i = _unclaimedKeys.FindIndex(k => k.Vk == vk);
        if (i >= 0) _unclaimedKeys.RemoveAt(i);             // its keystroke is already blocked
        else _unmatchedReports.Add((vk, Environment.TickCount64));
        _timer.Start();
        Knob?.Invoke(e);
    }

    void Sweep()
    {
        long now = Environment.TickCount64;
        for (int i = _unclaimedKeys.Count - 1; i >= 0; i--)
        {
            if (now - _unclaimedKeys[i].At < ReplayAfterMs) continue;
            Replay(_unclaimedKeys[i].Vk);
            _unclaimedKeys.RemoveAt(i);
        }
        _unmatchedReports.RemoveAll(r => now - r.At >= CreditLifetimeMs);
        if (_unclaimedKeys.Count == 0 && _unmatchedReports.Count == 0)
            _timer.Stop();
    }

    static void Replay(uint vk)
    {
        Native.INPUT Key(uint flags) => new()
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags, dwExtraInfo = ReplayTag } },
        };
        var inputs = new[] { Key(Native.KEYEVENTF_EXTENDEDKEY), Key(Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP) };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    bool IsKnobDevice(IntPtr device)
    {
        if (_isKnobDevice.TryGetValue(device, out bool known)) return known;
        uint chars = 0;
        Native.GetRawInputDeviceInfo(device, Native.RIDI_DEVICENAME, null, ref chars);
        var name = new StringBuilder((int)chars + 1);
        Native.GetRawInputDeviceInfo(device, Native.RIDI_DEVICENAME, name, ref chars);
        bool match = name.ToString().Contains(DeviceMatch, StringComparison.OrdinalIgnoreCase);
        _isKnobDevice[device] = match;
        return match;
    }

    static bool IsVolumeKey(uint vk) =>
        vk is Native.VK_VOLUME_UP or Native.VK_VOLUME_DOWN or Native.VK_VOLUME_MUTE;

    static (uint Vk, KnobEvent Event)? UsageToEvent(int usage) => usage switch
    {
        0xE9 => (Native.VK_VOLUME_UP, KnobEvent.Right),
        0xEA => (Native.VK_VOLUME_DOWN, KnobEvent.Left),
        0xE2 => (Native.VK_VOLUME_MUTE, KnobEvent.Press),
        _ => null,
    };

    public void Dispose()
    {
        _timer.Stop();
        Native.UnhookWindowsHookEx(_hook);
        _sink.Dispose();
    }
}
