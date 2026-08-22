using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.Relay.Tests;

internal sealed class RelayTestClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ECDsa _key;
    private readonly bool _ownsKey;

    public RelayTestClient(PrincipalRole role, string principalId, string name, ECDsa? key = null)
    {
        Role = role;
        PrincipalId = principalId;
        Name = name;
        _ownsKey = key is null;
        _key = key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = P256Keys.ExportPublicKey(_key);
    }

    public PrincipalRole Role { get; }
    public string PrincipalId { get; }
    public string Name { get; }
    public string PublicKey { get; }

    public async Task ConnectAsync(Uri baseUri)
    {
        var path = Role == PrincipalRole.Device ? "/ws/device" : "/ws/controller";
        var endpoint = new UriBuilder(new Uri(baseUri, path))
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        }.Uri;
        await _socket.ConnectAsync(endpoint, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task RegisterAndAuthenticateDeviceAsync()
    {
        EnsureRole(PrincipalRole.Device);
        var registerId = NewRequestId();
        await SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.DeviceRegister,
            new DeviceRegisterPayload(PrincipalId, Name, PublicKey),
            registerId,
            PrincipalId)).ConfigureAwait(false);
        _ = await ReceiveAsync(RelayMessageTypes.DeviceRegistered, registerId).ConfigureAwait(false);
        await AuthenticateAsync().ConfigureAwait(false);
    }

    public async Task AuthenticateAsync()
    {
        var requestId = NewRequestId();
        await SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.AuthHello,
            new AuthHelloPayload(Role, PrincipalId, "test-1.0"),
            requestId,
            Role == PrincipalRole.Device ? PrincipalId : null,
            Role == PrincipalRole.Controller ? PrincipalId : null)).ConfigureAwait(false);
        var challengeEnvelope = await ReceiveAsync(RelayMessageTypes.AuthChallenge, requestId).ConfigureAwait(false);
        var challenge = challengeEnvelope.ReadPayload<AuthChallengePayload>();
        var canonical = AuthCanonicalPayload.Build(
            Role,
            PrincipalId,
            challenge.ChallengeId,
            challenge.Nonce,
            challenge.ExpiresAt);
        await SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.AuthResponse,
            new AuthResponsePayload(challenge.ChallengeId, P256Keys.Sign(_key, canonical)),
            requestId,
            Role == PrincipalRole.Device ? PrincipalId : null,
            Role == PrincipalRole.Controller ? PrincipalId : null)).ConfigureAwait(false);
        _ = await ReceiveAsync(RelayMessageTypes.AuthOk, requestId).ConfigureAwait(false);
    }

    public PairingClaimPayload CreatePairingClaim(string code)
    {
        EnsureRole(PrincipalRole.Controller);
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var canonical = PairingProofCanonicalPayload.Build(code, PrincipalId, PublicKey, nonce);
        return new PairingClaimPayload(
            code,
            PrincipalId,
            Name,
            PublicKey,
            nonce,
            P256Keys.Sign(_key, canonical));
    }

    public async Task SendAsync(RelayEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, RelayJson.Options);
        await _socket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<RelayEnvelope> ReceiveAsync(
        string expectedType,
        string? requestId = null,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var envelope = await ReceiveOneAsync(deadline - DateTimeOffset.UtcNow).ConfigureAwait(false);
            if (envelope.Type == expectedType && (requestId is null || envelope.RequestId == requestId))
            {
                return envelope;
            }
        }

        throw new TimeoutException($"Did not receive {expectedType} requestId={requestId}.");
    }

    public async Task CloseAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "test complete",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _socket.Dispose();
        if (_ownsKey)
        {
            _key.Dispose();
        }
    }

    public static string NewRequestId() => string.Concat("req_", Guid.NewGuid().ToString("N"));

    private async Task<RelayEnvelope> ReceiveOneAsync(TimeSpan timeout)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None)
                .AsTask()
                .WaitAsync(timeout)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Relay socket closed unexpectedly.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonSerializer.Deserialize<RelayEnvelope>(stream.ToArray(), RelayJson.Options) ??
                       throw new JsonException("Relay envelope is null.");
            }
        }
    }

    private void EnsureRole(PrincipalRole expected)
    {
        if (Role != expected)
        {
            throw new InvalidOperationException($"Expected role {expected}, actual {Role}.");
        }
    }
}
