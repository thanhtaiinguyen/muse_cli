using System.Diagnostics;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Muse2Api.Agent;

namespace Muse2Api.Computer;

public sealed record DesktopWindow(long Handle, int ProcessId, string Title, long ProcessStarted = 0, string ApplicationName = "")
{
    public override string ToString() => ApplicationName.Length > 0 ? $"{Title} — {ApplicationName}" : Title;
}

/// <summary>Pure, testable auto-selection for screen control. Input must be in
/// Z-order (topmost first), exactly as EnumWindows returns it.</summary>
public static class WindowAutoPicker
{
    private static readonly string[] ShellHosts = ["SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "LockApp", "WindowsInputExperience", "InputApp"];
    // Shell overlays are also recognised by title: the input-experience
    // overlay shows a display title while its host process name varies by
    // Windows build, so the process-name list alone let it be picked as a
    // full-screen, content-free target.
    private static readonly string[] ShellTitles = ["Windows Input Experience"];

    public static DesktopWindow? Pick(IReadOnlyList<DesktopWindow> windowsInZOrder, long foregroundHandle, int selfProcessId, string? preferredHint = null)
    {
        var candidates = windowsInZOrder.Where(w => w.ProcessId != selfProcessId && !string.IsNullOrWhiteSpace(w.Title)
            && !ShellTitles.Contains(w.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            && !ShellHosts.Contains(w.ApplicationName, StringComparer.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferredHint))
        {
            var hinted = candidates.FirstOrDefault(w => w.Title.Contains(preferredHint.Trim(), StringComparison.OrdinalIgnoreCase)
                || w.ApplicationName.Contains(preferredHint.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hinted is not null) return hinted;
        }
        if (foregroundHandle != 0)
        {
            var foreground = candidates.FirstOrDefault(w => w.Handle == foregroundHandle);
            if (foreground is not null) return foreground;
        }
        return candidates[0];
    }
}

/// <summary>UI Automation is executed off the UI thread, on MTA workers.</summary>
public sealed class DesktopComputer : IComputerTools, IDisposable
{
    public static bool IsRuntimeAvailable => typeof(AutomationElement).Assembly.GetName().Name == "UIAutomationClient";
    public const int EmergencyHotkey = 0x4D55;
    private ComputerWorker? _worker;
    private readonly Lazy<ComputerWorker> _previewWorker = new(() => new ComputerWorker(TimeSpan.FromSeconds(6)));
    private readonly object _workerLock = new();
    private int _workerReplacements;
    private ComputerWorker Worker { get { lock (_workerLock) return _worker ??= new ComputerWorker(TimeSpan.FromSeconds(20)); } }
    private void RecoverReadWorker(ComputerWorker worker)
    {
        lock (_workerLock)
        {
            if (_worker != worker) return;
            if (_workerReplacements++ >= 1) { Disarm(); throw new InvalidOperationException("UI Automation của ứng dụng đích tiếp tục bị treo. Đã dừng điều khiển; không tạo thêm worker hay tự lặp thao tác."); }
            worker.Dispose(); _worker = null;
        }
    }
    private readonly Dictionary<string, AutomationElement> _elements = new();
    private readonly Dictionary<string, (string Name, string AutomationId, string Role)> _identities = new();
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private DesktopWindow? _target;
    private DateTime _inspectedAt, _capturedAt;
    private string _snapshot = "";
    private ScreenshotMap? _imageMap;
    private nint _captureWindow;
    private long _generation;
    private bool _authorizationRequested;
    private bool _authorizationDenied;
    private int _disposed;
    private readonly CancellationTokenSource _shutdown = new();
    public Func<CancellationToken, Task<bool>>? RequestAuthorization { get; set; }
    /// <summary>Test seam: replaces live window enumeration for auto-selection.</summary>
    internal Func<IReadOnlyList<DesktopWindow>>? WindowSource { get; set; }
    public void BeginTurn() { _authorizationRequested = false; _authorizationDenied = false; }
    public DesktopWindow? Target => _target;
    public string Context => _target is { } target
        ? $"enabled for selected window and its owned dialogs/menus: {target.Title}, process {target.ProcessId}. Use computer_inspect for fresh element_id. Screenshot coordinates are IMAGE pixels, not UIA physical bounds; use image_scale to convert. Captures expire after any UI action. Esc stops control."
        : RequestAuthorization is not null ? "not yet authorized. Calling computer_inspect asks the desktop host to select a window, enforce the current permission mode and arm emergency stop. Never bypass it through shell commands."
        : "disabled. The window is auto-selected on the first computer call when possible; otherwise ask the user to select a test window using the Màn hình button. Never attempt screen control through shell commands.";

    public static IReadOnlyList<DesktopWindow> ListWindows() => EnumerateWindows().OrderBy(w => w.Title).ToArray();

    /// <summary>Top-level windows in Z-order (topmost first); used for auto-selection.</summary>
    public static IReadOnlyList<DesktopWindow> ListWindowsInZOrder() => EnumerateWindows();

    private static List<DesktopWindow> EnumerateWindows()
    {
        var windows = new List<DesktopWindow>();
        NativeDesktop.EnumWindows((handle, _) =>
        {
            if (!NativeDesktop.IsWindowVisible(handle)) return true;
            NativeDesktop.GetWindowThreadProcessId(handle, out var pid);
            if (pid == Environment.ProcessId) return true; // Never automate Muse's own permission controls.
            var text = new StringBuilder(512); NativeDesktop.GetWindowText(handle, text, text.Capacity);
            if (text.Length > 0)
            {
                try { using var process = Process.GetProcessById((int)pid); windows.Add(new(handle.ToInt64(), (int)pid, text.ToString(), process.StartTime.ToUniversalTime().Ticks, process.ProcessName)); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return true;
        }, 0);
        return windows;
    }

    public void Authorize(DesktopWindow target)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        NativeDesktop.GetWindowThreadProcessId((nint)target.Handle, out var pid);
        if (pid != target.ProcessId || pid == Environment.ProcessId || !NativeDesktop.IsWindow((nint)target.Handle))
            throw new InvalidOperationException("Cửa sổ đã đóng hoặc thuộc chính Muse. Hãy làm mới danh sách và chọn lại.");
        using var process = Process.GetProcessById(target.ProcessId);
        var started = process.StartTime.ToUniversalTime().Ticks;
        if (target.ProcessStarted != 0 && target.ProcessStarted != started) throw new InvalidOperationException("Ứng dụng đích đã khởi động lại. Hãy chọn lại cửa sổ.");
        Disarm(); _target = target with { ProcessStarted = started }; _inspectedAt = DateTime.MinValue; _authorizationDenied = false;
    }

    /// <summary>Auto-select without any dialog: hint match, then the foreground
    /// window (or the top-level window owning it), then the topmost window in
    /// Z-order. Returns false when nothing safe can be picked, so the caller
    /// can fall back to the manual chooser.</summary>
    public bool TryAutoAuthorize(string? preferredHint = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_target is not null) return true;
        var windows = WindowSource?.Invoke() ?? EnumerateWindows();
        var foreground = NativeDesktop.GetForegroundWindow();
        long foregroundHandle = foreground.ToInt64();
        if (foreground != 0 && windows.All(w => w.Handle != foregroundHandle))
        {
            var owner = windows.FirstOrDefault(w => w.ProcessId != Environment.ProcessId && NativeDesktop.InScope((nint)w.Handle, foreground));
            if (owner is not null) foregroundHandle = owner.Handle;
        }
        var pick = WindowAutoPicker.Pick(windows, foregroundHandle, Environment.ProcessId, preferredHint);
        if (pick is null) return false;
        try { Authorize(pick); return _target is not null; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { return false; }
    }
    public void Disarm() { _target = null; Interlocked.Increment(ref _generation); _snapshot = ""; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel(); Disarm(); lock (_workerLock) { _worker?.Dispose(); }
        if (_previewWorker.IsValueCreated) _previewWorker.Value.Dispose(); _shutdown.Dispose();
    }
    public static bool RegisterEmergencyStop(nint window) => NativeDesktop.RegisterHotKey(window, EmergencyHotkey, 0x4000, 0x1B);
    public static void UnregisterEmergencyStop(nint window) => NativeDesktop.UnregisterHotKey(window, EmergencyHotkey);

    /// <summary>Local preview only: never focus a window, prompt, or replace tool snapshot coordinates.</summary>
    public async Task<string?> PreviewAsync(CancellationToken cancel)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel, _shutdown.Token);
        var target = _target;
        if (target is null) return null;
        var generation = Interlocked.Read(ref _generation);
        var frame = await _previewWorker.Value.RunAsync(operationToken =>
        {
            using var dpi = NativeDesktop.PhysicalCoordinates();
            Validate(target, generation, operationToken);
            if (!NativeDesktop.InScope((nint)target.Handle, NativeDesktop.GetForegroundWindow())) return null;
            return Capture(target, generation, operationToken, preview: true).ImageDataUrl;
        }, () => { }, lifetime.Token, skipWhenBusy: true);
        lifetime.Token.ThrowIfCancellationRequested();
        return generation == Interlocked.Read(ref _generation) ? frame : null;
    }

    public async Task<ComputerToolResult> ExecuteAsync(ToolCall call, CancellationToken cancel)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel, _shutdown.Token);
        cancel = lifetime.Token;
        cancel.ThrowIfCancellationRequested();
        ComputerActionPolicy.Validate(call);
        if (_target is null && RequestAuthorization is not null)
        {
            if (_authorizationRequested) return new(_authorizationDenied ? "error: user declined screen control for this turn. Do not request it again."
                : "error: screen_control_stopped. Quyền đã bị thu hồi trong lượt này; không tự xin lại hoặc dùng shell để điều khiển.");
            _authorizationRequested = true;
            if (!await RequestAuthorization(cancel).WaitAsync(cancel)) { _authorizationDenied = true; return new("error: user declined screen control for this turn. Do not request it again."); }
        }
        var target = _target ?? throw new InvalidOperationException("Chưa bật điều khiển cửa sổ và không tự chọn được cửa sổ nào. Hãy mở ứng dụng cần thao tác lên phía trước, hoặc bấm Màn hình và chọn thủ công.");
        _authorizationRequested = true;
        if (call.Name == "computer_list_windows") return new(JsonSerializer.Serialize(new[] { target }));
        var generation = Interlocked.Read(ref _generation);
        var worker = Worker;
        var readOnly = call.Name is "computer_inspect" or "computer_screenshot" or "computer_assert";
        return await worker.RunAsync(operationToken => Execute(call, target, generation, operationToken), Disarm, cancel,
            timedOut: readOnly ? () => RecoverReadWorker(worker) : null);
    }

    private void Validate(DesktopWindow target, long generation, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (generation != Interlocked.Read(ref _generation) || _target != target) throw new OperationCanceledException("Screen control was stopped.", cancel);
        NativeDesktop.GetWindowThreadProcessId((nint)target.Handle, out var pid);
        if (!NativeDesktop.IsWindow((nint)target.Handle) || pid != target.ProcessId) throw new InvalidOperationException("Cửa sổ đích đã đóng. Hãy chọn lại cửa sổ cần thử.");
        using var process = Process.GetProcessById(target.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != target.ProcessStarted) throw new InvalidOperationException("Ứng dụng đích đã thay đổi. Hãy chọn lại cửa sổ.");
    }

    private ComputerToolResult Execute(ToolCall call, DesktopWindow target, long generation, CancellationToken cancel)
    {
        using var dpi = NativeDesktop.PhysicalCoordinates();
        Validate(target, generation, cancel);
        var window = (nint)target.Handle;
        var args = call.Args;
        string S(string key) => args[key]?.ToString() ?? "";
        void Guard() => Validate(target, generation, cancel);
        void CheckElement(AutomationElement element, bool interactive)
        {
            Guard();
            var info = element.Current;
            if (info.ProcessId == Environment.ProcessId || info.IsPassword || interactive && (!info.IsEnabled || info.IsOffscreen)) throw new InvalidOperationException("Phần tử bị ẩn, bị khóa, là ô mật khẩu hoặc thuộc chính Muse. Hãy đọc lại giao diện.");
            // Chromium/Electron UIA descendants may live in renderer processes.
            // Check actual ancestry instead of incorrectly requiring one PID.
            AutomationElement? current = element;
            for (var depth = 0; current is not null && depth < 64; depth++)
            {
                Guard();
                if (current.Current.IsPassword) throw new InvalidOperationException("Không thao tác ô mật khẩu.");
                var handle = (nint)current.Current.NativeWindowHandle;
                if (handle != 0)
                {
                    if (NativeDesktop.InScope(window, handle)) return;
                    throw new InvalidOperationException("Phần tử trỏ tới cửa sổ khác phạm vi đã cho phép.");
                }
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            throw new InvalidOperationException("Phần tử không còn thuộc cửa sổ đã cho phép. Hãy đọc lại giao diện.");
        }
        AutomationElement Element(bool interactive = true)
        {
            if (DateTime.UtcNow - _inspectedAt > TimeSpan.FromSeconds(180) || !_elements.TryGetValue(S("element_id"), out var element))
                throw new InvalidOperationException("Mã phần tử đã cũ hoặc không thuộc lần đọc này. Hãy đọc lại giao diện.");
            CheckElement(element, interactive);
            if (interactive && _identities.TryGetValue(S("element_id"), out var identity))
            {
                var info = element.Current;
                if (info.Name != identity.Name || info.AutomationId != identity.AutomationId || info.ControlType.ProgrammaticName != identity.Role)
                    throw new InvalidOperationException("Nhãn hoặc loại phần tử đã đổi từ lần quan sát trước. Hãy đọc lại giao diện để tránh thao tác nhầm.");
            }
            return element;
        }
        AutomationElement Focused()
        {
            if (!NativeDesktop.InScope(window, NativeDesktop.GetForegroundWindow())) throw new InvalidOperationException("Cửa sổ nhận phím không thuộc phạm vi đã cho phép.");
            var focused = AutomationElement.FocusedElement ?? throw new InvalidOperationException("Chưa xác định được ô nhập đang có focus.");
            CheckElement(focused, true); return focused;
        }
        NativeDesktop.Point Point(string x, string y)
        {
            if (_imageMap is null || S("snapshot_id") != _snapshot || DateTime.UtcNow - _capturedAt > TimeSpan.FromSeconds(120))
                throw new InvalidOperationException("Ảnh chụp đã cũ hoặc đã có thao tác sau khi chụp. Hãy chụp lại và dùng snapshot_id mới.");
            if (!NativeDesktop.InScope(window, _captureWindow) || !NativeDesktop.GetWindowRect(_captureWindow, out var bounds) || !bounds.Equals(_imageMap.Bounds))
                throw new InvalidOperationException("Cửa sổ ảnh chụp đã di chuyển/đổi kích thước hoặc đóng. Hãy chụp lại.");
            return _imageMap.ToScreen(args[x]!.GetValue<int>(), args[y]!.GetValue<int>());
        }
        void CheckPoint(NativeDesktop.Point point)
        {
            Guard();
            try
            {
                var el = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                if (el is not null) CheckElement(el, true);
            }
            catch (Exception ex) when (ex is not InvalidOperationException) { }
        }
        // A failed/partial mutation must also invalidate the old image.
        var mutation = ComputerActionPolicy.Mutates(call.Name);
        var oldSnapshot = _snapshot;
        try
        {
        switch (call.Name)
        {
            case "computer_inspect":
                var active = NativeDesktop.ActiveWindow(window);
                NativeDesktop.WarmAccessibility(active);
                if (!NativeDesktop.GetWindowRect(active, out var rect)) throw new InvalidOperationException("Không đọc được vị trí cửa sổ.");
                var root = S("element_id").Length > 0 ? Element(interactive: false) : AutomationElement.FromHandle(active);
                lock (_workerLock) { Guard(); _elements.Clear(); _identities.Clear(); }
                var inspection = Guid.NewGuid().ToString("N")[..12];
                var pending = new Queue<(AutomationElement Element, int Depth)>(); pending.Enqueue((root, 0));
                var nodes = new List<JsonObject>(); var timer = Stopwatch.StartNew(); var scanned = 0; var matched = 0; var characters = 0; var more = false; var depthLimited = false; var childLimited = false;
                var offset = args["offset"]?.GetValue<int>() ?? 0; var limit = args["limit"]?.GetValue<int>() ?? 80; var maxDepth = args["max_depth"]?.GetValue<int>() ?? 12;
                while (pending.Count > 0 && scanned < 1500 && timer.Elapsed.TotalSeconds < 8)
                {
                    scanned++;
                    Validate(target, generation, cancel);
                    var (element, depth) = pending.Dequeue();
                    try
                    {
                        var info = element.Current;
                        if (info.IsPassword || info.ProcessId == Environment.ProcessId) continue;
                        if (S("query").Length == 0 || (info.Name + " " + info.AutomationId + " " + info.ControlType.ProgrammaticName).Contains(S("query"), StringComparison.OrdinalIgnoreCase))
                        {
                            if (matched++ >= offset)
                            {
                                if (nodes.Count >= limit) { more = true; break; }
                                var id = "e" + inspection + "_" + nodes.Count;
                                var box = info.BoundingRectangle;
                                if (box.IsEmpty || !double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height)) box = new System.Windows.Rect(0, 0, 0, 0);
                                var value = info.ControlType == ControlType.Edit && element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) ? ((ValuePattern)vp).Current.Value : "";
                                var node = JsonSerializer.SerializeToNode(new { element_id = id, depth, name = Limit(info.Name), automation_id = Limit(info.AutomationId),
                                    role = info.ControlType.ProgrammaticName, enabled = info.IsEnabled, offscreen = info.IsOffscreen,
                                    value = Limit(value), bounds = new { x = box.X - rect.Left, y = box.Y - rect.Top, width = box.Width, height = box.Height },
                                    patterns = info.IsOffscreen || !info.IsEnabled ? Array.Empty<string>() : element.GetSupportedPatterns().Take(16).Select(p => p.ProgrammaticName).ToArray() }, Json)!.AsObject();
                                var cost = node.ToJsonString(Json).Length;
                                if (characters + cost > 40000 && nodes.Count > 0) { more = true; break; }
                                lock (_workerLock)
                                {
                                    Guard(); characters += cost; nodes.Add(node); _elements[id] = element;
                                    _identities[id] = (info.Name, info.AutomationId, info.ControlType.ProgrammaticName);
                                }
                            }
                        }
                        if (depth >= maxDepth) { depthLimited = true; continue; }
                        var walker = TreeWalker.ControlViewWalker;
                        var child = walker.GetFirstChild(element); var count = 0;
                        while (child is not null && count++ < 500 && pending.Count < 1500)
                        { pending.Enqueue((child, depth + 1)); child = walker.GetNextSibling(child); }
                        if (child is not null) childLimited = true;
                    }
                    catch (ElementNotAvailableException) { }
                }
                Guard(); _inspectedAt = DateTime.UtcNow;
                return new(JsonSerializer.Serialize(new { window = target.Title, active_window = active.ToInt64(), inspection_id = inspection,
                    coordinate_space = "window physical pixels; NOT scaled screenshot pixels. Prefer element_id. Read image_scale from ui_image before using physical bounds for image coordinates.",
                    elements = nodes, scanned, next_offset = (more || pending.Count > 0) && nodes.Count > 0 ? offset + nodes.Count : (int?)null,
                    truncated = more || pending.Count > 0 || depthLimited || childLimited, depth_limit_reached = depthLimited,
                    scan_limit_reached = scanned >= 1500 || childLimited,
                    hint = "query searches name/automation_id/role; offset/limit paginate matches. Inspect a container element_id to narrow the tree. New inspection replaces old ids." }, Json));
            case "computer_screenshot": return Capture(target, generation, cancel);
            case "computer_focus":
                NativeDesktop.Focus(NativeDesktop.ActiveWindow(window), Guard);
                if (S("element_id").Length > 0) Element().SetFocus();
                break;
            case "computer_invoke":
                var clickable = Element();
                if (!clickable.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                {
                    var invokeBox = clickable.Current.BoundingRectangle;
                    if (invokeBox.Width > 0 && invokeBox.Height > 0)
                    {
                        var cx = (int)(invokeBox.Left + invokeBox.Width / 2);
                        var cy = (int)(invokeBox.Top + invokeBox.Height / 2);
                        NativeDesktop.Focus(NativeDesktop.ActiveWindow(window), Guard);
                        NativeDesktop.Click(window, cx, cy, false, false, Guard);
                        break;
                    }
                    throw new InvalidOperationException("Phần tử không hỗ trợ bấm trực tiếp. Hãy đọc vùng con hoặc dùng ảnh mới để bấm theo tọa độ.");
                }
                Validate(target, generation, cancel); ((InvokePattern)invoke).Invoke(); break;
            case "computer_set_value":
                var editable = Element();
                if (!editable.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || ((ValuePattern)pattern).Current.IsReadOnly)
                    throw new InvalidOperationException("Phần tử không hỗ trợ gán nội dung. Hãy bấm ô nhập rồi dùng computer_type.");
                var text = S("text"); if (text.Length > 16000) throw new InvalidOperationException("Value is too long.");
                Validate(target, generation, cancel); ((ValuePattern)pattern).SetValue(text); break;
            case "computer_type":
                NativeDesktop.Focus(NativeDesktop.ActiveWindow(window), Guard);
                var input = S("element_id").Length > 0 ? Element() : Focused();
                if (S("element_id").Length > 0) input.SetFocus();
                Validate(target, generation, cancel);
                if (!input.Current.HasKeyboardFocus) throw new InvalidOperationException("Ô yêu cầu chưa nhận focus. Đã dừng để tránh nhập nhầm.");
                NativeDesktop.Type(window, S("text"), () => { Guard(); if (!Focused().Equals(input)) throw new InvalidOperationException("Ô nhận phím đã đổi. Đã dừng nhập để tránh gõ nhầm."); }); break;
            case "computer_key":
                NativeDesktop.Focus(NativeDesktop.ActiveWindow(window), Guard); _ = Focused();
                NativeDesktop.Key(window, S("key"), Guard, args["hold_ms"]?.GetValue<int>() ?? (S("key").StartsWith("CTRL+", StringComparison.OrdinalIgnoreCase) ? 0 : 80)); break;
            case "computer_toggle":
                var checkbox = Element();
                if (!checkbox.TryGetCurrentPattern(TogglePattern.Pattern, out var toggler)) throw new InvalidOperationException("The element cannot be toggled.");
                var toggle = (TogglePattern)toggler;
                var desired = S("state").ToLowerInvariant() switch { "on" => ToggleState.On, "off" => ToggleState.Off, _ => throw new InvalidOperationException("state must be on or off") };
                for (var i = 0; toggle.Current.ToggleState != desired && i < 3; i++) { Validate(target, generation, cancel); toggle.Toggle(); }
                if (toggle.Current.ToggleState != desired) throw new InvalidOperationException("The requested toggle state was not reached."); break;
            case "computer_select":
                var option = Element();
                if (!option.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectable)) throw new InvalidOperationException("The element is not a selectable item.");
                Validate(target, generation, cancel); ((SelectionItemPattern)selectable).Select(); break;
            case "computer_expand":
                var container = Element();
                if (!container.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandable)) throw new InvalidOperationException("The element cannot expand/collapse.");
                Validate(target, generation, cancel);
                if (args["expanded"]?.GetValue<bool>() != false) ((ExpandCollapsePattern)expandable).Expand(); else ((ExpandCollapsePattern)expandable).Collapse(); break;
            case "computer_scroll":
                if (S("element_id").Length > 0)
                {
                    var scroller = Element();
                    if (!scroller.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll)) throw new InvalidOperationException("Phần tử không hỗ trợ ScrollPattern. Dùng ảnh mới và tọa độ vùng cần cuộn.");
                    Guard();
                    ((ScrollPattern)scroll).Scroll(S("direction") switch { "left" => ScrollAmount.LargeDecrement, "right" => ScrollAmount.LargeIncrement, _ => ScrollAmount.NoAmount },
                        S("direction") switch { "down" => ScrollAmount.LargeIncrement, "up" => ScrollAmount.LargeDecrement, _ => ScrollAmount.NoAmount });
                }
                else
                {
                    var scrollPoint = Point("x", "y"); NativeDesktop.Focus(NativeDesktop.ActiveWindow(window), Guard); CheckPoint(scrollPoint);
                    NativeDesktop.Scroll(window, scrollPoint.X, scrollPoint.Y, S("direction"), Guard);
                }
                break;
            case "computer_click":
                NativeDesktop.Point point;
                if (S("element_id").Length > 0 && args["x"] is null && args["y"] is null)
                {
                    var el = Element();
                    var clickBox = el.Current.BoundingRectangle;
                    if (clickBox.Width <= 0 || clickBox.Height <= 0)
                        throw new InvalidOperationException("Phần tử không có tọa độ hiển thị hợp lệ trên màn hình.");
                    point = new NativeDesktop.Point { X = (int)(clickBox.Left + clickBox.Width / 2), Y = (int)(clickBox.Top + clickBox.Height / 2) };
                }
                else
                {
                    point = Point("x", "y");
                }
                NativeDesktop.Focus(NativeDesktop.ActiveWindow(window), Guard); CheckPoint(point);
                NativeDesktop.Click(window, point.X, point.Y, args["double_click"]?.GetValue<bool>() == true, S("button") == "right", Guard); break;
            case "computer_drag":
                var from = Point("x", "y"); var to = Point("to_x", "to_y");
                NativeDesktop.Focus(NativeDesktop.ActiveWindow(window), Guard); CheckPoint(from); CheckPoint(to);
                NativeDesktop.Drag(window, from, to, Guard); break;
            case "computer_assert":
                var check = Element(interactive: false);
                string Observe() => S("property") switch
                {
                    "name" => check.Current.Name,
                    "value" when check.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) => ((ValuePattern)valuePattern).Current.Value,
                    "toggle" when check.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern) => ((TogglePattern)togglePattern).Current.ToggleState.ToString(),
                    "enabled" => check.Current.IsEnabled.ToString(), "offscreen" => check.Current.IsOffscreen.ToString(),
                    "selected" when check.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectedPattern) => ((SelectionItemPattern)selectedPattern).Current.IsSelected.ToString(),
                    _ => throw new InvalidOperationException("Supported assertions: name, value, toggle, enabled, offscreen, selected"),
                };
                var until = DateTime.UtcNow.AddMilliseconds(Math.Clamp(args["wait_ms"]?.GetValue<int>() ?? 1000, 0, 5000));
                string observed; bool passed;
                do
                {
                    Validate(target, generation, cancel); observed = Observe();
                    var comparison = S("property") is "enabled" or "offscreen" or "selected" or "toggle" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    passed = S("match") == "contains" ? observed.Contains(S("expected"), comparison) : observed.Equals(S("expected"), comparison);
                    if (passed || DateTime.UtcNow >= until) break;
                    cancel.WaitHandle.WaitOne(50);
                } while (true);
                return new((passed ? "assertion_passed=true" : "error: assertion_failed") + "; observed=" + JsonSerializer.Serialize(Limit(observed)));
            default: throw new InvalidOperationException("Unknown computer tool");
        }
        Validate(target, generation, cancel);
        return new("action_dispatched=true; inspect/capture and assert the resulting UI before reporting a successful test");
        }
        finally { if (mutation && _snapshot == oldSnapshot) _snapshot = ""; }
    }

    private ComputerToolResult Capture(DesktopWindow target, long generation, CancellationToken cancel, bool preview = false)
    {
        var root = (nint)target.Handle; var window = NativeDesktop.ActiveWindow(root);
        if (preview)
        {
            Validate(target, generation, cancel);
            if (NativeDesktop.GetForegroundWindow() != window) throw new InvalidOperationException("Cửa sổ kiểm thử không còn ở phía trước; đã tạm dừng xem trực tiếp.");
        }
        else NativeDesktop.Focus(window, () => Validate(target, generation, cancel));
        if (!NativeDesktop.GetWindowRect(window, out var rect) || rect.Width < 1 || rect.Height < 1) throw new InvalidOperationException("Cannot capture this window.");
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (!screen.Contains(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height))) throw new InvalidOperationException("Move the entire target window onto the screen before capturing.");
        if ((long)rect.Width * rect.Height > 32_000_000) throw new InvalidOperationException("The window is too large to capture. Resize it first.");
        using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height);
        if (!NativeDesktop.IsUncovered(root, rect)) throw new InvalidOperationException("Cửa sổ đích đang bị che. Hãy đưa cửa sổ lên trước để tránh chụp nội dung ứng dụng khác.");
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        var passwordFields = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsPasswordProperty, true));
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            foreach (AutomationElement password in passwordFields)
            {
                Validate(target, generation, cancel);
                var box = password.Current.BoundingRectangle;
                if (box.IsEmpty) continue;
                if (!double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height))
                    throw new InvalidOperationException("Không xác định được vùng ô mật khẩu; ảnh đã bị bỏ.");
                var mask = System.Drawing.Rectangle.Intersect(new(0, 0, bitmap.Width, bitmap.Height), new((int)(box.X - rect.Left), (int)(box.Y - rect.Top), (int)Math.Ceiling(box.Width), (int)Math.Ceiling(box.Height)));
                if (!mask.IsEmpty) graphics.FillRectangle(System.Drawing.Brushes.DimGray, mask);
            }
        Validate(target, generation, cancel);
        if (NativeDesktop.GetForegroundWindow() != window) throw new InvalidOperationException("Foreground window changed during capture. The image was discarded.");
        var scale = Math.Min(1, 1600d / rect.Width);
        using var scaled = new System.Drawing.Bitmap(bitmap, new System.Drawing.Size(Math.Max(1, (int)(rect.Width * scale)), Math.Max(1, (int)(rect.Height * scale))));
        using var stream = new MemoryStream(); scaled.Save(stream, ImageFormat.Jpeg);
        if (!NativeDesktop.GetWindowRect(window, out var finalBounds) || !finalBounds.Equals(rect)) throw new InvalidOperationException("Cửa sổ thay đổi khi chụp; ảnh đã bị bỏ, hãy chụp lại.");
        Validate(target, generation, cancel);
        if (preview) return new("Xem trực tiếp trên máy.", "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray()));
        _capturedAt = DateTime.UtcNow; _snapshot = Guid.NewGuid().ToString("N");
        _imageMap = new(rect, scaled.Width, scaled.Height); _captureWindow = window;
        var bytes = stream.ToArray();
        return new(JsonSerializer.Serialize(new { snapshot_id = _snapshot, active_window = window.ToInt64(), width = scaled.Width, height = scaled.Height, window_width = rect.Width, window_height = rect.Height,
            image_fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            image_scale = new { x = scaled.Width / (double)rect.Width, y = scaled.Height / (double)rect.Height },
            coordinate_space = "screenshot image pixels; multiply UIA physical bounds by image_scale for image coordinates", window = target.Title }, Json), "data:image/jpeg;base64," + Convert.ToBase64String(bytes));
    }
    private static string Limit(string? text) => text is null ? "" : text[..Math.Min(text.Length, 600)];
}
