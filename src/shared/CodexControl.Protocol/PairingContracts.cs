namespace CodexControl.Protocol;

public sealed record PairingCreatePayload;

public sealed record PairingCreatedPayload(
    string Code,
    long ExpiresAt,
    int AttemptsRemaining);

public sealed record PairingClaimPayload(
    string Code,
    string ControllerId,
    string ControllerName,
    string PublicKey,
    string ProofNonce,
    string ProofSignature);

public sealed record PairingCompletedPayload(
    string DeviceId,
    string ControllerId,
    PairingPermissions Permissions);

public sealed record PairingRevokedPayload(
    string DeviceId,
    string ControllerId);

public sealed record PairingPermissions(
    bool View,
    bool Steer,
    bool Interrupt,
    bool Approval)
{
    public static PairingPermissions Full { get; } = new(true, true, true, true);
}
