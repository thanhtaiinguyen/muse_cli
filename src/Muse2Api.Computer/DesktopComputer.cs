using System.Diagnostics;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Muse2Api.Agent;

namespace Muse2Api.Computer;

public sealed record DesktopWindow(long Handle, int ProcessId, string Title)
{
    public override string ToString() => Title;
}

/// <summary>UI Automation is executed off the UI thread, on MTA workers.</summary>
public sealed class DesktopComputer : IComputerTools
{
    public static bool IsRuntimeAvailable => typeof(AutomationElement).Assembly.GetName().Name == "UIAutomationClient";
    public const int EmergencyHotkey = 0x4D55;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AutomationElement> _elements = new();
    private DesktopWindow? _target;
    private DateTime _inspectedAt, _capturedAt;
    private NativeDesktop.Rect _capturedBounds;
    private string _snapshot = "";
    private double _imageScale = 1;
    private long _generation;
    private bool _authorizationRequested;
    public Func<CancellationToken, Task<bool>>? RequestAuthorization { get; set; }
    public void BeginTurn() => _authorizationRequested = false;
    public DesktopWindow? Target => _target;
    public string Context => _target is { } target
        ? $"enabled for one window: {target.Title}, process {target.ProcessId}. Use computer_inspect to get element_id; screenshot/click coordinates use screenshot pixels. Esc stops control."
        : RequestAuthorization is not null ? "not yet authorized. Calling computer_inspect will open a window chooser and ask the user for access. Never bypass it through shell commands."
        : "disabled. Ask the user to select a test window using the Màn hình button. Never attempt screen control through shell commands.";

    public static IReadOnlyList<DesktopWindow> ListWindows()
    {
        var windows = new List<DesktopWindow>();
        NativeDesktop.EnumWindows((handle, _) =>
        {
            if (!NativeDesktop.IsWindowVisible(handle)) return true;
            NativeDesktop.GetWindowThreadProcessId(handle, out var pid);
            if (pid == Environment.ProcessId) return true; // Never automate Muse's own permission controls.
            var text = new StringBuilder(512); NativeDesktop.GetWindowText(handle, text, text.Capacity);
            if (text.Length > 0) windows.Add(new(handle.ToInt64(), (int)pid, text.ToString()));
            return true;
        }, 0);
        return windows.OrderBy(w => w.Title).ToArray();
    }

    public void Authorize(DesktopWindow target)
    {
        NativeDesktop.GetWindowThreadProcessId((nint)target.Handle, out var pid);
        if (pid != target.ProcessId || pid == Environment.ProcessId || !NativeDesktop.IsWindow((nint)target.Handle))
            throw new InvalidOperationException("The selected window no longer exists or belongs to Muse. Refresh the window list.");
        Disarm(); _target = target; _snapshot = ""; _inspectedAt = DateTime.MinValue;
    }
    public void Disarm() { _target = null; Interlocked.Increment(ref _generation); }
    public static bool RegisterEmergencyStop(nint window) => NativeDesktop.RegisterHotKey(window, EmergencyHotkey, 0x4000, 0x1B);
    public static void UnregisterEmergencyStop(nint window) => NativeDesktop.UnregisterHotKey(window, EmergencyHotkey);

    public async Task<ComputerToolResult> ExecuteAsync(ToolCall call, CancellationToken cancel)
    {
        if (_target is null && RequestAuthorization is not null)
        {
            if (_authorizationRequested) return new("error: user declined screen control for this turn. Do not request it again.");
            _authorizationRequested = true;
            if (!await RequestAuthorization(cancel)) return new("error: user declined screen control for this turn. Do not request it again.");
        }
        var target = _target ?? throw new InvalidOperationException("Screen control is disabled. Select a target using Màn hình first.");
        if (call.Name == "computer_list_windows") return new(JsonSerializer.Serialize(new[] { target }));
        var generation = Interlocked.Read(ref _generation);
        await _gate.WaitAsync(cancel);
        // Hold the gate until the worker actually exits, even if its UI provider hangs.
        var work = Task.Run(() => Execute(call, target, generation, cancel), CancellationToken.None);
        _ = work.ContinueWith(_ => _gate.Release(), TaskScheduler.Default);
        try { return await work.WaitAsync(TimeSpan.FromSeconds(12), cancel); }
        catch (TimeoutException) { Disarm(); throw new InvalidOperationException("The target UI stopped responding. Screen control was stopped; retry after the application responds."); }
    }

    private void Validate(DesktopWindow target, long generation, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (generation != Interlocked.Read(ref _generation) || _target != target) throw new OperationCanceledException("Screen control was stopped.", cancel);
        NativeDesktop.GetWindowThreadProcessId((nint)target.Handle, out var pid);
        if (!NativeDesktop.IsWindow((nint)target.Handle) || pid != target.ProcessId) throw new InvalidOperationException("The target window closed. Select the test window again.");
    }

    private ComputerToolResult Execute(ToolCall call, DesktopWindow target, long generation, CancellationToken cancel)
    {
        Validate(target, generation, cancel);
        var window = (nint)target.Handle;
        var args = call.Args;
        string S(string key) => args[key]?.ToString() ?? "";
        AutomationElement Element(bool interactive = true)
        {
            if (DateTime.UtcNow - _inspectedAt > TimeSpan.FromSeconds(180) || !_elements.TryGetValue(S("element_id"), out var element))
                throw new InvalidOperationException("Element id is stale or unknown; run computer_inspect again.");
            if (element.Current.ProcessId != target.ProcessId || element.Current.IsPassword || interactive && (!element.Current.IsEnabled || element.Current.IsOffscreen))
                throw new InvalidOperationException("The element is unavailable, disabled, hidden or a password input. Inspect again.");
            return element;
        }
        switch (call.Name)
        {
            case "computer_inspect":
                _elements.Clear();
                NativeDesktop.GetWindowRect(window, out var rect);
                var root = AutomationElement.FromHandle(window);
                var pending = new Queue<(AutomationElement Element, int Depth)>(); pending.Enqueue((root, 0));
                var nodes = new List<object>(); var timer = Stopwatch.StartNew();
                while (pending.Count > 0 && nodes.Count < 180 && timer.Elapsed.TotalSeconds < 8)
                {
                    Validate(target, generation, cancel);
                    var (element, depth) = pending.Dequeue();
                    try
                    {
                        var info = element.Current;
                        if (info.IsPassword) continue;
                        var id = "e" + nodes.Count; _elements[id] = element;
                        var box = info.BoundingRectangle;
                        if (box.IsEmpty || !double.IsFinite(box.X) || !double.IsFinite(box.Y)) box = new System.Windows.Rect(0, 0, 0, 0);
                        var value = element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) ? ((ValuePattern)vp).Current.Value : "";
                        nodes.Add(new { element_id = id, depth, name = Limit(info.Name), automation_id = info.AutomationId,
                            role = info.ControlType.ProgrammaticName, enabled = info.IsEnabled, offscreen = info.IsOffscreen,
                            value = Limit(value), bounds = new { x = box.X - rect.Left, y = box.Y - rect.Top, width = box.Width, height = box.Height },
                            patterns = element.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray() });
                        if (depth >= 8) continue;
                        var walker = TreeWalker.ControlViewWalker;
                        var child = walker.GetFirstChild(element); var count = 0;
                        while (child is not null && count++ < 180 && pending.Count < 250)
                        { pending.Enqueue((child, depth + 1)); child = walker.GetNextSibling(child); }
                    }
                    catch (ElementNotAvailableException) { }
                }
                _inspectedAt = DateTime.UtcNow;
                return new(JsonSerializer.Serialize(new { window = target.Title, coordinate_space = "window physical pixels; prefer element_id", elements = nodes, truncated = pending.Count > 0 }));
            case "computer_screenshot": return Capture(target, generation, cancel);
            case "computer_focus": NativeDesktop.Focus(window); return new("action_dispatched=true; target window is foreground");
            case "computer_invoke":
                var clickable = Element();
                if (!clickable.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) throw new InvalidOperationException("This element cannot be invoked. Inspect its children or use a fresh screenshot.");
                Validate(target, generation, cancel); ((InvokePattern)invoke).Invoke(); break;
            case "computer_set_value":
                var editable = Element();
                if (!editable.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || ((ValuePattern)pattern).Current.IsReadOnly)
                    throw new InvalidOperationException("This element has no writable value. Use computer_type on a focused input.");
                var text = S("text"); if (text.Length > 16000) throw new InvalidOperationException("Value is too long.");
                Validate(target, generation, cancel); ((ValuePattern)pattern).SetValue(text); break;
            case "computer_type":
                var input = Element(); NativeDesktop.Focus(window); input.SetFocus();
                Validate(target, generation, cancel);
                if (!input.Current.HasKeyboardFocus) throw new InvalidOperationException("The requested input did not receive focus.");
                NativeDesktop.Type(window, S("text")); break;
            case "computer_key": Validate(target, generation, cancel); NativeDesktop.Key(window, S("key")); break;
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
                var scroller = Element();
                if (!scroller.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll)) throw new InvalidOperationException("This element does not support scrolling.");
                Validate(target, generation, cancel);
                ((ScrollPattern)scroll).Scroll(ScrollAmount.NoAmount, S("direction") switch { "down" => ScrollAmount.LargeIncrement, "up" => ScrollAmount.LargeDecrement, _ => throw new InvalidOperationException("direction must be up or down") }); break;
            case "computer_click":
                if (S("snapshot_id") != _snapshot || DateTime.UtcNow - _capturedAt > TimeSpan.FromSeconds(120)) throw new InvalidOperationException("Capture a fresh screenshot and use its snapshot_id before clicking.");
                NativeDesktop.GetWindowRect(window, out var now);
                if (!now.Equals(_capturedBounds)) throw new InvalidOperationException("The window moved or resized. Capture again.");
                var x = args["x"]?.GetValue<int>() ?? -1; var y = args["y"]?.GetValue<int>() ?? -1;
                if (x < 0 || y < 0 || x >= now.Width * _imageScale || y >= now.Height * _imageScale) throw new InvalidOperationException("Click coordinates are outside the screenshot.");
                Validate(target, generation, cancel);
                NativeDesktop.Click(window, now.Left + (int)(x / _imageScale), now.Top + (int)(y / _imageScale), args["double_click"]?.GetValue<bool>() == true); break;
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

    private ComputerToolResult Capture(DesktopWindow target, long generation, CancellationToken cancel)
    {
        var window = (nint)target.Handle; NativeDesktop.Focus(window);
        if (!NativeDesktop.GetWindowRect(window, out var rect) || rect.Width < 1 || rect.Height < 1) throw new InvalidOperationException("Cannot capture this window.");
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (!screen.Contains(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height))) throw new InvalidOperationException("Move the entire target window onto the screen before capturing.");
        if ((long)rect.Width * rect.Height > 32_000_000) throw new InvalidOperationException("The window is too large to capture. Resize it first.");
        using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        Validate(target, generation, cancel);
        if (NativeDesktop.GetForegroundWindow() != window) throw new InvalidOperationException("Foreground window changed during capture. The image was discarded.");
        _imageScale = Math.Min(1, 1600d / rect.Width);
        using var scaled = new System.Drawing.Bitmap(bitmap, new System.Drawing.Size((int)(rect.Width * _imageScale), (int)(rect.Height * _imageScale)));
        using var stream = new MemoryStream(); scaled.Save(stream, ImageFormat.Jpeg);
        _capturedBounds = rect; _capturedAt = DateTime.UtcNow; _snapshot = Guid.NewGuid().ToString("N");
        return new(JsonSerializer.Serialize(new { snapshot_id = _snapshot, width = scaled.Width, height = scaled.Height, coordinate_space = "screenshot image pixels", window = target.Title }), "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray()));
    }
    private static string Limit(string? text) => text is null ? "" : text[..Math.Min(text.Length, 600)];
}
