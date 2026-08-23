using System.Diagnostics;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Lifecycle;
using CodexControl.Protocol;
using WinFormsControl = System.Windows.Forms.Control;

namespace CodexControl.Agent.Desktop;

internal sealed partial class ControlCenterForm : Form
{
    private enum DesktopPage
    {
        Overview,
        Pairing,
        General,
        Diagnostics,
    }

    private readonly AgentDataPaths _paths;
    private readonly Func<AgentSettings, Task<bool>> _applySettings;
    private readonly Func<Task<ControlCenterPairingDisplay>> _createPairing;
    private readonly Func<bool, Task> _setPaused;
    private readonly Action _openTerminal;
    private readonly Action<bool> _restart;
    private readonly Func<Task<IReadOnlyList<PairedControllerSummaryPayload>>> _listPairings;
    private readonly Func<PairingUpdatePayload, Task> _updatePairing;
    private readonly Func<long, Task<bool>> _revokePairing;

    private readonly Panel _content = new()
    {
        Dock = DockStyle.Fill,
        BackColor = AgentPalette.Canvas,
    };
    private readonly Dictionary<DesktopPage, NavigationButton> _navigation = [];
    private readonly Dictionary<DesktopPage, WinFormsControl> _pages = [];

    private readonly Label _summary = Heading("未运行", 22F);
    private readonly StatusEmblem _summaryEmblem = new();
    private readonly StatusBadge _sidebarStatus = new();
    private readonly StatusBadge _coreStatus = new();
    private readonly StatusBadge _relayStatus = new();
    private readonly StatusBadge _proxyStatus = new();
    private readonly Label _coreDetail = Body("等待版本", 190);
    private readonly Label _relayDetail = Body("未配置地址", 190);
    private readonly Label _proxyDetail = Body(string.Empty, 190);
    private readonly RoundedPanel _errorCard = CreateCard(112);
    private readonly Label _error = Body(string.Empty, 650);
    private readonly ModernButton _terminalButton = ActionButton("打开 Codex 终端", 154, ModernButtonStyle.Primary);
    private readonly ModernButton _pauseButton = ActionButton("暂停远程访问", 136, ModernButtonStyle.Secondary);

    private readonly ModernTextBox _relayRoot = TextInput(620, "https://control.example.com");
    private readonly ModernTextBox _deviceName = TextInput(620, "这台电脑的显示名称");
    private readonly Label _pairingCode = Heading("尚未生成", 21F);
    private readonly Label _pairingMeta = Body("剩余 --:--", 430);
    private readonly CountdownProgressBar _pairingProgress = new()
    {
        Visible = false,
    };
    private readonly ModernButton _pairingButton = ActionButton("生成配对码", 132, ModernButtonStyle.Primary);
    private readonly System.Windows.Forms.Timer _pairingTimer = new() { Interval = 250 };
    private readonly PictureBox _pairingQr = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        Visible = false,
        BackColor = Color.White,
    };
    private readonly Label _pairingPlaceholder = new()
    {
        Dock = DockStyle.Fill,
        Text = "二维码",
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 9F, FontStyle.Regular),
        ForeColor = AgentPalette.TextMuted,
        BackColor = AgentPalette.SurfaceMuted,
    };
    private readonly Label _controllerCount = Body("尚未加载", 620);
    private readonly ListBox _controllers = ControllerList();
    private readonly ModernTextBox _controllerAlias = TextInput(360, "本机显示名称");
    private IReadOnlyList<PairedControllerSummaryPayload> _controllerItems = [];

    private readonly ToggleSwitch _runAtLogin = new()
    {
        Text = "随 Windows 登录启动",
        Description = string.Empty,
        Width = 640,
    };
    private readonly ToggleSwitch _manualCodex = new()
    {
        Text = "手动指定 Codex 路径",
        Description = string.Empty,
        Width = 640,
    };
    private readonly ModernTextBox _codexPath = TextInput(620, "codex.exe 的绝对路径");
    private readonly ToggleSwitch _fixedPort = new()
    {
        Text = "使用固定本地端口",
        Description = string.Empty,
        Width = 640,
    };
    private readonly ModernNumericInput _port = NumberInput();
    private readonly Label _saveStatus = Body(string.Empty, 260);

    private AgentSettings _settings;
    private AgentRuntimeSnapshot _runtime = AgentRuntimeSnapshot.Initial;
    private DesktopPage _activePage;
    private DateTimeOffset? _pairingExpiresAt;
    private double _pairingLifetimeSeconds = 180D;
    private bool _pairingWasGenerated;
    private bool _pairingRefreshInProgress;
    private bool _automaticPairingRefreshPending;
    private DateTimeOffset? _lastAutomaticallyRefreshedExpiry;
    private bool _pairingsLoading;

    public ControlCenterForm(
        AgentDataPaths paths,
        AgentSettings settings,
        Func<AgentSettings, Task<bool>> applySettings,
        Func<Task<ControlCenterPairingDisplay>> createPairing,
        Func<bool, Task> setPaused,
        Action openTerminal,
        Action<bool> restart,
        Func<Task<IReadOnlyList<PairedControllerSummaryPayload>>> listPairings,
        Func<PairingUpdatePayload, Task> updatePairing,
        Func<long, Task<bool>> revokePairing)
    {
        _paths = paths;
        _settings = settings;
        _applySettings = applySettings;
        _createPairing = createPairing;
        _setPaused = setPaused;
        _openTerminal = openTerminal;
        _restart = restart;
        _listPairings = listPairings;
        _updatePairing = updatePairing;
        _revokePairing = revokePairing;

        Text = "Codex Control";
        Icon = AgentBranding.ApplicationIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 700);
        Size = new Size(1160, 780);
        BackColor = AgentPalette.Canvas;
        ForeColor = AgentPalette.Text;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular);
        DoubleBuffered = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AgentPalette.Canvas,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 266F));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.Controls.Add(BuildNavigation(), 0, 0);
        root.Controls.Add(_content, 1, 0);
        Controls.Add(root);
        InitializePages();

        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                _pairingTimer.Stop();
                Hide();
            }
        };
        _terminalButton.Click += (_, _) => Execute(_openTerminal);
        _pauseButton.Click += async (_, _) =>
            await ExecuteAsync(() => _setPaused(!_settings.RemoteAccessPaused)).ConfigureAwait(true);
        _manualCodex.CheckedChanged += (_, _) => UpdateAdvancedInputState();
        _fixedPort.CheckedChanged += (_, _) => UpdateAdvancedInputState();
        _controllers.SelectedIndexChanged += (_, _) =>
        {
            _controllerAlias.Text = _controllers.SelectedIndex >= 0 &&
                                    _controllers.SelectedIndex < _controllerItems.Count
                ? _controllerItems[_controllers.SelectedIndex].Alias ?? string.Empty
                : string.Empty;
        };
        _controllers.DrawItem += DrawControllerItem;
        _pairingMeta.Visible = false;
        _pairingTimer.Tick += (_, _) => UpdatePairingCountdown();

        LoadSettings(settings);
        SetRuntimeSnapshot(_runtime);
        NavigateTo(settings.IsConfigured ? DesktopPage.Overview : DesktopPage.Pairing);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    public void SetRuntimeSnapshot(AgentRuntimeSnapshot snapshot)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetRuntimeSnapshot(snapshot));
            return;
        }

        _runtime = snapshot;
        _summary.Text = OverallStatusText(snapshot);
        _coreStatus.SetStatus(CoreStatusText(snapshot.CoreStatus), CoreStatusTone(snapshot.CoreStatus));
        _coreDetail.Text = snapshot.CodexVersion is null
            ? "等待版本"
            : FormatCodexVersion(snapshot.CodexVersion);
        _relayStatus.SetStatus(RelayStatusText(snapshot.RelayStatus), RelayStatusTone(snapshot.RelayStatus));
        _relayDetail.Text = RelayDescription(_settings);

        var proxyText = snapshot.LocalProxyUri is null
            ? "未就绪"
            : snapshot.LocalTuiConnected ? "终端已连接" : "等待终端";
        var proxyTone = snapshot.LocalProxyUri is null
            ? StatusTone.Neutral
            : snapshot.LocalTuiConnected ? StatusTone.Success : StatusTone.Info;
        _proxyStatus.SetStatus(proxyText, proxyTone);
        _proxyDetail.Text = snapshot.LocalProxyUri is null
            ? string.Empty
            : snapshot.LocalProxyUri.ToString();

        var overallTone = OverallStatusTone(snapshot);
        var overallText = OverallStatusText(snapshot);
        _sidebarStatus.SetStatus(overallText, overallTone);
        _summaryEmblem.Tone = overallTone;
        _error.Text = snapshot.LastError ?? string.Empty;
        _errorCard.Visible = !string.IsNullOrWhiteSpace(snapshot.LastError);
        _terminalButton.Enabled = snapshot.CoreStatus == RuntimeCoreStatus.Ready && !snapshot.LocalTuiConnected;
        _terminalButton.Text = snapshot.LocalTuiConnected ? "终端已连接" : "打开 Codex 终端";
        _pauseButton.Text = _settings.RemoteAccessPaused ? "恢复远程访问" : "暂停远程访问";
    }

    public void UpdateSettings(AgentSettings settings)
    {
        _settings = settings;
        LoadSettings(settings);
        SetRuntimeSnapshot(_runtime);
    }

    public void ActivateWindow()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ActivateWindow);
            return;
        }

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        ActivatePairingPageIfNeeded();
    }

    public void RefreshPairingsAfterPairing()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshPairingsAfterPairing);
            return;
        }

        if (_activePage == DesktopPage.Pairing)
        {
            _ = RefreshPairingsAsync(showErrors: false);
        }
    }

    public async Task<PairingConfirmationResolvePayload> ConfirmPairingAsync(
        PairingConfirmationRequestedPayload requested)
    {
        ActivateWindow();
        using var dialog = new PairingConfirmationDialog(requested);
        _ = dialog.ShowDialog(this);
        await Task.CompletedTask.ConfigureAwait(true);
        return new PairingConfirmationResolvePayload(
            requested.PairingRequestId,
            dialog.Decision,
            dialog.PermissionProfile);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pairingQr.Image?.Dispose();
            _pairingTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        NativeWindowStyling.SuppressSystemBorder(Handle);
    }

    private async Task SaveAsync()
    {
        var proposed = _settings with
        {
            DeviceName = _deviceName.Text,
            RelayRootUrl = _relayRoot.Text,
            RunAtLogin = _runAtLogin.Checked,
            CodexPathMode = _manualCodex.Checked ? CodexPathMode.Manual : CodexPathMode.Auto,
            CodexPath = _manualCodex.Checked ? _codexPath.Text : null,
            LocalPortMode = _fixedPort.Checked ? LocalPortMode.Fixed : LocalPortMode.Auto,
            FixedPort = _fixedPort.Checked ? decimal.ToInt32(_port.Value) : null,
        };
        await ExecuteAsync(async () =>
        {
            _saveStatus.Text = "正在验证…";
            _saveStatus.ForeColor = AgentPalette.Info;
            if (await _applySettings(proposed).ConfigureAwait(true))
            {
                _saveStatus.Text = "已保存并应用";
                _saveStatus.ForeColor = AgentPalette.Success;
            }
        }).ConfigureAwait(true);
    }

    private void LoadSettings(AgentSettings settings)
    {
        _relayRoot.Text = settings.RelayRootUrl ?? string.Empty;
        _deviceName.Text = settings.DeviceName;
        _runAtLogin.Checked = settings.RunAtLogin;
        _manualCodex.Checked = settings.CodexPathMode == CodexPathMode.Manual;
        _codexPath.Text = settings.CodexPath ?? string.Empty;
        _fixedPort.Checked = settings.LocalPortMode == LocalPortMode.Fixed;
        _port.Value = settings.FixedPort is >= 1 and <= 65535 ? settings.FixedPort.Value : 8765;
        UpdateAdvancedInputState();
    }

    private void UpdateAdvancedInputState()
    {
        _codexPath.Enabled = _manualCodex.Checked;
        _port.Enabled = _fixedPort.Checked;
    }

    private async Task RefreshPairingsAsync(bool showErrors = true)
    {
        if (_pairingsLoading)
        {
            return;
        }

        _pairingsLoading = true;
        _controllerCount.Text = "加载中…";
        try
        {
            _controllerItems = await _listPairings().ConfigureAwait(true);
            _controllers.Items.Clear();
            foreach (var controller in _controllerItems)
            {
                var name = controller.Alias ?? controller.ControllerName;
                var profile = controller.Permissions.Steer ? "完整控制" : "仅查看";
                var state = controller.Revoked ? "已撤销" : profile;
                _controllers.Items.Add(
                    $"{name} · {state} · {controller.ControllerId[..Math.Min(16, controller.ControllerId.Length)]}");
            }

            _controllerCount.Text = _controllerItems.Count == 0
                ? "暂无控制端"
                : $"{_controllerItems.Count} 个控制端";
        }
        catch (Exception exception)
        {
            _controllerCount.Text = "加载失败";
            if (showErrors)
            {
                MessageBox.Show(this, exception.Message, "Codex Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _pairingsLoading = false;
        }
    }

    private async Task UpdateSelectedPairingAsync(PairingPermissionProfile profile)
    {
        if (!TryGetSelectedController(out var selected))
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            await _updatePairing(new PairingUpdatePayload(selected.PairingId, selected.Alias, profile))
                .ConfigureAwait(true);
            await RefreshPairingsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RenameSelectedPairingAsync()
    {
        if (!TryGetSelectedController(out var selected))
        {
            return;
        }

        var profile = selected.Permissions.Steer
            ? PairingPermissionProfile.Full
            : PairingPermissionProfile.ViewOnly;
        await ExecuteAsync(async () =>
        {
            await _updatePairing(new PairingUpdatePayload(selected.PairingId, _controllerAlias.Text, profile))
                .ConfigureAwait(true);
            await RefreshPairingsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RevokeSelectedPairingAsync()
    {
        if (!TryGetSelectedController(out var selected))
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定撤销 {selected.Alias ?? selected.ControllerName}？Relay 离线时会安全排队。",
            "撤销控制端",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            var synchronized = await _revokePairing(selected.PairingId).ConfigureAwait(true);
            MessageBox.Show(
                this,
                synchronized ? "控制端已撤销。" : "撤销已保存，将在 Relay 重连且 Device Ready 前同步。",
                "Codex Control",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            if (synchronized)
            {
                await RefreshPairingsAsync().ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    private bool TryGetSelectedController(out PairedControllerSummaryPayload selected)
    {
        if (_controllers.SelectedIndex < 0 || _controllers.SelectedIndex >= _controllerItems.Count)
        {
            MessageBox.Show(this, "请先选择控制端。", "Codex Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
            selected = default!;
            return false;
        }

        selected = _controllerItems[_controllers.SelectedIndex];
        return true;
    }

    private async Task RefreshPairingCodeAsync(bool showErrors)
    {
        if (_pairingRefreshInProgress)
        {
            return;
        }

        _pairingRefreshInProgress = true;
        _pairingButton.Enabled = false;
        _pairingButton.Text = _pairingWasGenerated ? "正在刷新…" : "正在生成…";
        Image? nextQr = null;
        try
        {
            var pairing = await _createPairing().ConfigureAwait(true);
            nextQr = PairingQrRenderer.Render(pairing.PairingUrl);
            var previousQr = _pairingQr.Image;
            _pairingQr.Image = nextQr;
            nextQr = null;
            previousQr?.Dispose();

            _pairingCode.Text = pairing.CodeText;
            _pairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(pairing.ExpiresAt);
            _pairingLifetimeSeconds = Math.Max(
                1D,
                (_pairingExpiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds);
            _pairingWasGenerated = true;
            _pairingMeta.Visible = true;
            _pairingProgress.Visible = true;
            _pairingPlaceholder.Visible = false;
            _pairingQr.Visible = true;
            _pairingButton.Text = "重新生成";
            UpdatePairingCountdown();
            if (Visible && _activePage == DesktopPage.Pairing)
            {
                _pairingTimer.Start();
            }
        }
        catch (Exception exception)
        {
            if (_pairingExpiresAt is null || _pairingExpiresAt <= DateTimeOffset.UtcNow)
            {
                _pairingCode.Text = "生成失败";
                _pairingMeta.Text = "请重试";
                _pairingProgress.Visible = false;
                _pairingQr.Visible = false;
                _pairingPlaceholder.Visible = true;
            }

            _pairingButton.Text = "重试";
            _pairingTimer.Stop();
            if (showErrors)
            {
                MessageBox.Show(this, exception.Message, "Codex Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            nextQr?.Dispose();
            _pairingButton.Enabled = true;
            _pairingRefreshInProgress = false;
        }
    }

    private void UpdatePairingCountdown()
    {
        if (!_pairingWasGenerated || _pairingExpiresAt is null)
        {
            return;
        }

        var remaining = _pairingExpiresAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _pairingTimer.Stop();
            _pairingMeta.Text = "正在刷新…";
            _pairingProgress.Value = 0D;
            if (Visible && _activePage == DesktopPage.Pairing)
            {
                ScheduleAutomaticPairingRefresh();
            }

            return;
        }

        var totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        _pairingMeta.Text = $"剩余 {totalSeconds / 60:00}:{totalSeconds % 60:00}";
        _pairingProgress.Value = remaining.TotalSeconds / _pairingLifetimeSeconds;
    }

    private void ActivatePairingPageIfNeeded()
    {
        if (!IsHandleCreated || !Visible || _activePage != DesktopPage.Pairing)
        {
            return;
        }

        _ = RefreshPairingsAsync(showErrors: false);
        if (!_pairingWasGenerated)
        {
            return;
        }

        if (_pairingExpiresAt <= DateTimeOffset.UtcNow)
        {
            ScheduleAutomaticPairingRefresh();
        }
        else
        {
            UpdatePairingCountdown();
            _pairingTimer.Start();
        }
    }

    private void ScheduleAutomaticPairingRefresh()
    {
        if (_automaticPairingRefreshPending ||
            _pairingRefreshInProgress ||
            !IsHandleCreated ||
            _pairingExpiresAt is null ||
            _pairingExpiresAt > DateTimeOffset.UtcNow ||
            _lastAutomaticallyRefreshedExpiry == _pairingExpiresAt)
        {
            return;
        }

        _lastAutomaticallyRefreshedExpiry = _pairingExpiresAt;
        _automaticPairingRefreshPending = true;
        BeginInvoke(async () =>
        {
            try
            {
                await RefreshPairingCodeAsync(showErrors: false).ConfigureAwait(true);
            }
            finally
            {
                _automaticPairingRefreshPending = false;
            }
        });
    }

    private void NavigateTo(DesktopPage page)
    {
        if (!_pages.TryGetValue(page, out var pageControl))
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, null);
        }

        _content.SuspendLayout();
        foreach (var item in _pages)
        {
            item.Value.Visible = item.Key == page;
        }

        pageControl.BringToFront();
        _activePage = page;
        if (pageControl is ModernScrollHost scrollHost)
        {
            scrollHost.RefreshScrollMetrics();
        }

        if (page == DesktopPage.Pairing)
        {
            ActivatePairingPageIfNeeded();
        }
        else
        {
            _pairingTimer.Stop();
        }

        _content.ResumeLayout();
        foreach (var item in _navigation)
        {
            item.Value.Selected = item.Key == page;
        }
    }

    private void InitializePages()
    {
        var pages = new (DesktopPage Page, WinFormsControl Control)[]
        {
            (DesktopPage.Overview, BuildStatusPage()),
            (DesktopPage.Pairing, BuildConnectionPage()),
            (DesktopPage.General, BuildGeneralPage()),
            (DesktopPage.Diagnostics, BuildAdvancedPage()),
        };
        foreach (var item in pages)
        {
            item.Control.Dock = DockStyle.Fill;
            item.Control.Visible = false;
            _pages.Add(item.Page, item.Control);
            _content.Controls.Add(item.Control);
        }
    }

    private NavigationButton CreateNavigationButton(DesktopPage page, NavigationGlyph glyph, string text)
    {
        var button = new NavigationButton(glyph)
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 5),
        };
        button.Click += (_, _) => NavigateTo(page);
        _navigation.Add(page, button);
        return button;
    }

    private static void DrawControllerItem(object? sender, DrawItemEventArgs eventArgs)
    {
        if (sender is not ListBox list || eventArgs.Index < 0 || eventArgs.Index >= list.Items.Count)
        {
            return;
        }

        var selected = (eventArgs.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? AgentPalette.InfoSoft : AgentPalette.SurfaceMuted);
        eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            list.Items[eventArgs.Index]?.ToString() ?? string.Empty,
            list.Font,
            new Rectangle(eventArgs.Bounds.X + 14, eventArgs.Bounds.Y, eventArgs.Bounds.Width - 24, eventArgs.Bounds.Height),
            selected ? AgentPalette.Info : AgentPalette.Text,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis);
        eventArgs.DrawFocusRectangle();
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string FormatCodexVersion(string version)
    {
        var normalized = version.Trim();
        return normalized.StartsWith("codex-cli", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"codex-cli {normalized}";
    }

    private void Execute(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Codex Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Codex Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed record ControlCenterPairingDisplay(string CodeText, string PairingUrl, long ExpiresAt);
