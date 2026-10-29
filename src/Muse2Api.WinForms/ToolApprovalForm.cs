using Muse2Api.Agent;

namespace Muse2Api.WinForms;

internal sealed class ToolApprovalForm : Form
{
    public ToolApprovalForm(ToolApproval approval)
    {
        var editing = approval.Call.Name is "write_file" or "edit_file";
        var file = approval.FilePath is null ? "" : Path.GetFileName(approval.FilePath);
        Text = editing ? "Xem thay đổi trước khi lưu" : "Cho phép thao tác trong dự án";
        Size = new Size(980, 690); MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterParent; Font = DesktopTheme.Body;
        BackColor = DesktopTheme.Background; ForeColor = DesktopTheme.Text;
        var layout = DesktopTheme.Column(new(SizeType.Absolute, 50), new(SizeType.Absolute, 62), new(SizeType.Percent, 100), new(SizeType.Absolute, 58));
        layout.Padding = new Padding(22); layout.BackColor = DesktopTheme.Background;
        layout.Controls.Add(DesktopTheme.Label(editing ? $"Muse muốn cập nhật {file}" : "Muse muốn thực hiện một thao tác", 18, bold: true), 0, 0);
        layout.Controls.Add(DesktopTheme.Label(editing
            ? "Xem nội dung trước và sau bên dưới. Bạn có thể cho phép thay đổi, giữ nguyên tệp hoặc dừng yêu cầu."
            : "Muse cần chạy thao tác bên dưới để tiếp tục. Thao tác sẽ dùng quyền truy cập của bạn trên máy; hãy xem trước khi cho phép.", 10, DesktopTheme.Muted), 0, 1);
        if (editing)
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(900, 460), SplitterDistance = 445, Panel1MinSize = 220, Panel2MinSize = 220, BackColor = DesktopTheme.Border };
            split.Panel1.Controls.Add(TextPanel("TRƯỚC KHI THAY ĐỔI", approval.OriginalText ?? "(Tệp mới)", DesktopTheme.Muted));
            split.Panel2.Controls.Add(TextPanel("SAU KHI THAY ĐỔI", approval.NewText ?? "", DesktopTheme.Success));
            layout.Controls.Add(split, 0, 2);
        }
        else layout.Controls.Add(TextPanel("THAO TÁC ĐƯỢC ĐỀ XUẤT", approval.Preview, DesktopTheme.Accent), 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 12, 0, 0), BackColor = DesktopTheme.Background };
        var allow = new CleanButton { Text = editing ? "Cho phép thay đổi" : "Cho phép chạy", Primary = true, DialogResult = DialogResult.OK, Width = 180 };
        var deny = new CleanButton { Text = editing ? "Giữ nguyên tệp" : "Không chạy", DialogResult = DialogResult.Cancel, Width = 150 };
        var stop = new CleanButton { Text = "Dừng yêu cầu", Quiet = true, DialogResult = DialogResult.Abort, Width = 140 };
        actions.Controls.Add(allow); actions.Controls.Add(deny); actions.Controls.Add(stop);
        layout.Controls.Add(actions, 0, 3); Controls.Add(layout); CancelButton = deny;
    }

    private static Control TextPanel(string title, string content, Color color)
    {
        var panel = DesktopTheme.Column(new(SizeType.Absolute, 32), new(SizeType.Percent, 100));
        panel.Padding = new Padding(8); panel.BackColor = DesktopTheme.Surface;
        panel.Controls.Add(DesktopTheme.Label(title, 8.5f, color, true), 0, 0);
        panel.Controls.Add(new RichTextBox
        {
            Text = content, Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false,
            Font = DesktopTheme.Code, BackColor = DesktopTheme.Surface, ForeColor = DesktopTheme.Text,
            BorderStyle = BorderStyle.None, DetectUrls = false,
        }, 0, 1);
        return panel;
    }
}
