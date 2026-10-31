using System.ComponentModel;
using System.Drawing.Imaging;

namespace Muse2Api.WinForms;

internal sealed class ChatInputBox : TextBox
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<bool>? PasteImage { get; set; }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData is (Keys.Control | Keys.V) or (Keys.Shift | Keys.Insert) && PasteImage?.Invoke() == true) return true;
        return base.ProcessCmdKey(ref message, keyData);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0302 && PasteImage?.Invoke() == true) return; // WM_PASTE, including the context menu.
        base.WndProc(ref message);
    }
}

internal sealed record ChatImage(byte[] Bytes, string Name, string Mime)
{
    public string DataUrl => "data:" + Mime + ";base64," + Convert.ToBase64String(Bytes);
    public const int MaxBytes = 8 * 1024 * 1024;
    public static ChatImage FromImage(Image image, string name)
    {
        if ((long)image.Width * image.Height > 32_000_000) throw new InvalidOperationException("Ảnh quá lớn; hãy chụp một vùng nhỏ hơn.");
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
        if (stream.Length > MaxBytes) throw new InvalidOperationException("Mỗi ảnh được tối đa 8 MiB.");
        return new(stream.ToArray(), name, "image/png");
    }
    public static ChatImage FromFile(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidOperationException("Mỗi ảnh được tối đa 8 MiB.");
        using var image = Image.FromFile(path); return FromImage(image, Path.GetFileName(path));
    }
    public static List<ChatImage> Read(IDataObject data)
    {
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is Stream png)
        {
            if (png.CanSeek && png.Length > MaxBytes) throw new InvalidOperationException("Mỗi ảnh được tối đa 8 MiB.");
            using var copy = new MemoryStream();
            if (png.CanSeek) png.Position = 0;
            png.CopyTo(copy);
            if (copy.Length > MaxBytes) throw new InvalidOperationException("Mỗi ảnh được tối đa 8 MiB.");
            copy.Position = 0; using var image = Image.FromStream(copy); return new() { FromImage(image, "Ảnh chụp màn hình.png") };
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is Image bitmap)
            return new() { FromImage(bitmap, "Ảnh chụp màn hình.png") };
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            return files.Where(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
                .Take(5).Select(FromFile).ToList();
        return new();
    }
}

internal sealed class ChatImageCard : Panel
{
    private readonly Image _thumbnail;
    public ChatImageCard(ChatImage attachment, Action remove)
    {
        Size = new Size(164, 64); Margin = new Padding(4); BackColor = DesktopTheme.SurfaceStrong;
        using var bytes = new MemoryStream(attachment.Bytes); using var image = Image.FromStream(bytes);
        var ratio = Math.Min(72d / image.Width, 54d / image.Height);
        _thumbnail = new Bitmap(image, new Size(Math.Max(1, (int)(image.Width * ratio)), Math.Max(1, (int)(image.Height * ratio))));
        var picture = new PictureBox { Image = _thumbnail, SizeMode = PictureBoxSizeMode.CenterImage, Bounds = new Rectangle(4, 4, 76, 56) };
        var label = DesktopTheme.Label(attachment.Name, 8, DesktopTheme.Muted); label.Dock = DockStyle.None; label.SetBounds(84, 34, 76, 26); label.AutoEllipsis = true;
        var close = new CleanButton { Text = "", Icon = WorkspaceIcon.Close, Quiet = true, Bounds = new Rectangle(125, 0, 34, 32), AccessibleName = "Bỏ ảnh " + attachment.Name };
        close.Click += (_, _) => remove(); Controls.Add(picture); Controls.Add(label); Controls.Add(close);
    }
    protected override void Dispose(bool disposing) { if (disposing) _thumbnail.Dispose(); base.Dispose(disposing); }
}
