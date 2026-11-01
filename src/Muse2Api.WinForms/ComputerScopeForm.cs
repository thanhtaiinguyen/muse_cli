using Muse2Api.Computer;

namespace Muse2Api.WinForms;

internal sealed class ComputerScopeForm : Form
{
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Tìm tên ứng dụng hoặc tiêu đề cửa sổ…", Margin = new Padding(0, 4, 10, 4) };
    private readonly ListBox _windows = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, ItemHeight = 28 };
    private readonly Label _count = new() { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 8) };
    private readonly CleanButton _allow = new() { Text = "Cho phép cửa sổ này", Primary = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false, DialogResult = DialogResult.OK };
    private IReadOnlyList<DesktopWindow> _all = Array.Empty<DesktopWindow>();
    public DesktopWindow? SelectedWindow => _windows.SelectedItem as DesktopWindow;

    public ComputerScopeForm(Func<IReadOnlyList<DesktopWindow>>? listWindows = null)
    {
        listWindows ??= DesktopComputer.ListWindows;
        Text = "Chọn cửa sổ cho Muse"; Font = DesktopTheme.Body; AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(740, 500); MinimumSize = new Size(640, 520); StartPosition = FormStartPosition.CenterParent;
        var root = DesktopTheme.Column(new(SizeType.AutoSize), new(SizeType.AutoSize), new(SizeType.AutoSize), new(SizeType.AutoSize), new(SizeType.Percent, 100), new(SizeType.AutoSize));
        root.Padding = new Padding(20);
        var title = DesktopTheme.Label("Muse làm việc với cửa sổ nào?", 17, bold: true); title.AutoSize = true; title.Dock = DockStyle.Top; title.Margin = new Padding(0, 0, 0, 12);
        var description = new Label { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 12),
            Text = "Muse được đọc, chụp ảnh, nhập liệu và dùng chuột trong cửa sổ này cùng hộp thoại/menu do nó mở. Nội dung được gửi tới Muse. Quyền chỉ có trong lượt này; Esc hoặc Dừng để thu hồi ngay." };
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        searchRow.ColumnStyles.Add(new(SizeType.Percent, 100)); searchRow.ColumnStyles.Add(new(SizeType.AutoSize));
        var refresh = new CleanButton { Text = "Làm mới", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        searchRow.Controls.Add(_search, 0, 0); searchRow.Controls.Add(refresh, 1, 0);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 12, 0, 0), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new(SizeType.AutoSize)); footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        var off = new CleanButton { Text = "Tắt điều khiển", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Quiet = true, DialogResult = DialogResult.No };
        var cancel = new CleanButton { Text = "Hủy", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, DialogResult = DialogResult.Cancel };
        footer.Controls.Add(off, 0, 0); footer.Controls.Add(cancel, 2, 0); footer.Controls.Add(_allow, 3, 0);
        root.Controls.Add(title, 0, 0); root.Controls.Add(description, 0, 1); root.Controls.Add(searchRow, 0, 2); root.Controls.Add(_count, 0, 3); root.Controls.Add(_windows, 0, 4); root.Controls.Add(footer, 0, 5); Controls.Add(root);
        root.SizeChanged += (_, _) => { description.MaximumSize = new Size(Math.Max(160, root.ClientSize.Width - root.Padding.Horizontal), 0); _count.MaximumSize = description.MaximumSize; };
        _windows.SelectedIndexChanged += (_, _) => _allow.Enabled = SelectedWindow is not null;
        _windows.DoubleClick += (_, _) => { if (SelectedWindow is not null) DialogResult = DialogResult.OK; };
        void Filter()
        {
            var selected = SelectedWindow;
            _windows.BeginUpdate(); _windows.Items.Clear();
            foreach (var window in _all.Where(w => w.ToString().Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase))) _windows.Items.Add(window);
            _windows.SelectedItem = selected; _windows.EndUpdate();
            _allow.Enabled = SelectedWindow is not null;
            _count.Text = _windows.Items.Count == 0 ? "Chưa thấy cửa sổ phù hợp. Mở ứng dụng cần thử rồi bấm Làm mới." : $"{_windows.Items.Count} cửa sổ · chọn một cửa sổ để tiếp tục";
        }
        void Refresh()
        {
            try { _all = listWindows(); Filter(); }
            catch { _all = Array.Empty<DesktopWindow>(); Filter(); _count.Text = "Chưa đọc được danh sách cửa sổ. Hãy thử Làm mới."; }
        }
        _search.TextChanged += (_, _) => Filter(); refresh.Click += (_, _) => Refresh(); Refresh();
        AcceptButton = _allow; CancelButton = cancel; DesktopTheme.Apply(this);
    }
}
