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
                case RelayMessageTypes.PairingCompleted:
                    await SendInitialStateAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case RelayMessageTypes.ControlSteer:
                case RelayMessageTypes.ControlInterrupt:
                case RelayMessageTypes.ControlApproval:
                    await SendAsync(RelayEnvelope.Create(
                        RelayMessageTypes.ControlResult,
                        new ControlResultPayload(
                            envelope.Type == RelayMessageTypes.ControlInterrupt
                                ? ControlResultStatus.Accepted
                                : ControlResultStatus.Succeeded,
                            null,
                            null,
                            null),
                        envelope.RequestId,
                        DeviceId,
                        envelope.ControllerId), cancellationToken).ConfigureAwait(false);
                    if (envelope.Type == RelayMessageTypes.ControlApproval)
                    {
                        await SendEventAsync(
                            "ApprovalResolved",
                            new ApprovalResolvedPayload("apr-system", envelope.ControllerId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                            cancellationToken).ConfigureAwait(false);
                    }

                    break;
            }
        }
    }

    private async Task SendInitialStateAsync(CancellationToken cancellationToken)
    {
        await SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.CodexSnapshot,
            new CodexSnapshotPayload(
                10,
                "Thinking",
                "thr-system",
                "turn-system",
                DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "D:\\Projects\\SystemTest",
                "Running tests",
                "dotnet test",
                ["src/System.cs"],
                1,
                "等待远程控制",
                null),
            deviceId: DeviceId), cancellationToken).ConfigureAwait(false);
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
