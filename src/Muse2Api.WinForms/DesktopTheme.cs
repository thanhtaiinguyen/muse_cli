using System.Drawing.Drawing2D;

namespace Muse2Api.WinForms;

internal static class DesktopTheme
{
    public enum ThemeMode { Dark, Light }

    public sealed record ThemePalette(Color Background, Color Sidebar, Color Surface, Color Border, Color Text, Color Muted, Color Accent, Color Success, Color UserBackground, Color UserBorder, Color Notice, Color Folder);

    public static readonly ThemePalette DarkPalette = new(
        Color.FromArgb(20, 20, 20), Color.FromArgb(26, 26, 26),
        Color.FromArgb(32, 32, 32), Color.FromArgb(46, 46, 46),
        Color.FromArgb(236, 236, 236), Color.FromArgb(142, 142, 147),
        Color.FromArgb(59, 130, 246), Color.FromArgb(16, 185, 129),
        Color.FromArgb(29, 78, 216), Color.FromArgb(37, 99, 235),
        Color.FromArgb(239, 68, 68), Color.FromArgb(96, 165, 250));

    public static readonly ThemePalette LightPalette = new(
        Color.FromArgb(248, 248, 250), Color.FromArgb(242, 242, 245),
        Color.White, Color.FromArgb(226, 226, 232),
        Color.FromArgb(28, 28, 30), Color.FromArgb(110, 110, 115),
        Color.FromArgb(37, 99, 235), Color.FromArgb(16, 185, 129),
        Color.FromArgb(235, 242, 255), Color.FromArgb(191, 219, 254),
        Color.FromArgb(220, 38, 38), Color.FromArgb(59, 130, 246));

    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    public static ThemePalette Current => Mode == ThemeMode.Light ? LightPalette : DarkPalette;

    public static Color Background => Current.Background;
    public static Color Sidebar => Current.Sidebar;
    public static Color Surface => Current.Surface;
    public static Color Border => Current.Border;
    public static Color Text => Current.Text;
    public static Color Muted => Current.Muted;
    public static Color Accent => Current.Accent;
    public static Color Success => Current.Success;
    public static Color UserBackground => Current.UserBackground;
    public static Color UserBorder => Current.UserBorder;
    public static Color Notice => Current.Notice;
    public static Color Folder => Current.Folder;
    public static Color Primary => Color.FromArgb(37, 99, 235);
    public static Color PrimaryEnd => Color.FromArgb(29, 78, 216);
    public static Color SurfaceStrong => Mode == ThemeMode.Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(230, 230, 235);

    public static event EventHandler? ThemeChanged;

    public static void SetMode(ThemeMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public const int CornerDiameter = 22;

    public static GraphicsPath RoundedPath(Rectangle rectangle, int diameter)
    {
        var d = Math.Max(2, Math.Min(diameter, Math.Min(rectangle.Width, rectangle.Height)));
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, d, d, 180, 90);
        path.AddArc(rectangle.Right - d, rectangle.Y, d, d, 270, 90);
        path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Strong = new("Segoe UI", 10f, FontStyle.Bold);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Code = new("Consolas", 10f);

    public static Label Label(string text, float size = 10, Color? color = null, bool bold = false)
        => new ThemedLabel { Text = text, ForeColor = color ?? Text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };

    public static TableLayoutPanel Column(params RowStyle[] rows)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows) panel.RowStyles.Add(row);
        return panel;
    }

    public static void Apply(Control control)
    {
        // Message cards own role colors and code formatting. Generic traversal
        // must not replace their surfaces after a palette change.
        if (control is ChatTranscript transcript) { transcript.RefreshTheme(); return; }
        if (control is System.Windows.Forms.Label label)
        {
            label.ForeColor = label.ForeColor == DarkPalette.Muted || label.ForeColor == LightPalette.Muted ? Muted : label.ForeColor == DarkPalette.Success || label.ForeColor == LightPalette.Success ? Success : label.ForeColor == DarkPalette.Accent || label.ForeColor == LightPalette.Accent ? Accent : label.ForeColor == DarkPalette.Notice || label.ForeColor == LightPalette.Notice ? Notice : Text;
            label.Invalidate();
        }
        else if (control is MeshLayout mesh) mesh.RefreshPalette();
        else if (control is WorkspaceTabs tabs) tabs.RefreshPalette();
        else if (control is not CleanButton)
        {
            if (control.BackColor == DarkPalette.Sidebar || control.BackColor == LightPalette.Sidebar || control is ListBox { DrawMode: DrawMode.OwnerDrawFixed })
                control.BackColor = Sidebar;
            else if (control.BackColor == DarkPalette.Surface || control.BackColor == LightPalette.Surface || control is TextBoxBase)
                control.BackColor = Surface;
            else if (control.BackColor == Color.Transparent)
                control.BackColor = Color.Transparent;
            else
                control.BackColor = Background;

            control.ForeColor = Text;
        }
        if (control is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
        if (control is CleanButton button) button.Invalidate();
        foreach (Control child in control.Controls) Apply(child);
    }
}

internal sealed class ThemedLabel : Label
{
    public ThemedLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bg = GetEffectiveBackColor();
        using (var brush = new SolidBrush(bg))
        {
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
            | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
            ContentAlignment.TopCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Top,
            ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom,
            ContentAlignment.MiddleRight => TextFormatFlags.Right | TextFormatFlags.VerticalCenter,
            _ => TextFormatFlags.Left | TextFormatFlags.VerticalCenter,
        };
        if (AutoEllipsis) flags |= TextFormatFlags.WordBreak;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
    }

    private Color GetEffectiveBackColor()
    {
        for (Control? c = Parent; c != null; c = c.Parent)
        {
            if (c is MeshLayout mesh)
            {
                return mesh.Tone == MeshTone.Sidebar ? DesktopTheme.Sidebar
                    : mesh.Tone is MeshTone.Card or MeshTone.Composer ? DesktopTheme.Surface
                    : DesktopTheme.Background;
            }
            if (c.BackColor != Color.Transparent && c.BackColor.A > 0)
                return c.BackColor;
        }
        return DesktopTheme.Background;
    }
}

internal sealed class CleanButton : Control, IButtonControl
{
    public bool Primary { get; set; }
    public bool Quiet { get; set; }
    public bool Active { get; set; }
    public WorkspaceIcon Icon { get; set; }
    public string? Description { get; set; }
    public DialogResult DialogResult { get; set; } = DialogResult.None;
    public AutoSizeMode AutoSizeMode { get; set; }
    private bool _hover, _pressed, _isDefault;

    public CleanButton()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.StandardClick, true);
        Font = DesktopTheme.Body;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        Size = new Size(140, 38);
        MinimumSize = new Size(32, 32);
        Margin = new Padding(3);
        Padding = new Padding(10, 0, 10, 0);
    }

    public void NotifyDefault(bool value)
    {
        if (_isDefault != value)
        {
            _isDefault = value;
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var scale = DeviceDpi / 96f;
        var text = TextRenderer.MeasureText(Text, Description is null ? Font : DesktopTheme.Strong);
        var iconWidth = Icon == WorkspaceIcon.None ? 0 : (int)(26 * scale);
        return new Size(Math.Max(MinimumSize.Width, text.Width + iconWidth + (int)(32 * scale)),
            Math.Max(MinimumSize.Height, Description is null ? text.Height + (int)(16 * scale) : (int)(76 * scale)));
    }

    public void PerformClick()
    {
        if (CanSelect) OnClick(EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        if (DialogResult != DialogResult.None && FindForm() is { } form)
            form.DialogResult = DialogResult;
        base.OnClick(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var containerBg = GetEffectiveBackColor();
        using (var bgBrush = new SolidBrush(containerBg))
        {
            e.Graphics.FillRectangle(bgBrush, ClientRectangle);
        }

        var scale = DeviceDpi / 96f;
        var rectangle = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        using var path = DesktopTheme.RoundedPath(rectangle, (int)(Primary ? 24 * scale : 18 * scale));

        var fill = Active || _pressed ? DesktopTheme.SurfaceStrong : _hover ? DesktopTheme.SurfaceStrong : DesktopTheme.Surface;
        if (Primary && Enabled)
        {
            using var gradient = new LinearGradientBrush(rectangle, _hover ? ControlPaint.Light(DesktopTheme.Primary, .08f) : DesktopTheme.Primary, DesktopTheme.PrimaryEnd, 18f);
            e.Graphics.FillPath(gradient, path);
        }
        else if (!Quiet || Active || _hover || _pressed)
        {
            using var brush = new SolidBrush(fill);
            e.Graphics.FillPath(brush, path);
        }

        if ((!Quiet || Active) && !Primary)
        {
            using var border = new Pen(Active ? DesktopTheme.Accent : DesktopTheme.Border);
            e.Graphics.DrawPath(border, path);
        }

        var textColor = !Enabled ? DesktopTheme.Muted : Primary ? Color.White : Active ? DesktopTheme.Accent : DesktopTheme.Text;
        const TextFormatFlags graphicsFlags = TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        var hasDescription = !string.IsNullOrEmpty(Description);
        var iconSize = (int)(18 * scale);
        var gap = (int)(8 * scale);
        var textRect = Rectangle.Inflate(rectangle, -(int)(12 * scale), 0);

        if (Icon != WorkspaceIcon.None)
        {
            var textWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var iconX = hasDescription ? textRect.Left + 4 : Math.Max(textRect.Left, (Width - textWidth - iconSize - gap) / 2);
            DesktopIcons.Draw(e.Graphics, new Rectangle(iconX, hasDescription ? (int)(17 * scale) : (Height - iconSize) / 2, iconSize, iconSize), Icon, textColor);
            textRect.X = iconX + iconSize + gap;
            textRect.Width = Math.Max(1, Width - textRect.X - (int)(12 * scale));
        }

        if (hasDescription)
        {
            var titleRect = new Rectangle(textRect.X, (int)(10 * scale), textRect.Width, (int)(24 * scale));
            TextRenderer.DrawText(e.Graphics, Text, DesktopTheme.Strong, titleRect, textColor, graphicsFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, Description, DesktopTheme.Small, new Rectangle(textRect.X, titleRect.Bottom + 1, textRect.Width, Math.Max(1, Height - titleRect.Bottom - 8)), DesktopTheme.Muted, graphicsFlags | TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, textColor, graphicsFlags | (Icon == WorkspaceIcon.None ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(DesktopTheme.Accent, 2);
            e.Graphics.DrawPath(ring, path);
        }
    }

    private Color GetEffectiveBackColor()
    {
        for (Control? c = Parent; c != null; c = c.Parent)
        {
            if (c is MeshLayout mesh)
            {
                return mesh.Tone == MeshTone.Sidebar ? DesktopTheme.Sidebar
                    : mesh.Tone is MeshTone.Card or MeshTone.Composer ? DesktopTheme.Surface
                    : DesktopTheme.Background;
            }
            if (c.BackColor != Color.Transparent && c.BackColor.A > 0)
                return c.BackColor;
        }
        return DesktopTheme.Background;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Focus();
            Invalidate();
        }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_pressed && e.KeyCode is Keys.Space or Keys.Enter)
        {
            _pressed = false;
            Invalidate();
            PerformClick();
        }
        base.OnKeyUp(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { _pressed = false; base.OnLostFocus(e); Invalidate(); }
}
