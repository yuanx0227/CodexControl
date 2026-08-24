namespace CodexControl.Protocol;

public static class RelayMessageTypes
{
    public const string AuthHello = "auth.hello";
    public const string AuthChallenge = "auth.challenge";
    public const string AuthResponse = "auth.response";
    public const string AuthOk = "auth.ok";

    public const string DeviceRegister = "device.register";
    public const string DeviceRegistered = "device.registered";

    public const string PairingCreate = "pairing.create";
    public const string PairingCreated = "pairing.created";
    public const string PairingClaim = "pairing.claim";
    public const string PairingPending = "pairing.pending";
    public const string PairingConfirmationRequested = "pairing.confirmation.requested";
    public const string PairingConfirmationResolve = "pairing.confirmation.resolve";
    public const string PairingCompleted = "pairing.completed";
    public const string PairingDenied = "pairing.denied";
    public const string PairingList = "pairing.list";
    public const string PairingListResult = "pairing.list.result";
    public const string PairingUpdate = "pairing.update";
    public const string PairingUpdated = "pairing.updated";
    public const string PairingRevoke = "pairing.revoke";
    public const string PairingRevoked = "pairing.revoked";

    public const string DeviceReady = "device.ready";
    public const string DeviceReadyAck = "device.ready.ack";
    public const string DeviceOnline = "device.online";
    public const string DeviceOffline = "device.offline";
    public const string DeviceStatus = "device.status";
    public const string DeviceList = "device.list";
    public const string DeviceListResult = "device.list.result";
    public const string CodexSnapshot = "codex.snapshot";
    public const string CodexEvent = "codex.event";

    public const string ControlSteer = "control.steer";
    public const string ControlInterrupt = "control.interrupt";
    public const string ControlApproval = "control.approval";
    public const string ControlSessionOptions = "control.session.options";
    public const string ControlThreadList = "control.thread.list";
    public const string ControlThreadRead = "control.thread.read";
    public const string ControlThreadStart = "control.thread.start";
    public const string ControlThreadResume = "control.thread.resume";
    public const string ControlResult = "control.result";

    public const string Heartbeat = "heartbeat";
    public const string HeartbeatAck = "heartbeat.ack";
    public const string Error = "error";
}
