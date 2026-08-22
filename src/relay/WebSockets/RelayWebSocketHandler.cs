using System.Net.WebSockets;
using System.Text.Json;
using CodexControl.Protocol;
using CodexControl.Relay.Authentication;
using CodexControl.Relay.Configuration;
using CodexControl.Relay.Pairing;
using CodexControl.Relay.Routing;
using CodexControl.Relay.Security;
using Microsoft.AspNetCore.Connections;

namespace CodexControl.Relay.WebSockets;

public sealed class RelayWebSocketHandler
{
    private readonly RelayOptions _options;
    private readonly AuthService _auth;
    private readonly PairingService _pairing;
    private readonly RelayRouter _router;
    private readonly ConnectionRegistry _connections;
    private readonly SlidingWindowRateLimiter _rateLimiter;
    private readonly ILogger<RelayWebSocketHandler> _logger;

    public RelayWebSocketHandler(
        RelayOptions options,
        AuthService auth,
        PairingService pairing,
        RelayRouter router,
        ConnectionRegistry connections,
        SlidingWindowRateLimiter rateLimiter,
        ILogger<RelayWebSocketHandler> logger)
    {
        _options = options;
        _auth = auth;
        _pairing = pairing;
        _router = router;
        _connections = connections;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task HandleAsync(
        HttpContext context,
        PrincipalRole endpointRole)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await using var peer = new RelayPeer(socket, remoteAddress, endpointRole);
        try
        {
            while (!context.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var envelope = await WebSocketJson.ReceiveAsync(
                    socket,
                    _options.MaxMessageBytes,
                    context.RequestAborted).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                peer.Touch();
                if (!peer.IsAuthenticated)
                {
                    await HandleUnauthenticatedAsync(peer, envelope, context.RequestAborted).ConfigureAwait(false);
                }
                else
                {
                    await _router.HandleAsync(peer, envelope, context.RequestAborted).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Invalid relay message from {RemoteAddress}", remoteAddress);
            await peer.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid relay message", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            _logger.LogInformation(exception, "Relay WebSocket disconnected for {RemoteAddress}", remoteAddress);
        }
        catch (ConnectionAbortedException exception)
        {
            _logger.LogInformation(exception, "Relay WebSocket aborted for {RemoteAddress}", remoteAddress);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Relay connection failed for {RemoteAddress}", remoteAddress);
            await peer.CloseAsync(
                WebSocketCloseStatus.InternalServerError,
                "relay internal error",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (peer.IsAuthenticated)
            {
                await _router.OnDisconnectedAsync(peer, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleUnauthenticatedAsync(
        RelayPeer peer,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case RelayMessageTypes.DeviceRegister when peer.EndpointRole == PrincipalRole.Device:
                await RegisterDeviceAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
            case RelayMessageTypes.AuthHello:
                await BeginAuthAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
            case RelayMessageTypes.AuthResponse:
                await CompleteAuthAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
            case RelayMessageTypes.PairingClaim when peer.EndpointRole == PrincipalRole.Controller:
                await ClaimPairingAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
            default:
                SendError(peer, envelope.RequestId, "AUTH_REQUIRED", "Authenticate or pair before this message.");
                break;
        }
    }

    private async Task RegisterDeviceAsync(
        RelayPeer peer,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!_rateLimiter.TryAcquire($"register-attempt:{peer.RemoteAddress}", 120, TimeSpan.FromMinutes(1)))
        {
            SendError(peer, envelope.RequestId, "RATE_LIMITED", "Device registration rate limit exceeded.");
            return;
        }

        var payload = envelope.ReadPayload<DeviceRegisterPayload>();
        if (envelope.DeviceId is not null && envelope.DeviceId != payload.DeviceId)
        {
            SendError(peer, envelope.RequestId, "DEVICE_ID_MISMATCH", "Device ID mismatch.");
            return;
        }

        var isRegisteredIdentity = await _auth.IsRegisteredDeviceIdentityAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        var registrationAllowed = isRegisteredIdentity
            ? _rateLimiter.TryAcquire(
                $"register-device:{peer.RemoteAddress}:{payload.DeviceId}",
                30,
                TimeSpan.FromMinutes(1))
            : _rateLimiter.TryAcquire($"register-new:{peer.RemoteAddress}", 10, TimeSpan.FromHours(1));
        if (!registrationAllowed)
        {
            var message = isRegisteredIdentity
                ? "Device reconnect rate limit exceeded."
                : "New device enrollment rate limit exceeded.";
            SendError(peer, envelope.RequestId, "RATE_LIMITED", message);
            return;
        }

        var result = await _auth.RegisterDeviceAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            peer.TrySend(RelayEnvelope.Create(
                RelayMessageTypes.DeviceRegistered,
                result.Value!,
                envelope.RequestId,
                result.Value!.DeviceId));
        }
        else
        {
            SendError(peer, envelope.RequestId, result.ErrorCode!, result.ErrorMessage!);
        }
    }

    private async Task BeginAuthAsync(
        RelayPeer peer,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!_rateLimiter.TryAcquire($"auth:{peer.RemoteAddress}", 30, TimeSpan.FromMinutes(1)))
        {
            SendError(peer, envelope.RequestId, "RATE_LIMITED", "Authentication rate limit exceeded.");
            return;
        }

        var payload = envelope.ReadPayload<AuthHelloPayload>();
        var envelopePrincipal = payload.Role == PrincipalRole.Device ? envelope.DeviceId : envelope.ControllerId;
        if (envelopePrincipal is not null && envelopePrincipal != payload.PrincipalId)
        {
            SendError(peer, envelope.RequestId, "AUTH_FAILED", "Envelope principal ID mismatch.");
            return;
        }

        var result = await _auth.BeginAsync(
            peer,
            payload,
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            peer.TrySend(RelayEnvelope.Create(
                RelayMessageTypes.AuthChallenge,
                result.Value!,
                envelope.RequestId,
                peer.EndpointRole == PrincipalRole.Device ? peer.PendingPrincipalId : null,
                peer.EndpointRole == PrincipalRole.Controller ? peer.PendingPrincipalId : null));
        }
        else
        {
            SendError(peer, envelope.RequestId, result.ErrorCode!, result.ErrorMessage!);
        }
    }

    private async Task CompleteAuthAsync(
        RelayPeer peer,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var result = await _auth.CompleteAsync(
            peer,
            envelope.ReadPayload<AuthResponsePayload>(),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            SendError(peer, envelope.RequestId, result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        peer.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.AuthOk,
            result.Value!,
            envelope.RequestId,
            peer.Role == PrincipalRole.Device ? peer.PrincipalId : null,
            peer.Role == PrincipalRole.Controller ? peer.PrincipalId : null));
        await _router.OnAuthenticatedAsync(peer, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClaimPairingAsync(
        RelayPeer peer,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var payload = envelope.ReadPayload<PairingClaimPayload>();
        if (envelope.ControllerId is not null && envelope.ControllerId != payload.ControllerId)
        {
            SendError(peer, envelope.RequestId, "CONTROLLER_ID_MISMATCH", "Controller ID mismatch.");
            return;
        }

        if (!_rateLimiter.TryAcquire($"pair-ip:{peer.RemoteAddress}", 20, TimeSpan.FromMinutes(1)) ||
            !_rateLimiter.TryAcquire($"pair-code:{payload.Code}", 5, TimeSpan.FromMinutes(3)))
        {
            SendError(peer, envelope.RequestId, "PAIRING_RATE_LIMITED", "Pairing attempts exceeded.");
            return;
        }

        var result = await _pairing.ClaimAsync(payload, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            SendError(peer, envelope.RequestId, result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        peer.Authenticate(PrincipalRole.Controller, result.Value!.ControllerId);
        await _router.OnAuthenticatedAsync(peer, cancellationToken).ConfigureAwait(false);
        var completed = new PairingCompletedPayload(
            result.Value.DeviceId,
            result.Value.ControllerId,
            result.Value.Permissions);
        peer.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.PairingCompleted,
            completed,
            envelope.RequestId,
            result.Value.DeviceId,
            result.Value.ControllerId));
        _connections.GetDevice(result.Value.DeviceId)?.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.PairingCompleted,
            completed,
            envelope.RequestId,
            result.Value.DeviceId,
            result.Value.ControllerId));
    }

    private static void SendError(RelayPeer peer, string? requestId, string code, string message)
    {
        peer.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.Error,
            new ErrorPayload(code, message),
            requestId,
            peer.EndpointRole == PrincipalRole.Device ? peer.PendingPrincipalId : null,
            peer.EndpointRole == PrincipalRole.Controller ? peer.PendingPrincipalId : null));
    }
}
