using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.SystemHost;

internal sealed class SystemDeviceClient : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Uri _relayRoot;
    private readonly ClientWebSocket _socket = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _readerTask;
    private Task? _heartbeatTask;
    private string? _connectionId;
    private long _revision = 10;

    public SystemDeviceClient(Uri relayRoot)
    {
        _relayRoot = relayRoot;
        DeviceId = string.Concat("dev_", Guid.NewGuid().ToString("N"));
        PublicKey = P256Keys.ExportPublicKey(_key);
    }

    public string DeviceId { get; }
    public string PublicKey { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var endpoint = new UriBuilder(new Uri(_relayRoot, "/ws/device")) { Scheme = "ws" }.Uri;
        await _socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var registerId = RequestId();
        await SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.DeviceRegister,
            new DeviceRegisterPayload(DeviceId, "SYSTEM-DEV-PC", PublicKey),
            registerId,
            DeviceId), cancellationToken).ConfigureAwait(false);
        _ = await ReceiveExpectedAsync(RelayMessageTypes.DeviceRegistered, registerId, cancellationToken)
            .ConfigureAwait(false);

        var authId = RequestId();
        await SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.AuthHello,
            new AuthHelloPayload(PrincipalRole.Device, DeviceId, "system-test"),
            authId,
            DeviceId), cancellationToken).ConfigureAwait(false);
        var challenge = (await ReceiveExpectedAsync(RelayMessageTypes.AuthChallenge, authId, cancellationToken)
            .ConfigureAwait(false)).ReadPayload<AuthChallengePayload>();
        var signature = P256Keys.Sign(_key, AuthCanonicalPayload.Build(
            PrincipalRole.Device,
            DeviceId,
            challenge.ChallengeId,
            challenge.Nonce,
            challenge.ExpiresAt));
        await SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.AuthResponse,
            new AuthResponsePayload(challenge.ChallengeId, signature),
            authId,
            DeviceId), cancellationToken).ConfigureAwait(false);
        var auth = (await ReceiveExpectedAsync(RelayMessageTypes.AuthOk, authId, cancellationToken)
            .ConfigureAwait(false)).ReadPayload<AuthOkPayload>();
        _connectionId = auth.ConnectionId;
        var readyId = RequestId();
        await SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.DeviceReady,
            new { },
            readyId,
            DeviceId), cancellationToken).ConfigureAwait(false);
        _ = await ReceiveExpectedAsync(RelayMessageTypes.DeviceReadyAck, readyId, cancellationToken)
            .ConfigureAwait(false);

        _readerTask = ReaderLoopAsync(_lifetime.Token);
        _heartbeatTask = HeartbeatLoopAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "system test stopped",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }

        foreach (var task in new[] { _readerTask, _heartbeatTask })
        {
            if (task is null) continue;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }

        _sendGate.Dispose();
        _lifetime.Dispose();
        _key.Dispose();
        _socket.Dispose();
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (envelope.Type)
            {
                case RelayMessageTypes.PairingConfirmationRequested:
                    {
                        var requested = envelope.ReadPayload<PairingConfirmationRequestedPayload>();
                        await SendAsync(RelayEnvelope.Create(
                            RelayMessageTypes.PairingConfirmationResolve,
                            new PairingConfirmationResolvePayload(
                                requested.PairingRequestId,
                                PairingDecision.Allow,
                                PairingPermissionProfile.Full),
                            RequestId(),
                            DeviceId,
                            requested.ControllerId), cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case RelayMessageTypes.PairingCompleted:
                    await SendInitialStateAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case RelayMessageTypes.ControlSteer:
                case RelayMessageTypes.ControlApproval:
                    await SendControlResultAsync(
                        envelope,
                        ControlResultStatus.Succeeded,
                        null,
                        cancellationToken).ConfigureAwait(false);
                    if (envelope.Type == RelayMessageTypes.ControlApproval)
                    {
                        await SendEventAsync(
                            "ApprovalResolved",
                            new ApprovalResolvedPayload("apr-system", envelope.ControllerId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                            cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case RelayMessageTypes.ControlInterrupt:
                    await SendControlResultAsync(
                        envelope,
                        ControlResultStatus.Accepted,
                        null,
                        cancellationToken).ConfigureAwait(false);
                    await SendSnapshotAsync(
                        "Interrupted",
                        null,
                        null,
                        "D:\\Projects\\SystemTest",
                        "任务已停止",
                        cancellationToken).ConfigureAwait(false);
                    var interrupted = envelope.ReadPayload<InterruptControlPayload>();
                    await SendAsync(RelayEnvelope.Create(
                        RelayMessageTypes.CodexEvent,
                        new CodexEventPayload(
                            string.Concat("evt_", Guid.NewGuid().ToString("N")),
                            Interlocked.Read(ref _revision),
                            "TurnCompleted", interrupted.ThreadId, interrupted.TurnId, null,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            JsonSerializer.SerializeToElement(new { status = "interrupted" }, RelayJson.Options)),
                        deviceId: DeviceId), cancellationToken).ConfigureAwait(false);
                    await SendEventAsync(
                        "ApprovalResolved",
                        new ApprovalResolvedPayload(
                            "apr-system",
                            "turn-interrupted",
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RelayMessageTypes.ControlSessionOptions:
                    await SendControlResultAsync(
                        envelope,
                        ControlResultStatus.Succeeded,
                        JsonSerializer.SerializeToElement(
                            new CodexSessionOptionsPayload(
                            [
                                new CodexModelOptionPayload(
                                    "gpt-5.6-sol",
                                    "gpt-5.6-sol",
                                    "GPT-5.6 Sol",
                                    "System test model",
                                    true),
                            ],
                            [
                                new CodexApprovalPolicyOptionPayload(
                                    "untrusted",
                                    "严格审批",
                                    "System test policy",
                                    true),
                            ]),
                            RelayJson.Options),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RelayMessageTypes.ControlThreadList:
                    await SendControlResultAsync(
                        envelope,
                        ControlResultStatus.Succeeded,
                        JsonSerializer.SerializeToElement(
                            new CodexThreadListResultPayload(
                            [
                                new CodexThreadSummaryPayload(
                                    "thr-history-system",
                                    "系统历史会话",
                                    "继续系统测试",
                                    "D:\\Projects\\SystemTest",
                                    DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds(),
                                    DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
                                    DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds(),
                                    "notLoaded",
                                    "appServer",
                                    "project-system"),
                            ],
                            [
                                new CodexProjectSummaryPayload(
                                    "project-system",
                                    "SystemTest",
                                    0,
                                    ["D:\\Projects\\SystemTest"]),
                            ],
                            null),
                            RelayJson.Options),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RelayMessageTypes.ControlThreadRead:
                    {
                        var threadId = envelope.ReadPayload<ThreadReadControlPayload>().ThreadId;
                        await SendControlResultAsync(
                            envelope,
                            ControlResultStatus.Succeeded,
                            JsonSerializer.SerializeToElement(
                                new CodexThreadReadResultPayload(
                                    threadId,
                                    "系统历史会话",
                                    "D:\\Projects\\SystemTest",
                                    [
                                        new CodexThreadHistoryEntryPayload(
                                            "item-history-user",
                                            "turn-history-system",
                                            "user",
                                            "系统历史用户消息",
                                            null,
                                            [],
                                            []),
                                        new CodexThreadHistoryEntryPayload(
                                            "item-history-agent",
                                            "turn-history-system",
                                            "assistant",
                                            "系统历史助手回复",
                                            "final_answer",
                                            [],
                                            []),
                                    ],
                                    [
                                        new CodexTurnTimingPayload(
                                            "turn-history-system",
                                            "completed",
                                            DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds(),
                                            DateTimeOffset.UtcNow.AddSeconds(-24).ToUnixTimeSeconds(),
                                            96_000),
                                    ],
                                    false),
                                RelayJson.Options),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case RelayMessageTypes.ControlThreadStart:
                    await SendThreadActionAsync(
                        envelope,
                        "thr-created-system",
                        "turn-created-system",
                        cancellationToken).ConfigureAwait(false);
                    break;
                case RelayMessageTypes.ControlThreadResume:
                    await SendThreadActionAsync(
                        envelope,
                        envelope.ReadPayload<ThreadResumeControlPayload>().ThreadId,
                        "turn-resumed-system",
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task SendInitialStateAsync(CancellationToken cancellationToken)
    {
        await SendSnapshotAsync(
            "Thinking",
            "thr-system",
            "turn-system",
            "D:\\Projects\\SystemTest",
            "Running tests",
            cancellationToken,
            "dotnet test",
            1,
            "等待远程控制").ConfigureAwait(false);
        await SendEventAsync(
            "ApprovalRequested",
            new ApprovalRequestedPayload(
                "apr-system",
                "item/commandExecution/requestApproval",
                "thr-system",
                "turn-system",
                "item-system",
                "git push origin main",
                "D:\\Projects\\SystemTest",
                "系统测试审批",
                [
                    JsonSerializer.SerializeToElement("accept", RelayJson.Options),
                    JsonSerializer.SerializeToElement("decline", RelayJson.Options),
                ],
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            cancellationToken).ConfigureAwait(false);
    }

    private Task SendControlResultAsync(
        RelayEnvelope request,
        ControlResultStatus status,
        JsonElement? result,
        CancellationToken cancellationToken) =>
        SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.ControlResult,
            new ControlResultPayload(status, null, null, result),
            request.RequestId,
            DeviceId,
            request.ControllerId), cancellationToken);

    private async Task SendThreadActionAsync(
        RelayEnvelope request,
        string threadId,
        string turnId,
        CancellationToken cancellationToken)
    {
        await SendControlResultAsync(
            request,
            ControlResultStatus.Succeeded,
            JsonSerializer.SerializeToElement(
                new CodexThreadActionResultPayload(threadId, turnId),
                RelayJson.Options),
            cancellationToken).ConfigureAwait(false);
        await SendSnapshotAsync(
            "Thinking",
            threadId,
            turnId,
            "D:\\Projects\\SystemTest",
            "远程会话运行中",
            cancellationToken).ConfigureAwait(false);
    }

    private Task SendSnapshotAsync(
        string status,
        string? threadId,
        string? turnId,
        string project,
        string activity,
        CancellationToken cancellationToken,
        string? command = null,
        int pendingApprovalCount = 0,
        string? lastAgentMessage = null) =>
        SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.CodexSnapshot,
            new CodexSnapshotPayload(
                Interlocked.Increment(ref _revision),
                status,
                threadId,
                turnId,
                turnId is null ? null : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                project,
                activity,
                command,
                ["src/System.cs"],
                pendingApprovalCount,
                lastAgentMessage,
                null),
            deviceId: DeviceId), cancellationToken);

    private Task SendEventAsync<T>(string kind, T data, CancellationToken cancellationToken) =>
        SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.CodexEvent,
            new CodexEventPayload(
                string.Concat("evt_", Guid.NewGuid().ToString("N")),
                11,
                kind,
                "thr-system",
                "turn-system",
                "item-system",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                JsonSerializer.SerializeToElement(data, RelayJson.Options)),
            deviceId: DeviceId), cancellationToken);

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendAsync(RelayEnvelope.Create(
                RelayMessageTypes.Heartbeat,
                new HeartbeatPayload(_connectionId!, 10),
                deviceId: DeviceId), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RelayEnvelope> ReceiveExpectedAsync(
        string type,
        string requestId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var envelope = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (envelope.Type == type && envelope.RequestId == requestId) return envelope;
            if (envelope.Type == RelayMessageTypes.Error)
            {
                var error = envelope.ReadPayload<ErrorPayload>();
                throw new InvalidOperationException($"{error.Code}: {error.Message}");
            }
        }
    }

    private async Task SendAsync(RelayEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, RelayJson.Options);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<RelayEnvelope> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("Relay closed system device socket.");
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

    private static string RequestId() => string.Concat("req_", Guid.NewGuid().ToString("N"));
}
