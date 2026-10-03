using System.Text.Json;
using CodexControl.Protocol;
using CodexControl.Relay.Pairing;
using CodexControl.Relay.Persistence;
using CodexControl.Relay.WebSockets;
using Microsoft.EntityFrameworkCore;

namespace CodexControl.Relay.Routing;

public sealed class RelayRouter
{
    private readonly ConnectionRegistry _connections;
    private readonly PairingService _pairing;
    private readonly PendingPairingConnections _pendingPairings;
    private readonly IDbContextFactory<RelayDbContext> _dbFactory;
    private readonly ControlRequestTracker _controlRequests;

    public RelayRouter(
        ConnectionRegistry connections,
        PairingService pairing,
        PendingPairingConnections pendingPairings,
        IDbContextFactory<RelayDbContext> dbFactory,
        ControlRequestTracker controlRequests)
    {
        _connections = connections;
        _pairing = pairing;
        _pendingPairings = pendingPairings;
        _dbFactory = dbFactory;
        _controlRequests = controlRequests;
    }

    public async Task OnAuthenticatedAsync(RelayPeer peer, CancellationToken cancellationToken)
    {
        if (peer.Role == PrincipalRole.Controller)
        {
            _connections.Register(peer);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task OnDisconnectedAsync(RelayPeer peer, CancellationToken cancellationToken)
    {
        var removed = _connections.Remove(peer);
        if (removed && peer.Role == PrincipalRole.Device && peer.PrincipalId is not null && peer.IsReady)
        {
            await BroadcastPresenceAsync(peer.PrincipalId, online: false, cancellationToken).ConfigureAwait(false);
            var cancelled = await _pairing.CancelForDeviceAsync(peer.PrincipalId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var pending in _pendingPairings.RemoveMany(cancelled))
            {
                pending.Peer.TrySend(RelayEnvelope.Create(
                    RelayMessageTypes.PairingDenied,
                    new PairingDeniedPayload(pending.PairingRequestId, "DEVICE_OFFLINE", "Device went offline."),
                    pending.OriginalRequestId,
                    peer.PrincipalId,
                    pending.ControllerId));
            }
        }
    }

    public async Task HandleAsync(
        RelayPeer peer,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!peer.IsAuthenticated)
        {
            await SendErrorAsync(peer, envelope.RequestId, "AUTH_REQUIRED", "Authentication is required.")
                .ConfigureAwait(false);
            return;
        }

        if (envelope.Type == RelayMessageTypes.Heartbeat)
        {
            peer.TrySend(RelayEnvelope.Create(
                RelayMessageTypes.HeartbeatAck,
                new { },
                envelope.RequestId,
                peer.Role == PrincipalRole.Device ? peer.PrincipalId : null,
                peer.Role == PrincipalRole.Controller ? peer.PrincipalId : null));
            return;
        }

        if (peer.Role == PrincipalRole.Device)
        {
            await HandleDeviceAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await HandleControllerAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleDeviceAsync(
        RelayPeer peer,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var deviceId = peer.PrincipalId!;
        if (envelope.DeviceId is not null && envelope.DeviceId != deviceId)
        {
            await SendErrorAsync(peer, envelope.RequestId, "DEVICE_ID_MISMATCH", "Device ID mismatch.")
                .ConfigureAwait(false);
            return;
        }

        if (!peer.IsReady && envelope.Type is not (RelayMessageTypes.DeviceReady or RelayMessageTypes.PairingRevoke))
        {
            await SendErrorAsync(peer, envelope.RequestId, "DEVICE_NOT_READY", "Device synchronization is incomplete.")
                .ConfigureAwait(false);
            return;
        }

        switch (envelope.Type)
        {
            case RelayMessageTypes.DeviceReady:
                if (!peer.IsReady)
                {
                    peer.MarkReady();
                    _connections.Register(peer);
                    await BroadcastPresenceAsync(deviceId, online: true, cancellationToken).ConfigureAwait(false);
                }

                peer.TrySend(RelayEnvelope.Create(
                    RelayMessageTypes.DeviceReadyAck,
                    new { },
                    envelope.RequestId,
                    deviceId));
                break;
            case RelayMessageTypes.PairingCreate:
                {
                    var result = await _pairing.CreateAsync(deviceId, cancellationToken).ConfigureAwait(false);
                    if (result.Succeeded)
                    {
                        peer.TrySend(RelayEnvelope.Create(
                            RelayMessageTypes.PairingCreated,
                            result.Value!,
                            envelope.RequestId,
                            deviceId));
                    }
                    else
                    {
                        await SendErrorAsync(peer, envelope.RequestId, result.ErrorCode!, result.ErrorMessage!)
                            .ConfigureAwait(false);
                    }

                    break;
                }
            case RelayMessageTypes.PairingConfirmationResolve:
                await ResolvePairingAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
            case RelayMessageTypes.PairingList:
                {
                    var controllers = await _pairing.ListControllersAsync(deviceId, cancellationToken)
                        .ConfigureAwait(false);
                    peer.TrySend(RelayEnvelope.Create(
                        RelayMessageTypes.PairingListResult,
                        new PairingListResultPayload(controllers),
                        envelope.RequestId,
                        deviceId));
                    break;
                }
            case RelayMessageTypes.PairingUpdate:
                {
                    var result = await _pairing.UpdateAsync(
                        deviceId,
                        envelope.ReadPayload<PairingUpdatePayload>(),
                        cancellationToken).ConfigureAwait(false);
                    if (result.Succeeded)
                    {
                        peer.TrySend(RelayEnvelope.Create(
                            RelayMessageTypes.PairingUpdated,
                            result.Value!,
                            envelope.RequestId,
                            deviceId));
                    }
                    else
                    {
                        await SendErrorAsync(peer, envelope.RequestId, result.ErrorCode!, result.ErrorMessage!)
                            .ConfigureAwait(false);
                    }

                    break;
                }
            case RelayMessageTypes.PairingRevoke:
                await RevokePairingFromDeviceAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
            case RelayMessageTypes.CodexSnapshot:
                await SaveSnapshotAsync(deviceId, envelope, cancellationToken).ConfigureAwait(false);
                await BroadcastToControllersAsync(deviceId, envelope with { DeviceId = deviceId }, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case RelayMessageTypes.CodexEvent:
                await BroadcastToControllersAsync(deviceId, envelope with { DeviceId = deviceId }, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case RelayMessageTypes.ControlResult:
                if (envelope.RequestId is null || envelope.ControllerId is null ||
                    !_controlRequests.TryComplete(
                        envelope.RequestId,
                        deviceId,
                        envelope.ControllerId,
                        envelope with { DeviceId = deviceId }))
                {
                    await SendErrorAsync(
                        peer,
                        envelope.RequestId,
                        "CONTROL_RESULT_UNEXPECTED",
                        "Control result does not match a routed request.").ConfigureAwait(false);
                    break;
                }

                foreach (var controller in _connections.GetControllers(envelope.ControllerId))
                {
                    controller.TrySend(envelope with { DeviceId = deviceId });
                }

                await AuditAsync(envelope.Type, deviceId, envelope.ControllerId, envelope.RequestId, "result", cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                await SendErrorAsync(peer, envelope.RequestId, "MESSAGE_TYPE_UNSUPPORTED", "Unsupported device message.")
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleControllerAsync(
        RelayPeer peer,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var controllerId = peer.PrincipalId!;
        if (envelope.ControllerId is not null && envelope.ControllerId != controllerId)
        {
            await SendErrorAsync(peer, envelope.RequestId, "CONTROLLER_ID_MISMATCH", "Controller ID mismatch.")
                .ConfigureAwait(false);
            return;
        }

        switch (envelope.Type)
        {
            case RelayMessageTypes.DeviceList:
                await SendDeviceListAsync(peer, envelope.RequestId, cancellationToken).ConfigureAwait(false);
                break;
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
                await RouteControlAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
            case RelayMessageTypes.PairingRevoke:
            case RelayMessageTypes.PairingRevoked:
                await RevokePairingAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await SendErrorAsync(peer, envelope.RequestId, "MESSAGE_TYPE_UNSUPPORTED", "Unsupported controller message.")
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task RouteControlAsync(
        RelayPeer controller,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(envelope.DeviceId))
        {
            await SendControlFailureAsync(controller, envelope, "DEVICE_ID_REQUIRED", "Target device is required.")
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(envelope.RequestId))
        {
            await SendControlFailureAsync(controller, envelope, "REQUEST_ID_REQUIRED", "Control request ID is required.")
                .ConfigureAwait(false);
            return;
        }

        var pairing = await _pairing.GetActivePairingAsync(
            envelope.DeviceId,
            controller.PrincipalId!,
            cancellationToken).ConfigureAwait(false);
        var permitted = pairing is not null && envelope.Type switch
        {
            RelayMessageTypes.ControlSteer => pairing.SteerPermission,
            RelayMessageTypes.ControlInterrupt => pairing.InterruptPermission,
            RelayMessageTypes.ControlApproval => pairing.ApprovalPermission,
            RelayMessageTypes.ControlSessionOptions => pairing.ViewPermission,
            RelayMessageTypes.ControlThreadList => pairing.ViewPermission,
            RelayMessageTypes.ControlThreadRead => pairing.ViewPermission,
            RelayMessageTypes.ControlThreadStart => pairing.SteerPermission,
            RelayMessageTypes.ControlThreadResume => pairing.SteerPermission,
            RelayMessageTypes.ControlThreadWatch => pairing.ViewPermission,
            RelayMessageTypes.ControlThreadUnwatch => pairing.ViewPermission,
            RelayMessageTypes.ControlThreadSend => pairing.SteerPermission,
            _ => false,
        };
        if (!permitted)
        {
            await SendControlFailureAsync(controller, envelope, "PERMISSION_DENIED", "Pairing permission denied.")
                .ConfigureAwait(false);
            return;
        }

        // Never trust a Controller's claim that it can activate an upstream subscription.
        if (envelope.Type == RelayMessageTypes.ControlThreadWatch)
            envelope = envelope with { Payload = JsonSerializer.SerializeToElement(
                envelope.ReadPayload<ThreadWatchControlPayload>() with { AllowJoin = pairing!.SteerPermission }, RelayJson.Options) };

        var device = _connections.GetDevice(envelope.DeviceId);
        if (device is null)
        {
            await SendControlFailureAsync(controller, envelope, "DEVICE_OFFLINE", "Device is offline.")
                .ConfigureAwait(false);
            return;
        }

        var begin = _controlRequests.Begin(
            envelope.RequestId,
            envelope.DeviceId,
            controller.PrincipalId!,
            envelope.Type,
            envelope.Payload);
        if (begin.Status == ControlBeginStatus.Conflict)
        {
            await SendControlFailureAsync(
                controller,
                envelope,
                "REQUEST_ID_CONFLICT",
                "Request ID was already used for another control operation.").ConfigureAwait(false);
            return;
        }

        if (begin.Status == ControlBeginStatus.Completed)
        {
            controller.TrySend(begin.CachedResult!);
            return;
        }

        if (begin.Status == ControlBeginStatus.Pending)
        {
            return;
        }

        if (!device.TrySend(envelope with
        {
            DeviceId = envelope.DeviceId,
            ControllerId = controller.PrincipalId,
        }))
        {
            _controlRequests.Cancel(envelope.RequestId, envelope.DeviceId, controller.PrincipalId!);
            await SendControlFailureAsync(controller, envelope, "DEVICE_OFFLINE", "Device send queue is unavailable.")
                .ConfigureAwait(false);
            return;
        }
        await AuditAsync(
            envelope.Type,
            envelope.DeviceId,
            controller.PrincipalId,
            envelope.RequestId,
            "routed",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolvePairingAsync(
        RelayPeer device,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var payload = envelope.ReadPayload<PairingConfirmationResolvePayload>();
        var result = await _pairing.ResolveAsync(device.PrincipalId!, payload, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await SendErrorAsync(device, envelope.RequestId, result.ErrorCode!, result.ErrorMessage!)
                .ConfigureAwait(false);
            return;
        }

        var resolved = result.Value!;
        _pendingPairings.TryRemove(resolved.PairingRequestId, out var pending);
        if (!resolved.Allowed)
        {
            var denied = new PairingDeniedPayload(
                resolved.PairingRequestId,
                "PAIRING_DENIED",
                "Pairing was denied on the Device.");
            device.TrySend(RelayEnvelope.Create(
                RelayMessageTypes.PairingDenied,
                denied,
                envelope.RequestId,
                resolved.DeviceId,
                resolved.ControllerId));
            pending?.Peer.TrySend(RelayEnvelope.Create(
                RelayMessageTypes.PairingDenied,
                denied,
                pending.OriginalRequestId,
                resolved.DeviceId,
                resolved.ControllerId));
            return;
        }

        var completed = new PairingCompletedPayload(
            resolved.DeviceId,
            resolved.ControllerId,
            resolved.Permissions,
            typeof(RelayRouter).Assembly.GetName().Version?.ToString(3));
        device.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.PairingCompleted,
            completed,
            envelope.RequestId,
            resolved.DeviceId,
            resolved.ControllerId));
        if (pending is not null)
        {
            pending.Peer.Authenticate(PrincipalRole.Controller, resolved.ControllerId);
            await OnAuthenticatedAsync(pending.Peer, cancellationToken).ConfigureAwait(false);
            pending.Peer.TrySend(RelayEnvelope.Create(
                RelayMessageTypes.PairingCompleted,
                completed,
                pending.OriginalRequestId,
                resolved.DeviceId,
                resolved.ControllerId));
        }
    }

    private async Task RevokePairingFromDeviceAsync(
        RelayPeer device,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var payload = envelope.ReadPayload<PairingRevokePayload>();
        var controllers = await _pairing.ListControllersAsync(device.PrincipalId!, cancellationToken)
            .ConfigureAwait(false);
        var target = controllers.SingleOrDefault(value => value.PairingId == payload.PairingId && !value.Revoked);
        if (target is null ||
            !await _pairing.RevokeByIdAsync(device.PrincipalId!, payload.PairingId, cancellationToken)
                .ConfigureAwait(false))
        {
            await SendErrorAsync(device, envelope.RequestId, "PAIRING_NOT_FOUND", "Pairing was not found.")
                .ConfigureAwait(false);
            return;
        }

        var revoked = new PairingRevokedPayload(device.PrincipalId!, target.ControllerId);
        device.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.PairingRevoked,
            revoked,
            envelope.RequestId,
            device.PrincipalId,
            target.ControllerId));
        foreach (var controller in _connections.GetControllers(target.ControllerId))
        {
            controller.TrySend(RelayEnvelope.Create(
                RelayMessageTypes.PairingRevoked,
                revoked,
                deviceId: device.PrincipalId,
                controllerId: target.ControllerId));
        }
    }

    private async Task RevokePairingAsync(
        RelayPeer controller,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (envelope.DeviceId is null ||
            !await _pairing.RevokeAsync(envelope.DeviceId, controller.PrincipalId!, cancellationToken)
                .ConfigureAwait(false))
        {
            await SendErrorAsync(controller, envelope.RequestId, "PAIRING_NOT_FOUND", "Pairing was not found.")
                .ConfigureAwait(false);
            return;
        }

        var payload = new PairingRevokedPayload(envelope.DeviceId, controller.PrincipalId!);
        controller.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.PairingRevoked,
            payload,
            envelope.RequestId,
            envelope.DeviceId,
            controller.PrincipalId));
        _connections.GetDevice(envelope.DeviceId)?.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.PairingRevoked,
            payload,
            envelope.RequestId,
            envelope.DeviceId,
            controller.PrincipalId));
    }

    private async Task SendDeviceListAsync(
        RelayPeer controller,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var devices = await _pairing.ListDevicesAsync(controller.PrincipalId!, cancellationToken)
            .ConfigureAwait(false);
        var summaries = devices.Select(device => new DeviceSummaryPayload(
            device.Id,
            device.Name,
            _connections.GetDevice(device.Id) is not null,
            DeserializeSnapshot(device.LatestSnapshotJson),
            device.LastSeenAt?.ToUnixTimeMilliseconds())).ToArray();
        controller.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.DeviceListResult,
            new DeviceListResultPayload(summaries),
            requestId,
            controllerId: controller.PrincipalId));
    }

    private async Task SaveSnapshotAsync(
        string deviceId,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var snapshot = envelope.ReadPayload<CodexSnapshotPayload>();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.FindAsync([deviceId], cancellationToken).ConfigureAwait(false);
        if (device is null || (DeserializeSnapshot(device.LatestSnapshotJson)?.StreamEpoch == snapshot.StreamEpoch &&
                              snapshot.Revision < device.LatestSnapshotRevision))
        {
            return;
        }

        device.LatestSnapshotRevision = snapshot.Revision;
        // Database retains operational metadata, never response text, commands, or diffs.
        device.LatestSnapshotJson = JsonSerializer.Serialize(snapshot with
        {
            LastAgentMessage = null, RunningCommand = null, CurrentActivity = null, ChangedFiles = [], LastError = null,
            ActiveTurns = snapshot.ActiveTurns?.Select(active => active with
            { LastAgentMessage = null, RunningCommand = null, CurrentActivity = null, ChangedFiles = [], LastError = null }).ToArray(),
        }, RelayJson.Options);
        device.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastPresenceAsync(
        string deviceId,
        bool online,
        CancellationToken cancellationToken)
    {
        var envelope = RelayEnvelope.Create(
            online ? RelayMessageTypes.DeviceOnline : RelayMessageTypes.DeviceOffline,
            new { online },
            deviceId: deviceId);
        await BroadcastToControllersAsync(deviceId, envelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastToControllersAsync(
        string deviceId,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var controllerIds = await db.Pairings.AsNoTracking()
            .Where(value => value.DeviceId == deviceId && value.RevokedAt == null && value.ViewPermission)
            .Select(value => value.ControllerId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var controllerId in controllerIds)
        {
            foreach (var controller in _connections.GetControllers(controllerId))
            {
                controller.TrySend(envelope with { ControllerId = controllerId });
            }
        }
    }

    private async Task SendControlFailureAsync(
        RelayPeer peer,
        RelayEnvelope request,
        string code,
        string message)
    {
        peer.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.ControlResult,
            new ControlResultPayload(ControlResultStatus.Failed, code, message, null),
            request.RequestId,
            request.DeviceId,
            peer.PrincipalId));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static Task SendErrorAsync(
        RelayPeer peer,
        string? requestId,
        string code,
        string message)
    {
        peer.TrySend(RelayEnvelope.Create(
            RelayMessageTypes.Error,
            new ErrorPayload(code, message),
            requestId,
            peer.Role == PrincipalRole.Device ? peer.PrincipalId : null,
            peer.Role == PrincipalRole.Controller ? peer.PrincipalId : null));
        return Task.CompletedTask;
    }

    private async Task AuditAsync(
        string type,
        string? deviceId,
        string? controllerId,
        string? requestId,
        string outcome,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AuditEvents.Add(new AuditEventEntity
        {
            EventType = type,
            DeviceId = deviceId,
            ControllerId = controllerId,
            RequestId = requestId,
            Outcome = outcome,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CodexSnapshotPayload? DeserializeSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CodexSnapshotPayload>(json, RelayJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
