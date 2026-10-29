using Muse2Api.Computer;

namespace Muse2Api.WinForms;

public sealed partial class AgentWorkspaceControl
{
    private readonly DesktopComputer _computer = new();
    private readonly CleanButton _computerButton = new() { Text = "Màn hình", Icon = WorkspaceIcon.Monitor, Width = 112, Quiet = true };

    private bool ConfigureComputer(bool duringOperation = false, CancellationToken cancel = default)
    {
        if (_busy && !duringOperation) return false;
        if (_permissions.SelectedIndex == 1) { _state.Text = "Đổi chế độ Chỉ xem trong Cài đặt trước khi bật kiểm thử giao diện."; return false; }
        using var dialog = new Form { Text = "Chọn cửa sổ để Muse kiểm thử", Size = new Size(700, 430), MinimumSize = new Size(600, 400), StartPosition = FormStartPosition.CenterParent, Font = DesktopTheme.Body, BackColor = DesktopTheme.Background };
        var layout = DesktopTheme.Column(new(SizeType.Absolute, 42), new(SizeType.Absolute, 85), new(SizeType.Percent, 100), new(SizeType.Absolute, 48));
        layout.Padding = new Padding(20); layout.BackColor = DesktopTheme.Background;
        layout.Controls.Add(DesktopTheme.Label("Kiểm thử ứng dụng đang mở", 17, bold: true), 0, 0);
        layout.Controls.Add(DesktopTheme.Label("Chọn cửa sổ để Muse đọc giao diện, chụp ảnh, bấm nút và nhập dữ liệu. Nội dung và ảnh vùng cửa sổ sẽ được gửi tới Muse. Quyền có hiệu lực trong lượt làm việc tiếp theo; Esc hoặc Dừng để hủy.", 10, DesktopTheme.Muted), 0, 1);
        var windows = new ListBox { Dock = DockStyle.Fill, BackColor = DesktopTheme.Surface, ForeColor = DesktopTheme.Text, BorderStyle = BorderStyle.None, IntegralHeight = false };
        void RefreshWindows()
        {
            windows.Items.Clear(); foreach (var window in DesktopComputer.ListWindows()) windows.Items.Add(window);
            windows.SelectedIndex = -1;
        }
        RefreshWindows(); layout.Controls.Add(windows, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var allow = new CleanButton { Text = "Cho phép cửa sổ này", Primary = true, Width = 190, DialogResult = DialogResult.OK, Enabled = false };
        windows.SelectedIndexChanged += (_, _) => allow.Enabled = windows.SelectedItem is DesktopWindow;
        var refresh = Button("Làm mới", (_, _) => RefreshWindows()); refresh.Width = 100;
        var off = new CleanButton { Text = "Tắt điều khiển", Width = 145, DialogResult = DialogResult.No };
        actions.Controls.AddRange(new Control[] { allow, refresh, off }); layout.Controls.Add(actions, 0, 3); dialog.Controls.Add(layout);
        dialog.AcceptButton = allow; dialog.CancelButton = off;
        using var registration = cancel.Register(() => Post(() => { if (!dialog.IsDisposed) dialog.Close(); }));
        var result = dialog.ShowDialog(this);
        if (cancel.IsCancellationRequested) return false;
        if (result == DialogResult.OK && windows.SelectedItem is DesktopWindow target)
        {
            try
            {
                _computer.Authorize(target);
                if (duringOperation && !DesktopComputer.RegisterEmergencyStop(Handle)) { DisableComputer(); _state.Text = "Chưa đăng ký được phím Esc để dừng. Hãy thử lại sau."; return false; }
                _computerButton.Text = "Màn hình ●"; _state.Text = "Đã chọn " + target.Title + ". Esc để dừng khi Muse đang thao tác.";
                return true;
            }
            catch (Exception ex) { _state.Text = ex.Message; }
        }
        else if (result == DialogResult.No) DisableComputer();
        return false;
    }

    private Task<bool> RequestComputerAuthorization(CancellationToken cancel)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (cancel.IsCancellationRequested) { completion.TrySetCanceled(cancel); return; }
            try { var allowed = ConfigureComputer(duringOperation: true, cancel); if (cancel.IsCancellationRequested) completion.TrySetCanceled(cancel); else completion.TrySetResult(allowed); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task.WaitAsync(cancel);
    }

    private void DisableComputer()
    {
        _computer.Disarm();
        if (IsHandleCreated) DesktopComputer.UnregisterEmergencyStop(Handle);
        _computerButton.Text = "Màn hình";
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == DesktopComputer.EmergencyHotkey)
        {
            _cancel?.Cancel(); DisableComputer(); _state.Text = "Đã dừng điều khiển màn hình bằng Esc.";
        }
        base.WndProc(ref message);
    }
}
