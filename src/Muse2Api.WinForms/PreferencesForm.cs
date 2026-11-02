using System.Diagnostics;
using Muse2Api.Agent;

namespace Muse2Api.WinForms;

/// <summary>Everything the settings dialog needs to manage accounts and API
/// keys on the already-running local gateway (BaseUrl includes the port the
/// desktop picked for this launch).</summary>
internal sealed record ConnectPanelInfo(string BaseUrl, string ApiKey, GatewayClient Client);

/// <summary>Content-sized settings; a fixed footer stays reachable while content scrolls.</summary>
internal sealed class PreferencesForm : Form
{
    private readonly CheckBox _readOnly, _turbo, _projectReview;
    private readonly ComboBox _theme, _shell;
    private readonly ConnectPanelInfo? _connect;
    private readonly SkillStore? _skillStore;
    private readonly ListBox _accountsList = new() { Dock = DockStyle.Top, Height = 96, IntegralHeight = false };
    private readonly ListBox _keysList = new() { Dock = DockStyle.Top, Height = 80, IntegralHeight = false };
    private readonly ListBox _skillsList = new() { Dock = DockStyle.Top, Height = 96, IntegralHeight = false };
    private readonly TextBox _baseUrlBox = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox _apiKeyBox = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private Label _panelStatus = null!;
    private List<(string Id, string Label, string Status, bool Enabled)> _accounts = new();
    private List<(string Id, string Name, string Prefix, bool Revoked)> _keys = new();
    private List<SkillDefinition> _skillItems = new();
    public event EventHandler? LoginRequested;
    public event EventHandler? AddAccountRequested;
    public bool ReadOnly => _readOnly.Checked;
    public bool Turbo => _turbo.Checked;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ProjectReviewEnabled { get => _projectReview.Checked; set => _projectReview.Checked = value; }
    public int ShellIndex => _shell.SelectedIndex;
    public DesktopTheme.ThemeMode SelectedTheme => _theme.SelectedIndex == 1 ? DesktopTheme.ThemeMode.Light : DesktopTheme.ThemeMode.Dark;

    public PreferencesForm(bool connected, bool busy, bool readOnly, bool turbo, int shellIndex, string dataDirectory, string diagnostics,
        Func<Task<Muse2Api.UpdateCheckResult>>? checkUpdates = null, Action<Muse2Api.AvailableUpdate>? openUpdate = null,
        Muse2Api.UpdateCheckResult? latestUpdate = null, Muse2Api.UpdateFeed updateFeed = Muse2Api.UpdateFeed.Release,
        ConnectPanelInfo? connect = null, string? skillsDirectory = null)
    {
        SuspendLayout(); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Cài đặt Muse"; Font = DesktopTheme.Body; StartPosition = FormStartPosition.CenterParent;
        BackColor = DesktopTheme.Background; ForeColor = DesktopTheme.Text;
        ClientSize = new Size(640, 560); MinimumSize = new Size(520, 430);
        var root = DesktopTheme.Column(new(SizeType.Percent, 100), new(SizeType.Absolute, 64));
        root.Padding = new Padding(24, 16, 24, 12); root.BackColor = DesktopTheme.Background;
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = DesktopTheme.Background };
        var content = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = Padding.Empty, Padding = new Padding(0, 0, 12, 8) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var labels = new List<Label>();
        void Add(Control control, int bottom = 16)
        {
            var index = content.RowCount++;
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 0, 0, bottom);
            content.Controls.Add(control, 0, index);
        }
        Label TextBlock(string text, float size, Color? color = null, bool bold = false)
        {
            var label = DesktopTheme.Label(text, size, color, bold);
            label.AutoSize = true; label.Dock = DockStyle.Top; labels.Add(label); return label;
        }
        Add(TextBlock("Cài đặt", 22, bold: true), 18);
        Add(TextBlock(connected ? "Tài khoản Muse đã kết nối" : "Chưa kết nối tài khoản Muse", 11, connected ? DesktopTheme.Success : DesktopTheme.Muted));
        var account = new CleanButton { Text = "Mở / khôi phục đăng nhập Muse", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 40, Enabled = !busy };
        account.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty); Add(account, 20);
        _connect = connect;
        _skillStore = skillsDirectory is null ? null : new SkillStore(skillsDirectory);
        foreach (var list in new[] { _accountsList, _keysList, _skillsList }) { list.BackColor = DesktopTheme.Surface; list.ForeColor = DesktopTheme.Text; list.BorderStyle = BorderStyle.None; }
        Add(TextBlock("Tài khoản Muse", 10, bold: true), 8);
        Add(TextBlock("Nhiều tài khoản có thể đăng nhập và hoạt động cùng lúc; gateway tự chia phiên giữa các tài khoản đang bật.", 9, DesktopTheme.Muted), 8);
        Add(_accountsList, 8);
        Add(ButtonRow(
            ("Thêm tài khoản", (_, _) => AddAccountRequested?.Invoke(this, EventArgs.Empty)),
            ("Bật/Tắt", async (_, _) => await ToggleAccountAsync()),
            ("Xóa", async (_, _) => await DeleteAccountAsync()),
            ("Làm mới", async (_, _) => await RefreshAccountsAsync())), 18);
        Add(TextBlock("Kết nối OpenAI (trên máy này)", 10, bold: true), 8);
        Add(TextBlock("Dán Base URL và API key vào ứng dụng khác (kiểu 9router) để dùng Muse theo chuẩn OpenAI. Link chỉ hoạt động khi Muse đang mở; cổng được chọn ngẫu nhiên mỗi lần khởi động.", 9, DesktopTheme.Muted), 8);
        _baseUrlBox.Text = connect is null ? "" : connect.BaseUrl.TrimEnd('/') + "/v1";
        _apiKeyBox.Text = connect?.ApiKey ?? "";
        Add(LabeledRow("Base URL", _baseUrlBox, () => _baseUrlBox.Text), 8);
        Add(LabeledRow("API key", _apiKeyBox, () => _apiKeyBox.Text), 8);
        Add(_keysList, 8);
        Add(ButtonRow(
            ("Tạo key mới", async (_, _) => await CreateKeyAsync()),
            ("Thu hồi / khôi phục", async (_, _) => await ToggleKeyRevokedAsync()),
            ("Xóa key", async (_, _) => await DeleteKeyAsync()),
            ("Làm mới", async (_, _) => await RefreshKeysAsync())), 18);
        Add(TextBlock("Kỹ năng", 10, bold: true), 8);
        Add(TextBlock("Cài skill từ file .md/.txt hoặc gói .zip chứa SKILL.md. Khi chat, gọi skill bằng @tenskill, ví dụ @fe.", 9, DesktopTheme.Muted), 8);
        Add(_skillsList, 8);
        Add(ButtonRow(
            ("Cài từ file…", (_, _) => ImportSkill()),
            ("Bật/Tắt", (_, _) => ToggleSkill()),
            ("Xóa", (_, _) => DeleteSkill()),
            ("Làm mới", (_, _) => RefreshSkills())), 8);
        _panelStatus = TextBlock("", 9, DesktopTheme.Muted);
        Add(_panelStatus, 18);
        RefreshSkills();
        if (connect is null) _panelStatus.Text = "Gateway chưa sẵn sàng: mở lại Cài đặt sau khi đã kết nối để quản lý tài khoản và key. Kỹ năng vẫn dùng được.";
        else Shown += async (_, _) => { await RefreshAccountsAsync(); await RefreshKeysAsync(); };
        _readOnly = new CheckBox { Text = "Chỉ tìm hiểu dự án — không sửa tệp hoặc chạy lệnh", AutoSize = true, Checked = readOnly, Enabled = !busy, ForeColor = DesktopTheme.Text };
        Add(_readOnly);
        _turbo = new CheckBox { Text = "Turbo — tự sửa mã, chạy lệnh và kiểm thử; Esc hoặc Dừng để ngắt", AutoSize = true, Checked = turbo, Enabled = !busy, ForeColor = DesktopTheme.Text };
        Add(_turbo);
        _projectReview = new CheckBox { Text = "Phản biện dự án — viết mã → đọc lại/phản biện → sửa và kiểm thử lại", AutoSize = true, Enabled = !busy, ForeColor = DesktopTheme.Text };
        Add(_projectReview, 8);
        Add(TextBlock("Hai vai trò dùng cùng kết nối Muse. Vai trò phản biện chỉ đọc; vai trò viết mã xử lý các vấn đề cụ thể. Chế độ này dùng thêm lượt AI và chỉ kết thúc khi đủ kiểm chứng.", 9, DesktopTheme.Muted), 16);
        _turbo.CheckedChanged += (_, _) => { if (_turbo.Checked) _readOnly.Checked = false; };
        _readOnly.CheckedChanged += (_, _) => { if (_readOnly.Checked) _turbo.Checked = false; };
        Add(TextBlock("Muse tự quản lý kết nối trên máy; API key ở mục Kết nối OpenAI phía trên chỉ dùng khi bạn muốn gắn ứng dụng khác vào.", 10, DesktopTheme.Muted), 20);
        Add(TextBlock("Giao diện", 10, bold: true), 8);
        _theme = Choice(new object[] { "Tối", "Sáng" }, DesktopTheme.Mode == DesktopTheme.ThemeMode.Light ? 1 : 0); _theme.Enabled = !busy; Add(_theme);
        Add(new UpdateSettingsControl(checkUpdates, openUpdate, latestUpdate, updateFeed), 20);
        var advanced = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Margin = Padding.Empty };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) advanced.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var toggle = new CleanButton { Text = "Chi tiết nâng cao", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Quiet = true };
        toggle.Click += (_, _) => { advanced.Visible = !advanced.Visible; toggle.Text = advanced.Visible ? "Thu gọn chi tiết" : "Chi tiết nâng cao"; content.PerformLayout(); }; Add(toggle, 10);
        advanced.Controls.Add(TextBlock("Công cụ chạy lệnh", 10, bold: true), 0, 0);
        _shell = Choice(new object[] { "PowerShell (mặc định)", "Command Prompt", "Bash (nếu đã cài)" }, shellIndex); _shell.Enabled = !busy; _shell.Margin = new Padding(0, 8, 0, 14); advanced.Controls.Add(_shell, 0, 1);
        var openData = new CleanButton { Text = "Mở thư mục dữ liệu ứng dụng", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 12) };
        openData.Click += (_, _) => { Directory.CreateDirectory(dataDirectory); Process.Start(new ProcessStartInfo(dataDirectory) { UseShellExecute = true }); }; advanced.Controls.Add(openData, 0, 2);
        var logs = new RichTextBox { Text = diagnostics.Length == 0 ? "Chưa ghi nhận lỗi thiết lập." : diagnostics, Dock = DockStyle.Top, Height = 150, ReadOnly = true, BackColor = DesktopTheme.Surface, ForeColor = DesktopTheme.Text, Font = DesktopTheme.Code, BorderStyle = BorderStyle.None };
        advanced.Controls.Add(logs, 0, 3); Add(advanced);
        scroll.Controls.Add(content); root.Controls.Add(scroll, 0, 0);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = DesktopTheme.Background, Padding = new Padding(0, 12, 0, 0) };
        var done = new CleanButton { Text = "Xong", Primary = true, Width = 112, Height = 40, DialogResult = DialogResult.OK };
        footer.Controls.Add(done); root.Controls.Add(footer, 0, 1); Controls.Add(root); AcceptButton = done; CancelButton = done;
        void WrapContent()
        {
            var width = Math.Max(160, scroll.ClientSize.Width - content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            foreach (var label in labels) label.MaximumSize = new Size(width, 0);
            _readOnly.MaximumSize = new Size(width, 0);
            _turbo.MaximumSize = new Size(width, 0);
            _projectReview.MaximumSize = new Size(width, 0);
        }
        scroll.SizeChanged += (_, _) => WrapContent(); WrapContent(); ResumeLayout(performLayout: true);
    }

    private static FlowLayoutPanel ButtonRow(params (string Text, EventHandler Click)[] items)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty, BackColor = DesktopTheme.Background };
        foreach (var (text, click) in items)
        {
            var button = new CleanButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 36, Margin = new Padding(0, 0, 8, 0) };
            button.Click += click;
            row.Controls.Add(button);
        }
        return row;
    }

    private static TableLayoutPanel LabeledRow(string label, TextBox box, Func<string> copyValue)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        var caption = DesktopTheme.Label(label, 9, DesktopTheme.Muted);
        caption.Dock = DockStyle.Fill; caption.TextAlign = ContentAlignment.MiddleLeft;
        box.BackColor = DesktopTheme.Surface; box.ForeColor = DesktopTheme.Text; box.BorderStyle = BorderStyle.None; box.Font = DesktopTheme.Code;
        var copy = new CleanButton { Text = "Sao chép", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 32, Quiet = true };
        copy.Click += (_, _) => { var value = copyValue(); if (value.Length > 0) Clipboard.SetText(value); };
        row.Controls.Add(caption, 0, 0); row.Controls.Add(box, 1, 0); row.Controls.Add(copy, 2, 0);
        return row;
    }

    private async Task RefreshAccountsAsync()
    {
        if (_connect is null) return;
        try
        {
            _accounts = await _connect.Client.ListAccounts(_connect.BaseUrl, _connect.ApiKey);
            _accountsList.Items.Clear();
            foreach (var a in _accounts) _accountsList.Items.Add($"{a.Label} — {a.Status}" + (a.Enabled ? "" : " (đã tắt)"));
            if (_accounts.Count == 0) _accountsList.Items.Add("(chưa có tài khoản nào)");
        }
        catch (Exception ex) { _panelStatus.Text = "Không tải được danh sách tài khoản: " + ex.Message; }
    }

    private (string Id, string Label, string Status, bool Enabled)? SelectedAccount()
    {
        var i = _accountsList.SelectedIndex;
        return i >= 0 && i < _accounts.Count ? _accounts[i] : null;
    }

    private async Task ToggleAccountAsync()
    {
        if (_connect is null || SelectedAccount() is not { } account) { _panelStatus.Text = "Hãy chọn một tài khoản trước."; return; }
        try
        {
            await _connect.Client.SetAccountEnabled(_connect.BaseUrl, _connect.ApiKey, account.Id, !account.Enabled);
            _panelStatus.Text = $"Đã {(account.Enabled ? "tắt" : "bật")} tài khoản {account.Label}.";
            await RefreshAccountsAsync();
        }
        catch (Exception ex) { _panelStatus.Text = "Không đổi được trạng thái tài khoản: " + ex.Message; }
    }

    private async Task DeleteAccountAsync()
    {
        if (_connect is null || SelectedAccount() is not { } account) { _panelStatus.Text = "Hãy chọn một tài khoản trước."; return; }
        if (MessageBox.Show(this, $"Xóa tài khoản '{account.Label}' khỏi Muse? Phiên đăng nhập của tài khoản này sẽ bị gỡ.", "Xóa tài khoản", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            await _connect.Client.DeleteAccount(_connect.BaseUrl, _connect.ApiKey, account.Id);
            _panelStatus.Text = $"Đã xóa tài khoản {account.Label}.";
            await RefreshAccountsAsync();
        }
        catch (Exception ex) { _panelStatus.Text = "Không xóa được tài khoản: " + ex.Message; }
    }

    private async Task RefreshKeysAsync()
    {
        if (_connect is null) return;
        try
        {
            _keys = await _connect.Client.ListKeys(_connect.BaseUrl, _connect.ApiKey);
            _keysList.Items.Clear();
            foreach (var k in _keys) _keysList.Items.Add($"{k.Name} — {k.Prefix}…" + (k.Revoked ? " (đã thu hồi)" : ""));
            if (_keys.Count == 0) _keysList.Items.Add("(chưa tạo key riêng nào — key ở trên vẫn dùng được)");
        }
        catch (Exception ex) { _panelStatus.Text = "Không tải được danh sách key: " + ex.Message; }
    }

    private (string Id, string Name, string Prefix, bool Revoked)? SelectedKey()
    {
        var i = _keysList.SelectedIndex;
        return i >= 0 && i < _keys.Count ? _keys[i] : null;
    }

    private async Task CreateKeyAsync()
    {
        if (_connect is null) { _panelStatus.Text = "Gateway chưa sẵn sàng."; return; }
        try
        {
            var plain = await _connect.Client.CreateKey(_connect.BaseUrl, _connect.ApiKey, "Key " + DateTime.Now.ToString("HH:mm dd/MM"));
            Clipboard.SetText(plain);
            _panelStatus.Text = "Đã tạo key mới và sao chép vào clipboard. Key đầy đủ chỉ hiển thị một lần.";
            MessageBox.Show(this, "API key mới (đã sao chép vào clipboard):\n\n" + plain + "\n\nHãy dán vào ứng dụng cần kết nối ngay; key sẽ không hiển thị lại.", "Đã tạo API key", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshKeysAsync();
        }
        catch (Exception ex) { _panelStatus.Text = "Không tạo được key: " + ex.Message; }
    }

    private async Task ToggleKeyRevokedAsync()
    {
        if (_connect is null || SelectedKey() is not { } key) { _panelStatus.Text = "Hãy chọn một key trước."; return; }
        try
        {
            await _connect.Client.RevokeKey(_connect.BaseUrl, _connect.ApiKey, key.Id, !key.Revoked);
            _panelStatus.Text = key.Revoked ? $"Đã khôi phục key {key.Name}." : $"Đã thu hồi key {key.Name}.";
            await RefreshKeysAsync();
        }
        catch (Exception ex) { _panelStatus.Text = "Không đổi được trạng thái key: " + ex.Message; }
    }

    private async Task DeleteKeyAsync()
    {
        if (_connect is null || SelectedKey() is not { } key) { _panelStatus.Text = "Hãy chọn một key trước."; return; }
        if (MessageBox.Show(this, $"Xóa hẳn key '{key.Name}'? Ứng dụng đang dùng key này sẽ mất kết nối.", "Xóa API key", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            await _connect.Client.DeleteKey(_connect.BaseUrl, _connect.ApiKey, key.Id);
            _panelStatus.Text = $"Đã xóa key {key.Name}.";
            await RefreshKeysAsync();
        }
        catch (Exception ex) { _panelStatus.Text = "Không xóa được key: " + ex.Message; }
    }

    private void RefreshSkills()
    {
        if (_skillStore is null) { _skillsList.Items.Clear(); _skillsList.Items.Add("(chưa khả dụng)"); return; }
        try
        {
            _skillItems = _skillStore.List();
            _skillsList.Items.Clear();
            foreach (var s in _skillItems) _skillsList.Items.Add($"@{s.Alias} — {s.Name}" + (_skillStore.IsEnabled(s.Alias) ? "" : " (đã tắt)"));
            if (_skillItems.Count == 0) _skillsList.Items.Add("(chưa cài kỹ năng nào)");
        }
        catch (Exception ex) { _panelStatus.Text = "Không tải được kỹ năng: " + ex.Message; }
    }

    private SkillDefinition? SelectedSkill()
    {
        var i = _skillsList.SelectedIndex;
        return i >= 0 && i < _skillItems.Count ? _skillItems[i] : null;
    }

    private void ImportSkill()
    {
        if (_skillStore is null) { _panelStatus.Text = "Kỹ năng chưa khả dụng."; return; }
        using var dialog = new OpenFileDialog { Title = "Chọn file kỹ năng", Filter = "File kỹ năng (*.md;*.txt;*.zip)|*.md;*.txt;*.zip" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var imported = _skillStore.Import(dialog.FileName);
            RefreshSkills();
            _panelStatus.Text = $"Đã cài kỹ năng @{imported.Alias} — {imported.Name}. Gọi trong chat bằng @{imported.Alias}.";
        }
        catch (Exception ex) { _panelStatus.Text = "Không cài được kỹ năng: " + ex.Message; }
    }

    private void ToggleSkill()
    {
        if (_skillStore is null || SelectedSkill() is not { } skill) { _panelStatus.Text = "Hãy chọn một kỹ năng trước."; return; }
        try
        {
            var enabled = !_skillStore.IsEnabled(skill.Alias);
            _skillStore.SetEnabled(skill.Alias, enabled);
            RefreshSkills();
            _panelStatus.Text = $"Đã {(enabled ? "bật" : "tắt")} kỹ năng @{skill.Alias}.";
        }
        catch (Exception ex) { _panelStatus.Text = "Không đổi được trạng thái kỹ năng: " + ex.Message; }
    }

    private void DeleteSkill()
    {
        if (_skillStore is null || SelectedSkill() is not { } skill) { _panelStatus.Text = "Hãy chọn một kỹ năng trước."; return; }
        if (MessageBox.Show(this, $"Xóa kỹ năng @{skill.Alias} khỏi Muse?", "Xóa kỹ năng", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            _skillStore.Remove(skill.Alias);
            RefreshSkills();
            _panelStatus.Text = $"Đã xóa kỹ năng @{skill.Alias}.";
        }
        catch (Exception ex) { _panelStatus.Text = "Không xóa được kỹ năng: " + ex.Message; }
    }

    private static ComboBox Choice(object[] items, int selected)
    {
        var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = DesktopTheme.Surface, ForeColor = DesktopTheme.Text, FlatStyle = FlatStyle.Flat, Margin = Padding.Empty };
        combo.Items.AddRange(items); combo.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1); return combo;
    }
}
