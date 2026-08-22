using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.Security;
using CodexControl.Agent.State;
using CodexControl.Protocol;

namespace CodexControl.Agent.Relay;

public sealed class RelayClient : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly int[] BackoffSeconds = [1, 2, 5, 10, 30];

    private readonly AgentOptions _options;
    private readonly DeviceIdentity _identity;
    private readonly CodexStateManager _state;
    private readonly AppServerBridge _bridge;
    private readonly RemoteControlDispatcher _dispatcher;
    private readonly AgentLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _firstAuthenticated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayEnvelope>> _pending = new();
    private readonly HashSet<string> _pairedControllers = new(StringComparer.Ordinal);
    private readonly object _connectionGate = new();

    private Channel<RelayEnvelope>? _activeOutbound;
    private Task? _runTask;
    private CodexSnapshotPayload _latestSnapshot;
    private int _started;
    private int _disposed;

    public RelayClient(
        AgentOptions options,
        DeviceIdentity identity,
        CodexStateManager state,
        AppServerBridge bridge,
        RemoteControlDispatcher dispatcher,
        AgentLog log)
    {
        _options = options;
        _identity = identity;
        _state = state;
        _bridge = bridge;
        _dispatcher = dispatcher;
        _log = log;
        _latestSnapshot = MapSnapshot(state.Snapshot);
        _state.SnapshotChanged += OnSnapshotChanged;
        _bridge.ServerMessageReceived += OnServerMessage;
        _bridge.Approvals.ApprovalRequested += OnApprovalRequested;
        _bridge.Approvals.ApprovalResolved += OnApprovalResolved;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("RelayClient can only be started once.");
        }

        _runTask = RunAsync(_lifetime.Token);
    }

    public Task WaitUntilAuthenticatedAsync(CancellationToken cancellationToken) =>
        _firstAuthenticated.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

    public async Task<PairingCreatedPayload> CreatePairingAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            RelayMessageTypes.PairingCreate,
            new PairingCreatePayload(),
            cancellationToken).ConfigureAwait(false);
        if (response.Type == RelayMessageTypes.Error)
        {
            var error = response.ReadPayload<ErrorPayload>();
            throw new AgentException(error.Code, error.Message);
        }

        return response.ReadPayload<PairingCreatedPayload>();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _state.SnapshotChanged -= OnSnapshotChanged;
        _bridge.ServerMessageReceived -= OnServerMessage;
        _bridge.Approvals.ApprovalRequested -= OnApprovalRequested;
        _bridge.Approvals.ApprovalResolved -= OnApprovalResolved;
        _lifetime.Cancel();
        Channel<RelayEnvelope>? outbound;
        lock (_connectionGate)
        {
            outbound = _activeOutbound;
            _activeOutbound = null;
        }

        outbound?.Writer.TryComplete();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        FailPending(new AgentException("RELAY_OFFLINE", "Relay client stopped."));
        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.Warning("relay_disconnected", $"Relay connection failed: {exception.GetType().Name}");
            }
            finally
            {
                _state.SetRelayConnected(false);
                lock (_connectionGate)
                {
                    _activeOutbound = null;
                }

                FailPending(new AgentException("RELAY_OFFLINE", "Relay connection was lost."));
            }

            var baseDelay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
            attempt++;
            var delay = TimeSpan.FromMilliseconds(baseDelay * 1000 * (1 + Random.Shared.NextDouble() * 0.2));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var endpoint = BuildDeviceEndpoint(_options.RelayUrl!);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

        await RegisterAsync(socket, cancellationToken).ConfigureAwait(false);
        var auth = await AuthenticateAsync(socket, cancellationToken).ConfigureAwait(false);
        var outbound = Channel.CreateBounded<RelayEnvelope>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        lock (_connectionGate)
        {
            _activeOutbound = outbound;
        }

        _state.SetRelayConnected(true);
        _firstAuthenticated.TrySetResult(true);
        _log.Info("relay_authenticated", $"device={_identity.DeviceId}; connection={auth.ConnectionId}");
        outbound.Writer.TryWrite(RelayEnvelope.Create(
            RelayMessageTypes.CodexSnapshot,
            Volatile.Read(ref _latestSnapshot),
            deviceId: _identity.DeviceId));

        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writerTask = WriterLoopAsync(socket, outbound.Reader, connectionCancellation.Token);
        var readerTask = ReaderLoopAsync(socket, connectionCancellation.Token);
        var heartbeatTask = HeartbeatLoopAsync(outbound.Writer, auth.ConnectionId, connectionCancellation.Token);
        await Task.WhenAny(writerTask, readerTask, heartbeatTask).ConfigureAwait(false);
        connectionCancellation.Cancel();
        outbound.Writer.TryComplete();
        try
        {
            await Task.WhenAll(writerTask, readerTask, heartbeatTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "reconnecting",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private async Task RegisterAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var requestId = NewRequestId();
        await SendDirectAsync(socket, RelayEnvelope.Create(
            RelayMessageTypes.DeviceRegister,
            new DeviceRegisterPayload(_identity.DeviceId, _identity.Name, _identity.PublicKey),
            requestId,
            _identity.DeviceId), cancellationToken).ConfigureAwait(false);
        var response = await ReceiveDirectAsync(socket, cancellationToken).ConfigureAwait(false);
        EnsureResponse(response, RelayMessageTypes.DeviceRegistered, requestId);
    }

    private async Task<AuthOkPayload> AuthenticateAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var requestId = NewRequestId();
        await SendDirectAsync(socket, RelayEnvelope.Create(
            RelayMessageTypes.AuthHello,
            new AuthHelloPayload(PrincipalRole.Device, _identity.DeviceId, "0.1.0"),
            requestId,
            _identity.DeviceId), cancellationToken).ConfigureAwait(false);
        var challengeEnvelope = await ReceiveDirectAsync(socket, cancellationToken).ConfigureAwait(false);
        EnsureResponse(challengeEnvelope, RelayMessageTypes.AuthChallenge, requestId);
        var challenge = challengeEnvelope.ReadPayload<AuthChallengePayload>();
        var signature = _identity.Sign(AuthCanonicalPayload.Build(
            PrincipalRole.Device,
            _identity.DeviceId,
            challenge.ChallengeId,
            challenge.Nonce,
            challenge.ExpiresAt));
        await SendDirectAsync(socket, RelayEnvelope.Create(
            RelayMessageTypes.AuthResponse,
            new AuthResponsePayload(challenge.ChallengeId, signature),
            requestId,
            _identity.DeviceId), cancellationToken).ConfigureAwait(false);
        var authEnvelope = await ReceiveDirectAsync(socket, cancellationToken).ConfigureAwait(false);
        EnsureResponse(authEnvelope, RelayMessageTypes.AuthOk, requestId);
        return authEnvelope.ReadPayload<AuthOkPayload>();
    }

    private async Task WriterLoopAsync(
        ClientWebSocket socket,
        ChannelReader<RelayEnvelope> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendDirectAsync(socket, envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReaderLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await ReceiveDirectAsync(socket, cancellationToken).ConfigureAwait(false);
            if (envelope.RequestId is not null && _pending.TryRemove(envelope.RequestId, out var completion))
            {
                completion.TrySetResult(envelope);
                continue;
            }

            switch (envelope.Type)
            {
                case RelayMessageTypes.ControlSteer:
                case RelayMessageTypes.ControlInterrupt:
                case RelayMessageTypes.ControlApproval:
                    await HandleControlAsync(envelope, cancellationToken).ConfigureAwait(false);
                    break;
                case RelayMessageTypes.PairingCompleted:
                    {
                        var pairing = envelope.ReadPayload<PairingCompletedPayload>();
                        lock (_pairedControllers)
                        {
                            _pairedControllers.Add(pairing.ControllerId);
                            _state.SetPairedControllerCount(_pairedControllers.Count);
                        }

                        break;
                    }
                case RelayMessageTypes.PairingRevoked:
                    {
                        var pairing = envelope.ReadPayload<PairingRevokedPayload>();
                        lock (_pairedControllers)
                        {
                            _pairedControllers.Remove(pairing.ControllerId);
                            _state.SetPairedControllerCount(_pairedControllers.Count);
                        }

                        break;
                    }
                case RelayMessageTypes.Error:
                    {
                        var error = envelope.ReadPayload<ErrorPayload>();
                        _log.Warning("relay_error", $"Relay error code={error.Code}");
                        break;
                    }
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        ChannelWriter<RelayEnvelope> writer,
        string connectionId,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteAsync(RelayEnvelope.Create(
                RelayMessageTypes.Heartbeat,
                new HeartbeatPayload(connectionId, Volatile.Read(ref _latestSnapshot).Revision),
                deviceId: _identity.DeviceId), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleControlAsync(RelayEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.DeviceId != _identity.DeviceId || string.IsNullOrWhiteSpace(envelope.ControllerId))
        {
            return;
        }

        ControlDispatchResult result;
        try
        {
            switch (envelope.Type)
            {
                case RelayMessageTypes.ControlSteer:
                    {
                        var payload = envelope.ReadPayload<SteerControlPayload>();
                        result = await _dispatcher.SteerAsync(
                            payload.ThreadId,
                            payload.ExpectedTurnId,
                            payload.Text,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case RelayMessageTypes.ControlInterrupt:
                    {
                        var payload = envelope.ReadPayload<InterruptControlPayload>();
                        result = await _dispatcher.InterruptAsync(
                            payload.ThreadId,
                            payload.TurnId,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case RelayMessageTypes.ControlApproval:
                    {
                        var payload = envelope.ReadPayload<ApprovalControlPayload>();
                        result = await _dispatcher.ResolveApprovalAsync(
                            payload.ApprovalId,
                            payload.Decision,
                            envelope.ControllerId,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                default:
                    return;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            result = new ControlDispatchResult(
                false,
                "INVALID_CONTROL_PAYLOAD",
                "Remote control payload is invalid.",
                null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error("remote_control_failed", "Remote control dispatch failed", exception);
            result = new ControlDispatchResult(
                false,
                "INTERNAL_ERROR",
                "Remote control failed inside the Agent.",
                null);
        }

        TryEnqueue(RelayEnvelope.Create(
            RelayMessageTypes.ControlResult,
            new ControlResultPayload(
                result.Succeeded
                    ? envelope.Type == RelayMessageTypes.ControlInterrupt
                        ? ControlResultStatus.Accepted
                        : ControlResultStatus.Succeeded
                    : ControlResultStatus.Failed,
                result.ErrorCode,
                result.ErrorMessage,
                result.Result),
            envelope.RequestId,
            _identity.DeviceId,
            envelope.ControllerId));
    }

    private async Task<RelayEnvelope> SendRequestAsync<TPayload>(
        string type,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var requestId = NewRequestId();
        var completion = new TaskCompletionSource<RelayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("Duplicate Relay request ID.");
        }

        try
        {
            if (!TryEnqueue(RelayEnvelope.Create(
                    type,
                    payload,
                    requestId,
                    _identity.DeviceId)))
            {
                throw new AgentException("RELAY_OFFLINE", "Relay is offline.");
            }

            return await completion.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private void OnSnapshotChanged(CodexStateSnapshot snapshot)
    {
        Volatile.Write(ref _latestSnapshot, MapSnapshot(snapshot));
        TryEnqueue(RelayEnvelope.Create(
            RelayMessageTypes.CodexSnapshot,
            Volatile.Read(ref _latestSnapshot),
            deviceId: _identity.DeviceId));
    }

    private void OnServerMessage(JsonElement message)
    {
        var domainEvent = DomainEventNormalizer.Normalize(message, _state.Snapshot);
        if (domainEvent is not null)
        {
            TryEnqueue(RelayEnvelope.Create(
                RelayMessageTypes.CodexEvent,
                domainEvent,
                deviceId: _identity.DeviceId));
        }
    }

    private void OnApprovalRequested(PendingApprovalSnapshot approval)
    {
        var payload = new ApprovalRequestedPayload(
            approval.ApprovalId,
            approval.Method,
            approval.ThreadId,
            approval.TurnId,
            approval.ItemId,
            approval.Command,
            approval.Cwd,
            approval.Reason,
            approval.AvailableDecisions,
            approval.RequestedAt.ToUnixTimeMilliseconds());
        TryEnqueue(RelayEnvelope.Create(
            RelayMessageTypes.CodexEvent,
            ToEvent("ApprovalRequested", approval.ThreadId, approval.TurnId, approval.ItemId, payload),
            deviceId: _identity.DeviceId));
    }

    private void OnApprovalResolved(PendingApprovalSnapshot approval)
    {
        var payload = new ApprovalResolvedPayload(
            approval.ApprovalId,
            approval.ResolvedBy,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        TryEnqueue(RelayEnvelope.Create(
            RelayMessageTypes.CodexEvent,
            ToEvent("ApprovalResolved", approval.ThreadId, approval.TurnId, approval.ItemId, payload),
            deviceId: _identity.DeviceId));
    }

    private CodexEventPayload ToEvent<TPayload>(
        string kind,
        string? threadId,
        string? turnId,
        string? itemId,
        TPayload payload) =>
        new(
            string.Concat("evt_", Guid.NewGuid().ToString("N")),
            _state.Snapshot.Revision,
            kind,
            threadId,
            turnId,
            itemId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            JsonSerializer.SerializeToElement(payload, RelayJson.Options));

    private bool TryEnqueue(RelayEnvelope envelope)
    {
        lock (_connectionGate)
        {
            return _activeOutbound?.Writer.TryWrite(envelope) == true;
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pending in _pending.ToArray())
        {
            if (_pending.TryRemove(pending.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static async Task SendDirectAsync(
        ClientWebSocket socket,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, RelayJson.Options);
        await socket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RelayEnvelope> ReceiveDirectAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("Relay closed the WebSocket.");
                }

                if (result.MessageType != WebSocketMessageType.Text ||
                    stream.Length + result.Count > _options.MaxMessageBytes)
                {
                    throw new JsonException("Invalid Relay WebSocket message.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var json = StrictUtf8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
                    return JsonSerializer.Deserialize<RelayEnvelope>(json, RelayJson.Options) ??
                           throw new JsonException("Relay envelope is null.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureResponse(RelayEnvelope envelope, string expectedType, string requestId)
    {
        if (envelope.Type == RelayMessageTypes.Error)
        {
            var error = envelope.ReadPayload<ErrorPayload>();
            throw new AgentException(error.Code, error.Message);
        }

        if (envelope.Type != expectedType || envelope.RequestId != requestId)
        {
            throw new JsonException($"Expected {expectedType}, received {envelope.Type}.");
        }
    }

    private static Uri BuildDeviceEndpoint(Uri root)
    {
        var builder = new UriBuilder(root)
        {
            Scheme = root.Scheme switch
            {
                "https" => "wss",
                "http" => "ws",
                _ => root.Scheme,
            },
            Path = string.Concat(root.AbsolutePath.TrimEnd('/'), "/ws/device"),
        };
        return builder.Uri;
    }

    private static CodexSnapshotPayload MapSnapshot(CodexStateSnapshot snapshot) => new(
        snapshot.Revision,
        snapshot.Status.ToString(),
        snapshot.ActiveThreadId,
        snapshot.ActiveTurnId,
        snapshot.StartedAt?.ToUnixTimeMilliseconds(),
        snapshot.LastActivityAt.ToUnixTimeMilliseconds(),
        snapshot.CurrentProject,
        snapshot.CurrentActivity,
        snapshot.RunningCommand,
        snapshot.ChangedFiles,
        snapshot.PendingApprovalCount,
        snapshot.LastAgentMessage,
        snapshot.LastError);

    private static string NewRequestId() => string.Concat("req_", Guid.NewGuid().ToString("N"));
}
