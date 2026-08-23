using CodexControl.Protocol;

namespace CodexControl.Agent.Desktop;

internal sealed class PairingConfirmationDialog : Form
{
    private readonly DateTimeOffset _expiresAt;
    private readonly StatusBadge _countdown = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public PairingConfirmationDialog(PairingConfirmationRequestedPayload requested)
    {
        _expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(requested.ExpiresAt);
        Decision = PairingDecision.Deny;
        PermissionProfile = PairingPermissionProfile.ViewOnly;

        Text = "新的控制端请求";
        Icon = AgentBranding.ApplicationIcon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(570, 400);
        BackColor = AgentPalette.Canvas;
        ForeColor = AgentPalette.Text;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular);
        Padding = new Padding(28, 24, 28, 24);

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = AgentPalette.Canvas,
        };
        stack.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "是否允许这个控制端？",
            Font = new Font("Segoe UI Semibold", 20F, FontStyle.Regular),
            ForeColor = AgentPalette.Text,
            Margin = new Padding(0, 0, 0, 7),
        });
        stack.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(505, 0),
            Text = "请在本机选择权限；关闭窗口将拒绝申请。",
            Font = new Font("Segoe UI", 9.2F, FontStyle.Regular),
            ForeColor = AgentPalette.TextMuted,
            Margin = new Padding(0, 0, 0, 18),
        });

        var identityCard = new RoundedPanel
        {
            Width = 510,
            Height = 176,
            Padding = new Padding(20, 17, 20, 16),
            BackColor = Color.White,
            BorderColor = AgentPalette.Border,
            CornerRadius = 14,
            Margin = new Padding(0, 0, 0, 18),
        };
        var identity = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        identity.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "控制端",
            Font = new Font("Segoe UI Semibold", 8F, FontStyle.Regular),
            ForeColor = AgentPalette.Primary,
            Margin = new Padding(0, 0, 0, 5),
        });
        identity.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(466, 0),
            Text = requested.ControllerName,
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Regular),
            ForeColor = AgentPalette.Text,
            Margin = new Padding(0, 0, 0, 13),
        });
        identity.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "公钥短指纹",
            Font = new Font("Segoe UI Semibold", 8F, FontStyle.Regular),
            ForeColor = AgentPalette.TextMuted,
            Margin = new Padding(0, 0, 0, 4),
        });
        identity.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(466, 0),
            Text = requested.PublicKeyFingerprint,
            Font = new Font("Consolas", 9.5F, FontStyle.Regular),
            ForeColor = AgentPalette.Text,
            Margin = new Padding(0, 0, 0, 12),
        });
        _countdown.Margin = Padding.Empty;
        identity.Controls.Add(_countdown);
        identityCard.Controls.Add(identity);
        stack.Controls.Add(identityCard);

        stack.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(505, 0),
            Text = "完整控制可操控任务和处理审批；仅查看不能更改任务。",
            Font = new Font("Segoe UI", 8.8F, FontStyle.Regular),
            ForeColor = AgentPalette.TextMuted,
            Margin = new Padding(0, 0, 0, 16),
        });

        var deny = CreateButton("拒绝", 92, ModernButtonStyle.Danger);
        deny.Click += (_, _) => Resolve(PairingDecision.Deny, PairingPermissionProfile.ViewOnly);
        var viewOnly = CreateButton("仅允许查看", 126, ModernButtonStyle.Secondary);
        viewOnly.Click += (_, _) => Resolve(PairingDecision.Allow, PairingPermissionProfile.ViewOnly);
        var full = CreateButton("允许完整控制", 142, ModernButtonStyle.Primary);
        full.Click += (_, _) => Resolve(PairingDecision.Allow, PairingPermissionProfile.Full);
        var actions = new FlowLayoutPanel
        {
            Width = 510,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        full.Margin = Padding.Empty;
        viewOnly.Margin = new Padding(0, 0, 10, 0);
        deny.Margin = new Padding(0, 0, 10, 0);
        actions.Controls.Add(full);
        actions.Controls.Add(viewOnly);
        actions.Controls.Add(deny);
        stack.Controls.Add(actions);
        Controls.Add(stack);

        AcceptButton = full;
        CancelButton = deny;
        _timer.Tick += (_, _) => UpdateCountdown();
        Shown += (_, _) =>
        {
            UpdateCountdown();
            _timer.Start();
        };
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    public PairingDecision Decision { get; private set; }

    public PairingPermissionProfile PermissionProfile { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        NativeWindowStyling.SuppressSystemBorder(Handle);
    }

    private void UpdateCountdown()
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_expiresAt - DateTimeOffset.UtcNow).TotalSeconds));
        _countdown.SetStatus(
            seconds == 0 ? "申请已失效" : $"{seconds} 秒后失效",
            seconds <= 15 ? StatusTone.Warning : StatusTone.Info);
        if (seconds == 0)
        {
            Resolve(PairingDecision.Deny, PairingPermissionProfile.ViewOnly);
        }
    }

    private void Resolve(PairingDecision decision, PairingPermissionProfile profile)
    {
        Decision = decision;
        PermissionProfile = profile;
        DialogResult = decision == PairingDecision.Deny ? DialogResult.Cancel : DialogResult.OK;
        Close();
    }

    private static ModernButton CreateButton(string text, int width, ModernButtonStyle style) => new()
    {
        Text = text,
        Width = width,
        VisualStyle = style,
    };
}
