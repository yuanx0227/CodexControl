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
    private readonly IDbContextFactory<RelayDbContext> _dbFactory;
    private readonly ControlRequestTracker _controlRequests;

    public RelayRouter(
        ConnectionRegistry connections,
        PairingService pairing,
        IDbContextFactory<RelayDbContext> dbFactory,
        ControlRequestTracker controlRequests)
    {
        _connections = connections;
        _pairing = pairing;
        _dbFactory = dbFactory;
        _controlRequests = controlRequests;
    }

    public async Task OnAuthenticatedAsync(RelayPeer peer, CancellationToken cancellationToken)
    {
        _connections.Register(peer);
        if (peer.Role == PrincipalRole.Device)
        {
            await BroadcastPresenceAsync(peer.PrincipalId!, online: true, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnDisconnectedAsync(RelayPeer peer, CancellationToken cancellationToken)
    {
        var removed = _connections.Remove(peer);
        if (removed && peer.Role == PrincipalRole.Device && peer.PrincipalId is not null)
        {
            await BroadcastPresenceAsync(peer.PrincipalId, online: false, cancellationToken).ConfigureAwait(false);
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

        switch (envelope.Type)
        {
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

                _connections.GetController(envelope.ControllerId)?.TrySend(envelope with { DeviceId = deviceId });

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
            case RelayMessageTypes.ControlThreadList:
            case RelayMessageTypes.ControlThreadRead:
            case RelayMessageTypes.ControlThreadStart:
            case RelayMessageTypes.ControlThreadResume:
                await RouteControlAsync(peer, envelope, cancellationToken).ConfigureAwait(false);
                break;
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
            RelayMessageTypes.ControlThreadList => pairing.ViewPermission,
            RelayMessageTypes.ControlThreadRead => pairing.ViewPermission,
            RelayMessageTypes.ControlThreadStart => pairing.SteerPermission,
            RelayMessageTypes.ControlThreadResume => pairing.SteerPermission,
            _ => false,
        };
        if (!permitted)
        {
            await SendControlFailureAsync(controller, envelope, "PERMISSION_DENIED", "Pairing permission denied.")
                .ConfigureAwait(false);
            return;
        }

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
            envelope.Type);
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
        if (device is null || snapshot.Revision < device.LatestSnapshotRevision)
        {
            return;
        }

        device.LatestSnapshotRevision = snapshot.Revision;
        device.LatestSnapshotJson = JsonSerializer.Serialize(snapshot, RelayJson.Options);
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
            _connections.GetController(controllerId)?.TrySend(envelope with { ControllerId = controllerId });
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
