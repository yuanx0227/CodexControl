using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Lifecycle;
using CodexControl.Protocol;

namespace CodexControl.Agent.Desktop;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AgentDataPaths _paths;
    private readonly AgentSettingsStore _store;
    private readonly NotifyIcon _tray;
    private readonly ControlCenterForm _form;
    private AgentSettings _settings;
    private AgentRuntimeCoordinator? _runtime;
    private bool _exiting;

    public TrayApplicationContext(
        AgentDataPaths paths,
        AgentSettingsStore store,
        SettingsLoadResult loaded,
        bool startHidden)
    {
        _paths = paths;
        _store = store;
        _settings = loaded.Settings;
        _form = new ControlCenterForm(
            paths,
            _settings,
            ApplySettingsAsync,
            CreatePairingAsync,
            SetPausedAsync,
            OpenTerminal,
            RestartRuntime,
            ListPairingsAsync,
            UpdatePairingAsync,
            RevokePairingAsync);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 Codex Control", null, (_, _) => ActivateWindow());
        menu.Items.Add("打开 Codex 终端", null, (_, _) => OpenTerminal());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("暂停/恢复远程访问", null, async (_, _) =>
            await SetPausedAsync(!_settings.RemoteAccessPaused).ConfigureAwait(true));
        menu.Items.Add("重启内核", null, (_, _) => RestartRuntime(force: false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync().ConfigureAwait(true));
        _tray = new NotifyIcon
        {
            Icon = AgentBranding.ApplicationIcon,
            Text = "Codex Control · 尚未启动",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ActivateWindow();

        if (_settings.IsConfigured && _settings.StartCoreAutomatically)
        {
            StartRuntime();
        }

        if (!_settings.IsConfigured || !startHidden || loaded.Warning is not null)
        {
            ActivateWindow();
        }

        if (loaded.Warning is not null)
        {
            _tray.ShowBalloonTip(5000, "Codex Control 配置恢复", loaded.Warning, ToolTipIcon.Warning);
        }
    }

    public void ActivateWindow() => _form.ActivateWindow();

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _form.Dispose();
        if (_runtime is not null)
        {
            _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _runtime = null;
        }

        base.ExitThreadCore();
    }

    private async Task<bool> ApplySettingsAsync(AgentSettings proposed)
    {
        var validated = proposed.Validate();
        if (!await ValidateRelayAsync(validated.RelayRootUrl).ConfigureAwait(true))
        {
            return false;
        }

        if (_runtime?.Snapshot.Codex.ActiveTurnId is not null)
        {
            var answer = MessageBox.Show(
                _form,
                "当前 Turn 正在运行。保存后将在当前任务结束后应用；是否继续保存？",
                "Codex Control",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                return false;
            }
        }

        if (validated.RunAtLogin != StartupRegistrar.IsEnabled())
        {
            StartupRegistrar.SetEnabled(validated.RunAtLogin);
        }

        _settings = _store.Save(validated);
        _form.UpdateSettings(_settings);
        if (_runtime?.Snapshot.Codex.ActiveTurnId is not null)
        {
            _runtime.UpdateOptions(
                AgentOptions.FromSettings(_settings, _paths),
                _settings.RemoteAccessPaused);
            _runtime.RequestRestart(force: false);
        }
        else
        {
            await ReplaceRuntimeAsync().ConfigureAwait(true);
        }

        return true;
    }

    private async Task<ControlCenterPairingDisplay> CreatePairingAsync()
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Agent Runtime 尚未启动。");
        var pairing = await runtime.CreatePairingAsync(CancellationToken.None).ConfigureAwait(true);
        var relayRoot = _settings.RelayRootUrl ?? throw new InvalidOperationException("Relay 尚未配置。");
        var encodedRelay = Convert.ToBase64String(Encoding.UTF8.GetBytes(relayRoot))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var pairingUrl = $"{relayRoot}/#/pair?relay={encodedRelay}&code={pairing.Code}" +
                         $"&device={Uri.EscapeDataString(_settings.DeviceName)}";
        return new ControlCenterPairingDisplay(
            $"{pairing.Code[..3]} {pairing.Code[3..]}",
            pairingUrl,
            pairing.ExpiresAt);
    }

    private async Task SetPausedAsync(bool paused)
    {
        _settings = _store.Save(_settings with { RemoteAccessPaused = paused });
        _form.UpdateSettings(_settings);
        if (_runtime is null)
        {
            if (!paused && _settings.IsConfigured)
            {
                StartRuntime();
            }

            return;
        }

        if (paused)
        {
            await _runtime.PauseRemoteAccessAsync(CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            await _runtime.ResumeRemoteAccessAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task<IReadOnlyList<PairedControllerSummaryPayload>> ListPairingsAsync()
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Agent Runtime 尚未启动。");
        return await runtime.ListPairingsAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task UpdatePairingAsync(PairingUpdatePayload payload)
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Agent Runtime 尚未启动。");
        _ = await runtime.UpdatePairingAsync(payload, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task<bool> RevokePairingAsync(long pairingId)
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Agent Runtime 尚未启动。");
        return await runtime.RevokePairingAsync(pairingId, CancellationToken.None).ConfigureAwait(true);
    }

    private void OpenTerminal()
    {
        try
        {
            (_runtime ?? throw new InvalidOperationException("Agent Runtime 尚未启动。"))
                .OpenLocalTerminal();
        }
        catch (Exception exception)
        {
            MessageBox.Show(_form, exception.Message, "Codex Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestartRuntime(bool force)
    {
        if (_runtime is null)
        {
            StartRuntime();
            return;
        }

        if (_runtime.Snapshot.Codex.ActiveTurnId is not null && force)
        {
            var answer = MessageBox.Show(
                _form,
                "立即重启会中断当前 Turn。确定继续吗？",
                "Codex Control",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        _ = _runtime.RequestRestart(force);
    }

    private void StartRuntime()
    {
        if (_runtime is not null || !_settings.IsConfigured)
        {
            return;
        }

        var options = AgentOptions.FromSettings(_settings, _paths);
        var runtime = new AgentRuntimeCoordinator(options, _paths, _settings.RemoteAccessPaused);
        runtime.SnapshotChanged += OnRuntimeSnapshotChanged;
        runtime.PairingConfirmationRequested += OnPairingConfirmationRequested;
        _runtime = runtime;
        runtime.Start();
        _form.SetRuntimeSnapshot(runtime.Snapshot);
    }

    private async Task ReplaceRuntimeAsync()
    {
        var old = _runtime;
        _runtime = null;
        if (old is not null)
        {
            old.SnapshotChanged -= OnRuntimeSnapshotChanged;
            old.PairingConfirmationRequested -= OnPairingConfirmationRequested;
            await old.DisposeAsync().ConfigureAwait(true);
        }

        if (_settings.IsConfigured && _settings.StartCoreAutomatically)
        {
            StartRuntime();
        }
    }

    private void OnRuntimeSnapshotChanged(AgentRuntimeSnapshot snapshot)
    {
        _form.SetRuntimeSnapshot(snapshot);
        if (_form.IsHandleCreated)
        {
            _form.BeginInvoke(() =>
            {
                _tray.Text = TruncateTrayText($"Codex Control · {snapshot.Summary}");
                if (snapshot.CoreStatus == RuntimeCoreStatus.Failed && snapshot.LastError is not null)
                {
                    _tray.ShowBalloonTip(5000, "Codex Control 需要处理", snapshot.LastError, ToolTipIcon.Warning);
                }
            });
        }
    }

    private void OnPairingConfirmationRequested(PairingConfirmationRequestedPayload requested)
    {
        if (!_form.IsHandleCreated)
        {
            return;
        }

        _form.BeginInvoke(async () =>
        {
            _tray.ShowBalloonTip(
                5000,
                "新的控制端请求",
                $"{requested.ControllerName} 正在请求配对，请在 Codex Control 中确认。",
                ToolTipIcon.Info);
            var decision = await _form.ConfirmPairingAsync(requested).ConfigureAwait(true);
            try
            {
                if (_runtime is not null)
                {
                    await _runtime.ResolvePairingAsync(decision, CancellationToken.None).ConfigureAwait(true);
                    _form.RefreshPairingsAfterPairing();
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(_form, exception.Message, "配对失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
    }

    private async Task<bool> ValidateRelayAsync(string? relayRoot)
    {
        if (string.IsNullOrWhiteSpace(relayRoot))
        {
            MessageBox.Show(_form, "请填写 Relay 根地址。", "Codex Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(new Uri(new Uri(relayRoot + "/"), "healthz"))
                .ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            var health = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);
            if (!health.TryGetProperty("protocolVersion", out var version) ||
                version.GetInt32() != ProtocolVersion.Current)
            {
                MessageBox.Show(
                    _form,
                    "Relay 可访问，但协议版本与当前 Agent 不兼容。地址不会应用到运行连接。",
                    "Codex Control",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            var answer = MessageBox.Show(
                _form,
                $"Relay 当前不可达：{exception.Message}\n\n是否仍然离线保存并由 Agent 后台重连？",
                "Codex Control",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            return answer == DialogResult.Yes;
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (_runtime?.Snapshot.Codex.ActiveTurnId is not null)
        {
            var answer = MessageBox.Show(
                _form,
                "当前 Turn 正在运行。立即退出会中断任务。确定强制退出吗？",
                "Codex Control",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        _exiting = true;
        if (_runtime is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _runtime.StopAsync(timeout.Token).ConfigureAwait(true);
        }

        ExitThread();
    }

    private static string TruncateTrayText(string value) => value.Length <= 63 ? value : value[..63];
}
