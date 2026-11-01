using Muse2Api;

namespace Muse2Api.WinForms;

/// <summary>A passive banner outside the transcript: updates never interrupt a running task.</summary>
internal sealed class UpdateNoticeControl : UserControl
{
    private readonly Label _message = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 12, 0) };
    private readonly CleanButton _open = new() { Text = "Xem bản mới", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Primary = true };
    private readonly CleanButton _later = new() { Text = "Ẩn thông báo", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Quiet = true };
    public event Action<AvailableUpdate>? OpenRequested;
    public event Action<AvailableUpdate>? DismissRequested;
    public AvailableUpdate? Available { get; private set; }

    public UpdateNoticeControl()
    {
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Margin = Padding.Empty; Font = DesktopTheme.Body;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Padding = new Padding(16, 8, 16, 8), Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.ColumnStyles.Add(new(SizeType.AutoSize)); layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(_message, 0, 0); layout.Controls.Add(_open, 1, 0); layout.Controls.Add(_later, 2, 0); Controls.Add(layout);
        layout.SizeChanged += (_, _) => _message.MaximumSize = new Size(Math.Max(120, layout.ClientSize.Width - layout.Padding.Horizontal - _open.Width - _later.Width - 28), 0);
        _open.Click += (_, _) => { if (Available is { } update) OpenRequested?.Invoke(update); };
        _later.Click += (_, _) => { Visible = false; if (Available is { } update) DismissRequested?.Invoke(update); };
        AccessibleName = "Thông báo cập nhật Muse";
    }

    public void ShowUpdate(AvailableUpdate update)
    {
        if (!DesktopUpdates.IsTrustedLink(update)) return;
        Available = update;
        _message.Text = update.Feed == UpdateFeed.Source ? $"Có mã nguồn Muse {update.Version} mới — lưu thay đổi rồi pull/build lại." : $"Đã có Muse {update.Version}. Xem bản mới khi bạn sẵn sàng.";
        _open.Text = update.Feed == UpdateFeed.Source ? "Xem mã nguồn" : "Xem bản mới";
        Visible = true; PerformLayout();
    }
}

internal sealed class UpdateSettingsControl : UserControl
{
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 10) };
    private readonly CleanButton _check = new() { Text = "Kiểm tra cập nhật", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly CleanButton _open = new() { Text = "Xem bản mới", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Primary = true };
    private AvailableUpdate? _available;
    private bool _checking;

    public UpdateSettingsControl(Func<Task<UpdateCheckResult>>? check, Action<AvailableUpdate>? open, UpdateCheckResult? latest, UpdateFeed feed)
    {
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top; Font = DesktopTheme.Body;
        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        var title = DesktopTheme.Label("Cập nhật ứng dụng", 10, bold: true); title.AutoSize = true; title.Dock = DockStyle.Top;
        var version = new Label { Text = $"Muse {Application.ProductVersion.Split('+')[0]} · " + (feed == UpdateFeed.Source ? "Chạy từ mã nguồn" : "Bản đóng gói"), AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 8) };
        _status.Text = latest?.Message ?? "Tự kiểm tra khi mở ứng dụng và định kỳ 6 giờ; không ảnh hưởng cuộc chat.";
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
        actions.Controls.AddRange(new Control[] { _check, _open });
        layout.Controls.Add(title); layout.Controls.Add(version); layout.Controls.Add(_status); layout.Controls.Add(actions); Controls.Add(layout);
        SizeChanged += (_, _) => { _status.MaximumSize = new Size(Math.Max(160, Width - 12), 0); version.MaximumSize = _status.MaximumSize; };
        void Apply(UpdateCheckResult result)
        {
            _status.Text = result.Message; _available = result.Update;
            _open.Text = feed == UpdateFeed.Source ? "Xem mã nguồn" : "Xem bản mới";
            _open.Visible = _available is not null && DesktopUpdates.IsTrustedLink(_available);
        }
        if (latest is not null) Apply(latest);
        _check.Enabled = check is not null;
        _check.Click += async (_, _) =>
        {
            if (_checking || check is null) return;
            _checking = true; _check.Enabled = false; _status.Text = "Đang kiểm tra cập nhật…";
            try { var result = await check(); if (!IsDisposed) Apply(result); }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or ObjectDisposedException)
            { if (!IsDisposed) _status.Text = "Chưa kiểm tra được. Hãy thử lại sau."; }
            finally { if (!IsDisposed) { _checking = false; _check.Enabled = true; } }
        };
        _open.Click += (_, _) => { if (_available is { } update && DesktopUpdates.IsTrustedLink(update)) open?.Invoke(update); };
    }
}
