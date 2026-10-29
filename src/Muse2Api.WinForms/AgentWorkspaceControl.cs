using System.Collections.Concurrent;
using Muse2Api.Agent;

namespace Muse2Api.WinForms;

public sealed record AgentConnection(string BaseUrl, string ApiKey);

/// <summary>Conversation-first workspace; project files and technical tools are opened on demand.</summary>
public sealed partial class AgentWorkspaceControl : UserControl
{
    private readonly Func<AgentConnection> _connection;
    private readonly Func<Task> _login;
    private readonly Func<Task<bool>> _ensureReady;
    private readonly Action _settings;
    private readonly ChatSessionStore _store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Muse2Api.WinForms", "AgentSessions"));
    private readonly ConcurrentQueue<AgentEvent> _events = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly string _preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Muse2Api.WinForms", "agent_workspace.json");
    private ToolExecutor? _tools;
    private ChatSession? _session;
    private CancellationTokenSource? _cancel;
    private Task? _activeTask;
    private bool _busy, _binding, _settingEditor, _dirty, _connected, _preparing;
    private string? _editorPath, _editorBaseline;
    private string _editorDisplayBaseline = "";
    private ThinkingCard? _activeThinking;
    private ToolCallCard? _activeToolCall;
    private ChatMessageCard? _activeAssistantCard;
    private int _activityFrame;
    private readonly ToolTip _tips = new();

    private readonly Label _workspace = DesktopTheme.Label("Chưa chọn dự án", 11, bold: true);
    private readonly Label _projectHint = DesktopTheme.Label("Chọn thư mục để Muse có thể đọc và làm việc với mã nguồn của bạn.", 9, DesktopTheme.Muted);
    private readonly Label _state = DesktopTheme.Label("Mọi thay đổi sẽ được hỏi trước khi thực hiện.", 9, DesktopTheme.Muted);
    private readonly Label _activityGlyph = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = DesktopTheme.Accent, Visible = false, AccessibleName = "Muse đang làm việc" };
    private readonly Label _connectionTitle = DesktopTheme.Label("Đang chuẩn bị Muse", 10, bold: true);
    private readonly Label _connectionHint = DesktopTheme.Label("Ứng dụng tự thiết lập kết nối trên máy của bạn.", 9, DesktopTheme.Muted);
    private readonly Label _welcomeHint = DesktopTheme.Label("Chọn một dự án, kết nối tài khoản Muse, rồi mô tả điều bạn muốn làm.", 11, DesktopTheme.Muted);
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.None, ShowLines = false, ItemHeight = 28 };
    private readonly RichTextBox _editor = CodeBox(false), _diff = CodeBox(true), _console = CodeBox(true);
    private readonly ChatTranscript _chat = new();
    private readonly ChatInputBox _prompt = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.None, AcceptsReturn = true, BorderStyle = BorderStyle.None, PlaceholderText = "Mô tả điều bạn muốn làm… hoặc Ctrl+V để dán ảnh" };
    private readonly CleanButton _pasteImage = new() { Text = "Dán ảnh", Width = 96, Quiet = true, AccessibleName = "Dán ảnh từ clipboard" };
    private readonly TextBox _command = new() { Dock = DockStyle.Fill, PlaceholderText = "Nhập lệnh nếu bạn muốn tự chạy…" };
    private readonly ListBox _sessions = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 48, IntegralHeight = false };
    // Advanced preferences are intentionally kept out of the main workspace.
    private readonly ComboBox _models = new() { Text = "muse-chat" }, _shell = new() { DropDownStyle = ComboBoxStyle.DropDownList }, _permissions = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox _changes = new() { Dock = DockStyle.Fill, DisplayMember = nameof(WorkspaceChange.Path), BorderStyle = BorderStyle.None, IntegralHeight = false, ItemHeight = 28 };
    private readonly CleanButton _send = new() { Text = "Gửi", Icon = WorkspaceIcon.Send, Primary = true, Width = 108 }, _stop = new() { Text = "Dừng", Icon = WorkspaceIcon.Stop, Width = 96, Visible = false };
    private readonly CleanButton _save = new() { Text = "Lưu tệp", Width = 100 }, _undo = new() { Text = "Hoàn tác tệp này", Width = 160 };
    private readonly CleanButton _choose = new() { Text = "Chọn dự án…", Icon = WorkspaceIcon.Folder, Dock = DockStyle.Fill }, _new = new() { Text = "Cuộc trò chuyện mới", Icon = WorkspaceIcon.Plus, Primary = true, Dock = DockStyle.Fill };
    private readonly CleanButton _loginButton = new() { Text = "Kết nối Muse", Primary = true, Width = 150 };
    private readonly CleanButton _filesButton = new() { Text = "Tệp", Icon = WorkspaceIcon.Files, Width = 82, Quiet = true }, _changesButton = new() { Text = "Thay đổi", Icon = WorkspaceIcon.Changes, Width = 116, Quiet = true };
    private readonly CleanButton _terminalButton = new() { Text = "Terminal", Icon = WorkspaceIcon.Terminal, Width = 108, Quiet = true };
    private readonly ComboBox _terminalShell = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly CleanButton _run = new() { Text = "Chạy", Width = 70 };
    private SplitContainer _drawer = null!;
    private WorkspaceTabs _detailTabs = null!;
    private readonly Label _editorTitle = DesktopTheme.Label("Chọn một tệp trong dự án", 9, DesktopTheme.Muted);
    private Panel _welcome = null!;
    private CleanButton _welcomeProject = null!, _welcomeConnect = null!;

    public bool IsRunning => _activeTask is { IsCompleted: false };
    public string? WorkspaceRoot => _tools?.Root;
    public bool CanCloseWorkspace() => DiscardEditor();

    public AgentWorkspaceControl(Func<AgentConnection> connection, Func<Task> login, Func<Task<bool>> ensureReady, Action settings)
    {
        _connection = connection; _login = login; _ensureReady = ensureReady; _settings = settings;
        _workspace.AutoEllipsis = true; _projectHint.AutoEllipsis = true;
        _computer.RequestAuthorization = RequestComputerAuthorization;
        Dock = DockStyle.Fill; Font = DesktopTheme.Body; BackColor = DesktopTheme.Background; ForeColor = DesktopTheme.Text;
        BuildLayout();
        _models.Items.AddRange(new object[] { "muse-chat", "gpt-4o" });
        _shell.Items.AddRange(new object[] { "powershell", "cmd", "bash" }); _shell.SelectedIndex = 0;
        _terminalShell.Items.AddRange(new object[] { "PowerShell", "CMD", "Bash" }); _terminalShell.SelectedIndex = 0;
        _terminalShell.SelectedIndexChanged += (_, _) => { if (!_busy) _shell.SelectedIndex = _terminalShell.SelectedIndex; };
        _permissions.Items.AddRange(new object[] { "Hỏi trước khi thay đổi", "Chỉ xem dự án", "Turbo — hỏi trước khi chạy lệnh" }); _permissions.SelectedIndex = 0;
        _timer.Tick += async (_, _) =>
        {
            DrainEvents();
            if (_busy && ++_activityFrame % 2 == 0) _activityGlyph.Text = new[] { "◐", "◓", "◑", "◒" }[(_activityFrame / 2) % 4];
            await PollManualProcess();
        }; _timer.Start();
        _stopBackground.Click += async (_, _) => { try { await StopTerminalProcesses(); } catch (Exception ex) { _state.Text = ex.Message; } };
        _choose.Click += async (_, _) => await PickWorkspace();
        _welcomeProject.Click += async (_, _) => await PickWorkspace();
        _welcomeConnect.Click += async (_, _) => { if (!_connected && !_preparing) await _login(); };
        _new.Click += async (_, _) => { if (!_busy) { if (_tools is null) await PickWorkspace(); NewSession(); await RefreshSessions(); } };
        _loginButton.Click += async (_, _) => { if (!_busy && !_preparing) { if (_connected) _settings(); else await _login(); } };
        _send.Click += async (_, _) => await StartOperation(RunTurn);
        _prompt.PasteImage = PasteChatImages;
        _pasteImage.Click += (_, _) => { if (!PasteChatImages()) _state.Text = "Chụp màn hình rồi nhấn Ctrl+V trong ô chat để đính kèm ảnh."; };
        _stop.Click += (_, _) => { _state.Text = "Đang dừng thao tác…"; _cancel?.Cancel(); };
        _run.Click += async (_, _) => await StartOperation(RunManualCommand);
        _save.Click += async (_, _) => await SaveEditor();
        _undo.Click += async (_, _) => await UndoSelected();
        _filesButton.Click += async (_, _) => { if (_tools is null) await PickWorkspace(); if (_tools is not null) ToggleDrawer(0); };
        _changesButton.Click += (_, _) => ToggleDrawer(1);
        _terminalButton.Click += async (_, _) => { if (_tools is null) await PickWorkspace(); if (_tools is not null) { ToggleDrawer(2, forceOpen: true); _command.Focus(); } };
        _computerButton.Click += (_, _) => ConfigureComputer();
        _sessions.SelectedIndexChanged += async (_, _) => { if (!_binding && !_busy && _sessions.SelectedItem is ChatSession session) await LoadSession(session.Id); };
        _sessions.DrawItem += DrawSession;
        _tree.BeforeExpand += (_, e) => { if (e.Node is { } node && node.Nodes.Count == 1 && node.Nodes[0].Tag is null) Populate(node); };
        _tree.NodeMouseDoubleClick += async (_, e) => { if (e.Node.Tag is string path && File.Exists(path)) { await OpenFile(path); ToggleDrawer(0, forceOpen: true); } };
        _changes.SelectedIndexChanged += (_, _) => _diff.Text = (_changes.SelectedItem as WorkspaceChange)?.Diff ?? "Chưa có thay đổi nào trong lần làm việc này.";
        _editor.TextChanged += (_, _) => { if (!_settingEditor) { _dirty = _editor.Text != _editorDisplayBaseline; UpdateEditorTitle(); } };
        _editor.KeyDown += async (_, e) => { if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; await SaveEditor(); } };
        _prompt.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await StartOperation(RunTurn); }
        };
        _command.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await StartOperation(RunManualCommand); } };
        Load += async (_, _) => await RestoreWorkspace();
        _tips.SetToolTip(_prompt, "Enter để gửi · Shift+Enter để xuống dòng · Ctrl+V để dán ảnh");
        _tips.SetToolTip(_choose, "Chọn thư mục dự án trên máy của bạn");
        _tips.SetToolTip(_changesButton, "Xem và hoàn tác các tệp đã thay đổi");
        _tips.SetToolTip(_backgroundCommand, "Giữ ứng dụng hoặc máy chủ hoạt động để kiểm thử; dùng Dừng nền để kết thúc.");
    }

    private static RichTextBox CodeBox(bool readOnly) => new()
    {
        Dock = DockStyle.Fill, ReadOnly = readOnly, WordWrap = false, DetectUrls = false,
        Font = new Font("Consolas", 10), BorderStyle = BorderStyle.None, AcceptsTab = !readOnly,
        BackColor = DesktopTheme.Background, ForeColor = DesktopTheme.Text,
    };

    private static CleanButton Button(string text, EventHandler action)
    {
        var button = new CleanButton { Text = text }; button.Click += action; return button;
    }

    private void BuildLayout()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = DesktopTheme.Background };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(BuildSidebar(), 0, 0);
        var body = new MeshLayout(MeshTone.Canvas, new(SizeType.Absolute, 52), new(SizeType.Percent, 100));
        body.Controls.Add(BuildHeader(), 0, 0);
        _drawer = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1000, 620), SplitterDistance = 630, Panel1MinSize = 300, Panel2MinSize = 260, Panel2Collapsed = true, BackColor = DesktopTheme.Border, Margin = new Padding(0) };
        _drawer.Panel1.Controls.Add(BuildConversation()); _drawer.Panel2.Controls.Add(BuildDetails());
        body.Controls.Add(_drawer, 0, 1); shell.Controls.Add(body, 1, 0); Controls.Add(shell);
    }

    private Control BuildSidebar()
    {
        var side = new MeshLayout(MeshTone.Sidebar, new(SizeType.Absolute, 78), new(SizeType.Absolute, 44), new(SizeType.Absolute, 86), new(SizeType.Absolute, 30), new(SizeType.Percent, 100), new(SizeType.Absolute, 46));
        side.Padding = new Padding(12, 8, 12, 10);
        var brand = DesktopTheme.Column(new(SizeType.Absolute, 36), new(SizeType.Absolute, 20));
        brand.BackColor = DesktopTheme.Sidebar; brand.Padding = new Padding(6, 4, 0, 0);
        brand.Controls.Add(DesktopTheme.Label("muse", 22, bold: true), 0, 0);
        brand.Controls.Add(DesktopTheme.Label("KHÔNG GIAN SÁNG TẠO", 7.5f, DesktopTheme.Muted), 0, 1);
        side.Controls.Add(brand, 0, 0); side.Controls.Add(_new, 0, 1);
        var project = DesktopTheme.Column(new(SizeType.Absolute, 26), new(SizeType.Absolute, 42));
        project.Padding = new Padding(0, 6, 0, 0); project.BackColor = DesktopTheme.Sidebar;
        project.Controls.Add(DesktopTheme.Label("DỰ ÁN CỦA BẠN", 8, DesktopTheme.Muted, true), 0, 0);
        project.Controls.Add(_choose, 0, 1); side.Controls.Add(project, 0, 2);
        var sessionsLabel = DesktopTheme.Label("CUỘC TRÒ CHUYỆN", 8, DesktopTheme.Muted, true);
        sessionsLabel.Padding = new Padding(4, 0, 0, 0);
        side.Controls.Add(sessionsLabel, 0, 3);
        _sessions.BackColor = DesktopTheme.Sidebar; _sessions.ForeColor = DesktopTheme.Text;
        _sessions.Margin = new Padding(0, 2, 0, 2);
        side.Controls.Add(_sessions, 0, 4);
        var settings = Button("Cài đặt", (_, _) => _settings()); settings.Icon = WorkspaceIcon.Settings; settings.Quiet = true; settings.Dock = DockStyle.Fill; settings.Margin = new Padding(0, 2, 0, 0);
        side.Controls.Add(settings, 0, 5); return side;
    }

    private Control BuildHeader()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(20, 6, 20, 6), BackColor = DesktopTheme.Background };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = DesktopTheme.Column(new(SizeType.Absolute, 22), new(SizeType.Absolute, 18)); title.BackColor = DesktopTheme.Background;
        _workspace.Font = DesktopTheme.Strong;
        _projectHint.Font = DesktopTheme.Small;
        title.Controls.Add(_workspace, 0, 0); title.Controls.Add(_projectHint, 0, 1); row.Controls.Add(title, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = DesktopTheme.Background, Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 0), AutoSize = true };
        _loginButton.Width = 135; _loginButton.Quiet = true;
        actions.Controls.Add(_loginButton);
        actions.Controls.Add(_filesButton); actions.Controls.Add(_changesButton); actions.Controls.Add(_terminalButton); actions.Controls.Add(_computerButton); row.Controls.Add(actions, 1, 0);
        return row;
    }

    private Control BuildConversation()
    {
        var conversation = DesktopTheme.Column(new(SizeType.Percent, 100), new(SizeType.Absolute, 0), new(SizeType.Absolute, 140));
        _conversation = conversation;
        conversation.Padding = new Padding(16, 0, 16, 12); conversation.BackColor = DesktopTheme.Background;
        var surface = new Panel { Dock = DockStyle.Fill, BackColor = DesktopTheme.Background };
        _welcome = new Panel { Dock = DockStyle.Fill, BackColor = DesktopTheme.Background, AutoScroll = true };
        var welcomeLayout = new MeshLayout(MeshTone.Hero, new(SizeType.Absolute, 24), new(SizeType.Absolute, 58), new(SizeType.Absolute, 48), new(SizeType.Absolute, 44), new(SizeType.Absolute, 12), new(SizeType.Absolute, 144), new(SizeType.Percent, 100));
        welcomeLayout.Dock = DockStyle.Top; welcomeLayout.Height = 438; welcomeLayout.Padding = new Padding(24, 22, 24, 18);
        var eyebrow = DesktopTheme.Label("TỪ Ý TƯỞNG ĐẾN SẢN PHẨM", 8, DesktopTheme.Accent, true); eyebrow.TextAlign = ContentAlignment.MiddleCenter;
        welcomeLayout.Controls.Add(eyebrow, 0, 0);
        var heading = DesktopTheme.Label("Cùng Muse tạo nên điều mới.", 18, bold: true); heading.TextAlign = ContentAlignment.MiddleCenter;
        _welcomeHint.TextAlign = ContentAlignment.TopCenter;
        welcomeLayout.Controls.Add(heading, 0, 1); welcomeLayout.Controls.Add(_welcomeHint, 0, 2);
        var steps = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.None, AutoSize = true, BackColor = Color.Transparent };
        _welcomeProject = new CleanButton { Text = "Chọn dự án", Icon = WorkspaceIcon.Folder, Primary = true, Width = 160 };
        _welcomeConnect = new CleanButton { Text = "Kết nối Muse", Width = 160 };
        steps.Controls.Add(_welcomeProject); steps.Controls.Add(_welcomeConnect); welcomeLayout.Controls.Add(steps, 0, 3);
        var suggestions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = Padding.Empty, BackColor = Color.Transparent };
        suggestions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); suggestions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        suggestions.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); suggestions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var prompts = new[]
        {
            ("Tìm hiểu dự án", "Hãy đọc dự án và giải thích ngắn gọn cấu trúc, các chức năng chính và cách chúng hoạt động."),
            ("Tìm và sửa lỗi", "Hãy kiểm tra dự án, tìm các lỗi quan trọng, giải thích cách sửa và cho tôi xem thay đổi trước khi thực hiện."),
            ("Thêm tính năng", "Tôi muốn thêm một tính năng vào dự án: "),
            ("Kiểm tra chất lượng", "Hãy kiểm tra các thay đổi hiện tại và chạy những bài kiểm tra phù hợp. Giải thích kết quả bằng ngôn ngữ dễ hiểu."),
        };
        for (var i = 0; i < prompts.Length; i++)
        {
            var item = prompts[i]; var button = Button(item.Item1, (_, _) => { _prompt.Text = item.Item2; _prompt.Focus(); _prompt.SelectionStart = _prompt.TextLength; });
            button.Icon = new[] { WorkspaceIcon.Code, WorkspaceIcon.Search, WorkspaceIcon.Plus, WorkspaceIcon.Check }[i];
            button.Description = new[] { "Đọc và hiểu cấu trúc mã nguồn", "Tìm nguyên nhân, xem trước bản sửa", "Biến ý tưởng thành chức năng", "Chạy kiểm tra và xem kết quả" }[i];
            button.Dock = DockStyle.Fill; button.Margin = new Padding(4); suggestions.Controls.Add(button, i % 2, i / 2);
        }
        welcomeLayout.Controls.Add(suggestions, 0, 5);
        welcomeLayout.SizeChanged += (_, _) =>
        {
            var compact = welcomeLayout.Width < LogicalToDeviceUnits(590);
            if (suggestions.ColumnCount == (compact ? 1 : 2)) return;
            suggestions.SuspendLayout(); suggestions.ColumnCount = compact ? 1 : 2; suggestions.RowCount = compact ? 4 : 2;
            suggestions.ColumnStyles.Clear(); suggestions.RowStyles.Clear();
            for (var j = 0; j < suggestions.ColumnCount; j++) suggestions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / suggestions.ColumnCount));
            for (var j = 0; j < suggestions.RowCount; j++) suggestions.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / suggestions.RowCount));
            for (var j = 0; j < suggestions.Controls.Count; j++) suggestions.SetCellPosition(suggestions.Controls[j], new(j % suggestions.ColumnCount, j / suggestions.ColumnCount));
            welcomeLayout.RowStyles[5].Height = LogicalToDeviceUnits(compact ? 280 : 144); welcomeLayout.Height = LogicalToDeviceUnits(compact ? 574 : 438);
            suggestions.ResumeLayout();
        };
        _welcome.Controls.Add(welcomeLayout);
        surface.Controls.Add(_chat); surface.Controls.Add(_welcome); conversation.Controls.Add(surface, 0, 0);

        var spacer = new Panel { Dock = DockStyle.Fill, BackColor = DesktopTheme.Background, Visible = false };
        conversation.Controls.Add(spacer, 0, 1);

        var composerHost = new Panel { Dock = DockStyle.Fill, BackColor = DesktopTheme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        var composer = new MeshLayout(MeshTone.Composer, new(SizeType.Absolute, 0), new(SizeType.Percent, 100), new(SizeType.Absolute, 44));
        _composer = composer;
        composer.Padding = new Padding(16, 10, 14, 8);
        _prompt.BackColor = DesktopTheme.Surface; _prompt.ForeColor = DesktopTheme.Text; _prompt.Font = new Font("Segoe UI", 10.5f);
        composer.Controls.Add(_imageStrip, 0, 0); _imageStrip.Visible = false;
        composer.Controls.Add(_prompt, 0, 1);
        _prompt.Enter += (_, _) => { composer.FocusHighlight = true; composer.Invalidate(); };
        _prompt.Leave += (_, _) => { composer.FocusHighlight = false; composer.Invalidate(); };

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = DesktopTheme.Surface, Margin = Padding.Empty };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));

        var statusHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = DesktopTheme.Surface, Margin = Padding.Empty };
        statusHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        statusHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _state.AutoEllipsis = true;
        _state.Font = DesktopTheme.Small;
        _state.ForeColor = DesktopTheme.Muted;
        _state.TextChanged += (_, _) => _tips.SetToolTip(_state, _state.Text);
        statusHost.Controls.Add(_activityGlyph, 0, 0);
        statusHost.Controls.Add(_state, 1, 0);
        footer.Controls.Add(statusHost, 0, 0);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = DesktopTheme.Surface, Margin = Padding.Empty };
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 114));

        _pasteImage.Dock = DockStyle.Fill;
        _pasteImage.Margin = new Padding(2);
        actions.Controls.Add(_pasteImage, 0, 0);

        var sendStopHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.Transparent };
        _send.Dock = DockStyle.Fill;
        _send.Margin = new Padding(2);
        _stop.Dock = DockStyle.Fill;
        _stop.Margin = new Padding(2);
        sendStopHost.Controls.Add(_send);
        sendStopHost.Controls.Add(_stop);
        actions.Controls.Add(sendStopHost, 1, 0);

        footer.Controls.Add(actions, 1, 0);
        composer.Controls.Add(footer, 0, 2);

        void CenterComposer()
        {
            var hostWidth = composerHost.ClientSize.Width;
            var hostHeight = composerHost.ClientSize.Height;
            if (hostWidth < 20 || hostHeight < 20) return;
            var compWidth = Math.Min(820, Math.Max(260, hostWidth - 36));
            var left = (hostWidth - compWidth) / 2;
            composer.SetBounds(left, 2, compWidth, Math.Max(40, hostHeight - 4));
        }
        composerHost.Controls.Add(composer);
        composerHost.SizeChanged += (_, _) => CenterComposer();
        conversation.Controls.Add(composerHost, 0, 2);
        return conversation;
    }

    private Control BuildDetails()
    {
        var panel = DesktopTheme.Column(new(SizeType.Absolute, 40), new(SizeType.Percent, 100));
        panel.BackColor = DesktopTheme.Background; panel.Padding = new Padding(8);
        var close = Button("Đóng bảng chi tiết", (_, _) => _drawer.Panel2Collapsed = true); close.Icon = WorkspaceIcon.Close; close.Quiet = true; close.Dock = DockStyle.Fill; panel.Controls.Add(close, 0, 0);
        _detailTabs = new WorkspaceTabs();
        var editorTab = new Panel { BackColor = DesktopTheme.Background };
        var files = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(340, 560), SplitterDistance = 150, Panel1MinSize = 80, Panel2MinSize = 100 };
        var treeLayout = DesktopTheme.Column(new(SizeType.Absolute, 32), new(SizeType.Percent, 100));
        var reload = Button("Làm mới danh sách", (_, _) => RefreshTree()); reload.Dock = DockStyle.Fill;
        treeLayout.Controls.Add(reload, 0, 0); treeLayout.Controls.Add(_tree, 0, 1); files.Panel1.Controls.Add(treeLayout);
        var editorLayout = DesktopTheme.Column(new(SizeType.Absolute, 28), new(SizeType.Percent, 100), new(SizeType.Absolute, 42));
        _editorTitle.AutoEllipsis = true; editorLayout.Controls.Add(_editorTitle, 0, 0);
        editorLayout.Controls.Add(_editor, 0, 1);
        var fileActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        fileActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fileActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); fileActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        _save.Text = "Lưu"; _save.Icon = WorkspaceIcon.Files; _save.Dock = DockStyle.Fill; fileActions.Controls.Add(_save, 0, 0);
        var mention = Button("Hỏi Muse", (_, _) => { if (_editorPath is not null) { _prompt.AppendText($"Hãy đọc và giải thích tệp \"{_editorPath}\". "); _prompt.Focus(); } }); mention.Icon = WorkspaceIcon.Search; mention.Dock = DockStyle.Fill;
        fileActions.Controls.Add(mention, 1, 0); editorLayout.Controls.Add(fileActions, 0, 2); files.Panel2.Controls.Add(editorLayout); editorTab.Controls.Add(files);
        var changesTab = new Panel();
        var changesLayout = DesktopTheme.Column(new(SizeType.Absolute, 90), new(SizeType.Absolute, 42), new(SizeType.Percent, 100));
        _diff.Text = "Chưa có thay đổi nào trong lần làm việc này.";
        changesLayout.Controls.Add(_changes, 0, 0); changesLayout.Controls.Add(_undo, 0, 1); changesLayout.Controls.Add(_diff, 0, 2); changesTab.Controls.Add(changesLayout);
        var terminalTab = new Panel();
        var terminal = DesktopTheme.Column(new(SizeType.Absolute, 35), new(SizeType.Percent, 100), new(SizeType.Absolute, 28), new(SizeType.Absolute, 38));
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.Controls.Add(_terminalShell, 0, 0); options.Controls.Add(_backgroundCommand, 1, 0);
        terminal.Controls.Add(options, 0, 0);
        terminal.Controls.Add(_console, 0, 1); terminal.Controls.Add(DesktopTheme.Label("Lệnh chạy trong dự án đang chọn; Enter để chạy sau khi duyệt.", 8, DesktopTheme.Muted), 0, 2);
        var input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        input.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        input.Controls.Add(_command, 0, 0); input.Controls.Add(_run, 1, 0); input.Controls.Add(_stopBackground, 2, 0);
        terminal.Controls.Add(input, 0, 3); terminalTab.Controls.Add(terminal);
        _detailTabs.AddPage("Tệp", editorTab, WorkspaceIcon.Files); _detailTabs.AddPage("Thay đổi", changesTab, WorkspaceIcon.Changes); _detailTabs.AddPage("Terminal", terminalTab, WorkspaceIcon.Terminal); panel.Controls.Add(_detailTabs, 0, 1);
        DesktopTheme.Apply(panel); return panel;
    }

    private void ToggleDrawer(int tab, bool forceOpen = false)
    {
        if (!_drawer.Panel2Collapsed && _detailTabs.SelectedIndex == tab && !forceOpen) { _drawer.Panel2Collapsed = true; return; }
        _drawer.Panel2Collapsed = false;
        _drawer.SplitterDistance = Math.Max(300, _drawer.Width - Math.Min(440, _drawer.Width / 2));
        _detailTabs.SelectedIndex = tab;
    }

    private void DrawSession(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _sessions.Items[e.Index] is not ChatSession session) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var brush = new SolidBrush(DesktopTheme.Sidebar); e.Graphics.FillRectangle(brush, e.Bounds);
        if (selected)
        {
            using var selection = DesktopTheme.RoundedPath(Rectangle.Inflate(e.Bounds, -2, -2), 16);
            using var fill = new SolidBrush(DesktopTheme.UserBackground); e.Graphics.FillPath(fill, selection);
            using var pen = new Pen(DesktopTheme.UserBorder); e.Graphics.DrawPath(pen, selection);
        }
        TextRenderer.DrawText(e.Graphics, session.Title, Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 5, e.Bounds.Width - 20, 22), DesktopTheme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, session.UpdatedAt.ToLocalTime().ToString("dd/MM · HH:mm"), DesktopTheme.Small, new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 27, e.Bounds.Width - 20, 18), DesktopTheme.Muted);
    }

    public void SetConnectionState(string title, string hint, bool connected, bool preparing)
    {
        _connected = connected; _preparing = preparing;
        _connectionTitle.Text = title;
        _connectionTitle.ForeColor = connected ? DesktopTheme.Success : DesktopTheme.Text;
        _connectionHint.Text = hint;
        _loginButton.Text = preparing ? "Đang chuẩn bị…" : connected ? "● Đã kết nối" : "Kết nối Muse";
        _loginButton.ForeColor = connected ? DesktopTheme.Success : DesktopTheme.Text;
        _tips.SetToolTip(_loginButton, connected ? $"Tài khoản Muse: {title}\n{hint}" : "Nhấn để kết nối tài khoản Muse");
        _loginButton.Enabled = !preparing && !_busy;
        _welcomeConnect.Enabled = !connected && !preparing && !_busy;
        _welcomeConnect.Text = connected ? "Đã kết nối" : "Kết nối Muse";
        _welcomeConnect.Icon = connected ? WorkspaceIcon.Check : WorkspaceIcon.None;
        UpdateWelcome();
    }

    private void UpdateWelcome()
    {
        _welcome.Visible = _session is null || _session.Events.All(e => e.Kind != "user");
        _chat.Visible = !_welcome.Visible;
        if (_welcome.Visible) _welcome.BringToFront(); else _chat.BringToFront();
        _welcomeProject.Text = _tools is null ? "Chọn dự án" : "Đã chọn dự án";
        _welcomeHint.Text = _tools is null ? "Chọn thư mục dự án trên máy. Muse sẽ giúp bạn đọc, sửa và kiểm tra bằng những yêu cầu thông thường."
            : !_connected ? "Dự án đã sẵn sàng. Kết nối tài khoản Muse để bắt đầu làm việc."
            : "Mô tả điều bạn muốn làm. Muse sẽ đọc dự án, đề xuất cách làm và hỏi trước khi thay đổi.";
    }

    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed) action(); }); } catch (InvalidOperationException) { }
    }
    private void Publish(AgentEvent item) { if (_events.Count < 1000 || item.Kind != "command_output") _events.Enqueue(item); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cancel?.Cancel(); DisableComputer(); _timer.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
