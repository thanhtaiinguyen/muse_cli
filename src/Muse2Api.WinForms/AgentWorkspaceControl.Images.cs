namespace Muse2Api.WinForms;

public sealed partial class AgentWorkspaceControl
{
    private readonly List<ChatImage> _imageDraft = new();
    private readonly FlowLayoutPanel _imageStrip = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent, AccessibleName = "Ảnh chưa gửi" };
    private MeshLayout _composer = null!;
    private TableLayoutPanel _conversation = null!;

    private bool PasteChatImages()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return false;
            return AddChatImages(data);
        }
        catch (Exception ex) { _state.Text = "Không dán được ảnh: " + ex.Message; return true; }
    }

    private bool AddChatImages(IDataObject data)
    {
        var incoming = ChatImage.Read(data);
        if (incoming.Count == 0) return false; // Keep normal text paste intact.
        if (_busy) { _state.Text = "Đợi Muse hoàn tất lượt hiện tại rồi đính kèm ảnh."; return true; }
        if (_imageDraft.Count + incoming.Count > 4 || _imageDraft.Sum(i => (long)i.Bytes.Length) + incoming.Sum(i => (long)i.Bytes.Length) > 16 * 1024 * 1024)
            throw new InvalidOperationException("Tối đa 4 ảnh, tổng dung lượng 16 MiB mỗi lượt.");
        _imageDraft.AddRange(incoming); RefreshImageDraft();
        _state.Text = "Đã đính kèm " + _imageDraft.Count + " ảnh. Bạn có thể bỏ ảnh hoặc bấm Gửi.";
        return true;
    }

    private void RefreshImageDraft()
    {
        _imageStrip.SuspendLayout();
        foreach (Control card in _imageStrip.Controls.Cast<Control>().ToArray()) card.Dispose();
        _imageStrip.Controls.Clear();
        foreach (var image in _imageDraft.ToArray())
        {
            var card = new ChatImageCard(image, () => { if (_busy) return; _imageDraft.Remove(image); RefreshImageDraft(); });
            if (DeviceDpi != 96) card.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
            _imageStrip.Controls.Add(card);
        }
        var extra = _imageDraft.Count == 0 ? 0 : LogicalToDeviceUnits(88);
        _composer.RowStyles[0].Height = extra;
        _conversation.RowStyles[2].Height = LogicalToDeviceUnits(140) + extra;
        _imageStrip.Visible = extra > 0;
        _imageStrip.ResumeLayout();
    }

    private void ClearImageDraft() { _imageDraft.Clear(); RefreshImageDraft(); }
}
