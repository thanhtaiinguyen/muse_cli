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
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint handle);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
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

    internal static bool InScope(nint root, nint candidate)
    {
        if (!IsWindow(root) || !IsWindow(candidate)) return false;
        GetWindowThreadProcessId(root, out var pid);
        if (pid == Environment.ProcessId) return false;
        candidate = GetAncestor(candidate, 2);
        for (var depth = 0; candidate != 0 && depth < 16; depth++)
        {
            if (candidate == root) return true;
            GetWindowThreadProcessId(candidate, out var current);
            if (current != pid) return false;
            candidate = GetWindow(candidate, 4); // GW_OWNER: only dialogs/menus owned by the selected window.
        }
        return false;
    }

    internal static nint ActiveWindow(nint root)
    {
        var foreground = GetForegroundWindow();
        if (InScope(root, foreground)) return foreground;
        var popup = GetLastActivePopup(root);
        return popup != 0 && IsWindowVisible(popup) && InScope(root, popup) ? popup : root;
    }

    private sealed class DpiScope(nint previous) : IDisposable
    {
        public void Dispose() { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }
    internal static IDisposable PhysicalCoordinates() => new DpiScope(SetThreadDpiAwarenessContext((nint)(-4)));

    internal static void Focus(nint window, Action? validate = null)
    {
        validate?.Invoke();
        if (IsIconic(window)) ShowWindow(window, 9);
        if (GetForegroundWindow() == window) return;
        SetForegroundWindow(window);
        if (!SpinWait.SpinUntil(() => GetForegroundWindow() == window, 500))
            throw new InvalidOperationException("Windows chưa cho đưa cửa sổ đích lên trước. Hãy mở cửa sổ đó rồi thử lại.");
        validate?.Invoke();
    }

    private static void CheckModifiers()
    {
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x01, 0x02, 0x04, 0x05, 0x06 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("Hãy thả phím bổ trợ và nút chuột trước khi Muse thao tác.");
    }

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            // Release synthetic keys/buttons even when Windows accepted only
            // part of a batch. Never leave Ctrl/Shift or a mouse button down.
            var release = inputs.Where(i => i.Type == 1).Select(i => new Input { Type = 1, Data = new() { Keyboard = new() { Key = i.Data.Keyboard.Key, Scan = i.Data.Keyboard.Scan, Flags = i.Data.Keyboard.Flags | 2 } } })
                .Append(new Input { Type = 0, Data = new() { Mouse = new() { Flags = 4 | 16 } } }).ToArray();
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            throw new InvalidOperationException("Windows chặn thao tác. Muse không thể điều khiển cửa sổ bảo mật hoặc ứng dụng chạy quyền cao hơn.");
        }
    }

    private static void AtPoint(nint root, int x, int y, Action validate)
    {
        validate();
        if (!InScope(root, GetForegroundWindow()) || !InScope(root, WindowFromPoint(new() { X = x, Y = y })))
            throw new InvalidOperationException("Điểm thao tác bị cửa sổ khác che hoặc đã đổi cửa sổ. Hãy chụp lại trước khi thao tác.");
    }

    internal static void Click(nint root, int x, int y, bool twice, bool right, Action validate)
    {
        CheckModifiers(); Focus(ActiveWindow(root), validate); AtPoint(root, x, y, validate);
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Không di chuyển được con trỏ.");
        AtPoint(root, x, y, validate);
        var original = GetAncestor(WindowFromPoint(new() { X = x, Y = y }), 2);
        var pair = new[] { new Input { Type = 0, Data = new() { Mouse = new() { Flags = right ? 8u : 2u } } }, new Input { Type = 0, Data = new() { Mouse = new() { Flags = right ? 16u : 4u } } } };
        Send(pair);
        if (twice)
        {
            AtPoint(root, x, y, validate);
            if (GetAncestor(WindowFromPoint(new() { X = x, Y = y }), 2) != original) throw new InvalidOperationException("Cửa sổ tại điểm bấm đã thay đổi; không gửi lần bấm thứ hai.");
            Send(pair);
        }
    }

    internal static void Type(nint root, string text, Action validate)
    {
        CheckModifiers(); Focus(ActiveWindow(root), validate);
        // Short batches permit cancellation during long Unicode input.
        foreach (var chunk in text.Chunk(32))
        {
            CheckModifiers();
            validate();
            if (!InScope(root, GetForegroundWindow())) throw new InvalidOperationException("Cửa sổ nhận phím đã thay đổi. Đã dừng nhập.");
            Send(chunk.SelectMany(c => new[]
            {
                new Input { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = 4 } } },
                new Input { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = 6 } } },
            }).ToArray());
        }
    }

    internal static void Key(nint root, string chord, Action validate)
    {
        var keys = ComputerActionPolicy.Keys(chord);
        CheckModifiers(); Focus(ActiveWindow(root), validate); validate();
        if (!InScope(root, GetForegroundWindow())) throw new InvalidOperationException("Cửa sổ nhận phím đã thay đổi.");
        Send(keys.Select(k => new Input { Type = 1, Data = new() { Keyboard = new() { Key = k } } })
            .Concat(keys.Reverse().Select(k => new Input { Type = 1, Data = new() { Keyboard = new() { Key = k, Flags = 2 } } })).ToArray());
        // Allow the matching synthetic key-up to reach Windows before the next
        // action checks for user-held modifiers (for example Ctrl+A then type).
        if (!SpinWait.SpinUntil(() => keys.All(k => (GetAsyncKeyState(k) & 0x8000) == 0), 500))
            throw new InvalidOperationException("Phím chưa được nhả. Hãy thả phím rồi tiếp tục.");
    }

    internal static void Scroll(nint root, int x, int y, string direction, Action validate)
    {
        CheckModifiers(); Focus(ActiveWindow(root), validate); AtPoint(root, x, y, validate);
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Không di chuyển được con trỏ.");
        AtPoint(root, x, y, validate);
        var amount = direction is "up" or "right" ? 360 : -360;
        Send([new Input { Type = 0, Data = new() { Mouse = new() { Flags = direction is "left" or "right" ? 0x1000u : 0x800u, MouseData = unchecked((uint)amount) } } }]);
    }

    internal static void Drag(nint root, Point from, Point to, Action validate)
    {
        CheckModifiers(); Focus(ActiveWindow(root), validate); AtPoint(root, from.X, from.Y, validate); AtPoint(root, to.X, to.Y, validate);
        if (!SetCursorPos(from.X, from.Y)) throw new InvalidOperationException("Không di chuyển được con trỏ.");
        AtPoint(root, from.X, from.Y, validate);
        Send([new Input { Type = 0, Data = new() { Mouse = new() { Flags = 2 } } }]);
        try
        {
            for (var step = 1; step <= 16; step++)
            {
                var x = from.X + (int)((to.X - (long)from.X) * step / 16);
                var y = from.Y + (int)((to.Y - (long)from.Y) * step / 16);
                AtPoint(root, x, y, validate);
                if (!SetCursorPos(x, y)) throw new InvalidOperationException("Không di chuyển được con trỏ.");
                Thread.Sleep(15);
            }
            AtPoint(root, to.X, to.Y, validate);
        }
        finally { SendInput(1, [new Input { Type = 0, Data = new() { Mouse = new() { Flags = 4 } } }], Marshal.SizeOf<Input>()); }
    }

    internal static bool IsUncovered(nint root, Rect rectangle)
    {
        for (var row = 0; row < 6; row++)
            for (var column = 0; column < 8; column++)
                if (!InScope(root, WindowFromPoint(new() { X = rectangle.Left + (int)((column + 0.5) * rectangle.Width / 8), Y = rectangle.Top + (int)((row + 0.5) * rectangle.Height / 6) }))) return false;
        return true;
    }
}
