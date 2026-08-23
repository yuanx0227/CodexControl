using System.Drawing.Drawing2D;

namespace CodexControl.Agent.Desktop;

internal static class AgentPalette
{
    public static readonly Color Canvas = Color.FromArgb(246, 248, 252);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceMuted = Color.FromArgb(244, 247, 251);
    public static readonly Color Border = Color.FromArgb(226, 231, 240);
    public static readonly Color Text = Color.FromArgb(21, 29, 45);
    public static readonly Color TextMuted = Color.FromArgb(99, 112, 133);
    public static readonly Color Sidebar = Color.FromArgb(11, 18, 32);
    public static readonly Color SidebarElevated = Color.FromArgb(22, 31, 50);
    public static readonly Color SidebarText = Color.FromArgb(226, 232, 240);
    public static readonly Color SidebarMuted = Color.FromArgb(139, 153, 177);
    public static readonly Color Primary = Color.FromArgb(86, 114, 232);
    public static readonly Color PrimaryHover = Color.FromArgb(73, 99, 215);
    public static readonly Color PrimaryPressed = Color.FromArgb(60, 84, 192);
    public static readonly Color Accent = Color.FromArgb(47, 196, 172);
    public static readonly Color Success = Color.FromArgb(27, 151, 111);
    public static readonly Color SuccessSoft = Color.FromArgb(227, 247, 240);
    public static readonly Color Warning = Color.FromArgb(190, 126, 18);
    public static readonly Color WarningSoft = Color.FromArgb(255, 246, 220);
    public static readonly Color Danger = Color.FromArgb(198, 60, 67);
    public static readonly Color DangerSoft = Color.FromArgb(255, 236, 238);
    public static readonly Color Info = Color.FromArgb(74, 103, 205);
    public static readonly Color InfoSoft = Color.FromArgb(235, 239, 255);

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2F, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 1F)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static float Scale(System.Windows.Forms.Control control, float logicalPixels) =>
        logicalPixels * control.DeviceDpi / 96F;

    public static int ScaleInt(System.Windows.Forms.Control control, float logicalPixels) =>
        (int)Math.Round(Scale(control, logicalPixels));

    public static Color ResolveBackground(System.Windows.Forms.Control control, Color fallback)
    {
        for (var current = control.Parent; current is not null; current = current.Parent)
        {
            if (current.BackColor.A == byte.MaxValue)
            {
                return current.BackColor;
            }
        }

        return fallback;
    }
}

internal sealed class RoundedPanel : Panel
{
    private int _cornerRadius = 16;
    private Color _borderColor = AgentPalette.Border;
    private float _borderThickness = 1F;

    public RoundedPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        BackColor = AgentPalette.Surface;
    }

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(0, value);
            UpdateRoundedRegion();
            Invalidate();
        }
    }

    public Color BorderColor
    {
        get => _borderColor;
        set
        {
            _borderColor = value;
            Invalidate();
        }
    }

    public float BorderThickness
    {
        get => _borderThickness;
        set
        {
            _borderThickness = Math.Max(0F, value);
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        UpdateRoundedRegion();
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        UpdateRoundedRegion();
        Invalidate(true);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (_borderThickness <= 0F || Width <= 1 || Height <= 1)
        {
            return;
        }

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var borderThickness = AgentPalette.Scale(this, _borderThickness);
        var inset = borderThickness / 2F;
        using var path = AgentPalette.RoundedRectangle(
            new RectangleF(inset, inset, Width - borderThickness, Height - borderThickness),
            AgentPalette.Scale(this, _cornerRadius));
        using var pen = new Pen(_borderColor, borderThickness);
        eventArgs.Graphics.DrawPath(pen, path);
    }

    private void UpdateRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = AgentPalette.RoundedRectangle(
            new RectangleF(0, 0, Width, Height),
            AgentPalette.Scale(this, _cornerRadius));
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }
}

internal enum ModernButtonStyle
{
    Primary,
    Secondary,
    Ghost,
    Danger,
}

internal sealed class ModernButton : Button
{
    private bool _hovered;
    private bool _pressed;
    private ModernButtonStyle _visualStyle;

    public ModernButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.BorderColor = AgentPalette.Surface;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 42;
        Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular);
        TabStop = true;
    }

    public ModernButtonStyle VisualStyle
    {
        get => _visualStyle;
        set
        {
            _visualStyle = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(eventArgs);
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(AgentPalette.ResolveBackground(this, AgentPalette.Surface));
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var (fill, text, border) = ResolveColors();
        var edge = AgentPalette.Scale(this, 1F);
        using var path = AgentPalette.RoundedRectangle(
            new RectangleF(edge / 2F, edge / 2F, Width - edge, Height - edge),
            AgentPalette.Scale(this, 10F));
        using var brush = new SolidBrush(fill);
        eventArgs.Graphics.FillPath(brush, path);
        if (border != Color.Transparent)
        {
            using var pen = new Pen(border, edge);
            eventArgs.Graphics.DrawPath(pen, path);
        }

        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            ClientRectangle,
            text,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            var focusInset = AgentPalette.ScaleInt(this, 4F);
            var focusBounds = Rectangle.Inflate(ClientRectangle, -focusInset, -focusInset);
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, focusBounds, text, fill);
        }
    }

    private (Color Fill, Color Text, Color Border) ResolveColors()
    {
        if (!Enabled)
        {
            return (Color.FromArgb(235, 238, 244), Color.FromArgb(150, 159, 174), Color.Transparent);
        }

        return _visualStyle switch
        {
            ModernButtonStyle.Primary => (
                _pressed ? AgentPalette.PrimaryPressed : _hovered ? AgentPalette.PrimaryHover : AgentPalette.Primary,
                Color.White,
                Color.Transparent),
            ModernButtonStyle.Danger => (
                _pressed ? Color.FromArgb(236, 211, 214) : _hovered ? Color.FromArgb(249, 224, 226) : Color.White,
                AgentPalette.Danger,
                Color.FromArgb(237, 185, 190)),
            ModernButtonStyle.Ghost => (
                _pressed ? Color.FromArgb(229, 233, 240) : _hovered ? AgentPalette.SurfaceMuted : Color.Transparent,
                AgentPalette.TextMuted,
                Color.Transparent),
            _ => (
                _pressed ? Color.FromArgb(230, 234, 241) : _hovered ? AgentPalette.SurfaceMuted : Color.White,
                AgentPalette.Text,
                AgentPalette.Border),
        };
    }
}

internal enum NavigationGlyph
{
    Overview,
    Pairing,
    General,
    Diagnostics,
}

internal sealed class NavigationButton : Button
{
    private bool _selected;
    private bool _hovered;

    public NavigationButton(NavigationGlyph glyph)
    {
        Glyph = glyph;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.BorderColor = AgentPalette.Sidebar;
        UseVisualStyleBackColor = false;
        BackColor = AgentPalette.Sidebar;
        Cursor = Cursors.Hand;
        Height = 48;
        Width = 202;
        Font = new Font("Segoe UI Semibold", 10F, FontStyle.Regular);
        TextAlign = ContentAlignment.MiddleLeft;
    }

    public NavigationGlyph Glyph { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(AgentPalette.Sidebar);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = AgentPalette.Scale(this, 1F);
        var selectedFill = Color.FromArgb(30, 42, 66);
        var fill = _selected ? selectedFill : _hovered ? AgentPalette.SidebarElevated : AgentPalette.Sidebar;
        using var path = AgentPalette.RoundedRectangle(
            new RectangleF(0, scale, Width - scale, Height - (2F * scale)),
            10F * scale);
        using var fillBrush = new SolidBrush(fill);
        eventArgs.Graphics.FillPath(fillBrush, path);

        if (_selected)
        {
            using var indicator = AgentPalette.RoundedRectangle(
                new RectangleF(0, 11F * scale, 3F * scale, 26F * scale),
                1.5F * scale);
            using var indicatorBrush = new SolidBrush(AgentPalette.Accent);
            eventArgs.Graphics.FillPath(indicatorBrush, indicator);
        }

        var foreground = _selected ? Color.White : AgentPalette.SidebarMuted;
        DrawGlyph(
            eventArgs.Graphics,
            new Rectangle(
                AgentPalette.ScaleInt(this, 18F),
                AgentPalette.ScaleInt(this, 15F),
                AgentPalette.ScaleInt(this, 18F),
                AgentPalette.ScaleInt(this, 18F)),
            foreground,
            scale);
        var textLeft = AgentPalette.ScaleInt(this, 49F);
        var textRight = AgentPalette.ScaleInt(this, 10F);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - textRight), Height),
            _selected ? Color.White : AgentPalette.SidebarText,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine);

        if (Focused && ShowFocusCues)
        {
            var focusInset = AgentPalette.ScaleInt(this, 4F);
            ControlPaint.DrawFocusRectangle(
                eventArgs.Graphics,
                Rectangle.Inflate(ClientRectangle, -focusInset, -focusInset));
        }
    }

    private void DrawGlyph(Graphics graphics, Rectangle bounds, Color color, float scale)
    {
        int Pixel(float logicalPixels) => (int)Math.Round(logicalPixels * scale);

        using var pen = new Pen(color, 1.8F * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var brush = new SolidBrush(color);

        switch (Glyph)
        {
            case NavigationGlyph.Overview:
                graphics.FillRectangle(brush, bounds.X, bounds.Y, Pixel(6), Pixel(6));
                graphics.FillRectangle(brush, bounds.X + Pixel(11), bounds.Y, Pixel(6), Pixel(6));
                graphics.FillRectangle(brush, bounds.X, bounds.Y + Pixel(11), Pixel(6), Pixel(6));
                graphics.FillRectangle(
                    brush,
                    bounds.X + Pixel(11),
                    bounds.Y + Pixel(11),
                    Pixel(6),
                    Pixel(6));
                break;
            case NavigationGlyph.Pairing:
                graphics.DrawEllipse(pen, bounds.X, bounds.Y + Pixel(5), Pixel(8), Pixel(8));
                graphics.DrawEllipse(
                    pen,
                    bounds.X + Pixel(10),
                    bounds.Y + Pixel(5),
                    Pixel(8),
                    Pixel(8));
                graphics.DrawLine(
                    pen,
                    bounds.X + Pixel(6),
                    bounds.Y + Pixel(9),
                    bounds.X + Pixel(12),
                    bounds.Y + Pixel(9));
                break;
            case NavigationGlyph.General:
                graphics.DrawLine(pen, bounds.X, bounds.Y + Pixel(4), bounds.Right, bounds.Y + Pixel(4));
                graphics.DrawLine(pen, bounds.X, bounds.Y + Pixel(9), bounds.Right, bounds.Y + Pixel(9));
                graphics.DrawLine(pen, bounds.X, bounds.Y + Pixel(14), bounds.Right, bounds.Y + Pixel(14));
                graphics.FillEllipse(brush, bounds.X + Pixel(4), bounds.Y + Pixel(1), Pixel(6), Pixel(6));
                graphics.FillEllipse(brush, bounds.X + Pixel(11), bounds.Y + Pixel(6), Pixel(6), Pixel(6));
                graphics.FillEllipse(brush, bounds.X + Pixel(2), bounds.Y + Pixel(11), Pixel(6), Pixel(6));
                break;
            case NavigationGlyph.Diagnostics:
                graphics.DrawLines(
                    pen,
                    [
                        new Point(bounds.X, bounds.Y + Pixel(10)),
                        new Point(bounds.X + Pixel(4), bounds.Y + Pixel(10)),
                        new Point(bounds.X + Pixel(7), bounds.Y + Pixel(3)),
                        new Point(bounds.X + Pixel(11), bounds.Y + Pixel(15)),
                        new Point(bounds.X + Pixel(14), bounds.Y + Pixel(8)),
                        new Point(bounds.Right, bounds.Y + Pixel(8)),
                    ]);
                break;
        }
    }
}

internal enum StatusTone
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
}

internal sealed class StatusBadge : System.Windows.Forms.Control
{
    private StatusTone _tone;

    public StatusBadge()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Height = 30;
        Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Regular);
        SetStatus("等待状态", StatusTone.Neutral);
    }

    public StatusTone Tone
    {
        get => _tone;
        private set => _tone = value;
    }

    public void SetStatus(string text, StatusTone tone)
    {
        Text = text;
        Tone = tone;
        Width = Math.Max(
            AgentPalette.ScaleInt(this, 88F),
            TextRenderer.MeasureText(text, Font).Width + AgentPalette.ScaleInt(this, 38F));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(AgentPalette.ResolveBackground(this, AgentPalette.Surface));
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = AgentPalette.Scale(this, 1F);
        var (foreground, background) = ResolveColors(Tone);
        using var path = AgentPalette.RoundedRectangle(
            new RectangleF(0, 0, Width - 1, Height - 1),
            Height / 2F);
        using var backgroundBrush = new SolidBrush(background);
        eventArgs.Graphics.FillPath(backgroundBrush, path);
        using var dotBrush = new SolidBrush(foreground);
        var dotSize = 7F * scale;
        eventArgs.Graphics.FillEllipse(
            dotBrush,
            11F * scale,
            (Height - dotSize) / 2F,
            dotSize,
            dotSize);
        var textLeft = AgentPalette.ScaleInt(this, 25F);
        var textRight = AgentPalette.ScaleInt(this, 6F);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - textRight), Height),
            foreground,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine);
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        SetStatus(Text, Tone);
    }

    private static (Color Foreground, Color Background) ResolveColors(StatusTone tone) => tone switch
    {
        StatusTone.Success => (AgentPalette.Success, AgentPalette.SuccessSoft),
        StatusTone.Warning => (AgentPalette.Warning, AgentPalette.WarningSoft),
        StatusTone.Danger => (AgentPalette.Danger, AgentPalette.DangerSoft),
        StatusTone.Info => (AgentPalette.Info, AgentPalette.InfoSoft),
        _ => (AgentPalette.TextMuted, Color.FromArgb(235, 239, 245)),
    };
}

internal sealed class StatusEmblem : System.Windows.Forms.Control
{
    private StatusTone _tone;

    public StatusEmblem()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Size = new Size(72, 72);
    }

    public StatusTone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(AgentPalette.ResolveBackground(this, AgentPalette.Surface));
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = AgentPalette.Scale(this, 1F);
        var (foreground, background) = Tone switch
        {
            StatusTone.Success => (AgentPalette.Success, AgentPalette.SuccessSoft),
            StatusTone.Warning => (AgentPalette.Warning, AgentPalette.WarningSoft),
            StatusTone.Danger => (AgentPalette.Danger, AgentPalette.DangerSoft),
            StatusTone.Info => (AgentPalette.Info, AgentPalette.InfoSoft),
            _ => (AgentPalette.TextMuted, Color.FromArgb(235, 239, 245)),
        };
        using var backgroundBrush = new SolidBrush(background);
        eventArgs.Graphics.FillEllipse(backgroundBrush, 0, 0, Width - scale, Height - scale);
        using var foregroundPen = new Pen(foreground, 4F * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        if (Tone == StatusTone.Success)
        {
            eventArgs.Graphics.DrawLines(
                foregroundPen,
                [
                    new Point(AgentPalette.ScaleInt(this, 22F), AgentPalette.ScaleInt(this, 37F)),
                    new Point(AgentPalette.ScaleInt(this, 31F), AgentPalette.ScaleInt(this, 46F)),
                    new Point(AgentPalette.ScaleInt(this, 51F), AgentPalette.ScaleInt(this, 26F)),
                ]);
        }
        else if (Tone == StatusTone.Danger)
        {
            eventArgs.Graphics.DrawLine(
                foregroundPen,
                26F * scale,
                26F * scale,
                46F * scale,
                46F * scale);
            eventArgs.Graphics.DrawLine(
                foregroundPen,
                46F * scale,
                26F * scale,
                26F * scale,
                46F * scale);
        }
        else if (Tone == StatusTone.Warning)
        {
            eventArgs.Graphics.DrawLine(
                foregroundPen,
                36F * scale,
                22F * scale,
                36F * scale,
                40F * scale);
            using var dotBrush = new SolidBrush(foreground);
            eventArgs.Graphics.FillEllipse(
                dotBrush,
                33.5F * scale,
                48F * scale,
                5F * scale,
                5F * scale);
        }
        else
        {
            using var dotBrush = new SolidBrush(foreground);
            eventArgs.Graphics.FillEllipse(
                dotBrush,
                28F * scale,
                28F * scale,
                16F * scale,
                16F * scale);
        }
    }
}

internal sealed class ToggleSwitch : CheckBox
{
    private string _description = string.Empty;

    public ToggleSwitch()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Opaque,
            true);
        BackColor = AgentPalette.Surface;
        AutoSize = false;
        Height = 52;
        Font = new Font("Segoe UI Semibold", 10F, FontStyle.Regular);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public string Description
    {
        get => _description;
        set
        {
            _description = value;
            Invalidate();
        }
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(BackColor);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = AgentPalette.Scale(this, 1F);
        var titleColor = Enabled ? AgentPalette.Text : Color.FromArgb(151, 159, 173);
        var descriptionColor = Enabled ? AgentPalette.TextMuted : Color.FromArgb(174, 181, 192);
        var textRight = AgentPalette.ScaleInt(this, 76F);
        var hasDescription = !string.IsNullOrWhiteSpace(Description);
        var titleBounds = hasDescription
            ? new Rectangle(
                0,
                AgentPalette.ScaleInt(this, 5F),
                Math.Max(0, Width - textRight),
                AgentPalette.ScaleInt(this, 23F))
            : new Rectangle(0, 0, Math.Max(0, Width - textRight), Height);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            Text,
            Font,
            titleBounds,
            titleColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (hasDescription)
        {
            using var descriptionFont = new Font("Segoe UI", 8.5F, FontStyle.Regular);
            TextRenderer.DrawText(
                eventArgs.Graphics,
                Description,
                descriptionFont,
                new Rectangle(
                    0,
                    AgentPalette.ScaleInt(this, 29F),
                    Math.Max(0, Width - textRight),
                    AgentPalette.ScaleInt(this, 22F)),
                descriptionColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis);
        }

        var trackHeight = 26F * scale;
        var trackBounds = new RectangleF(
            Width - (54F * scale),
            (Height - trackHeight) / 2F,
            48F * scale,
            trackHeight);
        var trackColor = !Enabled
            ? Color.FromArgb(221, 225, 232)
            : Checked ? AgentPalette.Primary : Color.FromArgb(199, 206, 218);
        using var trackPath = AgentPalette.RoundedRectangle(trackBounds, 13F * scale);
        using var trackBrush = new SolidBrush(trackColor);
        eventArgs.Graphics.FillPath(trackBrush, trackPath);
        var thumbX = Checked ? trackBounds.Right - (23F * scale) : trackBounds.Left + (3F * scale);
        using var thumbBrush = new SolidBrush(Color.White);
        eventArgs.Graphics.FillEllipse(
            thumbBrush,
            thumbX,
            trackBounds.Y + (3F * scale),
            20F * scale,
            20F * scale);

        if (Focused && ShowFocusCues)
        {
            var focusInset = AgentPalette.ScaleInt(this, 1F);
            ControlPaint.DrawFocusRectangle(
                eventArgs.Graphics,
                Rectangle.Inflate(ClientRectangle, -focusInset, -focusInset));
        }
    }
}
