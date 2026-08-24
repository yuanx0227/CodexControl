using System.Net;
using System.Net.WebSockets;
using System.Drawing;
using System.Text;
using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.Desktop;
using CodexControl.Agent.Lifecycle;
using CodexControl.Agent.Proxy;
using CodexControl.Agent.Relay;
using CodexControl.Agent.Security;
using CodexControl.Agent.State;
using CodexControl.Protocol;
using CodexControl.Relay;
using CodexControl.Relay.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodexControl.Agent.Tests;

internal static class TestRunner
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(4);

    public static async Task<int> RunAsync()
    {
        var allTests = new (string Name, Func<Task> Execute)[]
        {
            ("JsonRpcProtocol", TestJsonRpcProtocolAsync),
            ("AgentOptions_SecureRelay", TestAgentOptionsAsync),
            ("AgentSettings_AtomicRecovery", TestAgentSettingsAsync),
            ("ThreadHistory_Markdown_Image_Timing_Summary", TestRichThreadHistoryAsync),
            ("CodexStateManager", TestCodexStateManagerAsync),
            ("CodexDesktopRuntimeResolver", TestCodexDesktopRuntimeResolverAsync),
            ("CodexExecutableProbe", TestCodexExecutableProbeAsync),
            ("AppServerBridge_LocalWsProxy", TestBridgeAndProxyAsync),
            ("AgentRuntimeCoordinator_Lifecycle", TestRuntimeCoordinatorAsync),
            ("RemoteControl_ApprovalArbitration", TestRemoteControlAndApprovalAsync),
            ("AgentRelay_DPAPI_Authentication_Pairing", TestAgentRelayAsync),
            ("RealCodex_AppServerBridge", TestRealCodexAsync),
            ("RealCodex_Steer_Approval_Interrupt", TestRealCodexControlAsync),
            ("RealCodex_History_Create_Resume", TestRealCodexHistoryControlAsync),
        };
        var filter = Environment.GetEnvironmentVariable("CODEX_CONTROL_TEST_FILTER");
        var tests = string.IsNullOrWhiteSpace(filter)
            ? allTests
            : allTests.Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        var failed = 0;
        var skipped = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Execute().WaitAsync(TestTimeout).ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (SkipTestException exception)
            {
                skipped++;
                Console.WriteLine($"SKIP {test.Name}: {exception.Message}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
            }
        }

        Console.WriteLine(
            $"RESULT total={tests.Length} passed={tests.Length - failed - skipped} skipped={skipped} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static Task TestJsonRpcProtocolAsync()
    {
        using var request = JsonRpcProtocol.Parse("""
            {"method":"thread/list","id":7,"params":{}}
            """);
        Assert(JsonRpcProtocol.TryGetMethod(request.RootElement, out var method), "method should exist");
        Assert(method == "thread/list", "method should round-trip");
        Assert(JsonRpcProtocol.TryGetId(request.RootElement, out var id), "id should exist");

        using var resultDocument = JsonDocument.Parse("""{"data":[]}""");
        var responseJson = JsonRpcProtocol.BuildResultResponse(id, resultDocument.RootElement);
        using var response = JsonRpcProtocol.Parse(responseJson);
        Assert(response.RootElement.GetProperty("id").GetInt32() == 7, "numeric id must be preserved");

        using var collision = JsonRpcProtocol.Parse("""
            {"method":"thread/list","id":"bridge:collision","params":{}}
            """);
        Assert(JsonRpcProtocol.HasReservedBridgeId(collision.RootElement), "bridge prefix must be reserved");
        return Task.CompletedTask;
    }

    private static Task TestAgentOptionsAsync()
    {
        AssertThrows<AgentConfigurationException>(
            () => AgentOptions.Parse(["--relay-url", "ws://relay.example.invalid"]),
            "remote plaintext Relay must be rejected");
        AssertThrows<AgentConfigurationException>(
            () => AgentOptions.Parse(["--pair"]),
            "pairing mode requires a Relay URL");
        var development = AgentOptions.Parse(
        [
            "--relay-url", "ws://127.0.0.1:5080",
            "--allow-insecure-relay",
            "--pair",
        ]);
        Assert(development.RelayUrl?.Scheme == "ws" && development.CreatePairing,
            "explicit localhost development Relay should parse");
        return Task.CompletedTask;
    }

    private static Task TestAgentSettingsAsync()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var testDirectory = Path.GetFullPath(Path.Combine(
            tempRoot,
            $"codex-control-settings-{Guid.NewGuid():N}"));
        Assert(testDirectory.StartsWith(
            tempRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "settings test directory must stay under temp");
        try
        {
            var paths = AgentDataPaths.FromDataDirectory(Path.Combine(testDirectory, "data"), testDirectory);
            var store = new AgentSettingsStore(paths);
            var defaults = store.Load();
            Assert(defaults.Source == SettingsLoadSource.Defaults, "missing settings should load defaults");

            var first = store.Save(AgentSettings.Default with
            {
                DeviceName = "Settings Test PC",
                RelayRootUrl = "https://control.example.com",
            });
            Assert(first.RelayRootUrl == "https://control.example.com", "Relay root should normalize");
            Assert(File.Exists(paths.SettingsPath), "primary settings should be persisted");
            Assert(File.Exists(paths.LastGoodSettingsPath), "last-good settings should be persisted");

            _ = store.Save(first with { DeviceName = "Second Name" });
            File.WriteAllText(paths.SettingsPath, "{invalid-json");
            var recovered = store.Load();
            Assert(recovered.Source == SettingsLoadSource.LastGood, "invalid primary should recover last-good");
            Assert(recovered.Settings.DeviceName == "Settings Test PC", "recovery should use previous valid settings");
            Assert(recovered.QuarantinedPath is not null && File.Exists(recovered.QuarantinedPath),
                "invalid settings should be quarantined");

            var options = AgentOptions.FromSettings(recovered.Settings, paths);
            Assert(options.Port == 0, "automatic local port should bind using port zero");
            Assert(options.DataDirectory == paths.DataDirectory, "settings should use install data directory");
            Assert(options.LogDirectory == paths.LogDirectory, "logs should stay below data directory");
            var stateStore = new AgentStateStore(paths);
            var queued = stateStore.AddRevocation(42);
            Assert(queued.PendingPairingRevocations.Count == 1 &&
                   queued.PendingPairingRevocations[0].PairingId == 42,
                "offline Pairing revocation should be persisted");
            Assert(new AgentStateStore(paths).Load().PendingPairingRevocations.Count == 1,
                "pending revocation should survive process recreation");
            Assert(stateStore.RemoveRevocation(42).PendingPairingRevocations.Count == 0,
                "acknowledged revocation should be removed");
            string deviceId;
            string publicKey;
            using (var identity = DeviceIdentity.LoadOrCreate(paths.DataDirectory, "Original Device"))
            {
                deviceId = identity.DeviceId;
                publicKey = identity.PublicKey;
            }
            using (var renamed = DeviceIdentity.LoadOrCreate(paths.DataDirectory, "Renamed Device"))
            {
                Assert(renamed.DeviceId == deviceId && renamed.PublicKey == publicKey,
                    "renaming a Device must not rotate its identity");
                Assert(renamed.Name == "Renamed Device", "renamed Device metadata should be persisted");
            }
            AssertThrows<AgentConfigurationException>(
                () => (AgentSettings.Default with { RelayRootUrl = "http://relay.example.com" }).Validate(),
                "remote plaintext Relay root must be rejected");
            Assert((AgentSettings.Default with { RelayRootUrl = "http://127.0.0.1:5080" })
                .Validate().RelayRootUrl == "http://127.0.0.1:5080",
                "loopback HTTP Relay root should be accepted for development");
            using var qr = PairingQrRenderer.Render(
                "https://control.example.com/#/pair?relay=ZXhhbXBsZQ&code=123456");
            Assert(qr.Width > 100 && qr.Height == qr.Width,
                "pairing QR should render locally as a square bitmap");
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static async Task TestRuntimeCoordinatorAsync()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var testDirectory = Path.GetFullPath(Path.Combine(
            tempRoot,
            $"codex-control-runtime-{Guid.NewGuid():N}"));
        Assert(testDirectory.StartsWith(
            tempRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "runtime test directory must stay under temp");
        try
        {
            var paths = AgentDataPaths.FromDataDirectory(Path.Combine(testDirectory, "data"), testDirectory);
            paths.EnsureWritable();
            var options = AgentOptions.ForTests(GetTestExecutablePath(), paths.LogDirectory, port: 0) with
            {
                DataDirectory = paths.DataDirectory,
            };
            await using var runtime = new AgentRuntimeCoordinator(options, paths, remoteAccessPaused: false);
            runtime.Start();
            await WaitUntilAsync(
                () => runtime.Snapshot.CoreStatus == RuntimeCoreStatus.Ready,
                TestTimeout).ConfigureAwait(false);
            var first = runtime.Snapshot;
            var proxyUri = first.LocalProxyUri ??
                           throw new InvalidOperationException("runtime should expose the actual proxy URI");
            Assert(proxyUri.IsLoopback, "runtime should expose a loopback proxy URI");
            Assert(proxyUri.Port > 0, "automatic port should resolve to a real port");

            await runtime.PauseRemoteAccessAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(runtime.Snapshot.RelayStatus == RuntimeRelayStatus.Paused,
                "pause should be independent from the local Codex core");
            Assert(runtime.Snapshot.CoreStatus == RuntimeCoreStatus.Ready,
                "pause should not stop the local Codex core");

            var revision = runtime.Snapshot.Revision;
            Assert(runtime.RequestRestart(force: true), "forced restart should be scheduled immediately");
            await WaitUntilAsync(
                () => runtime.Snapshot.CoreStatus == RuntimeCoreStatus.Ready &&
                      runtime.Snapshot.Revision > revision + 2,
                TestTimeout).ConfigureAwait(false);

            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await runtime.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            Assert(runtime.Snapshot.CoreStatus == RuntimeCoreStatus.Stopped,
                "runtime should publish a stopped terminal state");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static Task TestRichThreadHistoryAsync()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var testDirectory = Path.GetFullPath(Path.Combine(
            tempRoot,
            $"codex-control-rich-history-{Guid.NewGuid():N}"));
        Assert(testDirectory.StartsWith(
            tempRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "rich history directory must stay under temp");
        Directory.CreateDirectory(testDirectory);
        try
        {
            var imagePath = Path.Combine(testDirectory, "history-image.png");
            using (var bitmap = new Bitmap(80, 50))
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.DarkSlateBlue);
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
            }

            using var items = JsonDocument.Parse(
                $$"""
                {
                  "data":[
                    {
                      "turnId":"turn-rich",
                      "item":{
                        "id":"item-user-rich",
                        "type":"userMessage",
                        "content":[
                          {"type":"text","text":"## 标题\n\n- 第一项\n- 第二项"},
                          {"type":"localImage","path":{{JsonSerializer.Serialize(imagePath)}}}
                        ]
                      }
                    },
                    {
                      "turnId":"turn-rich",
                      "item":{
                        "id":"item-command-rich",
                        "type":"commandExecution",
                        "command":"dotnet test",
                        "status":"completed"
                      }
                    },
                    {
                      "turnId":"turn-rich",
                      "item":{
                        "id":"item-file-rich",
                        "type":"fileChange",
                        "status":"completed",
                        "changes":[{
                          "path":"src/App.cs",
                          "kind":"update",
                          "diff":"--- a/src/App.cs\n+++ b/src/App.cs\n@@ -10,2 +10,3 @@\n-old\n+new\n+added"
                        }]
                      }
                    }
                  ],
                  "nextCursor":null
                }
                """);
            var mapped = CodexThreadHistoryMapper.MapItemsPage(items.RootElement);
            var user = mapped.Entries.Single(value => value.Role == "user");
            var tool = mapped.Entries.Single(value => value.Text == "dotnet test");
            var fileChange = mapped.Entries.Single(value => value.Changes.Count > 0);
            Assert(user.Text.Contains("## 标题", StringComparison.Ordinal),
                "markdown source should be preserved for the PWA renderer");
            Assert(user.Attachments.Count == 1 &&
                   user.Attachments[0].DataUrl.StartsWith("data:image/", StringComparison.Ordinal) &&
                   user.Attachments[0].DataUrl.Length < CodexImageAttachmentMapper.MaxTotalDataUrlLength,
                "local image should become a bounded inline attachment");
            Assert(!JsonSerializer.Serialize(user, RelayJson.Options).Contains(imagePath, StringComparison.Ordinal),
                "attachment DTO must not disclose the absolute local path");
            Assert(tool.Text == "dotnet test" && tool.Phase == "completed",
                "command history should be available to the folded process summary");
            Assert(fileChange.Changes.Single().Additions == 2 &&
                   fileChange.Changes.Single().Deletions == 1 &&
                   fileChange.Text.Contains("+2 -1", StringComparison.Ordinal),
                "file history should preserve per-file addition and deletion counts");

            using var liveFileChange = JsonDocument.Parse("""
                {"method":"item/completed","params":{"threadId":"thr-rich","turnId":"turn-rich","item":{
                  "id":"item-file-live","type":"fileChange","status":"completed","changes":[{
                    "path":"src/Live.cs","kind":"add","diff":"@@ -0,0 +1,2 @@\n+one\n+two"
                  }]}}}
                """);
            var liveEvent = DomainEventNormalizer.Normalize(
                liveFileChange.RootElement,
                new CodexStateManager().Snapshot);
            Assert(liveEvent?.Kind == "FileChanged" &&
                   liveEvent.Data.GetProperty("changes")[0].GetProperty("additions").GetInt32() == 2,
                "live file events should include line impact statistics");

            using var turns = JsonDocument.Parse("""
                {
                  "data":[{
                    "id":"turn-rich",
                    "status":"completed",
                    "startedAt":1730831000,
                    "completedAt":1730831096,
                    "durationMs":96000,
                    "items":[]
                  }],
                  "nextCursor":null
                }
                """);
            var timing = CodexThreadHistoryMapper.MapTurnsPage(turns.RootElement).Turns.Single();
            Assert(timing.Status == "completed" &&
                   timing.DurationMs == 96_000 && timing.StartedAt == 1_730_831_000,
                "turn timing should preserve app-server status and duration metadata");
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static Task TestCodexStateManagerAsync()
    {
        var state = new CodexStateManager();
        state.MarkStarting();
        state.MarkIdle();
        Apply(state, """
            {"method":"turn/started","params":{"threadId":"thr-1","turn":{"id":"turn-1"}}}
            """);
        Assert(state.Snapshot.Status == CodexActivityStatus.Thinking, "turn/start should enter Thinking");
        Assert(state.Snapshot.ActiveThreadId == "thr-1", "thread id should be captured");
        Assert(state.Snapshot.ActiveTurnId == "turn-1", "turn id should be captured");

        Apply(state, """
            {"method":"item/started","params":{"item":{"type":"commandExecution","command":"dotnet test"}}}
            """);
        Assert(state.Snapshot.Status == CodexActivityStatus.RunningTests, "test command should enter RunningTests");

        Apply(state, """
            {"method":"item/commandExecution/requestApproval","id":"approval\u002D1","params":{}}
            """);
        Assert(state.Snapshot.Status == CodexActivityStatus.WaitingApproval, "approval should block the state");
        Assert(state.Snapshot.PendingApprovalCount == 1, "approval count should increment");

        using (var response = JsonRpcProtocol.Parse("""{"id":"approval-1","result":{"decision":"accept"}}"""))
        {
            state.ApplyClientMessage(response.RootElement);
        }

        Assert(state.Snapshot.PendingApprovalCount == 0, "approval response should resolve the pending request");
        Apply(state, """
            {"method":"turn/completed","params":{"turn":{"id":"turn-1","status":"interrupted"}}}
            """);
        Assert(state.Snapshot.Status == CodexActivityStatus.Interrupted, "terminal interrupted event is authoritative");

        Apply(state, """
            {"method":"turn/started","params":{"threadId":"thr-a","turn":{"id":"turn-a"}}}
            """);
        Apply(state, """
            {"method":"turn/started","params":{"threadId":"thr-b","turn":{"id":"turn-b"}}}
            """);
        Assert(state.Snapshot.ActiveTurns.Count == 2, "two threads should remain independently active");
        Apply(state, """
            {"method":"item/started","params":{"threadId":"thr-a","turnId":"turn-a","item":{"type":"commandExecution","command":"dotnet test"}}}
            """);
        Assert(
            state.Snapshot.ActiveTurns.Single(turn => turn.ThreadId == "thr-a").Status ==
            CodexActivityStatus.RunningTests,
            "thread-scoped activity should not overwrite the other active turn");
        Apply(state, """
            {"method":"turn/completed","params":{"threadId":"thr-a","turn":{"id":"turn-a","status":"completed"}}}
            """);
        Assert(
            state.Snapshot.ActiveTurns.Count == 1 &&
            state.Snapshot.ActiveThreadId == "thr-b" &&
            state.Snapshot.ActiveTurnId == "turn-b",
            "completing one thread should leave the other thread active");
        Apply(state, """
            {"method":"turn/completed","params":{"threadId":"thr-b","turn":{"id":"turn-b","status":"completed"}}}
            """);
        Assert(state.Snapshot.ActiveTurns.Count == 0, "all active turns should clear independently");
        return Task.CompletedTask;
    }

    private static async Task TestCodexExecutableProbeAsync()
    {
        var probe = await CodexExecutableProbe.ProbeAsync(
            GetTestExecutablePath(),
            CancellationToken.None).ConfigureAwait(false);
        Assert(probe.Version == "codex-cli 0.0.0-fake", "fake version should be observed");
        Assert(probe.SupportsRemote, "--remote should be confirmed");
        Assert(probe.SupportsAppServerStdio, "stdio app-server should be confirmed");
    }

    private static async Task TestCodexDesktopRuntimeResolverAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-desktop-runtime-tests-", Guid.NewGuid().ToString("N")));
        var sourceDirectory = Path.Combine(
            testRoot,
            "OpenAI.Codex_99.1.2.3_x64__test",
            "app",
            "resources");
        var dataDirectory = Path.Combine(testRoot, "data");
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            foreach (var fileName in new[]
                     {
                         "codex.exe",
                         "codex-code-mode-host.exe",
                         "codex-command-runner.exe",
                         "codex-windows-sandbox-setup.exe",
                     })
            {
                File.Copy(GetTestExecutablePath(), Path.Combine(sourceDirectory, fileName));
            }

            var sourceExecutable = Path.Combine(sourceDirectory, "codex.exe");
            var stages = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                CodexRuntimeResolver.StageDesktopRuntimeAsync(
                    sourceExecutable,
                    dataDirectory,
                    CancellationToken.None))).ConfigureAwait(false);
            Assert(
                stages.Select(value => value.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
                "concurrent staging should converge on one versioned runtime");
            Assert(stages.Any(value => value.WasStaged), "one concurrent caller should stage the runtime");
            Assert(File.Exists(stages[0].ExecutablePath), "staged codex executable should exist");
            Assert(
                Directory.GetDirectories(
                    Path.Combine(dataDirectory, "codex-runtimes", "desktop"),
                    "*.staging-*",
                    SearchOption.TopDirectoryOnly).Length == 0,
                "staging directories should be cleaned");

            var reused = await CodexRuntimeResolver.StageDesktopRuntimeAsync(
                sourceExecutable,
                dataDirectory,
                CancellationToken.None).ConfigureAwait(false);
            Assert(!reused.WasStaged, "completed desktop runtime should be reused");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task TestBridgeAndProxyAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-agent-tests-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);

        try
        {
            var options = AgentOptions.ForTests(GetTestExecutablePath(), Path.Combine(testRoot, "logs"));
            using var log = new AgentLog(options.LogDirectory, maxBytes: 64 * 1024, retainedFiles: 2);
            var state = new CodexStateManager();
            await using var bridge = new AppServerBridge(options, log, state);
            await using var proxy = new LocalCodexProxyServer(options, bridge, log);

            await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(bridge.IsReady, "bridge should complete initialize/initialized");
            Assert(
                bridge.InitializeResult.GetProperty("userAgent").GetString() == "Codex Fake/0.0.0",
                "bridge should cache initialize result");

            await proxy.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(proxy.BoundPort is > 0 and <= 65535, "dynamic loopback port should be resolved");
            Assert(proxy.WebSocketUri.Host == "127.0.0.1", "proxy must publish IPv4 loopback only");

            using (var http = new HttpClient())
            {
                var readyUri = new Uri($"http://127.0.0.1:{proxy.BoundPort}/readyz");
                using var ready = await http.GetAsync(readyUri).ConfigureAwait(false);
                Assert(ready.StatusCode == HttpStatusCode.OK, "readyz should return 200 after bridge init");
            }

            await AssertOriginRejectedAsync(proxy.WebSocketUri).ConfigureAwait(false);
            await AssertCapabilityRejectedAsync(proxy.WebSocketUri).ConfigureAwait(false);

            using var client = new ClientWebSocket();
            await client.ConnectAsync(proxy.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
            await SendAsync(client, """
                {"method":"initialize","id":7,"params":{"clientInfo":{"name":"test-tui","version":"0.1.0"},"capabilities":{"experimentalApi":true}}}
                """).ConfigureAwait(false);
            using (var initializeResponse = await ReceiveResponseAsync(client, 7).ConfigureAwait(false))
            {
                Assert(
                    initializeResponse.RootElement.GetProperty("result").GetProperty("userAgent").GetString() ==
                    "Codex Fake/0.0.0",
                    "proxy should synthesize TUI initialize response from cached app-server result");
            }

            await SendAsync(client, """{"method":"initialized"}""").ConfigureAwait(false);
            await SendAsync(client, """{"method":"thread/list","id":8,"params":{"limit":1}}""")
                .ConfigureAwait(false);
            using (var threadList = await ReceiveResponseAsync(client, 8).ConfigureAwait(false))
            {
                Assert(
                    threadList.RootElement.GetProperty("result").GetProperty("data").GetArrayLength() == 0,
                    "ordinary TUI request should round-trip through stdio app-server");
            }

            using (var secondClient = new ClientWebSocket())
            {
                await AssertThrowsAsync<WebSocketException>(
                    () => secondClient.ConnectAsync(proxy.WebSocketUri, CancellationToken.None),
                    "second TUI must be rejected while first is connected").ConfigureAwait(false);
            }

            await client.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "test complete",
                CancellationToken.None).ConfigureAwait(false);
            await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(state.Snapshot.Status == CodexActivityStatus.Offline, "shutdown should leave Offline snapshot");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task TestRealCodexAsync()
    {
        var codexPath = Environment.GetEnvironmentVariable("CODEX_CONTROL_REAL_CODEX_PATH");
        if (string.IsNullOrWhiteSpace(codexPath))
        {
            throw new SkipTestException("CODEX_CONTROL_REAL_CODEX_PATH is not set");
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-real-codex-tests-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);

        try
        {
            var probe = await CodexExecutableProbe.ProbeAsync(codexPath, CancellationToken.None)
                .ConfigureAwait(false);
            Assert(probe.SupportsRemote, "real Codex must support --remote");
            Assert(probe.SupportsAppServerStdio, "real Codex must support app-server stdio");

            var options = AgentOptions.ForTests(codexPath, Path.Combine(testRoot, "logs"));
            using var log = new AgentLog(options.LogDirectory, maxBytes: 64 * 1024, retainedFiles: 2);
            var state = new CodexStateManager();
            await using var bridge = new AppServerBridge(options, log, state);
            await using var proxy = new LocalCodexProxyServer(options, bridge, log);

            await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await proxy.StartAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(
                bridge.InitializeResult.GetProperty("platformOs").GetString() == "windows",
                "real app-server should report windows");
            var dispatcher = new RemoteControlDispatcher(bridge, state);
            var sessionOptions = await dispatcher.GetSessionOptionsAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var sessionOptionsResult = sessionOptions.Result ??
                                       throw new InvalidOperationException("real session options result is missing");
            var sessionOptionsPayload = sessionOptionsResult.Deserialize<CodexSessionOptionsPayload>(
                                            RelayJson.Options) ??
                                        throw new InvalidOperationException("real session options should deserialize");
            Assert(
                sessionOptions.Succeeded && sessionOptionsPayload.Models.Count > 0,
                "real model/list should provide at least one selectable model");

            using var client = new ClientWebSocket();
            await client.ConnectAsync(proxy.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
            await SendAsync(client, """
                {"method":"initialize","id":101,"params":{"clientInfo":{"name":"codex_control_real_probe","version":"0.1.0"},"capabilities":{"experimentalApi":true}}}
                """).ConfigureAwait(false);
            using (var initializeResponse = await ReceiveResponseAsync(client, 101).ConfigureAwait(false))
            {
                Assert(
                    initializeResponse.RootElement.GetProperty("result").GetProperty("platformFamily").GetString() ==
                    "windows",
                    "proxy should return real cached initialize result");
            }

            await SendAsync(client, """{"method":"initialized"}""").ConfigureAwait(false);
            await SendAsync(client, """{"method":"thread/list","id":102,"params":{"limit":1}}""")
                .ConfigureAwait(false);
            using (var threadList = await ReceiveResponseAsync(client, 102).ConfigureAwait(false))
            {
                Assert(
                    threadList.RootElement.TryGetProperty("result", out _),
                    "real thread/list should return a result");
            }

            await client.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "real probe complete",
                CancellationToken.None).ConfigureAwait(false);
            await proxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task TestRealCodexControlAsync()
    {
        var codexPath = Environment.GetEnvironmentVariable("CODEX_CONTROL_REAL_CODEX_PATH");
        if (string.IsNullOrWhiteSpace(codexPath) ||
            Environment.GetEnvironmentVariable("CODEX_CONTROL_RUN_REAL_TURNS") != "1")
        {
            throw new SkipTestException(
                "CODEX_CONTROL_REAL_CODEX_PATH and CODEX_CONTROL_RUN_REAL_TURNS=1 are required");
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-real-turn-tests-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        var forbiddenFile = Path.Combine(
            @"D:\Yuanx\Code\Ai\CodexControl\out",
            string.Concat("approval-probe-", Guid.NewGuid().ToString("N"), ".txt"));

        try
        {
            var options = AgentOptions.ForTests(codexPath, Path.Combine(testRoot, "logs"));
            using var log = new AgentLog(options.LogDirectory);
            var state = new CodexStateManager();
            await using var bridge = new AppServerBridge(options, log, state);
            await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var dispatcher = new RemoteControlDispatcher(bridge, state);
            var observedMethods = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var agentText = new System.Text.StringBuilder();
            var turnCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.ServerMessageReceived += message =>
            {
                if (JsonRpcProtocol.TryGetMethod(message, out var method))
                {
                    observedMethods.Enqueue(method);
                    if (method == "item/agentMessage/delta" &&
                        message.TryGetProperty("params", out var parameters) &&
                        parameters.TryGetProperty("delta", out var delta) &&
                        delta.ValueKind == JsonValueKind.String)
                    {
                        agentText.Append(delta.GetString());
                    }

                    if (method == "turn/completed")
                    {
                        turnCompleted.TrySetResult(true);
                    }
                }
            };
            var approvalCompletion = new TaskCompletionSource<PendingApprovalSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.Approvals.ApprovalRequested += approval => approvalCompletion.TrySetResult(approval);

            var threadResult = await bridge.SendRequestAsync(
                "thread/start",
                new
                {
                    cwd = "D:\\Yuanx\\Code\\Ai\\CodexControl",
                    ephemeral = true,
                    approvalPolicy = "untrusted",
                    sandbox = "workspace-write",
                },
                TimeSpan.FromSeconds(30),
                CancellationToken.None).ConfigureAwait(false);
            var threadId = threadResult.GetProperty("thread").GetProperty("id").GetString() ??
                           throw new InvalidOperationException("thread/start did not return an ID");
            var turnResult = await bridge.SendRequestAsync(
                "turn/start",
                new
                {
                    threadId,
                    approvalPolicy = "untrusted",
                    input = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Run exactly this shell command now and do not use apply_patch: & \"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -NoProfile -Command 'Set-Content -LiteralPath \"{forbiddenFile}\" -Value APPROVAL_TEST'. Do not merely explain; invoke the shell tool so the client can test Command Approval.",
                        },
                    },
                },
                TimeSpan.FromSeconds(30),
                CancellationToken.None).ConfigureAwait(false);
            var turnId = turnResult.GetProperty("turn").GetProperty("id").GetString() ??
                         throw new InvalidOperationException("turn/start did not return an ID");
            await WaitUntilAsync(
                () => state.Snapshot.ActiveTurnId == turnId,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            PendingApprovalSnapshot approval;
            try
            {
                var first = await Task.WhenAny(
                    approvalCompletion.Task,
                    turnCompleted.Task,
                    Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false);
                if (ReferenceEquals(first, turnCompleted.Task))
                {
                    throw new InvalidOperationException(
                        $"Real turn completed without approval. Agent message: {agentText}");
                }

                if (!ReferenceEquals(first, approvalCompletion.Task))
                {
                    throw new TimeoutException("Approval wait timed out.");
                }

                approval = await approvalCompletion.Task.ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                if (state.Snapshot.ActiveTurnId == turnId)
                {
                    _ = await dispatcher.InterruptAsync(threadId, turnId, CancellationToken.None).ConfigureAwait(false);
                }

                throw new TimeoutException(
                    $"No real approval arrived. Methods: {string.Join(',', observedMethods.Distinct())}",
                    exception);
            }

            var steer = await dispatcher.SteerAsync(
                threadId,
                turnId,
                "Keep the same target. After this approval is resolved, stop further work.",
                CancellationToken.None).ConfigureAwait(false);
            Assert(steer.Succeeded, "real turn/steer should be accepted while approval is pending");
            var declineDecision = approval.AvailableDecisions.FirstOrDefault(decision =>
                decision.ValueKind == JsonValueKind.String && decision.GetString() == "decline");
            if (declineDecision.ValueKind == JsonValueKind.Undefined)
            {
                declineDecision = approval.AvailableDecisions.FirstOrDefault(decision =>
                    decision.ValueKind == JsonValueKind.String && decision.GetString() == "cancel");
            }

            if (declineDecision.ValueKind == JsonValueKind.Undefined)
            {
                if (approval.AvailableDecisions.Count == 0)
                {
                    declineDecision = JsonSerializer.SerializeToElement("decline");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Real approval did not offer a deny decision: {string.Join(',', approval.AvailableDecisions.Select(value => value.GetRawText()))}");
                }
            }

            var decline = await dispatcher.ResolveApprovalAsync(
                approval.ApprovalId,
                declineDecision,
                "real-test-controller",
                CancellationToken.None).ConfigureAwait(false);
            Assert(
                decline.Succeeded,
                $"real approval decline should reach app-server: {decline.ErrorCode} {decline.ErrorMessage}");
            await WaitUntilAsync(
                () => state.Snapshot.Status is CodexActivityStatus.Completed or
                    CodexActivityStatus.Interrupted or
                    CodexActivityStatus.Failed,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert(!File.Exists(forbiddenFile), "declined real approval must not create the file");

            var interruptTurnResult = await bridge.SendRequestAsync(
                "turn/start",
                new
                {
                    threadId,
                    approvalPolicy = "never",
                    input = new[]
                    {
                        new
                        {
                            type = "text",
                            text = "Read the repository and prepare a long, exhaustive analysis. Do not finish immediately.",
                        },
                    },
                },
                TimeSpan.FromSeconds(30),
                CancellationToken.None).ConfigureAwait(false);
            var interruptTurnId = interruptTurnResult.GetProperty("turn").GetProperty("id").GetString() ??
                                  throw new InvalidOperationException("second turn/start did not return an ID");
            await WaitUntilAsync(
                () => state.Snapshot.ActiveTurnId == interruptTurnId,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var interrupt = await dispatcher.InterruptAsync(
                threadId,
                interruptTurnId,
                CancellationToken.None).ConfigureAwait(false);
            Assert(interrupt.Succeeded, "real turn/interrupt should be accepted");
            await WaitUntilAsync(
                () => state.Snapshot.Status == CodexActivityStatus.Interrupted,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(forbiddenFile))
            {
                File.Delete(forbiddenFile);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task TestRealCodexHistoryControlAsync()
    {
        var codexPath = Environment.GetEnvironmentVariable("CODEX_CONTROL_REAL_CODEX_PATH");
        var runRealTurns = Environment.GetEnvironmentVariable("CODEX_CONTROL_RUN_REAL_TURNS");
        if (string.IsNullOrWhiteSpace(codexPath) || runRealTurns != "1")
        {
            throw new SkipTestException(
                "CODEX_CONTROL_REAL_CODEX_PATH and CODEX_CONTROL_RUN_REAL_TURNS=1 are required");
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-real-history-tests-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        string? createdThreadId = null;
        string? activeTurnId = null;
        var completedTurns = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(
            StringComparer.Ordinal);
        var options = AgentOptions.ForTests(codexPath, Path.Combine(testRoot, "logs"));
        using var log = new AgentLog(options.LogDirectory, maxBytes: 128 * 1024, retainedFiles: 2);
        var state = new CodexStateManager();
        await using var bridge = new AppServerBridge(options, log, state);
        var dispatcher = new RemoteControlDispatcher(bridge, state);
        try
        {
            bridge.ServerMessageReceived += message =>
            {
                if (!JsonRpcProtocol.TryGetMethod(message, out var method) || method != "turn/completed" ||
                    !message.TryGetProperty("params", out var parameters) ||
                    !parameters.TryGetProperty("turn", out var turn) ||
                    !turn.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(id.GetString()))
                {
                    return;
                }

                completedTurns.TryAdd(id.GetString()!, 0);
            };
            await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var initialHistory = await dispatcher.ListThreadsAsync(
                50,
                null,
                CancellationToken.None).ConfigureAwait(false);
            Assert(initialHistory.Succeeded, "real thread/list should succeed through remote dispatcher");

            var created = await dispatcher.StartThreadAsync(
                testRoot,
                "Reply with exactly REMOTE_CREATE_OK and do not call tools.",
                CancellationToken.None).ConfigureAwait(false);
            Assert(
                created.Succeeded && created.Result is not null,
                $"real remote thread creation should succeed: {created.ErrorCode} {created.ErrorMessage}");
            var createdResult = created.Result ?? throw new InvalidOperationException("real create result missing");
            var createdPayload = createdResult.Deserialize<CodexThreadActionResultPayload>(RelayJson.Options) ??
                                 throw new InvalidOperationException("real create payload should deserialize");
            createdThreadId = createdPayload.ThreadId;
            activeTurnId = createdPayload.TurnId;
            await WaitUntilAsync(
                () => completedTurns.ContainsKey(createdPayload.TurnId),
                TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            await WaitUntilAsync(
                () => state.Snapshot.ActiveTurnId is null,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            activeTurnId = null;

            var updatedHistory = await dispatcher.ListThreadsAsync(
                100,
                null,
                CancellationToken.None).ConfigureAwait(false);
            var updatedResult = updatedHistory.Result ??
                                throw new InvalidOperationException("updated real history result missing");
            var updatedPayload = updatedResult.Deserialize<CodexThreadListResultPayload>(RelayJson.Options) ??
                                 throw new InvalidOperationException("updated real history should deserialize");
            Assert(
                updatedPayload.Threads.Any(thread => thread.ThreadId == createdThreadId),
                "new appServer thread should appear in real history list");
            Assert(
                updatedPayload.Projects.Select(project => project.Position)
                    .SequenceEqual(updatedPayload.Projects.Select(project => project.Position).Order()),
                "project/list results should preserve Codex position order when available");
            Assert(
                updatedPayload.Threads.Any(thread => thread.RecencyAt is not null),
                "real thread/list should preserve recencyAt for Desktop-compatible task ordering");

            var resumed = await dispatcher.ResumeThreadAsync(
                createdThreadId,
                "Reply with exactly REMOTE_RESUME_OK and do not call tools.",
                CancellationToken.None).ConfigureAwait(false);
            Assert(
                resumed.Succeeded && resumed.Result is not null,
                $"real historical thread resume should succeed: {resumed.ErrorCode} {resumed.ErrorMessage}");
            var resumedResult = resumed.Result ?? throw new InvalidOperationException("real resume result missing");
            var resumedPayload = resumedResult.Deserialize<CodexThreadActionResultPayload>(RelayJson.Options) ??
                                 throw new InvalidOperationException("real resume payload should deserialize");
            Assert(resumedPayload.ThreadId == createdThreadId, "resume should continue the selected real thread");
            activeTurnId = resumedPayload.TurnId;
            await WaitUntilAsync(
                () => completedTurns.ContainsKey(resumedPayload.TurnId),
                TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            await WaitUntilAsync(
                () => state.Snapshot.ActiveTurnId is null,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            activeTurnId = null;

            var read = await dispatcher.ReadThreadAsync(
                createdThreadId,
                CancellationToken.None).ConfigureAwait(false);
            Assert(
                read.Succeeded && read.Result is not null,
                $"real thread/read should succeed: {read.ErrorCode} {read.ErrorMessage}");
            var readResult = read.Result ?? throw new InvalidOperationException("real thread/read result missing");
            var readPayload = readResult.Deserialize<CodexThreadReadResultPayload>(RelayJson.Options) ??
                              throw new InvalidOperationException("real thread/read payload should deserialize");
            Assert(
                readPayload.ThreadId == createdThreadId &&
                readPayload.Entries.Any(entry => entry.Role == "user" && entry.Text.Contains("REMOTE_CREATE_OK", StringComparison.Ordinal)) &&
                readPayload.Entries.Any(entry => entry.Role == "assistant" && entry.Text.Contains("REMOTE_RESUME_OK", StringComparison.Ordinal)),
                "real thread/read should return persisted user and assistant messages");
        }
        finally
        {
            if (activeTurnId is not null && createdThreadId is not null)
            {
                _ = await dispatcher.InterruptAsync(
                    createdThreadId,
                    activeTurnId,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (createdThreadId is not null && bridge.IsReady)
            {
                try
                {
                    _ = await bridge.SendRequestAsync(
                        "thread/delete",
                        new { threadId = createdThreadId },
                        TimeSpan.FromSeconds(30),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is AppServerRpcException or TimeoutException or AgentException)
                {
                    throw new InvalidOperationException(
                        $"Failed to delete real history test thread {createdThreadId}.",
                        exception);
                }
            }

            await bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
            log.Dispose();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task TestRemoteControlAndApprovalAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-dispatcher-tests-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);

        try
        {
            var options = AgentOptions.ForTests(GetTestExecutablePath(), Path.Combine(testRoot, "logs"));
            using var log = new AgentLog(options.LogDirectory);
            var state = new CodexStateManager();
            await using var bridge = new AppServerBridge(options, log, state);
            await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var dispatcher = new RemoteControlDispatcher(bridge, state);

            var sessionOptions = await dispatcher.GetSessionOptionsAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var sessionOptionsResult = sessionOptions.Result ??
                                       throw new InvalidOperationException("session options result is missing");
            var sessionOptionsPayload = sessionOptionsResult.Deserialize<CodexSessionOptionsPayload>(
                                            RelayJson.Options) ??
                                        throw new InvalidOperationException("session options should deserialize");
            Assert(
                sessionOptions.Succeeded &&
                sessionOptionsPayload.Models.Count == 2 &&
                sessionOptionsPayload.Models.Single(model => model.IsDefault).Model == "gpt-5.6-sol" &&
                sessionOptionsPayload.ApprovalPolicies.Select(option => option.Id)
                    .SequenceEqual(["untrusted", "on-request", "never"]),
                "session options should map model/list and the supported approval policies");

            Apply(state, """
                {"method":"turn/started","params":{"threadId":"thr-1","turn":{"id":"turn-1"}}}
                """);
            var steer = await dispatcher.SteerAsync(
                "thr-1",
                "turn-1",
                "focus on the failing test",
                CancellationToken.None).ConfigureAwait(false);
            Assert(steer.Succeeded, "steer should be routed through an internal bridge request");
            Assert(steer.Result?.GetProperty("turnId").GetString() == "turn-1", "steer should return active turn");

            var interrupt = await dispatcher.InterruptAsync(
                "thr-1",
                "turn-1",
                CancellationToken.None).ConfigureAwait(false);
            Assert(interrupt.Succeeded, "interrupt RPC should be accepted");
            await WaitUntilAsync(
                () => state.Snapshot.Status == CodexActivityStatus.Interrupted,
                TestTimeout).ConfigureAwait(false);

            var history = await dispatcher.ListThreadsAsync(
                50,
                null,
                CancellationToken.None).ConfigureAwait(false);
            Assert(history.Succeeded && history.Result is not null, "history list should use real app-server RPC");
            var historyResult = history.Result ?? throw new InvalidOperationException("history result is missing");
            var historyPayload = historyResult.Deserialize<CodexThreadListResultPayload>(RelayJson.Options) ??
                                 throw new InvalidOperationException("history payload should deserialize");
            Assert(historyPayload.Threads.Count == 1, "history list should normalize stored threads");
            Assert(
                historyPayload.Threads[0].ThreadId == "thr-history-1" &&
                historyPayload.Threads[0].SourceKind == "appServer" &&
                historyPayload.Threads[0].ProjectId == "project-mes",
                "history metadata and project assignment should be preserved");
            Assert(
                historyPayload.Projects.Count == 2 &&
                historyPayload.Projects[0].ProjectId == "project-vision" &&
                historyPayload.Projects[1].ProjectId == "project-mes" &&
                historyPayload.Projects[1].Roots.Single() == "D:\\Projects\\MES",
                "project/list position and roots should be preserved");

            var threadRead = await dispatcher.ReadThreadAsync(
                "thr-history-1",
                CancellationToken.None).ConfigureAwait(false);
            Assert(threadRead.Succeeded && threadRead.Result is not null, "thread/read should load stored messages");
            var threadReadResult = threadRead.Result ?? throw new InvalidOperationException("thread/read result is missing");
            var threadReadPayload = threadReadResult.Deserialize<CodexThreadReadResultPayload>(RelayJson.Options) ??
                                    throw new InvalidOperationException("thread/read payload should deserialize");
            Assert(
                threadReadPayload.Entries.Count == 3 &&
                threadReadPayload.Entries.Single(entry => entry.Role == "user").Text == "修复登录模块" &&
                threadReadPayload.Entries.Single(entry => entry.Role == "assistant").Text == "登录模块已修复" &&
                threadReadPayload.Entries.Single(entry => entry.Changes.Count > 0).Changes.Single().Additions == 2 &&
                threadReadPayload.Entries.Single(entry => entry.Changes.Count > 0).Changes.Single().Deletions == 1,
                "thread/read should normalize user, assistant, and file impact history");
            Assert(threadReadPayload.Turns.Single().DurationMs == 96_000,
                "thread/read should include turn duration metadata");

            var legacyThreadRead = await dispatcher.ReadThreadAsync(
                "thr-history-legacy",
                CancellationToken.None).ConfigureAwait(false);
            var legacyThreadReadResult = legacyThreadRead.Result ??
                                         throw new InvalidOperationException("legacy thread/read result is missing");
            var legacyThreadReadPayload = legacyThreadReadResult.Deserialize<CodexThreadReadResultPayload>(
                                              RelayJson.Options) ??
                                          throw new InvalidOperationException("legacy thread/read payload should deserialize");
            Assert(
                legacyThreadRead.Succeeded &&
                legacyThreadReadPayload.Entries.Count == 2 &&
                legacyThreadReadPayload.Entries[0].Role == "user" &&
                legacyThreadReadPayload.Entries[1].Role == "assistant",
                "unsupported thread/items/list should fall back to bounded legacy thread/read");

            Apply(state, """
                {"method":"turn/started","params":{"threadId":"thr-concurrent","turn":{"id":"turn-concurrent"}}}
                """);
            var duplicateResume = await dispatcher.ResumeThreadAsync(
                "thr-concurrent",
                "should be rejected",
                CancellationToken.None).ConfigureAwait(false);
            Assert(
                !duplicateResume.Succeeded && duplicateResume.ErrorCode == "TURN_ALREADY_ACTIVE",
                "the same thread must still reject a second active turn");
            var created = await dispatcher.StartThreadAsync(
                testRoot,
                "start a real remote task",
                "gpt-5.6-terra",
                "on-request",
                CancellationToken.None).ConfigureAwait(false);
            Assert(created.Succeeded && created.Result is not null, "remote thread creation should start a turn");
            var createdResult = created.Result ?? throw new InvalidOperationException("created result is missing");
            var createdPayload = createdResult.Deserialize<CodexThreadActionResultPayload>(RelayJson.Options) ??
                                 throw new InvalidOperationException("created thread payload should deserialize");
            Assert(
                createdPayload.ThreadId == "thr-created" && createdPayload.TurnId == "turn-created",
                "thread/start and turn/start IDs should be returned");
            Assert(
                state.Snapshot.ActiveTurns.Any(turn => turn.ThreadId == "thr-concurrent"),
                "starting a second thread must not evict or reject another active turn");
            Apply(state, """
                {"method":"turn/completed","params":{"threadId":"thr-concurrent","turn":{"id":"turn-concurrent","status":"completed"}}}
                """);
            await WaitUntilAsync(
                () => state.Snapshot.ActiveTurns.Count == 0,
                TestTimeout).ConfigureAwait(false);

            var resumed = await dispatcher.ResumeThreadAsync(
                "thr-history-1",
                "continue the historical task",
                CancellationToken.None).ConfigureAwait(false);
            Assert(resumed.Succeeded && resumed.Result is not null, "historical thread should resume and start a turn");
            var resumedResult = resumed.Result ?? throw new InvalidOperationException("resumed result is missing");
            var resumedPayload = resumedResult.Deserialize<CodexThreadActionResultPayload>(RelayJson.Options) ??
                                 throw new InvalidOperationException("resumed thread payload should deserialize");
            Assert(
                resumedPayload.ThreadId == "thr-history-1" && resumedPayload.TurnId == "turn-resumed",
                "thread/resume and turn/start IDs should be returned");
            await WaitUntilAsync(
                () => state.Snapshot.ActiveTurnId is null,
                TestTimeout).ConfigureAwait(false);

            var approvalCompletion = new TaskCompletionSource<PendingApprovalSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.Approvals.ApprovalRequested += approval => approvalCompletion.TrySetResult(approval);
            _ = await bridge.SendRequestAsync(
                "test/emitApproval",
                new { },
                TimeSpan.FromSeconds(5),
                CancellationToken.None).ConfigureAwait(false);
            var approval = await approvalCompletion.Task.WaitAsync(TestTimeout).ConfigureAwait(false);
            Assert(approval.AvailableDecisions.Count == 2, "server decisions should be preserved");

            using var decisionDocument = JsonDocument.Parse("\"accept\"");
            var resolution = await dispatcher.ResolveApprovalAsync(
                approval.ApprovalId,
                decisionDocument.RootElement,
                "controller-1",
                CancellationToken.None).ConfigureAwait(false);
            Assert(resolution.Succeeded, "first remote approval response should win");

            var duplicate = await dispatcher.ResolveApprovalAsync(
                approval.ApprovalId,
                decisionDocument.RootElement,
                "controller-2",
                CancellationToken.None).ConfigureAwait(false);
            Assert(
                !duplicate.Succeeded && duplicate.ErrorCode == "APPROVAL_ALREADY_RESOLVED",
                "second approval response must be rejected");

            await bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task TestAgentRelayAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-agent-relay-tests-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        WebApplication? relayApp = null;
        try
        {
            var identityDirectory = Path.Combine(testRoot, "identity");
            string deviceId;
            using (var firstIdentity = DeviceIdentity.LoadOrCreate(identityDirectory, "Agent Relay Test"))
            {
                deviceId = firstIdentity.DeviceId;
            }

            using (var reloadedIdentity = DeviceIdentity.LoadOrCreate(identityDirectory, "Ignored New Name"))
            {
                Assert(reloadedIdentity.DeviceId == deviceId, "DPAPI identity should persist across reloads");
            }

            var database = Path.Combine(testRoot, "relay.db");
            relayApp = RelayApplication.Build(
            [
                "--environment=Development",
                "--urls=http://127.0.0.1:0",
                $"--CODEX_CONTROL_DB={database}",
                "--CODEX_CONTROL_PAIRING_SECRET=agent-relay-test-secret",
                "--Logging:LogLevel:Default=Warning",
            ]);
            await RelayApplication.InitializeDatabaseAsync(relayApp, CancellationToken.None).ConfigureAwait(false);
            await relayApp.StartAsync().ConfigureAwait(false);
            var relayUri = ResolveRelayUri(relayApp);

            var baseOptions = AgentOptions.ForTests(
                GetTestExecutablePath(),
                Path.Combine(testRoot, "logs"));
            var options = baseOptions with
            {
                RelayUrl = relayUri,
                AllowInsecureRelay = true,
                DataDirectory = identityDirectory,
                DeviceName = "Agent Relay Test",
            };
            using var log = new AgentLog(options.LogDirectory);
            using var identity = DeviceIdentity.LoadOrCreate(options.DataDirectory, options.DeviceName);
            var state = new CodexStateManager();
            await using var bridge = new AppServerBridge(options, log, state);
            await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var dispatcher = new RemoteControlDispatcher(bridge, state);
            await using var relay = new RelayClient(options, identity, state, bridge, dispatcher, log);
            relay.Start();
            await relay.WaitUntilAuthenticatedAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => state.Snapshot.RelayConnected, TestTimeout).ConfigureAwait(false);
            var pairing = await relay.CreatePairingAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(pairing.Code.Length == 6, "Agent should receive a six-digit pairing code");

            var factory = relayApp.Services.GetRequiredService<IDbContextFactory<RelayDbContext>>();
            await using (var db = await factory.CreateDbContextAsync().ConfigureAwait(false))
            {
                Assert(await db.Devices.AnyAsync(value => value.Id == identity.DeviceId).ConfigureAwait(false),
                    "Relay should persist the Agent device identity");
                Assert(await db.PairingSessions.AnyAsync().ConfigureAwait(false),
                    "Agent pairing request should create a persisted session");
            }

            await relay.DisposeAsync().ConfigureAwait(false);
            await WaitUntilAsync(() => !state.Snapshot.RelayConnected, TestTimeout).ConfigureAwait(false);
            await bridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (relayApp is not null)
            {
                await relayApp.StopAsync().ConfigureAwait(false);
                await relayApp.DisposeAsync().ConfigureAwait(false);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task AssertOriginRejectedAsync(Uri uri)
    {
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", "https://example.invalid");
        await AssertThrowsAsync<WebSocketException>(
            () => client.ConnectAsync(uri, CancellationToken.None),
            "browser Origin header must be rejected").ConfigureAwait(false);
    }

    private static async Task AssertCapabilityRejectedAsync(Uri uri)
    {
        using var client = new ClientWebSocket();
        await client.ConnectAsync(uri, CancellationToken.None).ConfigureAwait(false);
        await SendAsync(client, """
            {"method":"initialize","id":6,"params":{"clientInfo":{"name":"legacy-tui","version":"0.1.0"}}}
            """).ConfigureAwait(false);
        using var response = await ReceiveResponseAsync(client, 6).ConfigureAwait(false);
        Assert(
            response.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32602,
            "TUI without experimentalApi must be rejected");

        var buffer = new byte[1024];
        var close = await client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None)
            .AsTask()
            .WaitAsync(TestTimeout)
            .ConfigureAwait(false);
        Assert(close.MessageType == WebSocketMessageType.Close, "capability rejection should close the socket");
    }

    private static async Task SendAsync(ClientWebSocket socket, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveResponseAsync(ClientWebSocket socket, int expectedId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var json = await ReceiveAsync(socket).ConfigureAwait(false);
            var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.GetInt32() == expectedId)
            {
                return document;
            }

            document.Dispose();
        }

        throw new InvalidOperationException($"没有收到 JSON-RPC response id={expectedId}。");
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None)
                .AsTask()
                .WaitAsync(TestTimeout)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("WebSocket closed before expected response.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static string GetTestExecutablePath()
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, "CodexControlAgentTests.exe");
        if (File.Exists(appHost))
        {
            return appHost;
        }

        return Environment.ProcessPath ?? throw new InvalidOperationException("无法确定测试进程路径。");
    }

    private static Uri ResolveRelayUri(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single() ??
                      throw new InvalidOperationException("Relay test address is unavailable.");
        return new Uri(address, UriKind.Absolute);
    }

    private static void Apply(CodexStateManager state, string json)
    {
        using var document = JsonRpcProtocol.Parse(json);
        state.ApplyServerMessage(document.RootElement);
    }

    private static async Task AssertThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class SkipTestException(string message) : Exception(message);
}
