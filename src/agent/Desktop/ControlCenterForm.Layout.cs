using CodexControl.Agent.Configuration;
using CodexControl.Agent.Lifecycle;
using WinFormsControl = System.Windows.Forms.Control;

namespace CodexControl.Agent.Desktop;

internal sealed partial class ControlCenterForm
{
    private static ModernScrollHost CreatePage(
        string title,
        out TableLayoutPanel stack)
    {
        var page = new ModernScrollHost
        {
            Dock = DockStyle.Fill,
            BackColor = AgentPalette.Canvas,
            ContentPadding = new Padding(36, 28, 26, 36),
        };
        stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            BackColor = AgentPalette.Canvas,
            Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = AgentPalette.Canvas,
            Margin = new Padding(0, 0, 0, 18),
        };
        var titleLabel = Heading(title, 23F);
        titleLabel.Margin = Padding.Empty;
        header.Controls.Add(titleLabel);
        AddStackItem(stack, header);
        page.Content.Controls.Add(stack);
        return page;
    }

    private static RoundedPanel CreateSectionCard(
        int height,
        string title,
        out FlowLayoutPanel body)
    {
        var card = CreateCard(height);
        card.Padding = new Padding(24, 21, 24, 20);
        body = VerticalFlow();
        var heading = Heading(title, 12.5F);
        heading.Margin = new Padding(0, 0, 0, 15);
        body.Controls.Add(heading);
        card.Controls.Add(body);
        return card;
    }

    private static RoundedPanel BuildMetricCard(string title, StatusBadge badge, Label detail)
    {
        var card = CreateCard(166);
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(20, 18, 20, 16);
        var flow = VerticalFlow();
        var metricLabel = MetricLabel(title);
        metricLabel.Margin = new Padding(0, 0, 0, 14);
        badge.Margin = new Padding(0, 0, 0, 12);
        detail.Margin = Padding.Empty;
        flow.Controls.Add(metricLabel);
        flow.Controls.Add(badge);
        flow.Controls.Add(detail);
        card.Controls.Add(flow);
        return card;
    }

    private static RoundedPanel CreateCard(int height) => new()
    {
        Dock = DockStyle.Top,
        Height = height,
        BackColor = AgentPalette.Surface,
        BorderColor = AgentPalette.Border,
        BorderThickness = 1F,
        CornerRadius = 16,
        Margin = new Padding(0, 0, 0, 18),
    };

    private static FlowLayoutPanel VerticalFlow() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
    };

    private static Panel Field(string label, WinFormsControl control)
    {
        var field = new Panel
        {
            Width = 650,
            Height = 68,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 9),
        };
        field.Controls.Add(new Label
        {
            AutoSize = true,
            Location = new Point(0, 0),
            Text = label,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular),
            ForeColor = AgentPalette.Text,
        });
        control.Location = new Point(0, 26);
        field.Controls.Add(control);
        return field;
    }

    private static FlowLayoutPanel ActionRow(params WinFormsControl[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 0),
        };
        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 0, 10, 0);
            row.Controls.Add(control);
        }

        return row;
    }

    private static Panel InfoLine(string label, string value)
    {
        var row = new Panel
        {
            Width = 650,
            Height = 38,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        row.Controls.Add(new Panel
        {
            Location = new Point(0, 12),
            Size = new Size(8, 8),
            BackColor = AgentPalette.Accent,
        });
        row.Controls.Add(new Label
        {
            AutoSize = true,
            Location = new Point(20, 4),
            Text = label,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular),
            ForeColor = AgentPalette.Text,
        });
        row.Controls.Add(new Label
        {
            AutoEllipsis = true,
            Location = new Point(132, 4),
            Size = new Size(505, 24),
            Text = value,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = AgentPalette.TextMuted,
        });
        return row;
    }

    private static void AddStackItem(TableLayoutPanel stack, WinFormsControl control)
    {
        var row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        stack.Controls.Add(control, 0, row);
    }

    private static ModernButton ActionButton(string text, int width, ModernButtonStyle style) => new()
    {
        Text = text,
        Width = width,
        VisualStyle = style,
    };

    private static ModernTextBox TextInput(int width, string placeholder) => new()
    {
        Width = width,
        PlaceholderText = placeholder,
    };

    private static ModernNumericInput NumberInput() => new()
    {
        Minimum = 1,
        Maximum = 65535,
        Value = 8765,
        Width = 160,
    };

    private static ListBox ControllerList() => new()
    {
        Width = 650,
        Height = 112,
        BorderStyle = BorderStyle.None,
        BackColor = AgentPalette.SurfaceMuted,
        ForeColor = AgentPalette.Text,
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
        DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 38,
        IntegralHeight = false,
    };

    private static Label Heading(string text, float size) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", size, FontStyle.Regular),
        ForeColor = AgentPalette.Text,
    };

    private static Label Body(string text, int maximumWidth) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(maximumWidth, 0),
        Font = new Font("Segoe UI", 9.2F, FontStyle.Regular),
        ForeColor = AgentPalette.TextMuted,
    };

    private static Label MetricLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 8F, FontStyle.Regular),
        ForeColor = AgentPalette.Primary,
    };

    private static string CoreStatusText(RuntimeCoreStatus status) => status switch
    {
        RuntimeCoreStatus.Stopped => "已停止",
        RuntimeCoreStatus.Discovering => "正在发现",
        RuntimeCoreStatus.Probing => "正在探测",
        RuntimeCoreStatus.Starting => "正在启动",
        RuntimeCoreStatus.Ready => "已就绪",
        RuntimeCoreStatus.RestartPending => "等待重启",
        RuntimeCoreStatus.Failed => "启动失败",
        RuntimeCoreStatus.Stopping => "正在停止",
        _ => status.ToString(),
    };

    private static StatusTone CoreStatusTone(RuntimeCoreStatus status) => status switch
    {
        RuntimeCoreStatus.Ready => StatusTone.Success,
        RuntimeCoreStatus.Failed => StatusTone.Danger,
        RuntimeCoreStatus.RestartPending => StatusTone.Warning,
        RuntimeCoreStatus.Discovering or RuntimeCoreStatus.Probing or RuntimeCoreStatus.Starting or RuntimeCoreStatus.Stopping => StatusTone.Info,
        _ => StatusTone.Neutral,
    };

    private static string RelayStatusText(RuntimeRelayStatus status) => status switch
    {
        RuntimeRelayStatus.Disabled => "未配置",
        RuntimeRelayStatus.Paused => "已暂停",
        RuntimeRelayStatus.Offline => "离线",
        RuntimeRelayStatus.Connecting => "连接中",
        RuntimeRelayStatus.Online => "在线",
        RuntimeRelayStatus.Failed => "连接失败",
        _ => status.ToString(),
    };

    private static StatusTone RelayStatusTone(RuntimeRelayStatus status) => status switch
    {
        RuntimeRelayStatus.Online => StatusTone.Success,
        RuntimeRelayStatus.Paused => StatusTone.Warning,
        RuntimeRelayStatus.Failed => StatusTone.Danger,
        RuntimeRelayStatus.Connecting => StatusTone.Info,
        _ => StatusTone.Neutral,
    };

    private static StatusTone OverallStatusTone(AgentRuntimeSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.LastError) ||
            snapshot.CoreStatus == RuntimeCoreStatus.Failed ||
            snapshot.RelayStatus == RuntimeRelayStatus.Failed)
        {
            return StatusTone.Danger;
        }

        if (snapshot.CoreStatus == RuntimeCoreStatus.Ready && snapshot.RelayStatus == RuntimeRelayStatus.Online)
        {
            return StatusTone.Success;
        }

        if (snapshot.CoreStatus == RuntimeCoreStatus.Ready)
        {
            return StatusTone.Warning;
        }

        return snapshot.CoreStatus is RuntimeCoreStatus.Discovering or
            RuntimeCoreStatus.Probing or
            RuntimeCoreStatus.Starting or
            RuntimeCoreStatus.Stopping
            ? StatusTone.Info
            : StatusTone.Neutral;
    }

    private static string OverallStatusText(AgentRuntimeSnapshot snapshot)
    {
        var tone = OverallStatusTone(snapshot);
        return tone switch
        {
            StatusTone.Success => "运行正常",
            StatusTone.Danger => "需要处理",
            StatusTone.Info => "启动中",
            StatusTone.Warning when snapshot.RelayStatus == RuntimeRelayStatus.Paused => "远程已暂停",
            StatusTone.Warning => "仅本机可用",
            _ => "未运行",
        };
    }

    private static string RelayDescription(AgentSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.RelayRootUrl))
        {
            return "尚未配置 Relay 根地址";
        }

        var host = Uri.TryCreate(settings.RelayRootUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : settings.RelayRootUrl;
        return host;
    }

    private static string VersionLabel()
    {
        var version = typeof(ControlCenterForm).Assembly.GetName().Version;
        return version is null ? ".NET 8 · Windows Agent" : $"v{version.Major}.{version.Minor}.{version.Build} · .NET 8";
    }

    private static string CompactPath(string path) => path.Length <= 64 ? path : $"…{path[^61..]}";
}
