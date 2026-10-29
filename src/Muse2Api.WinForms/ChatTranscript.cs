using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;

namespace Muse2Api.WinForms;

/// <summary>Base card for all transcript elements in the conversation.</summary>
public abstract class ChatCard : Panel
{
    public event EventHandler? ContentHeightChanged;
    public abstract void RefreshTheme();
    public virtual int DesiredHeight => Height;
    protected void OnContentHeightChanged() => ContentHeightChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Codex-style conversational transcript with thinking blocks, typewriter streaming, and compact tool cards.</summary>
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
    private bool _stickToBottom = true;
    private bool _scrollQueued;
    private bool _autoScrolling;

    public ChatTranscript()
    {
        Dock = DockStyle.Fill;
        BackColor = _viewport.BackColor = DesktopTheme.Background;
        Controls.Add(_viewport);
        _viewport.SizeChanged += (_, _) => { Reflow(); if (_stickToBottom) QueueScrollToBottom(); };
        _viewport.Scroll += (_, _) => { if (!_layingOut && !_autoScrolling) _stickToBottom = IsNearBottom(); };
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
        var follow = _stickToBottom || IsNearBottom();
        card.SetText(text);
        Reflow();
        if (follow) { _stickToBottom = true; QueueScrollToBottom(); }
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
        var follow = _stickToBottom || IsNearBottom() || card is ChatMessageCard { IsUser: true };
        card.ContentHeightChanged += (_, _) =>
        {
            Reflow();
            if (_stickToBottom) QueueScrollToBottom();
        };
        _cards.Add(card);
        _viewport.Controls.Add(card);
        Reflow();
        if (follow) { _stickToBottom = true; QueueScrollToBottom(); }
    }

    public void Clear()
    {
        foreach (var card in _cards) card.Dispose();
        _cards.Clear();
        _viewport.Controls.Clear();
        _viewport.AutoScrollMinSize = Size.Empty;
        _viewport.AutoScrollPosition = Point.Empty;
        _stickToBottom = true;
    }

    public bool IsNearBottom()
        => _viewport.VerticalScroll.Value + _viewport.ClientSize.Height >= _viewport.AutoScrollMinSize.Height - 120;

    public void ScrollToBottom()
    {
        var max = _viewport.AutoScrollMinSize.Height - _viewport.ClientSize.Height;
        _stickToBottom = true;
        if (max <= 0) return;
        _autoScrolling = true;
        try { _viewport.AutoScrollPosition = new Point(0, max); }
        finally { _autoScrolling = false; }
    }

    private void QueueScrollToBottom()
    {
        ScrollToBottom();
        if (_scrollQueued || IsDisposed || !IsHandleCreated) return;
        _scrollQueued = true;
        BeginInvoke(new Action(() =>
        {
            _scrollQueued = false;
            if (!IsDisposed && _stickToBottom) ScrollToBottom();
        }));
    }

    public void Reflow()
    {
        if (_layingOut || IsDisposed) return;
        _layingOut = true;
        try
        {
            var clientWidth = _viewport.ClientSize.Width > 50 ? _viewport.ClientSize.Width : Width > 50 ? Width : 1000;
            var available = Math.Max(260, clientWidth - 36);
            var columnWidth = Math.Min(820, available);
            var leftMargin = Math.Max(18, (clientWidth - columnWidth) / 2);
            var y = 16;
            _viewport.SuspendLayout();
            foreach (var card in _cards)
            {
                if (card is ChatMessageCard { IsUser: true } userCard)
                {
                    var bubbleWidth = userCard.MeasureBubbleWidth(columnWidth);
                    userCard.Width = bubbleWidth;
                    userCard.Location = new Point(leftMargin + columnWidth - bubbleWidth, y);
                    y += userCard.Height + 14;
                }
                else
                {
                    card.Width = columnWidth;
                    card.Location = new Point(leftMargin, y);
                    y += card.Height + (card is ToolCallCard ? 8 : 14);
                }
            }
            _viewport.AutoScrollMinSize = new Size(0, y + 24);
            _viewport.ResumeLayout();
        }
        finally { _layingOut = false; }
    }
}

/// <summary>Codex-style thinking disclosure card showing reasoning duration and collapsible thoughts.</summary>
public sealed class ThinkingCard : ChatCard
{
    private readonly Label _icon = new() { AutoSize = false, Font = new Font("Segoe UI Emoji", 10.5f), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _title = new() { AutoSize = false, Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _chevron = new() { AutoSize = false, Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand };
    private readonly Panel _header = new() { Height = 32, Dock = DockStyle.Top, Cursor = Cursors.Hand };
    private readonly ListBox _thoughts = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        Font = new Font("Segoe UI", 8.5f),
        SelectionMode = SelectionMode.None
    };
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private bool _expanded = true;
    private bool _active = true;
    private string _baseTitle;
    private int _calculatedHeight = 34;

    public bool IsActive => _active;
    public override int DesiredHeight => _calculatedHeight;

    public ThinkingCard(string title)
    {
        _baseTitle = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = DesktopTheme.Surface;
        ForeColor = DesktopTheme.Text;
        Padding = new Padding(10, 2, 10, 4);

        _icon.SetBounds(0, 2, 24, 28);
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
        _thoughts.ItemHeight = 20;

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
        _title.Text = summary ?? $"Đã suy nghĩ trong {duration:F1}s";
        _title.ForeColor = DesktopTheme.Muted;
        _icon.Text = "💡";
        _expanded = false; // Auto-compact like Codex
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
        var headerWidth = Math.Max(100, Width - 20);
        _header.Width = headerWidth;
        _title.SetBounds(26, 2, Math.Max(60, headerWidth - 130), 28);
        _chevron.SetBounds(Math.Max(26, headerWidth - 100), 2, 95, 28);

        if (_expanded && _thoughts.Items.Count > 0)
        {
            _thoughts.Visible = true;
            var thoughtHeight = Math.Min(140, Math.Max(22, _thoughts.Items.Count * 20 + 4));
            _thoughts.Height = thoughtHeight;
            _calculatedHeight = _header.Height + thoughtHeight + 8;
        }
        else
        {
            _thoughts.Visible = false;
            _calculatedHeight = _header.Height + 2;
        }
        Height = _calculatedHeight;
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
        if (Width < 6 || Height < 6) return;
        Region?.Dispose();
        Region = new Region(DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12);
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

/// <summary>Codex-style compact single-line tool interaction indicator.</summary>
public sealed class ToolCallCard : ChatCard
{
    private readonly Label _icon = new() { AutoSize = false, Font = new Font("Segoe UI Emoji", 10.5f), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _title = new() { AutoSize = false, Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _status = new() { AutoSize = false, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _viewTerminal = new() { Text = "Xem Terminal ›", AutoSize = false, Font = DesktopTheme.Small, TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand };

    public event Action? ViewDetailsClicked;
    private bool _hasError;
    private readonly string _tool;
    private readonly string? _path;
    private readonly string _detail;

    public override int DesiredHeight => 32;

    public ToolCallCard(string tool, string? path, string detail)
    {
        _tool = tool;
        _path = path;
        _detail = detail;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = DesktopTheme.Surface;
        Height = 32;

        _icon.Text = GetToolIcon(tool);
        _icon.ForeColor = DesktopTheme.Accent;

        _title.Text = FormatToolSummary(tool, path, detail, running: true);
        _title.ForeColor = DesktopTheme.Text;

        _status.ForeColor = DesktopTheme.Accent;
        _status.Text = "● Đang chạy…";

        _viewTerminal.ForeColor = DesktopTheme.Muted;
        _viewTerminal.Click += (_, _) => ViewDetailsClicked?.Invoke();

        Controls.AddRange(new Control[] { _icon, _title, _status, _viewTerminal });
        RefreshHeight();
    }

    public void AppendOutput(string chunk)
    {
        // Full output is preserved in Terminal (_console), keeping chat uncluttered.
    }

    public void SetResult(string result, bool isError)
    {
        _hasError = isError;
        _status.ForeColor = isError ? DesktopTheme.Notice : DesktopTheme.Success;
        _status.Text = isError ? "✕ Thất bại" : "✓ Hoàn tất";
        _title.Text = FormatToolSummary(_tool, _path, _detail, running: false);
        _title.ForeColor = isError ? DesktopTheme.Notice : DesktopTheme.Text;
        RefreshHeight();
    }

    private static string FormatToolSummary(string tool, string? path, string detail, bool running)
    {
        var fileName = !string.IsNullOrWhiteSpace(path) ? Path.GetFileName(path) : null;
        var prefix = running ? "Đang " : "Đã ";
        return tool switch
        {
            "read_file" => fileName != null ? $"{prefix}đọc {fileName}" : $"{prefix}đọc tệp",
            "read_files" => $"{prefix}đọc các tệp",
            "write_file" => fileName != null ? $"{prefix}ghi {fileName}" : $"{prefix}ghi tệp",
            "edit_file" => fileName != null ? $"{prefix}sửa {fileName}" : $"{prefix}sửa tệp",
            "run_command" => detail.Length > 0 ? $"{prefix}chạy lệnh: {(detail.Length > 48 ? detail[..45] + "…" : detail)}" : $"{prefix}chạy lệnh",
            "search_files" => $"{prefix}tìm kiếm trong dự án",
            "list_files" => $"{prefix}kiểm tra cấu trúc thư mục",
            "git_status" or "git_diff" => $"{prefix}kiểm tra thay đổi Git",
            _ when tool.StartsWith("computer_") => $"{prefix}thao tác giao diện",
            _ => $"{prefix}thực hiện {tool}"
        };
    }

    private static string GetToolIcon(string tool) => tool switch
    {
        "read_file" or "read_files" => "📖",
        "write_file" or "edit_file" => "✎",
        "run_command" or "terminal_result" => "⌕",
        "search_files" or "list_files" => "🔍",
        "git_status" or "git_diff" => "🔀",
        _ when tool.StartsWith("computer_") => "🖥️",
        _ => "⚙️"
    };

    private void RefreshHeight()
    {
        var scale = DeviceDpi / 96f;
        var iconSize = (int)(20 * scale);
        _icon.SetBounds(8, (Height - iconSize) / 2, iconSize, iconSize);

        var terminalWidth = (int)(95 * scale);
        var statusWidth = (int)(75 * scale);
        _viewTerminal.SetBounds(Math.Max(10, Width - terminalWidth - 8), 0, terminalWidth, Height);
        _status.SetBounds(Math.Max(10, _viewTerminal.Left - statusWidth - 4), 0, statusWidth, Height);

        var titleLeft = _icon.Right + 6;
        var titleWidth = Math.Max(20, _status.Left - titleLeft - 6);
        _title.SetBounds(titleLeft, 0, titleWidth, Height);

        UpdateRegion();
        OnContentHeightChanged();
        Invalidate();
    }

    public override void RefreshTheme()
    {
        BackColor = DesktopTheme.Surface;
        _title.ForeColor = _hasError ? DesktopTheme.Notice : DesktopTheme.Text;
        _viewTerminal.ForeColor = DesktopTheme.Muted;
        UpdateRegion();
        Invalidate();
    }

    private void UpdateRegion()
    {
        if (Width < 6 || Height < 6) return;
        Region?.Dispose();
        Region = new Region(DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 10));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 10);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var border = new Pen(_hasError ? DesktopTheme.Notice : DesktopTheme.Border);
        e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Notification card for system errors, cancellations, and status messages.</summary>
public sealed class NoticeCard : ChatCard
{
    private readonly Label _body = new() { AutoSize = false, Font = DesktopTheme.Body, TextAlign = ContentAlignment.TopLeft };
    private int _calculatedHeight = 36;
    public override int DesiredHeight => _calculatedHeight;

    public NoticeCard(string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = DesktopTheme.Surface;
        ForeColor = DesktopTheme.Notice;
        Padding = new Padding(14, 8, 14, 8);

        _body.ForeColor = DesktopTheme.Text;
        _body.Text = text;

        Controls.Add(_body);
        RefreshHeight();
    }

    private void RefreshHeight()
    {
        var textWidth = Math.Max(40, Width - 28);
        var measured = TextRenderer.MeasureText(_body.Text, _body.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak);
        _body.SetBounds(14, 8, textWidth, Math.Max(20, measured.Height + 4));
        _calculatedHeight = _body.Bottom + 8;
        Height = _calculatedHeight;
        UpdateRegion();
        OnContentHeightChanged();
        Invalidate();
    }

    public override void RefreshTheme()
    {
        BackColor = DesktopTheme.Surface;
        _body.ForeColor = DesktopTheme.Text;
        UpdateRegion();
        Invalidate();
    }

    private void UpdateRegion()
    {
        if (Width < 6 || Height < 6) return;
        Region?.Dispose();
        Region = new Region(DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12));
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var border = new Pen(DesktopTheme.Notice);
        e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Message bubble: sleek blue pill for user, borderless direct markdown canvas for assistant with typewriter streaming ("nhả chữ").</summary>
public sealed class ChatMessageCard : ChatCard
{
    private readonly Label _copy = new() { Text = "Sao chép", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = DesktopTheme.Small, Cursor = Cursors.Hand };
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
    private int _contentHeight = 24;
    private bool _formatting, _resizing, _streaming;
    private int _calculatedHeight = 36;

    public bool IsUser { get; }
    public bool IsActivity { get; }
    public override int DesiredHeight => _calculatedHeight;

    public ChatMessageCard(string role, string text, bool isUser, bool stream)
    {
        IsUser = isUser || role is "Bạn" or "bạn" or "user";
        IsActivity = role is "Hoạt động" or "Terminal";
        BackColor = IsUser ? DesktopTheme.UserBackground : DesktopTheme.Background;

        _body.BackColor = BackColor;
        _body.ForeColor = IsUser ? Color.White : DesktopTheme.Text;
        _copy.ForeColor = DesktopTheme.Muted;
        _copy.BackColor = BackColor;
        _copy.Visible = !IsUser;

        AccessibleName = IsUser ? "Tin nhắn của bạn" : "Phản hồi từ Muse";
        AccessibleRole = AccessibleRole.Grouping;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Controls.AddRange(new Control[] { _copy, _body });

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
            _contentHeight = Math.Max(20, e.NewRectangle.Height + 4);
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

    public int MeasureBubbleWidth(int maxColumnWidth)
    {
        if (!IsUser) return maxColumnWidth;
        var maxAllowed = Math.Max(120, (int)(maxColumnWidth * 0.78f));
        var measured = TextRenderer.MeasureText(_source, _body.Font, new Size(maxAllowed - 28, 0), TextFormatFlags.WordBreak);
        return Math.Clamp(measured.Width + 30, 80, maxAllowed);
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
            _body.SelectionColor = IsUser ? Color.White : DesktopTheme.Text;
            _body.SelectionBackColor = code ? DesktopTheme.SurfaceStrong : BackColor;
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
        if (_formatting || _resizing || Width < 40) return;
        _resizing = true;
        try
        {
            if (IsUser)
            {
                var padX = 14;
                var padY = 8;
                var bodyWidth = Math.Max(20, Width - padX * 2);
                var measured = TextRenderer.MeasureText(_source, _body.Font, new Size(bodyWidth, 0), TextFormatFlags.WordBreak);
                var bodyHeight = Math.Max(22, measured.Height + 4);
                _body.SetBounds(padX, padY, bodyWidth, bodyHeight);
                _copy.Visible = false;
                _calculatedHeight = _body.Bottom + padY;
            }
            else
            {
                var bodyWidth = Math.Max(40, Width);
                _body.SetBounds(0, 4, bodyWidth, Math.Clamp(_contentHeight, 22, 4000));
                if (_body.IsHandleCreated)
                {
                    _contentHeight = _body.GetPositionFromCharIndex(Math.Max(0, _body.TextLength - 1)).Y
                        - _body.GetPositionFromCharIndex(0).Y + _body.Font.Height + 8;
                    _body.Height = Math.Clamp(_contentHeight, 22, 4000);
                }
                _copy.Visible = _body.TextLength > 0;
                _copy.SetBounds(0, _body.Bottom + 4, 75, 20);
                _calculatedHeight = _copy.Bottom + 6;
            }
            Height = _calculatedHeight;
            UpdateRegion();
            OnContentHeightChanged();
        }
        finally { _resizing = false; }
    }

    public override void RefreshTheme()
    {
        BackColor = IsUser ? DesktopTheme.UserBackground : DesktopTheme.Background;
        _body.BackColor = BackColor;
        _body.ForeColor = IsUser ? Color.White : DesktopTheme.Text;
        _copy.ForeColor = DesktopTheme.Muted;
        _copy.BackColor = BackColor;
        SetText(_source, force: true);
        UpdateRegion();
        Invalidate();
    }

    private void UpdateRegion()
    {
        if (Width < 6 || Height < 6) return;
        Region?.Dispose();
        if (IsUser)
        {
            Region = new Region(DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 18));
        }
        else
        {
            Region = null;
        }
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (IsUser)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = DesktopTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 18);
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillPath(brush, path);
            using var border = new Pen(DesktopTheme.UserBorder);
            e.Graphics.DrawPath(border, path);
        }
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
