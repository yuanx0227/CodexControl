using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexControl.Agent.Desktop;

internal sealed class ModernScrollHost : UserControl
{
    private const int SbHorz = 0;
    private const int SbVert = 1;
    private readonly Panel _viewport = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        BackColor = AgentPalette.Canvas,
    };
    private readonly SlimScrollBar _scrollBar = new()
    {
        Dock = DockStyle.Right,
        Width = 10,
        BackColor = AgentPalette.Canvas,
        Visible = false,
    };
    private bool _synchronizing;
    private int _contentHeight;

    public ModernScrollHost()
    {
        BackColor = AgentPalette.Canvas;
        Controls.Add(_viewport);
        Controls.Add(_scrollBar);
        _scrollBar.BringToFront();
        _scrollBar.ValueChanged += (_, _) => ScrollViewportTo(_scrollBar.Value);
        _viewport.Scroll += (_, _) => SynchronizeFromViewport();
        _viewport.Layout += (_, _) => QueueMetricUpdate();
        _viewport.ControlAdded += (_, eventArgs) =>
        {
            if (eventArgs.Control is not null)
            {
                eventArgs.Control.SizeChanged += (_, _) => QueueMetricUpdate();
            }

            QueueMetricUpdate();
        };
        SizeChanged += (_, _) => QueueMetricUpdate();
    }

    public Panel Content => _viewport;

    public Padding ContentPadding
    {
        get => _viewport.Padding;
        set => _viewport.Padding = value;
    }

    public void RefreshScrollMetrics() => QueueMetricUpdate();

    internal bool CustomScrollBarVisible => _scrollBar.Visible;

    internal int ScrollMaximum => _scrollBar.Maximum;

    internal int ContentHeight => _contentHeight;

    internal int ViewportHeight => _viewport.ClientSize.Height;

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        QueueMetricUpdate();
    }

    private void QueueMetricUpdate()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(UpdateScrollMetrics);
    }

    private void UpdateScrollMetrics()
    {
        if (IsDisposed || !_viewport.IsHandleCreated)
        {
            return;
        }

        HideNativeScrollBars();
        var contentHeight = _viewport.Padding.Vertical;
        foreach (System.Windows.Forms.Control child in _viewport.Controls)
        {
            contentHeight = Math.Max(
                contentHeight,
                _viewport.Padding.Vertical + child.Height + child.Margin.Vertical);
        }

        var minimumSize = new Size(0, contentHeight);
        if (_viewport.AutoScrollMinSize != minimumSize)
        {
            _viewport.AutoScrollMinSize = minimumSize;
        }
        _contentHeight = contentHeight;
        var maximum = Math.Max(0, contentHeight - _viewport.ClientSize.Height);
        var current = Math.Clamp(-_viewport.AutoScrollPosition.Y, 0, maximum);
        _scrollBar.SetRange(maximum, _viewport.ClientSize.Height, current);
        _scrollBar.Visible = maximum > 0;
        HideNativeScrollBars();
    }

    private void SynchronizeFromViewport()
    {
        if (_synchronizing)
        {
            return;
        }

        _scrollBar.Value = Math.Clamp(-_viewport.AutoScrollPosition.Y, 0, _scrollBar.Maximum);
        HideNativeScrollBars();
    }

    private void ScrollViewportTo(int value)
    {
        if (_synchronizing)
        {
            return;
        }

        _synchronizing = true;
        try
        {
            _viewport.AutoScrollPosition = new Point(0, value);
            HideNativeScrollBars();
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void HideNativeScrollBars()
    {
        if (!_viewport.IsHandleCreated)
        {
            return;
        }

        _ = ShowScrollBar(_viewport.Handle, SbVert, false);
        _ = ShowScrollBar(_viewport.Handle, SbHorz, false);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowScrollBar(IntPtr windowHandle, int bar, bool show);
}

internal sealed class SlimScrollBar : System.Windows.Forms.Control
{
    private int _maximum;
    private int _viewportSize = 1;
    private int _value;
    private bool _dragging;
    private float _dragOffset;
    private bool _hovered;

    public SlimScrollBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public event EventHandler? ValueChanged;

    public int Maximum => _maximum;

    public int Value
    {
        get => _value;
        set
        {
            var normalized = Math.Clamp(value, 0, _maximum);
            if (_value == normalized)
            {
                return;
            }

            _value = normalized;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetRange(int maximum, int viewportSize, int value)
    {
        _maximum = Math.Max(0, maximum);
        _viewportSize = Math.Max(1, viewportSize);
        _value = Math.Clamp(value, 0, _maximum);
        Invalidate();
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
        if (!_dragging)
        {
            Invalidate();
        }

        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            base.OnMouseDown(eventArgs);
            return;
        }

        var thumb = ThumbBounds();
        if (thumb.Contains(eventArgs.Location))
        {
            _dragging = true;
            _dragOffset = eventArgs.Y - thumb.Y;
            Capture = true;
        }
        else
        {
            SetValueFromThumbTop(eventArgs.Y - thumb.Height / 2);
        }

        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        if (_dragging)
        {
            SetValueFromThumbTop(eventArgs.Y - _dragOffset);
        }

        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        base.OnMouseUp(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var trackWidth = AgentPalette.Scale(this, 4F);
        var trackX = (Width - trackWidth) / 2F;
        using var trackPath = AgentPalette.RoundedRectangle(
            new RectangleF(trackX, 0, trackWidth, Height),
            trackWidth / 2F);
        using var trackBrush = new SolidBrush(Color.FromArgb(231, 235, 242));
        eventArgs.Graphics.FillPath(trackBrush, trackPath);
        var thumb = ThumbBounds();
        using var thumbPath = AgentPalette.RoundedRectangle(thumb, thumb.Width / 2F);
        using var thumbBrush = new SolidBrush(_hovered || _dragging
            ? AgentPalette.Primary
            : Color.FromArgb(150, 160, 178));
        eventArgs.Graphics.FillPath(thumbBrush, thumbPath);
    }

    private RectangleF ThumbBounds()
    {
        var total = _maximum + _viewportSize;
        var minimumThumb = AgentPalette.Scale(this, 38F);
        var thumbHeight = total <= 0
            ? Height
            : Math.Max(minimumThumb, Height * (_viewportSize / (float)total));
        thumbHeight = Math.Min(Height, thumbHeight);
        var travel = Math.Max(0F, Height - thumbHeight);
        var top = _maximum <= 0 ? 0F : travel * (_value / (float)_maximum);
        var width = AgentPalette.Scale(this, _hovered || _dragging ? 6F : 4F);
        return new RectangleF((Width - width) / 2F, top, width, thumbHeight);
    }

    private void SetValueFromThumbTop(float top)
    {
        var thumb = ThumbBounds();
        var travel = Math.Max(1F, Height - thumb.Height);
        var normalizedTop = Math.Clamp(top, 0F, travel);
        Value = _maximum <= 0 ? 0 : (int)Math.Round(_maximum * (normalizedTop / travel));
    }
}
