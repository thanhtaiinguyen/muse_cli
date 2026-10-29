using System.Diagnostics;

namespace Muse2Api.WinForms;

/// <summary>Content-sized settings; a fixed footer stays reachable while content scrolls.</summary>
internal sealed class PreferencesForm : Form
{
    private readonly CheckBox _readOnly, _turbo;
    private readonly ComboBox _theme, _shell;
    public event EventHandler? LoginRequested;
    public bool ReadOnly => _readOnly.Checked;
    public bool Turbo => _turbo.Checked;
    public int ShellIndex => _shell.SelectedIndex;
    public DesktopTheme.ThemeMode SelectedTheme => _theme.SelectedIndex == 1 ? DesktopTheme.ThemeMode.Light : DesktopTheme.ThemeMode.Dark;

    public PreferencesForm(bool connected, bool busy, bool readOnly, bool turbo, int shellIndex, string dataDirectory, string diagnostics)
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
        _readOnly = new CheckBox { Text = "Chỉ tìm hiểu dự án — không sửa tệp hoặc chạy lệnh", AutoSize = true, Checked = readOnly, Enabled = !busy, ForeColor = DesktopTheme.Text };
        Add(_readOnly);
        _turbo = new CheckBox { Text = "Turbo — tự chạy mọi thao tác, không hỏi trước; trả lời bằng tiếng Việt", AutoSize = true, Checked = turbo, Enabled = !busy, ForeColor = DesktopTheme.Text };
        Add(_turbo);
        _turbo.CheckedChanged += (_, _) => { if (_turbo.Checked) _readOnly.Checked = false; };
        _readOnly.CheckedChanged += (_, _) => { if (_readOnly.Checked) _turbo.Checked = false; };
        Add(TextBlock("Muse tự quản lý kết nối trên máy. Bạn không cần lấy hoặc nhập mã truy cập.", 10, DesktopTheme.Muted), 20);
        Add(TextBlock("Giao diện", 10, bold: true), 8);
        _theme = Choice(new object[] { "Tối", "Sáng" }, DesktopTheme.Mode == DesktopTheme.ThemeMode.Light ? 1 : 0); _theme.Enabled = !busy; Add(_theme);
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
        }
        scroll.SizeChanged += (_, _) => WrapContent(); WrapContent(); ResumeLayout(performLayout: true);
    }

    private static ComboBox Choice(object[] items, int selected)
    {
        var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = DesktopTheme.Surface, ForeColor = DesktopTheme.Text, FlatStyle = FlatStyle.Flat, Margin = Padding.Empty };
        combo.Items.AddRange(items); combo.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1); return combo;
    }
}
