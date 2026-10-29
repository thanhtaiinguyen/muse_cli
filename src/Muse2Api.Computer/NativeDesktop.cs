using System.Runtime.InteropServices;
using System.Text;

namespace Muse2Api.Computer;

internal static class NativeDesktop
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    internal delegate bool EnumCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flag);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);

    internal static void Focus(nint window)
    {
        if (IsIconic(window)) ShowWindow(window, 9);
        if (GetForegroundWindow() == window) return;
        SetForegroundWindow(window);
        if (!SpinWait.SpinUntil(() => GetForegroundWindow() == window, 500))
            throw new InvalidOperationException("Windows did not allow activating this window. Bring the target window to the front and retry.");
    }

    private static void CheckModifiers()
    {
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("Release Shift/Ctrl/Alt/Windows before retrying this action.");
    }

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("Windows blocked the input. Elevated/secure windows cannot be controlled from this process.");
    }

    internal static void Click(nint window, int x, int y, bool twice)
    {
        CheckModifiers(); Focus(window);
        if (GetAncestor(WindowFromPoint(new() { X = x, Y = y }), 2) != window)
            throw new InvalidOperationException("The target point is covered by another window. Capture again before clicking.");
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Cannot move the pointer.");
        var pair = new[] { new Input { Type = 0, Data = new() { Mouse = new() { Flags = 2 } } }, new Input { Type = 0, Data = new() { Mouse = new() { Flags = 4 } } } };
        Send(pair);
        if (twice) Send(pair);
    }

    internal static void Type(nint window, string text)
    {
        CheckModifiers(); Focus(window);
        if (text.Length is < 1 or > 4000 || text.Contains('\0')) throw new InvalidOperationException("Text must contain 1 to 4000 characters, without NUL.");
        Send(text.SelectMany(c => new[]
        {
            new Input { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = 4 } } },
            new Input { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = 6 } } },
        }).ToArray());
    }

    internal static void Key(nint window, string chord)
    {
        var keys = chord.ToUpperInvariant() switch
        {
            "ENTER" => new ushort[] { 13 }, "TAB" => new ushort[] { 9 }, "SHIFT+TAB" => new ushort[] { 16, 9 },
            "CTRL+A" => new ushort[] { 17, 65 }, "CTRL+C" => new ushort[] { 17, 67 }, "CTRL+V" => new ushort[] { 17, 86 }, "CTRL+Z" => new ushort[] { 17, 90 },
            "BACKSPACE" => new ushort[] { 8 }, "DELETE" => new ushort[] { 46 }, "LEFT" => new ushort[] { 37 }, "RIGHT" => new ushort[] { 39 },
            "UP" => new ushort[] { 38 }, "DOWN" => new ushort[] { 40 }, "HOME" => new ushort[] { 36 }, "END" => new ushort[] { 35 },
            "PAGEUP" => new ushort[] { 33 }, "PAGEDOWN" => new ushort[] { 34 }, "F5" => new ushort[] { 116 }, "SPACE" => new ushort[] { 32 },
            _ => throw new InvalidOperationException("Unsupported key. Esc is reserved for the user's emergency stop."),
        };
        CheckModifiers(); Focus(window);
        Send(keys.Select(k => new Input { Type = 1, Data = new() { Keyboard = new() { Key = k } } })
            .Concat(keys.Reverse().Select(k => new Input { Type = 1, Data = new() { Keyboard = new() { Key = k, Flags = 2 } } })).ToArray());
    }
}
