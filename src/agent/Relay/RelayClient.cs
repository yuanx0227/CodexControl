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
    private static readonly int[] BackoffSeconds = [1, 2, 3, 5, 10];

    private readonly AgentOptions _options;
    private readonly DeviceIdentity _identity;
    private readonly CodexStateManager _state;
    private readonly AppServerBridge _bridge;
    private readonly RemoteControlDispatcher _dispatcher;
    private readonly AgentLog _log;
    private readonly IReadOnlyList<PendingPairingRevocation> _preReadyRevocations;
    private readonly Action<long>? _revocationSynchronized;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _firstAuthenticated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayEnvelope>> _pending = new();
    private readonly HashSet<string> _pairedControllers = new(StringComparer.Ordinal);
    private readonly object _connectionGate = new();
    private readonly object _eventPublishGate = new();
    private readonly SessionEventJournal _journal = new();
    private readonly ControlSubmissionLedger _submissions;
    private readonly ConcurrentDictionary<string, string> _watches = new(StringComparer.Ordinal);
    private int _recoveringWatches;
    private int _streamGap;

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
        AgentLog log,
        IReadOnlyList<PendingPairingRevocation>? preReadyRevocations = null,
        Action<long>? revocationSynchronized = null,
        SharedSessionRuntimeContext? runtimeContext = null)
    {
        _options = options;
        _submissions = runtimeContext?.GetSubmissionLedger(options.DataDirectory) ?? new ControlSubmissionLedger(options.DataDirectory);
        _identity = identity;
        _state = state;
        _bridge = bridge;
        _dispatcher = dispatcher;
        _log = log;
        _preReadyRevocations = preReadyRevocations ?? [];
        _revocationSynchronized = revocationSynchronized;
        _latestSnapshot = MapSnapshot(state.Snapshot);
        _state.SnapshotChanged += OnSnapshotChanged;
        _bridge.ServerMessageReceived += OnServerMessage;
        _bridge.Approvals.ApprovalRequested += OnApprovalRequested;
        _bridge.Approvals.ApprovalResolved += OnApprovalResolved;
        _bridge.Approvals.ApprovalResolving += OnApprovalResolving;
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

    public event Action<PairingConfirmationRequestedPayload>? PairingConfirmationRequested;

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

    public async Task<RelayEnvelope> ResolvePairingAsync(
        PairingConfirmationResolvePayload payload,
        CancellationToken cancellationToken) =>
        await SendRequestAsync(
            RelayMessageTypes.PairingConfirmationResolve,
            payload,
            cancellationToken).ConfigureAwait(false);

    public async Task<PairingListResultPayload> ListPairingsAsync(CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            RelayMessageTypes.PairingList,
            new PairingListPayload(),
            cancellationToken).ConfigureAwait(false);
        return response.ReadPayload<PairingListResultPayload>();
    }

    public async Task<PairingUpdatedPayload> UpdatePairingAsync(
        PairingUpdatePayload payload,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            RelayMessageTypes.PairingUpdate,
            payload,
            cancellationToken).ConfigureAwait(false);
        return response.ReadPayload<PairingUpdatedPayload>();
    }

    public async Task RevokePairingAsync(long pairingId, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            RelayMessageTypes.PairingRevoke,
            new PairingRevokePayload(pairingId),
            cancellationToken).ConfigureAwait(false);
        _ = response.ReadPayload<PairingRevokedPayload>();
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
        _bridge.Approvals.ApprovalResolving -= OnApprovalResolving;
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
        foreach (var pending in _preReadyRevocations)
        {
            await SendDirectAsync(socket, RelayEnvelope.Create(
                RelayMessageTypes.PairingRevoke,
                new PairingRevokePayload(pending.PairingId),
                pending.RequestId,
                _identity.DeviceId), cancellationToken).ConfigureAwait(false);
            var revoked = await ReceiveDirectAsync(socket, cancellationToken).ConfigureAwait(false);
            EnsureResponse(revoked, RelayMessageTypes.PairingRevoked, pending.RequestId);
            _revocationSynchronized?.Invoke(pending.PairingId);
        }

        var readyRequestId = NewRequestId();
        await SendDirectAsync(socket, RelayEnvelope.Create(
            RelayMessageTypes.DeviceReady,
            new { },
            readyRequestId,
            _identity.DeviceId), cancellationToken).ConfigureAwait(false);
        var ready = await ReceiveDirectAsync(socket, cancellationToken).ConfigureAwait(false);
        EnsureResponse(ready, RelayMessageTypes.DeviceReadyAck, readyRequestId);
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
        await using var controls = new BoundedControlDispatcher(HandleControlAsync, OnControlDispatchFailure,
            connectionCancellation.Token);
        var writerTask = WriterLoopAsync(socket, outbound.Reader, connectionCancellation.Token);
        var readerTask = ReaderLoopAsync(socket, controls, connectionCancellation.Token);
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
            new AuthHelloPayload(
                PrincipalRole.Device,
                _identity.DeviceId,
                typeof(RelayClient).Assembly.GetName().Version?.ToString() ?? "0.2.0"),
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

    private async Task ReaderLoopAsync(ClientWebSocket socket, BoundedControlDispatcher controls, CancellationToken cancellationToken)
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
                case RelayMessageTypes.ControlSessionOptions:
                case RelayMessageTypes.ControlThreadList:
                case RelayMessageTypes.ControlThreadRead:
                case RelayMessageTypes.ControlThreadStart:
                case RelayMessageTypes.ControlThreadResume:
                case RelayMessageTypes.ControlThreadWatch:
                case RelayMessageTypes.ControlThreadUnwatch:
                case RelayMessageTypes.ControlThreadSend:
                    if (!controls.TryDispatch(envelope))
                        TryEnqueue(RelayEnvelope.Create(RelayMessageTypes.ControlResult,
                            new ControlResultPayload(ControlResultStatus.Failed, "CONTROL_BUSY",
                                "当前控制请求较多，此请求未执行。", null), envelope.RequestId,
                            _identity.DeviceId, envelope.ControllerId));
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
                case RelayMessageTypes.PairingConfirmationRequested:
                    {
                        var requested = envelope.ReadPayload<PairingConfirmationRequestedPayload>();
                        var handlers = PairingConfirmationRequested;
                        if (handlers is not null)
                        {
                            foreach (Action<PairingConfirmationRequestedPayload> handler in handlers.GetInvocationList())
                            {
                                try
                                {
                                    handler(requested);
                                }
                                catch
                                {
                                }
                            }
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

    private void OnControlDispatchFailure(RelayEnvelope envelope, Exception exception)
    {
        _log.Warning("control_dispatch_failed", $"Control worker failed: {exception.GetType().Name}");
        TryEnqueue(RelayEnvelope.Create(RelayMessageTypes.ControlResult,
            new ControlResultPayload(ControlResultStatus.Failed, "CONTROL_OUTCOME_UNKNOWN",
                "控制请求未得到确定结果，请同步后核实；没有自动重发。", null),
            envelope.RequestId, _identity.DeviceId, envelope.ControllerId));
    }

    private async Task HeartbeatLoopAsync(
        ChannelWriter<RelayEnvelope> writer,
        string connectionId,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_bridge.IsSharedSession && Interlocked.Exchange(ref _streamGap, 0) != 0)
            {
                PublishEvent(ToEvent("StreamGap", null, null, null, new { resyncRequired = true }));
            }
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
        var trackSubmission = _bridge.IsSharedSession && ControlSubmissionLedger.IsMutation(envelope.Type);
        if (trackSubmission && _submissions.Begin(envelope) is { } previous)
        {
            TryEnqueue(RelayEnvelope.Create(RelayMessageTypes.ControlResult, previous, envelope.RequestId,
                _identity.DeviceId, envelope.ControllerId));
            return;
        }
        try
        {
            switch (envelope.Type)
            {
                case RelayMessageTypes.ControlThreadWatch:
                    result = await WatchThreadAsync(envelope.ReadPayload<ThreadWatchControlPayload>(), envelope.ControllerId,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RelayMessageTypes.ControlThreadUnwatch:
                    _watches.TryRemove(envelope.ControllerId + ":" + envelope.ReadPayload<ThreadUnwatchControlPayload>().ThreadId, out _);
                    result = new(true, null, null, null);
                    break;
                case RelayMessageTypes.ControlThreadSend:
                    var send = envelope.ReadPayload<ThreadSendControlPayload>();
                    result = await _dispatcher.SharedSessions.SendAsync(send.ThreadId, send.Text, send.ExpectedTurnId, cancellationToken)
                        .ConfigureAwait(false);
                    break;
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
                case RelayMessageTypes.ControlSessionOptions:
                    {
                        _ = envelope.ReadPayload<SessionOptionsControlPayload>();
                        result = await _dispatcher.GetSessionOptionsAsync(cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case RelayMessageTypes.ControlThreadList:
                    {
                        var payload = envelope.ReadPayload<ThreadListControlPayload>();
                        result = await _dispatcher.ListThreadsAsync(
                            payload.Limit,
                            payload.Cursor,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case RelayMessageTypes.ControlThreadRead:
                    {
                        var payload = envelope.ReadPayload<ThreadReadControlPayload>();
                        result = await _dispatcher.ReadThreadAsync(
                            payload.ThreadId,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case RelayMessageTypes.ControlThreadStart:
                    {
                        var payload = envelope.ReadPayload<ThreadStartControlPayload>();
                        result = await _dispatcher.StartThreadAsync(
                            payload.Cwd,
                            payload.Text,
                            payload.Model,
                            payload.ApprovalPolicy,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case RelayMessageTypes.ControlThreadResume:
                    {
                        var payload = envelope.ReadPayload<ThreadResumeControlPayload>();
                        result = await _dispatcher.ResumeThreadAsync(
                            payload.ThreadId,
                            payload.Text,
                            payload.Model,
                            payload.ApprovalPolicy,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                default:
                    return;
            }
        }
        catch (AgentException exception)
        {
            result = new(false, exception.Code, "共享会话操作不可用；未修改权限或自动重发。", null);
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

        var controlResult = new ControlResultPayload(
                result.Succeeded
                    ? envelope.Type == RelayMessageTypes.ControlInterrupt
                        ? ControlResultStatus.Accepted
                        : ControlResultStatus.Succeeded
                    : ControlResultStatus.Failed,
                result.ErrorCode,
                result.ErrorMessage,
                result.Result);
        if (trackSubmission) _submissions.Complete(envelope, controlResult);
        TryEnqueue(RelayEnvelope.Create(
            RelayMessageTypes.ControlResult,
            controlResult,
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
        _journal.SetConnection(_bridge.ConnectionEpoch);
        Volatile.Write(ref _latestSnapshot, MapSnapshot(snapshot));
        TryEnqueue(RelayEnvelope.Create(
            RelayMessageTypes.CodexSnapshot,
            Volatile.Read(ref _latestSnapshot),
            deviceId: _identity.DeviceId));
        if (_bridge.IsSharedSession && snapshot.ConnectionState == "online" &&
            _watches.Values.Any(epoch => epoch != _bridge.ConnectionEpoch) && Interlocked.Exchange(ref _recoveringWatches, 1) == 0)
            _ = RecoverWatchesAsync();
    }

    private void OnServerMessage(JsonElement message)
    {
        var domainEvent = DomainEventNormalizer.Normalize(message, _state.Snapshot);
        if (domainEvent is not null)
        {
            PublishEvent(domainEvent);
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
            approval.RequestedAt.ToUnixTimeMilliseconds(), approval.IsResolving);
        PublishEvent(ToEvent("ApprovalRequested", approval.ThreadId, approval.TurnId, approval.ItemId, payload));
    }

    private void OnApprovalResolving(PendingApprovalSnapshot approval) =>
        PublishEvent(ToEvent("ApprovalResolving", approval.ThreadId, approval.TurnId, approval.ItemId,
            new { approvalId = approval.ApprovalId, isResolving = true }));

    private void OnApprovalResolved(PendingApprovalSnapshot approval)
    {
        var payload = new ApprovalResolvedPayload(
            approval.ApprovalId,
            approval.ResolvedBy,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        PublishEvent(ToEvent("ApprovalResolved", approval.ThreadId, approval.TurnId, approval.ItemId, payload));
    }

    private void PublishEvent(CodexEventPayload value)
    {
        // Numbering and enqueueing are one operation across protocol, approval and
        // heartbeat callbacks. TryEnqueue never waits for a network write.
        lock (_eventPublishGate)
        {
            _journal.SetConnection(_bridge.ConnectionEpoch);
            // Budget the actual wire envelope, including UTF-8/JSON escaping and
            // attachments. The maximum sequence width also covers the later Append.
            if (_bridge.IsSharedSession)
                value = value with { ServiceInstanceId = _bridge.ServiceInstanceId,
                    StreamEpoch = _journal.Cursor.Epoch, Sequence = long.MaxValue };
            value = ConstrainDomainEvent(value, _identity.DeviceId);
            if (_bridge.IsSharedSession) value = _journal.Append(value, _bridge.ServiceInstanceId);
            if (!TryEnqueue(RelayEnvelope.Create(RelayMessageTypes.CodexEvent, value, deviceId: _identity.DeviceId)))
                Interlocked.Exchange(ref _streamGap, 1);
        }
    }

    internal static CodexEventPayload ConstrainDomainEvent(CodexEventPayload value, string deviceId)
    {
        const int maximumEnvelopeBytes = 900 * 1024;
        int Size(CodexEventPayload candidate) => JsonSerializer.SerializeToUtf8Bytes(
            RelayEnvelope.Create(RelayMessageTypes.CodexEvent, candidate, deviceId: deviceId), RelayJson.Options).Length;
        if (Size(value) <= maximumEnvelopeBytes) return value;
        var incomplete = value with
        {
            Kind = "ContentIncomplete",
            Data = JsonSerializer.SerializeToElement(new
            {
                truncated = true, resyncRequired = true, reason = "EVENT_TOO_LARGE", originalKind = value.Kind,
            }, RelayJson.Options),
        };
        if (Size(incomplete) <= maximumEnvelopeBytes) return incomplete;
        // Malformed oversized identity fields cannot be safely attributed. Emit only
        // a small recovery signal instead of copying them or guessing UI focus.
        return new CodexEventPayload("evt_" + Guid.NewGuid().ToString("N"), value.Revision, "StreamGap",
            null, null, null, value.OccurredAt,
            JsonSerializer.SerializeToElement(new { resyncRequired = true, reason = "EVENT_IDENTITY_TOO_LARGE" }, RelayJson.Options),
            value.ServiceInstanceId, value.StreamEpoch, value.Sequence);
    }

    private async Task<ControlDispatchResult> WatchThreadAsync(ThreadWatchControlPayload payload, string controller, CancellationToken token)
    {
        if (payload.AfterSequence is < 0) return new(false, "INVALID_CURSOR", "Invalid stream cursor.", null);
        _journal.SetConnection(_bridge.ConnectionEpoch);
        var cursor = _journal.Cursor;
        var (_, reason) = await _dispatcher.SharedSessions.JoinAsync(payload.ThreadId, token, payload.AllowJoin).ConfigureAwait(false);
        _watches[controller + ":" + payload.ThreadId] = _bridge.ConnectionEpoch;
        var replay = _journal.Read(payload.StreamEpoch, payload.AfterSequence, payload.ThreadId);
        var resyncRequired = !replay.Complete;
        CodexThreadReadResultPayload history;
        var sequence = payload.AfterSequence ?? cursor.Sequence;
        if (!resyncRequired)
        {
            // The client already has a view at this cursor. Empty history is deliberately
            // not a replacement snapshot; preserve it and replay only missing events.
            history = new(payload.ThreadId, null, null, [], [], false);
            var replayResult = BuildWatchResult(payload.ThreadId, cursor.Epoch, sequence, history, replay.Events,
                resyncRequired: false, reason);
            if (TrySerializeWatchResult(replayResult, out var serializedReplay))
                return new(true, null, null, serializedReplay);
            // The retained stream is larger than one response. A current snapshot may
            // fit; do not emit an oversized frame or label a partial replay complete.
            resyncRequired = true;
        }
        if (resyncRequired)
        {
            var historyResult = await _dispatcher.ReadThreadAsync(payload.ThreadId, token).ConfigureAwait(false);
            if (!historyResult.Succeeded || historyResult.Result is null) return historyResult;
            history = historyResult.Result.Value.Deserialize<CodexThreadReadResultPayload>(RelayJson.Options)!;
            replay = _journal.Read(cursor.Epoch, cursor.Sequence, payload.ThreadId);
            if (!replay.Complete) return new(false, "STREAM_CHANGED_RETRY_WATCH", "连接或事件窗口已变化，请重新同步。", null);
            sequence = cursor.Sequence;
        }
        else throw new InvalidOperationException("Watch response selection failed.");
        var result = BuildWatchResult(payload.ThreadId, cursor.Epoch, sequence, history, replay.Events, true, reason);
        return TrySerializeWatchResult(result, out var serialized)
            ? new(true, null, null, serialized)
            : new(false, "WATCH_RESPONSE_TOO_LARGE", "会话同步内容超过单次传输上限；已保留原内容，当前尚未同步。", null);
    }

    private ThreadWatchResultPayload BuildWatchResult(string threadId, string epoch, long sequence,
        CodexThreadReadResultPayload history, IReadOnlyList<CodexEventPayload> events, bool resyncRequired, string? reason)
    {
        var approvals = _bridge.Approvals.PendingApprovals.Where(a => a.ThreadId == threadId)
            .Select(a => new ApprovalRequestedPayload(a.ApprovalId, a.Method, a.ThreadId, a.TurnId, a.ItemId,
                a.Command, a.Cwd, a.Reason, a.AvailableDecisions, a.RequestedAt.ToUnixTimeMilliseconds(), a.IsResolving)).ToArray();
        return new(threadId, _bridge.ServiceInstanceId, epoch, sequence,
            history, MapSnapshot(_state.Snapshot), approvals, events, resyncRequired, reason is null, reason);
    }

    internal static bool TrySerializeWatchResult(ThreadWatchResultPayload result, out JsonElement serialized)
    {
        serialized = JsonSerializer.SerializeToElement(result, RelayJson.Options);
        var payload = new ControlResultPayload(ControlResultStatus.Succeeded, null, null, serialized);
        if (JsonSerializer.SerializeToUtf8Bytes(payload, RelayJson.Options).Length <= 256 * 1024) return true;
        serialized = default;
        return false;
    }

    private async Task RecoverWatchesAsync()
    {
        try
        {
            foreach (var entry in _watches.ToArray())
            {
                var threadId = entry.Key[(entry.Key.LastIndexOf(':') + 1)..];
                await _dispatcher.SharedSessions.JoinAsync(threadId, _lifetime.Token).ConfigureAwait(false);
                _watches[entry.Key] = _bridge.ConnectionEpoch;
            }
            PublishEvent(ToEvent("StreamGap", null, null, null, new { resyncRequired = true }));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        { _log.Warning("shared_watch_restore_failed", "Shared subscriptions require resynchronization."); }
        catch (OperationCanceledException) { }
        finally { Interlocked.Exchange(ref _recoveringWatches, 0); }
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

    private CodexSnapshotPayload MapSnapshot(CodexStateSnapshot snapshot) => new(
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
        snapshot.LastError,
        typeof(RelayClient).Assembly.GetName().Version?.ToString(3),
        snapshot.ActiveTurns.Select(static active => new CodexActiveTurnPayload(
            active.ThreadId,
            active.TurnId,
            active.Status.ToString(),
            active.StartedAt.ToUnixTimeMilliseconds(),
            active.LastActivityAt.ToUnixTimeMilliseconds(),
            active.CurrentProject,
            active.CurrentActivity,
            active.RunningCommand,
            active.ChangedFiles,
            active.PendingApprovalCount,
            active.LastAgentMessage,
            active.LastError)).ToArray(),
        _bridge.ServiceInstanceId, _journal.Cursor.Epoch, _journal.Cursor.Sequence, _bridge.IsSharedSession,
        snapshot.ConnectionState, _bridge.IsSharedSession ? ["sharedSessionV1"] : [],
        snapshot.Threads.Select(thread => new CodexThreadStatePayload(thread.ThreadId, thread.ThreadState,
            thread.ActiveTurnId, thread.LastTurnId, thread.LastTurnStatus, thread.Activity.ToString(),
            thread.WaitingOnApproval, thread.WaitingOnUserInput, thread.RequiresRefresh ? "stale" : "current")).ToArray());

    private static string NewRequestId() => string.Concat("req_", Guid.NewGuid().ToString("N"));
}
