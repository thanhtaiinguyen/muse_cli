using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;

namespace Muse2Api.WinForms;

/// <summary>Base card for all transcript elements in the conversation.</summary>
public abstract class ChatCard : Panel
{
    public event EventHandler? ContentHeightChanged;
    public abstract void RefreshTheme();
    protected void OnContentHeightChanged() => ContentHeightChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Codex-style conversational transcript with thinking blocks, streaming typewriter text, and tool interaction cards.</summary>
public sealed class ChatTranscript : UserControl
{
    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
    }

    private readonly BufferedPanel _viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly List<ChatCard> _cards = new();
    private bool _layingOut;

    public ChatTranscript()
    {
        Dock = DockStyle.Fill;
        BackColor = _viewport.BackColor = DesktopTheme.Background;
        Controls.Add(_viewport);
        _viewport.SizeChanged += (_, _) => Reflow();
        DesktopTheme.ThemeChanged += ThemeChanged;
    }

    public void RefreshTheme()
    {
        BackColor = _viewport.BackColor = DesktopTheme.Background;
        foreach (var card in _cards) card.RefreshTheme();
        Invalidate();
    }

    private void ThemeChanged(object? sender, EventArgs e) => RefreshTheme();

    protected override void Dispose(bool disposing)
    {
        if (disposing) DesktopTheme.ThemeChanged -= ThemeChanged;
        base.Dispose(disposing);
    }

    public ChatMessageCard AddUserMessage(string text)
    {
        var card = new ChatMessageCard("Bạn", text, isUser: true, stream: false);
        AddCard(card);
        return card;
    }

    public ChatMessageCard AddAssistantMessage(string text, bool stream = false)
    {
        var card = new ChatMessageCard("Muse", text, isUser: false, stream: stream);
        AddCard(card);
        return card;
    }

    public ThinkingCard AddThinking(string title = "Đang suy nghĩ…")
    {
        var card = new ThinkingCard(title);
        AddCard(card);
        return card;
    }

    public ToolCallCard AddToolCall(string tool, string? path, string detail)
    {
        var card = new ToolCallCard(tool, path, detail);
        AddCard(card);
        return card;
    }

    public NoticeCard AddNotice(string text)
    {
        var card = new NoticeCard(text);
        AddCard(card);
        return card;
    }

    public ChatMessageCard AddMessage(string role, string text)
    {
        var isUser = role is "Bạn" or "bạn" or "user";
        var isNotice = role is "Thông báo" or "notice";
        if (isNotice)
        {
            var notice = AddNotice(text);
            return new ChatMessageCard(role, text, isUser, false);
        }
        var card = new ChatMessageCard(role, text, isUser, stream: false);
        AddCard(card);
        return card;
    }

    public void UpdateMessage(ChatMessageCard card, string text)
    {
        var follow = IsNearBottom();
        card.SetText(text);
        Reflow();
        if (follow) ScrollToBottom();
    }

    public void RemoveMessage(ChatMessageCard card)
    {
        _cards.Remove(card);
        _viewport.Controls.Remove(card);
        card.Dispose();
        Reflow();
    }

    private void AddCard(ChatCard card)
    {
        var follow = IsNearBottom();
        card.ContentHeightChanged += (_, _) => Reflow();
        _cards.Add(card);
        _viewport.Controls.Add(card);
        Reflow();
        if (follow) ScrollToBottom();
    }

    public void Clear()
    {
        foreach (var card in _cards) card.Dispose();
        _cards.Clear();
        _viewport.Controls.Clear();
        _viewport.AutoScrollMinSize = Size.Empty;
        _viewport.AutoScrollPosition = Point.Empty;
    }

    public bool IsNearBottom()
        => _viewport.VerticalScroll.Value + _viewport.ClientSize.Height >= _viewport.AutoScrollMinSize.Height - 120;

    public void ScrollToBottom()
    {
        if (_viewport.AutoScrollMinSize.Height > _viewport.ClientSize.Height)
        {
            _viewport.AutoScrollPosition = new Point(0, _viewport.AutoScrollMinSize.Height - _viewport.ClientSize.Height);
        }
    }

    public void Reflow()
    {
        if (_layingOut || IsDisposed) return;
        _layingOut = true;
        try
        {
            var available = Math.Max(200, _viewport.ClientSize.Width - 24);
            var y = 14;
            _viewport.SuspendLayout();
            foreach (var card in _cards)
            {
                int width;
                int x;
                if (card is ChatMessageCard { IsUser: true })
                {
                    width = Math.Min(740, (int)(available * 0.82f));
                    x = available - width;
                }
                else if (card is ThinkingCard or ToolCallCard)
                {
                    width = Math.Min(880, available);
                    x = 0;
                }
                else
                {
                    width = Math.Min(900, available);
                    x = 0;
                }
                card.Width = Math.Max(120, width);
                card.Location = new Point(x, y);
                y += card.Height + 14;
            }
            _viewport.AutoScrollMinSize = new Size(0, y + 10);
            _viewport.ResumeLayout();
        }
        finally { _layingOut = false; }
    }
}

/// <summary>Codex-style thinking disclosure card showing active reasoning and duration.</summary>
public sealed class ThinkingCard : ChatCard
{
    private readonly Label _icon = new() { AutoSize = false, Font = new Font("Segoe UI Emoji", 11f), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _title = new() { AutoSize = false, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _chevron = new() { AutoSize = false, Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand };
    private readonly Panel _header = new() { Height = 36, Dock = DockStyle.Top, Cursor = Cursors.Hand };
    private readonly ListBox _thoughts = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        Font = new Font("Segoe UI", 9f),
        SelectionMode = SelectionMode.None
    };
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private bool _expanded = true;
    private bool _active = true;
    private string _baseTitle;

    public bool IsActive => _active;

    public ThinkingCard(string title)
    {
        _baseTitle = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = DesktopTheme.Surface;
        ForeColor = DesktopTheme.Text;
        Padding = new Padding(12, 4, 12, 8);

        _icon.SetBounds(0, 4, 28, 28);
        _icon.Text = "💭";
        _title.ForeColor = DesktopTheme.Accent;
        _title.Text = $"{title} (0s)";
        _chevron.ForeColor = DesktopTheme.Muted;
        _chevron.Text = "▾ Thu gọn";

        _header.Controls.Add(_icon);
        _header.Controls.Add(_title);
        _header.Controls.Add(_chevron);
        _header.Click += (_, _) => ToggleExpanded();
        _chevron.Click += (_, _) => ToggleExpanded();
        _title.Click += (_, _) => ToggleExpanded();
        _icon.Click += (_, _) => ToggleExpanded();

        _thoughts.BackColor = DesktopTheme.Surface;
        _thoughts.ForeColor = DesktopTheme.Muted;
        _thoughts.ItemHeight = 22;

        Controls.Add(_thoughts);
        Controls.Add(_header);

        _timer.Tick += (_, _) =>
        {
            if (_active)
            {
                var secs = _watch.Elapsed.TotalSeconds;
                _title.Text = $"{_baseTitle} ({secs:F1}s)";
            }
        };
        _timer.Start();
        RefreshHeight();
    }

    public void AddThought(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _thoughts.Items.Add("• " + text.Trim());
        if (_thoughts.Items.Count > 0)
            _thoughts.TopIndex = _thoughts.Items.Count - 1;
        RefreshHeight();
    }

    public void Complete(string? summary = null)
    {
        if (!_active) return;
        _active = false;
        _timer.Stop();
        _watch.Stop();
        var duration = _watch.Elapsed.TotalSeconds;
        _title.Text = summary ?? $"Đã suy nghĩ xong trong {duration:F1}s";
        _title.ForeColor = DesktopTheme.Muted;
        _icon.Text = "💡";
        _expanded = false; // Auto-compact when thinking finishes like Codex
        _chevron.Text = "▸ Chi tiết suy nghĩ";
        RefreshHeight();
    }

    private void ToggleExpanded()
    {
        _expanded = !_expanded;
        _chevron.Text = _expanded ? "▾ Thu gọn" : "▸ Chi tiết suy nghĩ";
        RefreshHeight();
    }

    private void RefreshHeight()
    {
        var headerWidth = Math.Max(100, Width - 24);
        _header.Width = headerWidth;
        _title.SetBounds(32, 4, Math.Max(60, headerWidth - 140), 28);
        _chevron.SetBounds(Math.Max(32, headerWidth - 105), 4, 100, 28);

        if (_expanded && _thoughts.Items.Count > 0)
        {
            _thoughts.Visible = true;
            var thoughtHeight = Math.Min(160, Math.Max(26, _thoughts.Items.Count * 22 + 6));
            _thoughts.Height = thoughtHeight;
            Height = _header.Height + thoughtHeight + 14;
        }
        else
        {
            _thoughts.Visible = false;
            Height = _header.Height + 8;
        }
        UpdateRegion();
        OnContentHeightChanged();
        Invalidate();
    }

    public override void RefreshTheme()
    {
        BackColor = DesktopTheme.Surface;
        _thoughts.BackColor = DesktopTheme.Surface;
        _title.ForeColor = _active ? DesktopTheme.Accent : DesktopTheme.Muted;
        _thoughts.ForeColor = DesktopTheme.Muted;
        _chevron.ForeColor = DesktopTheme.Muted;
        UpdateRegion();
        Invalidate();
    }

    private void UpdateRegion()
    {
        if (Width < 10 || Height < 10) return;
        Region?.Dispose();
        Region = new Region(DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 16));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 16);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var border = new Pen(DesktopTheme.Border);
        e.Graphics.DrawPath(border, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Codex-style tool card displaying tool action, arguments, status badge, and expandable output.</summary>
public sealed class ToolCallCard : ChatCard
{
    private readonly Label _icon = new() { AutoSize = false, Font = new Font("Segoe UI Emoji", 11f), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _action = new() { AutoSize = false, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _target = new() { AutoSize = false, Font = DesktopTheme.Code, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _status = new() { AutoSize = false, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _viewTerminal = new() { Text = "Xem trong Terminal ›", AutoSize = false, Font = DesktopTheme.Small, TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand };
    private readonly RichTextBox _preview = new()
    {
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        WordWrap = false,
        Font = DesktopTheme.Code,
        Visible = false,
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.None
    };

    public event Action? ViewDetailsClicked;
    private bool _hasError;
    private string _tool;
    private string? _path;

    public ToolCallCard(string tool, string? path, string detail)
    {
        _tool = tool;
        _path = path;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = DesktopTheme.Surface;
        Padding = new Padding(14, 10, 14, 10);

        _icon.SetBounds(12, 10, 24, 24);
        _icon.Text = GetToolIcon(tool);

        _action.ForeColor = DesktopTheme.Text;
        _action.Text = GetToolVerb(tool);

        _target.ForeColor = DesktopTheme.Accent;
        _target.Text = !string.IsNullOrWhiteSpace(path) ? path : detail.Length > 80 ? detail[..77] + "…" : detail;

        _status.ForeColor = Color.White;
        _status.BackColor = DesktopTheme.Accent;
        _status.Text = "● Đang chạy…";

        _viewTerminal.ForeColor = DesktopTheme.Muted;
        _viewTerminal.Click += (_, _) => ViewDetailsClicked?.Invoke();

        _preview.BackColor = DesktopTheme.Background;
        _preview.ForeColor = DesktopTheme.Muted;

        Controls.AddRange(new Control[] { _icon, _action, _target, _status, _viewTerminal, _preview });
        RefreshHeight();
    }

    public void AppendOutput(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return;
        if (!_preview.Visible)
        {
            _preview.Visible = true;
            _preview.Clear();
        }
        _preview.AppendText(chunk);
        if (_preview.TextLength > 300)
        {
            _preview.Select(0, _preview.TextLength - 300);
            _preview.SelectedText = "";
        }
        RefreshHeight();
    }

    public void SetResult(string result, bool isError)
    {
        _hasError = isError;
        _status.BackColor = isError ? DesktopTheme.Notice : DesktopTheme.Success;
        _status.ForeColor = Color.FromArgb(20, 20, 30);
        _status.Text = isError ? "✕ Thất bại" : "✓ Hoàn tất";

        var snippet = SummarizeResult(result);
        if (!string.IsNullOrWhiteSpace(snippet))
        {
            _preview.Visible = true;
            _preview.Text = snippet;
        }
        RefreshHeight();
    }

    private string SummarizeResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Replace("\r\n", "\n").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(3);
        return string.Join("\n", lines);
    }

    private static string GetToolIcon(string tool) => tool switch
    {
        "read_file" or "read_files" => "📖",
        "write_file" or "edit_file" => "✏️",
        "run_command" or "terminal_result" => "⚡",
        "search_files" or "list_files" => "🔍",
        "git_status" or "git_diff" => "🔀",
        _ when tool.StartsWith("computer_") => "🖥️",
        _ => "⚙️"
    };

    private static string GetToolVerb(string tool) => tool switch
    {
        "read_file" => "Đọc tệp:",
        "read_files" => "Đọc các tệp:",
        "write_file" => "Ghi tệp:",
        "edit_file" => "Sửa tệp:",
        "run_command" => "Chạy lệnh:",
        "search_files" => "Tìm kiếm:",
        "list_files" => "Cấu trúc thư mục:",
        "git_status" => "Trạng thái Git:",
        "git_diff" => "Thay đổi Git:",
        "computer_screenshot" => "Chụp màn hình:",
        "computer_inspect" => "Đọc giao diện:",
        "computer_click" or "computer_invoke" => "Thao tác giao diện:",
        _ => "Công cụ:"
    };

    private void RefreshHeight()
    {
        var scale = DeviceDpi / 96f;
        var padding = (int)(12 * scale);
        _icon.SetBounds(padding, padding, (int)(24 * scale), (int)(24 * scale));

        var verbWidth = TextRenderer.MeasureText(_action.Text, _action.Font).Width + 8;
        _action.SetBounds(_icon.Right + 6, padding + 2, verbWidth, (int)(22 * scale));

        var statusWidth = (int)(100 * scale);
        _status.SetBounds(Math.Max(padding, Width - padding - statusWidth), padding, statusWidth, (int)(24 * scale));

        var targetWidth = Math.Max(40, _status.Left - _action.Right - 10);
        _target.SetBounds(_action.Right + 2, padding + 2, targetWidth, (int)(22 * scale));

        var currentY = _action.Bottom + 6;
        if (_preview.Visible && _preview.TextLength > 0)
        {
            var previewLines = Math.Min(3, _preview.Lines.Length);
            var previewHeight = Math.Max(26, previewLines * 18 + 8);
            _preview.SetBounds(padding + (int)(28 * scale), currentY, Math.Max(40, Width - padding * 2 - (int)(28 * scale)), previewHeight);
            currentY = _preview.Bottom + 4;
        }
        else
        {
            _preview.Visible = false;
        }

        _viewTerminal.SetBounds(Math.Max(padding, Width - padding - (int)(160 * scale)), currentY, (int)(160 * scale), (int)(20 * scale));
        Height = _viewTerminal.Bottom + padding;

        UpdateRegion();
        OnContentHeightChanged();
        Invalidate();
    }

    public override void RefreshTheme()
    {
        BackColor = DesktopTheme.Surface;
        _action.ForeColor = DesktopTheme.Text;
        _target.ForeColor = DesktopTheme.Accent;
        _preview.BackColor = DesktopTheme.Background;
        _preview.ForeColor = DesktopTheme.Muted;
        _viewTerminal.ForeColor = DesktopTheme.Muted;
        UpdateRegion();
        Invalidate();
    }

    private void UpdateRegion()
    {
        if (Width < 10 || Height < 10) return;
        Region?.Dispose();
        Region = new Region(DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 16));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 16);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var border = new Pen(_hasError ? DesktopTheme.Notice : DesktopTheme.Border);
        e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Notification card for system errors, cancellations, and status messages.</summary>
public sealed class NoticeCard : ChatCard
{
    private readonly Label _role = new() { AutoSize = false, Font = DesktopTheme.Strong, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _body = new() { AutoSize = false, Font = DesktopTheme.Body, TextAlign = ContentAlignment.TopLeft };

    public NoticeCard(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = DesktopTheme.Surface;
        ForeColor = DesktopTheme.Notice;
        Padding = new Padding(16, 12, 16, 12);

        _role.Text = "THÔNG BÁO";
        _role.ForeColor = DesktopTheme.Notice;
        _role.SetBounds(16, 10, 160, 22);

        _body.ForeColor = DesktopTheme.Text;
        _body.Text = text;

        Controls.Add(_role);
        Controls.Add(_body);
        RefreshHeight();
    }

    private void RefreshHeight()
    {
        var textWidth = Math.Max(40, Width - 32);
        var measured = TextRenderer.MeasureText(_body.Text, _body.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak);
        _body.SetBounds(16, 34, textWidth, Math.Max(24, measured.Height + 4));
        Height = _body.Bottom + 12;
        UpdateRegion();
        OnContentHeightChanged();
        Invalidate();
    }

    public override void RefreshTheme()
    {
        BackColor = DesktopTheme.Surface;
        _role.ForeColor = DesktopTheme.Notice;
        _body.ForeColor = DesktopTheme.Text;
        UpdateRegion();
        Invalidate();
    }

    private void UpdateRegion()
    {
        if (Width < 10 || Height < 10) return;
        Region?.Dispose();
        Region = new Region(DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 16));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 16);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var border = new Pen(DesktopTheme.Notice);
        e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Message bubble for user prompts and assistant streaming responses with typewriter output ("nhả chữ").</summary>
public sealed class ChatMessageCard : ChatCard
{
    private readonly Label _role = new() { AutoSize = false, Font = DesktopTheme.Strong, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _copy = new() { Text = "Sao chép", AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = DesktopTheme.Small, Cursor = Cursors.Hand };
    private readonly RichTextBox _body = new()
    {
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        WordWrap = true,
        ScrollBars = RichTextBoxScrollBars.None,
        DetectUrls = false,
        HideSelection = false,
        Font = new Font("Segoe UI", 10.5f),
        TabStop = true,
    };

    private readonly Queue<char> _streamQueue = new();
    private readonly System.Windows.Forms.Timer _streamTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _copyFeedbackTimer = new() { Interval = 1500 };
    private string _source = "";
    private int _contentHeight = 30;
    private bool _formatting, _resizing;
    private bool _streaming;

    public bool IsUser { get; }
    public bool IsActivity { get; }

    public ChatMessageCard(string role, string text, bool isUser, bool stream)
    {
        IsUser = isUser || role is "Bạn" or "bạn" or "user";
        IsActivity = role is "Hoạt động" or "Terminal";
        BackColor = IsUser ? DesktopTheme.UserBackground : DesktopTheme.Surface;

        _role.Text = IsUser ? "BẠN" : "MUSE · AI";
        _role.ForeColor = IsUser ? DesktopTheme.Accent : DesktopTheme.Primary;
        _body.BackColor = BackColor;
        _body.ForeColor = DesktopTheme.Text;
        _copy.ForeColor = DesktopTheme.Muted;
        _copy.BackColor = _role.BackColor = BackColor;

        AccessibleName = _role.Text;
        AccessibleRole = AccessibleRole.Grouping;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Controls.AddRange(new Control[] { _role, _copy, _body });

        _copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_source);
                _copy.Text = "Đã sao chép!";
                _copyFeedbackTimer.Start();
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };

        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copy.Text = "Sao chép";
            _copyFeedbackTimer.Stop();
        };

        _streamTimer.Tick += OnStreamTick;

        _body.ContentsResized += (_, e) =>
        {
            _contentHeight = Math.Max(24, e.NewRectangle.Height + 6);
            if (!_formatting) RefreshHeight();
        };

        if (stream && !IsUser && text.Length > 0)
        {
            StartStream(text);
        }
        else
        {
            SetText(text);
        }
    }

    public void StartStream(string initialText)
    {
        _streaming = true;
        _source = initialText;
        _streamQueue.Clear();
        foreach (var ch in initialText) _streamQueue.Enqueue(ch);
        _body.Clear();
        _body.AppendText("▊");
        _streamTimer.Start();
    }

    public void AppendStream(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _source += delta;
        foreach (var ch in delta) _streamQueue.Enqueue(ch);
        if (!_streamTimer.Enabled) _streamTimer.Start();
    }

    public void SetStreamTarget(string fullText)
    {
        if (string.IsNullOrEmpty(fullText)) return;
        if (!_streaming)
        {
            SetText(fullText);
            return;
        }
        if (fullText.StartsWith(_source, StringComparison.Ordinal))
        {
            var delta = fullText[_source.Length..];
            AppendStream(delta);
        }
        else
        {
            _source = fullText;
            _streamQueue.Clear();
            foreach (var ch in fullText) _streamQueue.Enqueue(ch);
            if (!_streamTimer.Enabled) _streamTimer.Start();
        }
    }

    public void FinalizeStream()
    {
        _streamTimer.Stop();
        _streaming = false;
        _streamQueue.Clear();
        SetText(_source, force: true);
    }

    private void OnStreamTick(object? sender, EventArgs e)
    {
        if (_streamQueue.Count == 0)
        {
            if (!_streaming) _streamTimer.Stop();
            return;
        }

        // Output in small bursts for smooth 60fps typewriter "nhả chữ"
        var burst = Math.Min(_streamQueue.Count, 3);
        var chars = new char[burst];
        for (var i = 0; i < burst; i++) chars[i] = _streamQueue.Dequeue();
        var chunk = new string(chars);

        _body.SuspendLayout();
        if (_body.Text.EndsWith('▊'))
        {
            _body.Select(_body.TextLength - 1, 1);
            _body.SelectedText = chunk + "▊";
        }
        else
        {
            _body.AppendText(chunk + "▊");
        }
        _body.Select(_body.TextLength, 0);
        _body.ResumeLayout();

        RefreshHeight();

        if (_streamQueue.Count == 0 && !_streaming)
        {
            FinalizeStream();
        }
    }

    public void SetText(string text, bool force = false)
    {
        if (_source == text && !force && !_streaming) return;
        _source = text;
        _formatting = true;
        _body.SuspendLayout();
        _body.Clear();
        var code = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) { code = !code; continue; }
            var heading = !code && line.StartsWith('#');
            var visible = heading ? line.TrimStart('#', ' ') : line;
            if (!code && visible.StartsWith("- ")) visible = "• " + visible[2..];
            var bold = !code && Regex.IsMatch(visible, @"\*\*.+?\*\*");
            if (!code) visible = Regex.Replace(visible, @"\*\*(.+?)\*\*", "$1");
            _body.SelectionFont = code ? DesktopTheme.Code : heading || bold ? DesktopTheme.Strong : _body.Font;
            _body.SelectionColor = DesktopTheme.Text;
            _body.SelectionBackColor = code ? DesktopTheme.Background : BackColor;
            _body.AppendText(visible + "\n");
        }
        if (_body.TextLength > 0)
        {
            _body.Select(_body.TextLength - 1, 1);
            _body.SelectedText = "";
        }
        _body.Select(0, 0);
        _body.ResumeLayout();
        _formatting = false;
        RefreshHeight();
    }

    public void RefreshHeight()
    {
        if (_formatting || _resizing || Width < 80) return;
        _resizing = true;
        try
        {
            var padding = 16;
            _role.SetBounds(padding, 10, Math.Max(60, Width - 120), 24);
            _copy.SetBounds(Math.Max(padding, Width - 95), 10, 80, 24);
            _body.SetBounds(padding, 38, Math.Max(30, Width - padding * 2), Math.Clamp(_contentHeight, 26, 3000));
            if (_body.IsHandleCreated)
            {
                _contentHeight = _body.GetPositionFromCharIndex(Math.Max(0, _body.TextLength - 1)).Y
                    - _body.GetPositionFromCharIndex(0).Y + _body.Font.Height + 10;
                _body.Height = Math.Clamp(_contentHeight, 26, 3000);
            }
            _body.ScrollBars = _contentHeight > 3000 ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None;
            var desired = _body.Bottom + padding;
            if (Height != desired)
            {
                Height = desired;
                OnContentHeightChanged();
            }
            UpdateRegion();
        }
        finally { _resizing = false; }
    }

    public override void RefreshTheme()
    {
        BackColor = IsUser ? DesktopTheme.UserBackground : DesktopTheme.Surface;
        _role.ForeColor = IsUser ? DesktopTheme.Accent : DesktopTheme.Primary;
        _role.BackColor = _copy.BackColor = _body.BackColor = BackColor;
        _body.ForeColor = DesktopTheme.Text;
        _copy.ForeColor = DesktopTheme.Muted;
        SetText(_source, force: true);
        UpdateRegion();
        Invalidate();
    }

    private void UpdateRegion()
    {
        if (Width < 10 || Height < 10) return;
        Region?.Dispose();
        Region = new Region(DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), DesktopTheme.CornerDiameter));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), DesktopTheme.CornerDiameter);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var border = new Pen(IsUser ? DesktopTheme.UserBorder : DesktopTheme.Border);
        e.Graphics.DrawPath(border, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _streamTimer.Dispose();
            _copyFeedbackTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
