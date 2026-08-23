using System.Drawing.Drawing2D;
using System.Diagnostics.CodeAnalysis;

namespace CodexControl.Agent.Desktop;

internal sealed class ModernTextBox : UserControl
{
    private readonly TextBox _editor = new();
    private bool _focused;

    public ModernTextBox()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular);
        Height = 42;
        Padding = new Padding(12, 9, 12, 8);
        AccessibleRole = AccessibleRole.Text;

        _editor.Dock = DockStyle.Fill;
        _editor.BorderStyle = BorderStyle.None;
        _editor.BackColor = AgentPalette.Surface;
        _editor.ForeColor = AgentPalette.Text;
        _editor.Font = Font;
        _editor.Enter += (_, _) =>
        {
            _focused = true;
            Invalidate();
        };
        _editor.Leave += (_, _) =>
        {
            _focused = false;
            Invalidate();
        };
        _editor.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        Controls.Add(_editor);
    }

    [AllowNull]
    public override string Text
    {
        get => _editor.Text;
        set
        {
            if (!string.Equals(_editor.Text, value, StringComparison.Ordinal))
            {
                _editor.Text = value ?? string.Empty;
            }
        }
    }

    public string PlaceholderText
    {
        get => _editor.PlaceholderText;
        set => _editor.PlaceholderText = value;
    }

    protected override void OnEnter(EventArgs eventArgs)
    {
        base.OnEnter(eventArgs);
        _editor.Focus();
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        base.OnEnabledChanged(eventArgs);
        _editor.Enabled = Enabled;
        UpdateEditorColors();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs eventArgs)
    {
        base.OnFontChanged(eventArgs);
        _editor.Font = Font;
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(AgentPalette.ResolveBackground(this, AgentPalette.Surface));
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        PaintFrame(eventArgs.Graphics, _focused, Enabled);
    }

    private void UpdateEditorColors()
    {
        _editor.BackColor = Enabled ? AgentPalette.Surface : AgentPalette.SurfaceMuted;
        _editor.ForeColor = Enabled ? AgentPalette.Text : Color.FromArgb(145, 154, 169);
    }

    private void PaintFrame(Graphics graphics, bool focused, bool enabled)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var edge = AgentPalette.Scale(this, 1F);
        using var path = AgentPalette.RoundedRectangle(
            new RectangleF(edge / 2F, edge / 2F, Width - edge, Height - edge),
            AgentPalette.Scale(this, 9F));
        using var fill = new SolidBrush(enabled ? AgentPalette.Surface : AgentPalette.SurfaceMuted);
        graphics.FillPath(fill, path);
        using var border = new Pen(focused ? AgentPalette.Primary : AgentPalette.Border, edge);
        graphics.DrawPath(border, path);
    }
}

internal sealed class ModernNumericInput : UserControl
{
    private readonly NumericUpDown _editor = new();
    private bool _focused;

    public ModernNumericInput()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular);
        Height = 42;
        Padding = new Padding(10, 8, 8, 7);

        _editor.Dock = DockStyle.Fill;
        _editor.BorderStyle = BorderStyle.None;
        _editor.BackColor = AgentPalette.Surface;
        _editor.ForeColor = AgentPalette.Text;
        _editor.Font = Font;
        _editor.Enter += (_, _) =>
        {
            _focused = true;
            Invalidate();
        };
        _editor.Leave += (_, _) =>
        {
            _focused = false;
            Invalidate();
        };
        Controls.Add(_editor);
    }

    public decimal Minimum
    {
        get => _editor.Minimum;
        set => _editor.Minimum = value;
    }

    public decimal Maximum
    {
        get => _editor.Maximum;
        set => _editor.Maximum = value;
    }

    public decimal Value
    {
        get => _editor.Value;
        set => _editor.Value = Math.Clamp(value, _editor.Minimum, _editor.Maximum);
    }

    protected override void OnEnter(EventArgs eventArgs)
    {
        base.OnEnter(eventArgs);
        _editor.Focus();
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        base.OnEnabledChanged(eventArgs);
        _editor.Enabled = Enabled;
        _editor.BackColor = Enabled ? AgentPalette.Surface : AgentPalette.SurfaceMuted;
        _editor.ForeColor = Enabled ? AgentPalette.Text : Color.FromArgb(145, 154, 169);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs eventArgs)
    {
        base.OnFontChanged(eventArgs);
        _editor.Font = Font;
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(AgentPalette.ResolveBackground(this, AgentPalette.Surface));
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var edge = AgentPalette.Scale(this, 1F);
        using var path = AgentPalette.RoundedRectangle(
            new RectangleF(edge / 2F, edge / 2F, Width - edge, Height - edge),
            AgentPalette.Scale(this, 9F));
        using var fill = new SolidBrush(Enabled ? AgentPalette.Surface : AgentPalette.SurfaceMuted);
        eventArgs.Graphics.FillPath(fill, path);
        using var border = new Pen(_focused ? AgentPalette.Primary : AgentPalette.Border, edge);
        eventArgs.Graphics.DrawPath(border, path);
    }
}

internal sealed class CountdownProgressBar : System.Windows.Forms.Control
{
    private double _value;

    public CountdownProgressBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Height = 8;
        Width = 360;
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0D, 1D);
            Invalidate();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(AgentPalette.ResolveBackground(this, AgentPalette.Surface));
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var radius = Height / 2F;
        using var trackPath = AgentPalette.RoundedRectangle(new RectangleF(0, 0, Width, Height), radius);
        using var trackBrush = new SolidBrush(AgentPalette.SurfaceMuted);
        eventArgs.Graphics.FillPath(trackBrush, trackPath);
        var fillWidth = (float)(Width * Value);
        if (fillWidth <= 0F)
        {
            return;
        }

        using var fillPath = AgentPalette.RoundedRectangle(
            new RectangleF(0, 0, Math.Max(Height, fillWidth), Height),
            radius);
        using var fillBrush = new SolidBrush(Value <= 0.15D ? AgentPalette.Warning : AgentPalette.Accent);
        eventArgs.Graphics.FillPath(fillBrush, fillPath);
    }
}
