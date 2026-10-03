using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexControl.SharedSessionProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "help")
        {
            Console.WriteLine("""
                SharedSessionProbe (.NET 8, public app-server WebSocket only)
                inspect (--socket ABSOLUTE_PATH | --endpoint ws://127.0.0.1:PORT)
                idle-check (--socket ABSOLUTE_PATH | --endpoint ws://127.0.0.1:PORT)
                create-test --endpoint ws://127.0.0.1:PORT --workspace EMPTY_TEST_DIRECTORY --marker UNIQUE_TEST_MARKER
                create-observe --endpoint ws://127.0.0.1:PORT --workspace EMPTY_TEST_DIRECTORY --marker UNIQUE_TEST_MARKER
                observe (--socket ABSOLUTE_PATH | --endpoint ws://127.0.0.1:PORT)
                        --thread TEST_THREAD_ID --workspace EXISTING_TEST_DIRECTORY --marker UNIQUE_TEST_MARKER
                        [--duration-seconds 1..3600]
                inspect opens two independent connections and closes only those connections.
                create-test creates and names one new thread without submitting input.
                create-observe keeps its creation connection open while observing the new thread interactively.
                idle-check only reads all loaded thread statuses; it never resumes a thread.
                observe accepts only a thread whose cwd matches --workspace and whose name/preview contains --marker.
                A duration observes without reading stdin, exits when elapsed, and fails if the connection is lost.
                Commands: status, reconnect, start TEXT, steer TEXT, interrupt, decline REQUEST_ID, cancel REQUEST_ID, quit
                No service start/stop, no automatic approval, no credentials, no full transcript output.
                This does not prove official Desktop compatibility.
                """);
            return 0;
        }

        try
        {
            var options = Options.Parse(args);
            if (options.Mode is "create-test" or "create-observe")
            {
                await CreateTestThread.RunAsync(options);
                return 0;
            }
            if (options.Mode == "idle-check")
            {
                await RunIdleCheckAsync(options);
                return 0;
            }
            if (options.Mode == "inspect")
            {
                await using var first = await ConnectAsync(options, "probe_a");
                await using var second = await ConnectAsync(options, "probe_b");
                // Verify the first connection is still usable after the second initializes.
                var firstLoaded = await RequestAsync(first, "thread/loaded/list", new { limit = 1 });
                var secondLoaded = await RequestAsync(second, "thread/loaded/list", new { limit = 1 });
                if (!HasArray(firstLoaded, "data") || !HasArray(secondLoaded, "data"))
                    throw new ProbeRejectedException("LOADED_THREAD_LIST_UNAVAILABLE");
                Write(new { kind = "two_connections_ready", firstListReadable = HasArray(firstLoaded, "data"),
                    secondListReadable = HasArray(secondLoaded, "data"), desktopVerified = false });
                return 0;
            }

            return await RunObserverAsync(options);
        }
        catch (Exception exception)
        {
            WriteFailure(exception, "connection_or_preflight_failed");
            return 2;
        }
    }

    internal static async Task<int> RunObserverAsync(Options options)
    {
            await using var probe = new SessionProbe(options);
            await probe.ReconnectAsync();
            if (options.DurationSeconds is int durationSeconds)
            {
                var elapsed = Task.Delay(TimeSpan.FromSeconds(durationSeconds));
                if (await Task.WhenAny(elapsed, probe.Completion) != elapsed || probe.Completion.IsCompleted)
                {
                    Write(new { kind = "connection_lost", taskState = "unknown", automaticResend = false });
                    return 3;
                }
                Write(new { kind = "observation_completed", durationSeconds, inputSubmitted = false, automaticResend = false });
                return 0;
            }
            while (true)
            {
                var lineTask = Console.In.ReadLineAsync();
                if (await Task.WhenAny(lineTask, probe.Completion) != lineTask)
                {
                    Write(new { kind = "connection_lost", taskState = "unknown", automaticResend = false });
                    return 3;
                }
                var line = await lineTask;
                if (line is null || line.Trim() == "quit") return 0;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { await probe.CommandAsync(line); }
                catch (Exception exception) { WriteFailure(exception, "command_rejected_or_unconfirmed"); }
            }
    }

    internal static async Task<SharedRpcClient> ConnectAsync(Options options, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = options.Socket is not null
            ? await SharedRpcClient.ConnectUnixAsync(options.Socket, timeout.Token)
            : await SharedRpcClient.ConnectLoopbackAsync(new Uri(options.Endpoint!), timeout.Token);
        try
        {
            await RequestAsync(client, "initialize", new
            {
                clientInfo = new { name = "codex_control_" + name, version = "0.1.0" },
                capabilities = new { experimentalApi = true },
            });
            await client.NotifyAsync("initialized", null, timeout.Token);
            Write(new { kind = "initialized", client = name });
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    internal static async Task<JsonElement> RequestAsync(SharedRpcClient client, string method, object parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await client.RequestAsync(method, parameters, timeout.Token);
    }

    private static async Task RunIdleCheckAsync(Options options)
    {
        await using var client = await ConnectAsync(options, "idle_check");
        var threads = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; ; page++)
        {
            if (page >= 200) throw new ProbeRejectedException("LOADED_THREAD_PAGINATION_LIMIT_REACHED");
            var result = await RequestAsync(client, "thread/loaded/list", new { cursor, limit = 100 });
            var data = At(result, "data");
            if (data.ValueKind != JsonValueKind.Array) throw new ProbeRejectedException("LOADED_THREAD_LIST_UNAVAILABLE");
            foreach (var entry in data.EnumerateArray())
            {
                var id = entry.ValueKind == JsonValueKind.String ? SafeId(entry.GetString()) : null;
                if (id is null) throw new ProbeRejectedException("LOADED_THREAD_ID_INVALID");
                threads.Add(id);
            }
            var nextCursor = At(result, "nextCursor");
            if (nextCursor.ValueKind == JsonValueKind.Null) break;
            if (nextCursor.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(nextCursor.GetString()) ||
                nextCursor.GetString()!.Length > 4096 || !cursors.Add(nextCursor.GetString()!))
                throw new ProbeRejectedException("LOADED_THREAD_PAGINATION_INVALID");
            cursor = nextCursor.GetString();
        }
        var activeOrUnknownCount = 0;
        foreach (var threadId in threads)
        {
            var result = await RequestAsync(client, "thread/read", new { threadId, includeTurns = false });
            if (Text(result, "thread", "id") != threadId) throw new ProbeRejectedException("LOADED_THREAD_ID_MISMATCH");
            if (Text(result, "thread", "status", "type") is not ("idle" or "notLoaded")) activeOrUnknownCount++;
        }
        Write(new { kind = "loaded_threads_state", allIdle = activeOrUnknownCount == 0,
            loadedCount = threads.Count, activeOrUnknownCount });
    }

    internal static JsonElement At(JsonElement root, params string[] path)
    {
        foreach (var key in path)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out root)) return default;
        }
        return root;
    }

    internal static string? Text(JsonElement root, params string[] path)
    {
        var value = At(root, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool HasArray(JsonElement root, string name) => At(root, name).ValueKind == JsonValueKind.Array;
    internal static string? SafeId(string? value) => value is not null && Regex.IsMatch(value, @"\A[a-zA-Z0-9_.:-]{1,160}\z") ? value : null;
    internal static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    private static void WriteFailure(Exception exception, string kind) => Write(new
    {
        kind, errorType = exception.GetType().Name,
        reason = exception switch
        {
            ProbeRejectedException rejected => rejected.Reason,
            SharedRpcException => "RPC_REJECTED",
            OperationCanceledException => "REQUEST_CANCELLED_OR_TIMED_OUT",
            TimeoutException => "REQUEST_TIMED_OUT",
            ArgumentException => "INVALID_ARGUMENTS",
            IOException => "CONNECTION_OR_PROTOCOL_FAILURE",
            _ => "UNEXPECTED_FAILURE",
        },
        rpcCode = (exception as SharedRpcException)?.Code,
        rpcCategory = (exception as SharedRpcException)?.Category,
        rpcMethod = exception is SharedRpcException rpc && rpc.Method is
            "initialize" or "model/list" or "thread/start" or "thread/name/set" or
            "thread/loaded/list" or "thread/read" or "thread/resume" or "thread/turns/list" or
            "turn/start" or "turn/steer" or "turn/interrupt" ? rpc.Method : null,
        // Exception text and server error data may contain private paths or prompt content.
        automaticResend = false,
    });
}

internal sealed class ProbeRejectedException(string reason) : Exception
{
    public string Reason { get; } = reason;
}

internal sealed record Options(string Mode, string? Socket, string? Endpoint, string? Thread, string? Workspace, string? Marker,
    int? DurationSeconds)
{
    public static Options Parse(string[] args)
    {
        if (args[0] is not ("inspect" or "observe" or "create-test" or "create-observe" or "idle-check")) throw new ArgumentException("Unknown mode.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--socket" or "--endpoint" or "--thread" or "--workspace" or "--marker" or "--duration-seconds") ||
                !values.TryAdd(args[index], args[index + 1])) throw new ArgumentException("Invalid options.");
        }
        var socket = values.GetValueOrDefault("--socket");
        var endpoint = values.GetValueOrDefault("--endpoint");
        if ((socket is null) == (endpoint is null)) throw new ArgumentException("Choose one endpoint.");
        var thread = values.GetValueOrDefault("--thread");
        var workspace = values.GetValueOrDefault("--workspace");
        var marker = values.GetValueOrDefault("--marker");
        if (args[0] == "observe" && Program.SafeId(thread) is null)
            throw new ArgumentException("A test thread is required.");
        if (args[0] is "observe" or "create-test" or "create-observe" && (string.IsNullOrEmpty(workspace) ||
            !Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace) ||
            marker is null || !Regex.IsMatch(marker, @"\ACC_SHARED_[a-zA-Z0-9_-]{8,64}\z")))
            throw new ArgumentException("A test thread, existing absolute workspace and CC_SHARED_ marker are required.");
        if (args[0] != "observe" && (thread is not null || values.ContainsKey("--duration-seconds")))
            throw new ArgumentException("Thread and duration are observation options.");
        if (args[0] is "inspect" or "idle-check" && (workspace is not null || marker is not null))
            throw new ArgumentException("Workspace and marker are not valid in this mode.");
        if (args[0] is "create-test" or "create-observe" && endpoint is null)
            throw new ArgumentException("create-test requires an explicit loopback endpoint.");
        int? durationSeconds = null;
        if (values.TryGetValue("--duration-seconds", out var durationText))
        {
            if (!int.TryParse(durationText, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var duration) || duration is < 1 or > 3600)
                throw new ArgumentException("Invalid observation duration.");
            durationSeconds = duration;
        }
        return new Options(args[0], socket, endpoint, thread, workspace is null ? null : Path.GetFullPath(workspace), marker, durationSeconds);
    }
}

internal sealed class SessionProbe(Options options) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (JsonElement Id, string[] Decisions)> _approvals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _answeredApprovals = new(StringComparer.Ordinal);
    private SharedRpcClient? _client;
    private string? _turn;
    private string _status = "unknown";
    private bool _controlAllowed;
    private string _approvalPolicy = "unknown";
    private string[] _activeFlags = [];
    private long _turnRevision;
    private long _statusRevision;
    private long _policyRevision;
    private int _epoch;
    public Task Completion => _client!.Completion;

    public async Task ReconnectAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        lock (_gate)
        {
            _epoch++;
            _approvals.Clear();
            _answeredApprovals.Clear();
            _turn = null;
            _status = "unknown";
            _activeFlags = [];
            _controlAllowed = false;
            _turnRevision++;
            _statusRevision++;
            _policyRevision++;
        }
        _client = await Program.ConnectAsync(options, "session_probe");
        var epoch = _epoch;
        _client.Notification += message => Observe(message, epoch);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        lock (_gate) _controlAllowed = false;
        var metadata = await Program.RequestAsync(_client!, "thread/read", new { threadId = options.Thread, includeTurns = false });
        var thread = Program.At(metadata, "thread");
        var cwd = Program.Text(thread, "cwd");
        var markerMatches = (Program.Text(thread, "preview")?.Contains(options.Marker!, StringComparison.Ordinal) ?? false) ||
                            (Program.Text(thread, "name")?.Contains(options.Marker!, StringComparison.Ordinal) ?? false);
        if (Program.Text(thread, "id") != options.Thread || !TestThreadPolicy.SameDirectory(cwd, options.Workspace!) || !markerMatches)
            throw new ProbeRejectedException("TEST_THREAD_GUARD_FAILED");

        long turnRevision;
        long statusRevision;
        long policyRevision;
        lock (_gate)
        {
            turnRevision = _turnRevision;
            statusRevision = _statusRevision;
            policyRevision = _policyRevision;
        }
        // No model/cwd/policy overrides: this joins the existing test thread without submitting work.
        var resumed = await Program.RequestAsync(_client!, "thread/resume", new { threadId = options.Thread, excludeTurns = true });
        if (Program.Text(resumed, "thread", "id") != options.Thread ||
            !TestThreadPolicy.SameDirectory(Program.Text(resumed, "cwd"), options.Workspace!))
            throw new ProbeRejectedException("RESUME_IDENTITY_CHANGED");
        var turns = await Program.RequestAsync(_client!, "thread/turns/list", new
        {
            threadId = options.Thread, limit = 20, sortDirection = "desc", itemsView = "summary",
        });
        var policy = Program.Text(resumed, "approvalPolicy");
        var sandbox = Program.Text(resumed, "sandbox", "type");
        var reviewer = Program.Text(resumed, "approvalsReviewer");
        var permissionScopeCompatible = TestThreadPolicy.IsIsolatedSandbox(resumed, options.Workspace!, options.Thread);
        var turnItems = Program.At(turns, "data");
        if (turnItems.ValueKind != JsonValueKind.Array) throw new ProbeRejectedException("AUTHORITATIVE_TURN_PAGE_UNAVAILABLE");
        var active = turnItems.EnumerateArray().FirstOrDefault(turn => Program.Text(turn, "status") == "inProgress");
        lock (_gate)
        {
            _approvalPolicy = policy is "untrusted" or "on-request" or "never" ? policy : "unknown";
            // A lifecycle change after the policy read makes that read insufficient for control.
            _controlAllowed = permissionScopeCompatible && reviewer == "user" &&
                              _approvalPolicy != "unknown" && _turnRevision == turnRevision && _policyRevision == policyRevision;
            if (_turnRevision == turnRevision)
            {
                _turn = Program.SafeId(Program.Text(active, "id"));
            }
            if (_statusRevision == statusRevision)
            {
                var status = Program.At(resumed, "thread", "status");
                _status = ReadStatus(status);
                _activeFlags = ReadActiveFlags(status);
                // The turn page is read after resume; an active turn is newer evidence than idle metadata.
                if (_turn is not null) _status = "active";
            }
            Program.Write(new { kind = "authoritative_state", epoch = _epoch, threadId = options.Thread,
                turnId = Program.SafeId(_turn), status = _status, activeFlags = _activeFlags,
                sandbox = sandbox is "workspaceWrite" or "readOnly" or "dangerFullAccess" or "externalSandbox" ? sandbox : "unknown",
                approvalPolicy = _approvalPolicy,
                approvalsReviewer = reviewer is "user" or "auto_review" or "guardian_subagent" ? reviewer : "unknown",
                permissionScopeCompatible,
                temporaryDirectoryRestrictions = new
                {
                    excludeTmpdirEnvVar = BooleanSetting(resumed, "sandbox", "excludeTmpdirEnvVar"),
                    excludeSlashTmp = BooleanSetting(resumed, "sandbox", "excludeSlashTmp"),
                    requiredValue = true,
                },
                controlAllowed = _controlAllowed, pendingApprovals = _approvals.Count });
        }
    }

    public async Task CommandAsync(string line)
    {
        var parts = line.Trim().Split(' ', 2);
        var command = parts[0];
        var text = parts.Length == 2 ? parts[1].Trim() : "";
        if (command == "help")
        {
            Program.Write(new { kind = "command_help", commands = new[]
            {
                "status", "reconnect", "start TEXT", "steer TEXT", "interrupt",
                "decline REQUEST_ID", "cancel REQUEST_ID", "quit",
            }, inputSubmitted = false });
            return;
        }
        if (command == "reconnect") { await ReconnectAsync(); return; }
        if (command == "status") { await RefreshAsync(); return; }
        if (command is "decline" or "cancel")
        {
            (JsonElement Id, string[] Decisions) approval;
            lock (_gate)
            {
                if (!_approvals.TryGetValue(text, out approval) || !approval.Decisions.Contains(command) ||
                    !_answeredApprovals.Add(text))
                    throw new ProbeRejectedException("APPROVAL_UNAVAILABLE_OR_ALREADY_ANSWERED");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _client!.RespondAsync(approval.Id, new { decision = command }, timeout.Token);
            Program.Write(new { kind = "decision_sent_waiting_server_resolution", epoch = _epoch, requestId = text, decision = command });
            return;
        }
        if (command is not ("start" or "steer" or "interrupt")) throw new ProbeRejectedException("UNKNOWN_COMMAND_USE_HELP");
        await RefreshAsync();
        string? turn;
        string? returnedTurnId = null;
        string status;
        string approvalPolicy;
        lock (_gate)
        {
            if (!_controlAllowed) throw new ProbeRejectedException("EFFECTIVE_POLICY_INCOMPATIBLE_OR_CHANGED");
            turn = _turn;
            status = _status;
            approvalPolicy = _approvalPolicy;
        }
        if (command == "interrupt")
        {
            if (turn is null) throw new ProbeRejectedException("AUTHORITATIVE_ACTIVE_TURN_REQUIRED");
            await Program.RequestAsync(_client!, "turn/interrupt", new { threadId = options.Thread, turnId = turn });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new ArgumentException("Invalid test input.");
            var input = new[] { new { type = "text", text = options.Marker + " " + text } };
            if (command == "steer")
            {
                if (turn is null || status != "active") throw new ProbeRejectedException("AUTHORITATIVE_ACTIVE_TURN_REQUIRED");
                var steered = await Program.RequestAsync(_client!, "turn/steer", new { threadId = options.Thread, expectedTurnId = turn, input });
                returnedTurnId = Program.SafeId(Program.Text(steered, "turnId"));
            }
            else
            {
                if (turn is not null || status != "idle") throw new ProbeRejectedException("IDLE_THREAD_REQUIRED");
                var result = await Program.RequestAsync(_client!, "turn/start", new
                {
                    threadId = options.Thread, input, approvalPolicy, approvalsReviewer = "user",
                    sandboxPolicy = new { type = "workspaceWrite", writableRoots = new[] { options.Workspace },
                        networkAccess = false, excludeTmpdirEnvVar = true, excludeSlashTmp = true },
                });
                returnedTurnId = Program.SafeId(Program.Text(result, "turn", "id"));
            }
        }
        Program.Write(new { kind = "rpc_accepted", command, epoch = _epoch, targetTurnId = turn, returnedTurnId,
            newTurnConfirmed = false, mayHaveSteeredConcurrentTurn = command == "start", terminalStateConfirmed = false });
    }

    private void Observe(JsonElement message, int epoch)
    {
        var method = Program.Text(message, "method");
        var parameters = Program.At(message, "params");
        var threadId = Program.Text(parameters, "threadId") ?? Program.Text(parameters, "thread", "id");
        lock (_gate)
        {
            if (epoch != _epoch || method is null) return;
            var resolvedRequestId = method == "serverRequest/resolved"
                ? IdText(Program.At(parameters, "requestId")) : null;
            // Older servers may omit threadId here. Only a request tracked in this epoch can correlate it.
            if (threadId is null && resolvedRequestId is not null && _approvals.ContainsKey(resolvedRequestId))
                threadId = options.Thread;
            if (threadId != options.Thread) return;
            if (method == "thread/settings/updated")
            {
                _policyRevision++;
                _controlAllowed = false;
            }
            var turn = Program.Text(parameters, "turnId") ?? Program.Text(parameters, "turn", "id");
            if (method == "turn/started")
            {
                _turnRevision++;
                _statusRevision++;
                _turn = Program.SafeId(turn);
                _status = "active";
                _activeFlags = [];
                _controlAllowed = false;
            }
            if (method == "turn/completed" && turn is not null && (_turn is null || turn == _turn))
            {
                _turnRevision++;
                _statusRevision++;
                _turn = null;
                _status = "idle";
                _activeFlags = [];
                _controlAllowed = false;
                _approvals.Clear();
                _answeredApprovals.Clear();
            }
            if (method == "thread/status/changed")
            {
                _statusRevision++;
                var previousStatus = _status;
                var status = Program.At(parameters, "status");
                _status = ReadStatus(status);
                _activeFlags = ReadActiveFlags(status);
                if (_status is "idle" or "notLoaded" or "systemError")
                {
                    if (_turn is not null || previousStatus != _status) _turnRevision++;
                    _turn = null;
                    _controlAllowed = false;
                    _approvals.Clear();
                    _answeredApprovals.Clear();
                }
            }
            var id = Program.At(message, "id");
            var requestId = IdText(id);
            string[] decisions = [];
            if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" && requestId is not null)
            {
                var advertised = Program.At(parameters, "availableDecisions");
                decisions = advertised.ValueKind == JsonValueKind.Array
                    ? advertised.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString()!).Where(value => value is "decline" or "cancel").ToArray()
                    : ["decline", "cancel"];
                _approvals[requestId] = (id.Clone(), decisions);
            }
            if (method == "serverRequest/resolved")
            {
                requestId = resolvedRequestId;
                if (requestId is not null)
                {
                    _approvals.Remove(requestId);
                    _answeredApprovals.Remove(requestId);
                }
            }
            // Only identifiers, lengths and a test-marker hit leave memory. No transcript/body dump.
            var body = Program.Text(parameters, "delta") ?? Program.Text(parameters, "item", "text") ?? "";
            var itemType = Program.Text(parameters, "item", "type") switch
            {
                "userMessage" => "userMessage",
                "agentMessage" => "agentMessage",
                "reasoning" => "reasoning",
                "commandExecution" => "commandExecution",
                "fileChange" => "fileChange",
                "mcpToolCall" => "mcpToolCall",
                "webSearch" => "webSearch",
                "plan" => "plan",
                _ when method == "item/agentMessage/delta" => "agentMessage",
                _ => "unknown",
            };
            var direction = itemType == "userMessage" ? "user_to_agent"
                : itemType == "agentMessage" ? "agent_to_user" : "lifecycle_or_tool";
            var content = Program.At(parameters, "item", "content");
            var markerSeen = body.Contains(options.Marker!, StringComparison.Ordinal) ||
                (content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(part =>
                    Program.Text(part, "text")?.Contains(options.Marker!, StringComparison.Ordinal) == true));
            if (method is "thread/status/changed" or "thread/settings/updated" or "turn/started" or "turn/completed" or "item/started" or "item/completed" or
                "item/agentMessage/delta" or "serverRequest/resolved" or "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                Program.Write(new { kind = "event", epoch, at = DateTimeOffset.UtcNow, method,
                    threadId, turnId = Program.SafeId(turn), itemId = Program.SafeId(Program.Text(parameters, "itemId") ?? Program.Text(parameters, "item", "id")),
                    requestId, decisions, itemType, direction, status = _status, activeFlags = _activeFlags,
                    controlAllowed = _controlAllowed, textCharacters = body.Length, markerSeen,
                    terminalTurnStatus = method == "turn/completed" ? Program.Text(parameters, "turn", "status") switch
                    {
                        "completed" => "completed", "interrupted" => "interrupted", "failed" => "failed", _ => "unknown",
                    } : null });
        }
    }

    private static bool? BooleanSetting(JsonElement value, params string[] path) => Program.At(value, path).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string? IdText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Program.SafeId(value.GetString()),
        JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string ReadStatus(JsonElement status) => Program.Text(status, "type") switch
    {
        "active" => "active",
        "idle" => "idle",
        "notLoaded" => "notLoaded",
        "systemError" => "systemError",
        _ => "unknown",
    };

    private static string[] ReadActiveFlags(JsonElement status)
    {
        var flags = Program.At(status, "activeFlags");
        return flags.ValueKind == JsonValueKind.Array
            ? flags.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).Where(value => value is "waitingOnApproval" or "waitingOnUserInput").Distinct().ToArray()
            : [];
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
