using CodexControl.Protocol;
using WinFormsControl = System.Windows.Forms.Control;

namespace CodexControl.Agent.Desktop;

internal sealed partial class ControlCenterForm
{
    private WinFormsControl BuildNavigation()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AgentPalette.Sidebar,
            Padding = new Padding(20, 24, 20, 20),
        };
        var brand = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 88,
            BackColor = AgentPalette.Sidebar,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62F));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        var logo = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = AgentBranding.LogoImage,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 10, 28),
        };
        var brandCopy = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            BackColor = AgentPalette.Sidebar,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 0, 24),
        };
        brandCopy.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Text = "Codex Control",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 11.5F, FontStyle.Regular),
            ForeColor = Color.White,
            Margin = Padding.Empty,
        }, 0, 0);
        brand.Controls.Add(logo, 0, 0);
        brand.Controls.Add(brandCopy, 1, 0);

        var navigation = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 12, 0, 0),
            BackColor = AgentPalette.Sidebar,
        };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        navigation.Controls.Add(CreateNavigationButton(DesktopPage.Overview, NavigationGlyph.Overview, "运行概览"), 0, 0);
        navigation.Controls.Add(CreateNavigationButton(DesktopPage.Pairing, NavigationGlyph.Pairing, "连接与配对"), 0, 1);
        navigation.Controls.Add(CreateNavigationButton(DesktopPage.General, NavigationGlyph.General, "通用设置"), 0, 2);
        navigation.Controls.Add(CreateNavigationButton(DesktopPage.Diagnostics, NavigationGlyph.Diagnostics, "高级与诊断"), 0, 3);

        var footer = new RoundedPanel
        {
            Dock = DockStyle.Bottom,
            Height = 90,
            Padding = new Padding(16, 12, 16, 10),
            BackColor = AgentPalette.SidebarElevated,
            BorderColor = Color.FromArgb(40, 54, 80),
            CornerRadius = 14,
        };
        var footerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62F));
        footerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38F));
        _sidebarStatus.Anchor = AnchorStyles.None;
        _sidebarStatus.Margin = Padding.Empty;
        footerLayout.Controls.Add(_sidebarStatus, 0, 0);
        footerLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = VersionLabel(),
            TextAlign = ContentAlignment.TopCenter,
            Font = new Font("Segoe UI", 8F, FontStyle.Regular),
            ForeColor = AgentPalette.SidebarMuted,
            Margin = Padding.Empty,
        }, 0, 1);
        footer.Controls.Add(footerLayout);
        sidebar.Controls.Add(footer);
        sidebar.Controls.Add(navigation);
        sidebar.Controls.Add(brand);
        brand.BringToFront();
        navigation.BringToFront();
        return sidebar;
    }

    private WinFormsControl BuildStatusPage()
    {
        var page = CreatePage("运行概览", out var stack);

        var hero = CreateCard(128);
        hero.Padding = new Padding(26, 20, 26, 20);
        var heroLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
        };
        heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
        heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _summaryEmblem.Anchor = AnchorStyles.Left;
        heroLayout.Controls.Add(_summaryEmblem, 0, 0);
        _summary.Anchor = AnchorStyles.Left;
        _summary.Margin = Padding.Empty;
        heroLayout.Controls.Add(_summary, 1, 0);
        hero.Controls.Add(heroLayout);
        AddStackItem(stack, hero);

        var statusGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 150,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = AgentPalette.Canvas,
            Margin = new Padding(0, 0, 0, 18),
        };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
        var coreCard = BuildMetricCard("CODEX 内核", _coreStatus, _coreDetail);
        coreCard.Margin = new Padding(0, 0, 6, 0);
        var relayCard = BuildMetricCard("RELAY 通道", _relayStatus, _relayDetail);
        relayCard.Margin = new Padding(6, 0, 6, 0);
        var proxyCard = BuildMetricCard("本机终端", _proxyStatus, _proxyDetail);
        proxyCard.Margin = new Padding(6, 0, 0, 0);
        statusGrid.Controls.Add(coreCard, 0, 0);
        statusGrid.Controls.Add(relayCard, 1, 0);
        statusGrid.Controls.Add(proxyCard, 2, 0);
        AddStackItem(stack, statusGrid);

        _errorCard.BackColor = AgentPalette.DangerSoft;
        _errorCard.BorderColor = Color.FromArgb(247, 197, 201);
        _errorCard.Padding = new Padding(22, 18, 22, 16);
        var errorTitle = Heading("需要处理", 11.5F);
        errorTitle.ForeColor = AgentPalette.Danger;
        errorTitle.Margin = new Padding(0, 0, 0, 6);
        var errorFlow = VerticalFlow();
        errorFlow.Controls.Add(errorTitle);
        errorFlow.Controls.Add(_error);
        _errorCard.Controls.Add(errorFlow);
        AddStackItem(stack, _errorCard);

        var actions = CreateCard(78);
        actions.Padding = new Padding(20, 12, 20, 12);
        var restart = ActionButton("重启内核", 108, ModernButtonStyle.Secondary);
        restart.Click += (_, _) => _restart(false);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = Padding.Empty,
        };
        _terminalButton.Margin = new Padding(10, 0, 0, 0);
        _pauseButton.Margin = new Padding(10, 0, 0, 0);
        buttons.Controls.Add(_terminalButton);
        buttons.Controls.Add(_pauseButton);
        buttons.Controls.Add(restart);
        actions.Controls.Add(buttons);
        AddStackItem(stack, actions);
        return page;
    }

    private WinFormsControl BuildConnectionPage()
    {
        var page = CreatePage("连接与配对", out var stack);

        var relayCard = CreateSectionCard(
            280,
            "Relay 与电脑身份",
            out var relayBody);
        relayBody.Controls.Add(Field("Relay 根地址", _relayRoot));
        relayBody.Controls.Add(Field("电脑名称", _deviceName));
        var apply = ActionButton("测试并应用", 126, ModernButtonStyle.Primary);
        apply.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        relayBody.Controls.Add(ActionRow(apply, _saveStatus));
        AddStackItem(stack, relayCard);

        var pairingCard = CreateCard(282);
        pairingCard.Padding = new Padding(24, 22, 24, 22);
        var pairingLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
        };
        pairingLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pairingLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 222F));
        var pairingCopy = VerticalFlow();
        pairingCopy.Padding = new Padding(0, 1, 18, 0);
        var pairingTitle = Heading("添加控制端", 12.5F);
        pairingTitle.Margin = new Padding(0, 0, 0, 14);
        pairingCopy.Controls.Add(pairingTitle);
        _pairingCode.Margin = new Padding(0, 0, 0, 5);
        pairingCopy.Controls.Add(_pairingCode);
        _pairingMeta.Margin = new Padding(0, 0, 0, 8);
        pairingCopy.Controls.Add(_pairingMeta);
        _pairingProgress.Margin = new Padding(0, 0, 0, 16);
        pairingCopy.Controls.Add(_pairingProgress);
        _pairingButton.Click += async (_, _) => await RefreshPairingCodeAsync(showErrors: true).ConfigureAwait(true);
        pairingCopy.Controls.Add(_pairingButton);
        pairingLayout.Controls.Add(pairingCopy, 0, 0);
        var qrFrame = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            BackColor = AgentPalette.SurfaceMuted,
            BorderColor = AgentPalette.Border,
            CornerRadius = 14,
        };
        qrFrame.Controls.Add(_pairingQr);
        qrFrame.Controls.Add(_pairingPlaceholder);
        pairingLayout.Controls.Add(qrFrame, 1, 0);
        pairingCard.Controls.Add(pairingLayout);
        AddStackItem(stack, pairingCard);

        var controllersCard = CreateSectionCard(
            390,
            "已配对控制端",
            out var controllerBody);
        controllerBody.Controls.Add(_controllerCount);
        _controllers.Margin = new Padding(0, 6, 0, 14);
        controllerBody.Controls.Add(_controllers);
        controllerBody.Controls.Add(Field("本机别名", _controllerAlias));
        var refresh = ActionButton("刷新", 82, ModernButtonStyle.Secondary);
        refresh.Click += async (_, _) => await RefreshPairingsAsync().ConfigureAwait(true);
        var rename = ActionButton("保存名称", 106, ModernButtonStyle.Secondary);
        rename.Click += async (_, _) => await RenameSelectedPairingAsync().ConfigureAwait(true);
        var full = ActionButton("完整控制", 106, ModernButtonStyle.Secondary);
        full.Click += async (_, _) => await UpdateSelectedPairingAsync(PairingPermissionProfile.Full).ConfigureAwait(true);
        var view = ActionButton("仅查看", 90, ModernButtonStyle.Secondary);
        view.Click += async (_, _) => await UpdateSelectedPairingAsync(PairingPermissionProfile.ViewOnly).ConfigureAwait(true);
        var revoke = ActionButton("撤销", 82, ModernButtonStyle.Danger);
        revoke.Click += async (_, _) => await RevokeSelectedPairingAsync().ConfigureAwait(true);
        controllerBody.Controls.Add(ActionRow(refresh, rename, full, view, revoke));
        AddStackItem(stack, controllersCard);
        return page;
    }

    private WinFormsControl BuildGeneralPage()
    {
        var page = CreatePage("通用设置", out var stack);
        var startupCard = CreateSectionCard(
            195,
            "启动",
            out var startupBody);
        _runAtLogin.Margin = new Padding(0, 4, 0, 12);
        startupBody.Controls.Add(_runAtLogin);
        var save = ActionButton("保存通用设置", 142, ModernButtonStyle.Primary);
        save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        startupBody.Controls.Add(ActionRow(save));
        AddStackItem(stack, startupCard);
        return page;
    }

    private WinFormsControl BuildAdvancedPage()
    {
        var page = CreatePage("高级与诊断", out var stack);
        var runtimeCard = CreateSectionCard(
            405,
            "Codex 与本地端口",
            out var runtimeBody);
        runtimeBody.Controls.Add(_manualCodex);
        runtimeBody.Controls.Add(Field("Codex 路径", _codexPath));
        runtimeBody.Controls.Add(_fixedPort);
        runtimeBody.Controls.Add(Field("固定端口", _port));
        var save = ActionButton("保存并重启内核", 158, ModernButtonStyle.Primary);
        save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        runtimeBody.Controls.Add(ActionRow(save));
        AddStackItem(stack, runtimeCard);

        var diagnosticsCard = CreateSectionCard(
            220,
            "本机诊断",
            out var diagnosticsBody);
        diagnosticsBody.Controls.Add(InfoLine("数据目录", CompactPath(_paths.DataDirectory)));
        diagnosticsBody.Controls.Add(InfoLine("日志目录", CompactPath(_paths.LogDirectory)));
        var openData = ActionButton("打开 data 文件夹", 142, ModernButtonStyle.Secondary);
        openData.Click += (_, _) => OpenDirectory(_paths.DataDirectory);
        var openLogs = ActionButton("打开日志文件夹", 142, ModernButtonStyle.Secondary);
        openLogs.Click += (_, _) => OpenDirectory(_paths.LogDirectory);
        diagnosticsBody.Controls.Add(ActionRow(openData, openLogs));
        AddStackItem(stack, diagnosticsCard);
        return page;
    }
}
