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
    public const string PairingCompleted = "pairing.completed";
    public const string PairingRevoked = "pairing.revoked";

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
    public const string ControlResult = "control.result";

    public const string Heartbeat = "heartbeat";
    public const string HeartbeatAck = "heartbeat.ack";
    public const string Error = "error";
}
