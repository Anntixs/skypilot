using System.Runtime.InteropServices;
using System.Text;

namespace SkyNetwork.Voice;

public enum PttKind
{
    None,
    Keyboard,
    Joystick,
}

/// <summary>
/// A push-to-talk control: a keyboard key or mouse button (Windows virtual-key code) or a joystick
/// button. Stored in the clients' settings as a string, e.g. "key:162" or "joy:0:4".
/// </summary>
public readonly record struct PttBinding(PttKind Kind, int Code, int Device = 0)
{
    public static readonly PttBinding None = new(PttKind.None, 0);

    public override string ToString() => Kind switch
    {
        PttKind.Keyboard => $"key:{Code}",
        PttKind.Joystick => $"joy:{Device}:{Code}",
        _ => "",
    };

    public static PttBinding Parse(string? text)
    {
        var p = (text ?? "").Split(':');
        return p switch
        {
            ["key", var c] when int.TryParse(c, out int code) && code is > 0 and < 256 => new(PttKind.Keyboard, code),
            ["joy", var d, var b] when int.TryParse(d, out int dev) && int.TryParse(b, out int button) && button is >= 0 and < 32
                => new(PttKind.Joystick, button, dev),
            _ => None,
        };
    }

    /// <summary>Human-readable name: "Right Ctrl", "Joystick 1 button 5".</summary>
    public string Describe() => Kind switch
    {
        PttKind.Keyboard => PushToTalk.KeyName(Code),
        PttKind.Joystick => $"Joystick {Device + 1} button {Code + 1}",
        _ => "—",
    };
}

/// <summary>
/// Watches the push-to-talk control while any window has focus (the simulator usually does), by
/// polling the key and joystick state every 10 ms. Windows only; elsewhere it never fires.
/// </summary>
public sealed class PushToTalk : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _down;

    public PushToTalk()
    {
        if (OperatingSystem.IsWindows()) new Thread(Poll) { IsBackground = true, Name = "PTT" }.Start();
    }

    public PttBinding Binding { get; set; } = PttBinding.None;

    public bool IsDown => _down;

    /// <summary>Pressed (true) / released (false). Raised on the polling thread.</summary>
    public event Action<bool>? Changed;

    private void Poll()
    {
        while (!_cts.IsCancellationRequested)
        {
            bool down = IsPressed(Binding);
            if (down != _down)
            {
                _down = down;
                Changed?.Invoke(down);
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>Waits for the next key or joystick button press, to set a new binding.</summary>
    public static async Task<PttBinding> CaptureAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return PttBinding.None;
        // Ignore whatever is already held (for example the mouse button that started the capture).
        var held = new HashSet<PttBinding>(Snapshot());
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(15, ct);
            foreach (var b in Snapshot())
                if (!held.Contains(b)) return b;
            held.IntersectWith(Snapshot());
        }
        return PttBinding.None;
    }

    private static IEnumerable<PttBinding> Snapshot()
    {
        // Mouse buttons 1 and 2 are left out: they are needed to use the program.
        for (int vk = 3; vk < 256; vk++)
            if (vk is not (0x10 or 0x11 or 0x12) && (Native.GetAsyncKeyState(vk) & 0x8000) != 0)
                yield return new PttBinding(PttKind.Keyboard, vk);
        for (int joy = 0; joy < 16; joy++)
        {
            var info = new Native.JoyInfoEx { dwSize = (uint)Marshal.SizeOf<Native.JoyInfoEx>(), dwFlags = Native.JoyReturnButtons };
            if (Native.joyGetPosEx((uint)joy, ref info) != 0) continue;
            for (int b = 0; b < 32; b++)
                if ((info.dwButtons & (1u << b)) != 0) yield return new PttBinding(PttKind.Joystick, b, joy);
        }
    }

    private static bool IsPressed(PttBinding b)
    {
        switch (b.Kind)
        {
            case PttKind.Keyboard:
                return (Native.GetAsyncKeyState(b.Code) & 0x8000) != 0;
            case PttKind.Joystick:
                var info = new Native.JoyInfoEx { dwSize = (uint)Marshal.SizeOf<Native.JoyInfoEx>(), dwFlags = Native.JoyReturnButtons };
                return Native.joyGetPosEx((uint)b.Device, ref info) == 0 && (info.dwButtons & (1u << b.Code)) != 0;
            default:
                return false;
        }
    }

    internal static string KeyName(int vk)
    {
        string? known = vk switch
        {
            0x04 => "Middle mouse button", 0x05 => "Mouse button 4", 0x06 => "Mouse button 5",
            0xA0 => "Left Shift", 0xA1 => "Right Shift", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl",
            0xA4 => "Left Alt", 0xA5 => "Right Alt", 0x14 => "Caps Lock", 0x20 => "Space",
            0x91 => "Scroll Lock", 0x13 => "Pause",
            _ => null,
        };
        if (known != null) return known;
        if (!OperatingSystem.IsWindows()) return $"Key {vk}";
        uint scan = Native.MapVirtualKey((uint)vk, 0);
        var sb = new StringBuilder(64);
        return scan != 0 && Native.GetKeyNameText((int)(scan << 16), sb, sb.Capacity) > 0 ? sb.ToString() : $"Key {vk}";
    }

    public void Dispose() => _cts.Cancel();

    private static class Native
    {
        public const uint JoyReturnButtons = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct JoyInfoEx
        {
            public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
        }

        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetKeyNameText(int lParam, StringBuilder buffer, int size);
        [DllImport("winmm.dll")] public static extern uint joyGetPosEx(uint joyId, ref JoyInfoEx info);
    }
}
