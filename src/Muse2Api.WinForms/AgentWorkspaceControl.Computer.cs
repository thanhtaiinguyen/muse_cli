using Muse2Api.Computer;

namespace Muse2Api.WinForms;

public sealed partial class AgentWorkspaceControl
{
    private readonly DesktopComputer _computer = new();
    private readonly CleanButton _computerButton = new() { Text = "Màn hình", Icon = WorkspaceIcon.Monitor, Width = 112, Quiet = true };
    private readonly PictureBox _screenPreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, AccessibleName = "Xem trực tiếp cửa sổ kiểm thử" };
    private readonly CheckBox _previewEnabled = new() { Text = "Xem trực tiếp trên máy", Dock = DockStyle.Fill, Checked = true, AutoSize = true };
    private readonly Label _previewState = DesktopTheme.Label("Cửa sổ được tự chọn khi Muse thao tác; nút Màn hình để đổi cửa sổ. Ảnh xem trực tiếp chỉ ở trên máy.", 9, DesktopTheme.Muted);
    private bool _previewPending;
    private DateTime _nextPreview;

    private Control BuildScreenPreview()
    {
        var layout = DesktopTheme.Column(new(SizeType.Absolute, 36), new(SizeType.Percent, 100), new(SizeType.Absolute, 66));
        layout.Controls.Add(_previewEnabled, 0, 0); layout.Controls.Add(_screenPreview, 0, 1);
        _previewState.Dock = DockStyle.Fill; _previewState.AutoSize = false;
        layout.Controls.Add(_previewState, 0, 2);
        _previewEnabled.CheckedChanged += (_, _) => { if (!_previewEnabled.Checked) ClearScreenPreview("Đã tắt xem trực tiếp."); };
        return layout;
    }

    private void ClearScreenPreview(string status)
    {
        var old = _screenPreview.Image; _screenPreview.Image = null; old?.Dispose();
        _previewState.Text = status;
    }

    // One local frame at most every 1.5 s; no web calls, saved frames or frame queue.
    private async Task PollScreenPreview()
    {
        if (_previewPending || DateTime.UtcNow < _nextPreview) return;
        _nextPreview = DateTime.UtcNow.AddMilliseconds(1500);
        if (!_busy || !_previewEnabled.Checked || _computer.Target is null || _drawer.Panel2Collapsed || _detailTabs.SelectedIndex != 3) return;
        var target = _computer.Target; var cancel = _cancel?.Token ?? CancellationToken.None;
        _previewPending = true;
        try
        {
            var frame = await _computer.PreviewAsync(cancel);
            if (IsDisposed || cancel.IsCancellationRequested || target != _computer.Target || !_previewEnabled.Checked) return;
            if (frame is null) { ClearScreenPreview("Tạm dừng ảnh: cửa sổ kiểm thử bị che, không ở phía trước hoặc đang có thao tác."); return; }
            SetScreenPreview(frame);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) ClearScreenPreview("Tạm dừng xem trực tiếp: " + ex.Message); }
        finally { _previewPending = false; }
    }

    private void SetScreenPreview(string dataUrl)
    {
        // Clone before disposing the decoder's stream. Dispose the replaced bitmap.
        using var stream = new MemoryStream(Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]));
        using var decoded = Image.FromStream(stream);
        var image = new Bitmap(decoded); var old = _screenPreview.Image;
        _screenPreview.Image = image; old?.Dispose();
        _previewState.Text = "Trực tiếp · " + DateTime.Now.ToString("HH:mm:ss") + " · Esc hoặc Dừng để ngắt. Chỉ ảnh từ lệnh chụp mới được gửi cho AI.";
    }

    private bool ConfigureComputer(bool duringOperation = false, CancellationToken cancel = default)
    {
        if (_busy && !duringOperation) return false;
        if (_permissions.SelectedIndex == 1) { _state.Text = "Đổi chế độ Chỉ xem trong Cài đặt trước khi bật kiểm thử giao diện."; return false; }
        using var dialog = new ComputerScopeForm();
        using var registration = cancel.Register(() => Post(() => { if (!dialog.IsDisposed) dialog.Close(); }));
        var result = dialog.ShowDialog(this);
        if (cancel.IsCancellationRequested) return false;
        if (result == DialogResult.OK && dialog.SelectedWindow is { } target)
        {
            try { _computer.Authorize(target); return ApplyAuthorizedTarget(duringOperation, "Đã chọn "); }
            catch (Exception ex) { _state.Text = ex.Message; }
        }
        else if (result == DialogResult.No) DisableComputer();
        return false;
    }

    private bool ApplyAuthorizedTarget(bool duringOperation, string prefix)
    {
        var target = _computer.Target;
        if (target is null) return false;
        if (duringOperation && !DesktopComputer.RegisterEmergencyStop(Handle)) { DisableComputer(); _state.Text = "Chưa đăng ký được phím Esc để dừng. Hãy thử lại sau."; return false; }
        _computerButton.Text = "Màn hình ●"; _tips.SetToolTip(_computerButton, target.ToString()); _state.Text = prefix + target.Title + ". Esc để dừng khi Muse đang thao tác.";
        ToggleDrawer(3, forceOpen: true);
        return true;
    }

    private Task<bool> RequestComputerAuthorization(CancellationToken cancel)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (cancel.IsCancellationRequested) { completion.TrySetCanceled(cancel); return; }
            try
            {
                // One host decision: a hotkey/activation failure must stop,
                // not fall through to another picker or bypass read-only.
                var allowed = _permissions.SelectedIndex != 1 && (_computer.TryAutoAuthorize()
                    ? ApplyAuthorizedTarget(duringOperation: true, "Đã tự chọn ")
                    : ConfigureComputer(duringOperation: true, cancel));
                if (cancel.IsCancellationRequested) completion.TrySetCanceled(cancel); else completion.TrySetResult(allowed);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task.WaitAsync(cancel);
    }

    private void DisableComputer()
    {
        _computer.Disarm();
        ClearScreenPreview("Đã dừng truyền ảnh. Quyền màn hình không được lưu qua lượt mới hoặc khởi động lại.");
        if (IsHandleCreated) DesktopComputer.UnregisterEmergencyStop(Handle);
        _computerButton.Text = "Màn hình";
        _tips.SetToolTip(_computerButton, "Chọn cửa sổ để Muse đọc và thao tác trong lượt tiếp theo.");
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == DesktopComputer.EmergencyHotkey)
        {
            _cancel?.Cancel(); DisableComputer(); _state.Text = "Đã dừng điều khiển màn hình bằng Esc.";
        }
        base.WndProc(ref message);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (_busy && keyData == Keys.Escape)
        {
            _cancel?.Cancel(); DisableComputer(); _state.Text = "Đã dừng bằng Esc."; return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }
}
