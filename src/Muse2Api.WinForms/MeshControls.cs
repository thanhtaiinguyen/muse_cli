using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Muse2Api.WinForms;

internal enum MeshTone { Canvas, Sidebar, Card, Hero, Composer }
internal enum WorkspaceIcon { None, Plus, Folder, Files, Changes, Terminal, Monitor, Settings, Send, Stop, Search, Code, Check, Close }

/// <summary>Native, cached mesh surfaces. No browser, animation loop or new dependencies.</summary>
internal sealed class MeshLayout : TableLayoutPanel
{
    private Bitmap? _field;
    private DesktopTheme.ThemeMode _renderedMode;
    public MeshTone Tone { get; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FocusHighlight { get; set; }

    public MeshLayout(MeshTone tone, params RowStyle[] rows)
    {
        Tone = tone; Dock = DockStyle.Fill; Margin = Padding.Empty;
        ColumnCount = 1; RowCount = rows.Length;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows) RowStyles.Add(row);
        RefreshPalette();
        DesktopTheme.ThemeChanged += (_, _) => RefreshPalette();
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void RefreshPalette()
    {
        _field?.Dispose();
        _field = null;
        BackColor = Tone == MeshTone.Sidebar ? DesktopTheme.Sidebar : Tone is MeshTone.Card or MeshTone.Composer ? DesktopTheme.Surface : DesktopTheme.Background;
        Invalidate(true);
    }
    protected override void OnSizeChanged(EventArgs e) { _field?.Dispose(); _field = null; base.OnSizeChanged(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Tu ve day du toan bo hinh chu nhat, ke ca 4 goc bo tron:
        // khong dua vao nen trong suot cua cha, tranh vet ve cu.
        using (var corner = new SolidBrush(Parent is { } parent && parent.BackColor != Color.Transparent ? parent.BackColor : DesktopTheme.Background)) e.Graphics.FillRectangle(corner, ClientRectangle);
        if (Width < 2 || Height < 2) return;
        if (_field is null || _renderedMode != DesktopTheme.Mode)
        {
            _field?.Dispose(); _field = CreateField(); _renderedMode = DesktopTheme.Mode;
        }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rounded = Tone is MeshTone.Card or MeshTone.Hero or MeshTone.Composer;
        using var outline = DesktopTheme.RoundedPath(new Rectangle(1, 1, Width - 3, Height - 3), (int)(26 * DeviceDpi / 96f));
        var saved = e.Graphics.Save();
        if (rounded) e.Graphics.SetClip(outline);
        e.Graphics.DrawImage(_field, ClientRectangle);
        e.Graphics.Restore(saved);
        if (rounded)
        {
            using var border = new Pen(FocusHighlight ? DesktopTheme.Accent : DesktopTheme.Border, FocusHighlight ? 2f : 1f);
            e.Graphics.DrawPath(border, outline);
        }
    }

    private Bitmap CreateField()
    {
        var ratio = Math.Min(1f, 1200f / Math.Max(Width, Height));
        var bitmap = new Bitmap(Math.Max(2, (int)(Width * ratio)), Math.Max(2, (int)(Height * ratio)));
        using var graphics = Graphics.FromImage(bitmap);
        var baseColor = Tone == MeshTone.Sidebar ? DesktopTheme.Sidebar : Tone is MeshTone.Card or MeshTone.Composer ? DesktopTheme.Surface : DesktopTheme.Background;
        graphics.Clear(baseColor);
        if (Tone == MeshTone.Hero)
        {
            var w = bitmap.Width; var h = bitmap.Height;
            var diameter = Math.Max(w, h) * 1.2f;
            using var shape = new GraphicsPath();
            shape.AddEllipse(w * 0.5f - diameter / 2, -diameter * 0.35f, diameter, diameter);
            using var glow = new PathGradientBrush(shape)
            {
                CenterColor = Color.FromArgb(DesktopTheme.Mode == DesktopTheme.ThemeMode.Dark ? 22 : 10, DesktopTheme.Accent),
                SurroundColors = new[] { Color.FromArgb(0, DesktopTheme.Accent) },
            };
            graphics.FillPath(glow, shape);
        }
        return bitmap;
    }

    protected override void Dispose(bool disposing) { if (disposing) _field?.Dispose(); base.Dispose(disposing); }
}

internal static class DesktopIcons
{
    public static void Draw(Graphics graphics, Rectangle bounds, WorkspaceIcon icon, Color color)
    {
        if (icon == WorkspaceIcon.None) return;
        var saved = graphics.Save(); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(bounds.X, bounds.Y); graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Line(float a, float b, float c, float d) => graphics.DrawLine(pen, a, b, c, d);
        void Lines(params PointF[] points) => graphics.DrawLines(pen, points);
        switch (icon)
        {
            case WorkspaceIcon.Plus: Line(12, 5, 12, 19); Line(5, 12, 19, 12); break;
            case WorkspaceIcon.Folder: Lines(new(3, 6), new(10, 6), new(12, 9), new(21, 9), new(21, 19), new(3, 19), new(3, 6)); break;
            case WorkspaceIcon.Files: Lines(new(6, 3), new(15, 3), new(20, 8), new(20, 21), new(6, 21), new(6, 3)); Lines(new(15, 3), new(15, 8), new(20, 8)); Line(9, 12, 16, 12); Line(9, 16, 16, 16); break;
            case WorkspaceIcon.Changes: graphics.DrawEllipse(pen, 3, 3, 5, 5); graphics.DrawEllipse(pen, 3, 16, 5, 5); graphics.DrawEllipse(pen, 16, 3, 5, 5); Line(5.5f, 8, 5.5f, 16); Lines(new(18.5f, 8), new(18.5f, 12), new(5.5f, 12)); break;
            case WorkspaceIcon.Terminal: graphics.DrawRectangle(pen, 3, 4, 18, 16); Lines(new(7, 9), new(10, 12), new(7, 15)); Line(13, 15, 17, 15); break;
            case WorkspaceIcon.Monitor: graphics.DrawRectangle(pen, 3, 4, 18, 13); Line(12, 17, 12, 21); Line(8, 21, 16, 21); break;
            case WorkspaceIcon.Settings: graphics.DrawEllipse(pen, 8, 8, 8, 8); for (var i = 0; i < 8; i++) { var a = i * Math.PI / 4; Line(12 + 7 * (float)Math.Cos(a), 12 + 7 * (float)Math.Sin(a), 12 + 10 * (float)Math.Cos(a), 12 + 10 * (float)Math.Sin(a)); } break;
            case WorkspaceIcon.Send: Line(12, 20, 12, 4); Lines(new(5, 11), new(12, 4), new(19, 11)); break;
            case WorkspaceIcon.Stop: graphics.DrawRectangle(pen, 6, 6, 12, 12); break;
            case WorkspaceIcon.Search: graphics.DrawEllipse(pen, 4, 3, 12, 12); Line(14, 14, 21, 21); break;
            case WorkspaceIcon.Code: Lines(new(8, 6), new(3, 12), new(8, 18)); Lines(new(16, 6), new(21, 12), new(16, 18)); Line(14, 4, 10, 20); break;
            case WorkspaceIcon.Check: Lines(new(5, 12), new(10, 17), new(20, 6)); break;
            case WorkspaceIcon.Close: Line(6, 6, 18, 18); Line(18, 6, 6, 18); break;
        }
        graphics.Restore(saved);
    }
}

/// <summary>Keyboard-accessible native pages with themed navigation.</summary>
internal sealed class WorkspaceTabs : UserControl
{
    private readonly TableLayoutPanel _navigation = new() { Dock = DockStyle.Top, Height = 44, RowCount = 1, Margin = Padding.Empty };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
    private readonly List<Control> _pages = new();
    private readonly List<CleanButton> _buttons = new();
    private readonly List<WorkspaceIcon> _icons = new();
    private int _selected = -1;

    public WorkspaceTabs()
    {
        Dock = DockStyle.Fill; Controls.Add(_content); Controls.Add(_navigation); RefreshPalette();
        _navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _navigation.SizeChanged += (_, _) =>
        {
            for (var i = 0; i < _buttons.Count; i++) { _buttons[i].Icon = _navigation.Width < 380 ? WorkspaceIcon.None : _icons[i]; _buttons[i].Invalidate(); }
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _pages.Count) throw new ArgumentOutOfRangeException(nameof(value));
            _selected = value;
            for (var i = 0; i < _pages.Count; i++) { _pages[i].Visible = i == value; _buttons[i].Active = i == value; _buttons[i].Invalidate(); }
            _pages[value].BringToFront();
        }
    }

    public void AddPage(string title, Control page, WorkspaceIcon icon)
    {
        var index = _pages.Count;
        var button = new CleanButton { Text = title, Icon = icon, Quiet = true, Dock = DockStyle.Fill, Margin = new Padding(2), AccessibleName = title };
        button.Click += (_, _) => SelectedIndex = index;
        _pages.Add(page); _buttons.Add(button); _icons.Add(icon); page.Dock = DockStyle.Fill; _content.Controls.Add(page);
        _navigation.ColumnCount = _pages.Count; _navigation.ColumnStyles.Clear();
        foreach (var _ in _pages) _navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / _pages.Count));
        _navigation.Controls.Add(button, index, 0);
        SelectedIndex = _selected < 0 ? 0 : _selected;
    }

    public void RefreshPalette() { BackColor = _content.BackColor = _navigation.BackColor = DesktopTheme.Background; foreach (var button in _buttons) button.Invalidate(); }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (_pages.Count > 0 && (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab)))
        { SelectedIndex = (_selected + (keyData.HasFlag(Keys.Shift) ? _pages.Count - 1 : 1)) % _pages.Count; return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }
}
