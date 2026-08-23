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
    PairingPermissions Permissions,
    string? ServerVersion = null);

public sealed record PairingRevokedPayload(
    string DeviceId,
    string ControllerId);

public sealed record PairingPendingPayload(
    string PairingRequestId,
    string DeviceId,
    string ControllerId,
    long ExpiresAt);

public sealed record PairingConfirmationRequestedPayload(
    string PairingRequestId,
    string ControllerId,
    string ControllerName,
    string PublicKeyFingerprint,
    long ExpiresAt);

public enum PairingDecision
{
    Allow,
    Deny,
}

public enum PairingPermissionProfile
{
    ViewOnly,
    Full,
}

public sealed record PairingConfirmationResolvePayload(
    string PairingRequestId,
    PairingDecision Decision,
    PairingPermissionProfile PermissionProfile);

public sealed record PairingDeniedPayload(
    string PairingRequestId,
    string Code,
    string Message);

public sealed record PairingListPayload;

public sealed record PairingListResultPayload(
    IReadOnlyList<PairedControllerSummaryPayload> Controllers);

public sealed record PairedControllerSummaryPayload(
    long PairingId,
    string ControllerId,
    string ControllerName,
    string? Alias,
    PairingPermissions Permissions,
    long? LastSeenAt,
    bool Revoked);

public sealed record PairingUpdatePayload(
    long PairingId,
    string? Alias,
    PairingPermissionProfile PermissionProfile);

public sealed record PairingUpdatedPayload(
    long PairingId,
    string? Alias,
    PairingPermissions Permissions);

public sealed record PairingRevokePayload(long PairingId);

public sealed record PairingPermissions(
    bool View,
    bool Steer,
    bool Interrupt,
    bool Approval)
{
    public static PairingPermissions Full { get; } = new(true, true, true, true);
    public static PairingPermissions ViewOnly { get; } = new(true, false, false, false);

    public static PairingPermissions FromProfile(PairingPermissionProfile profile) =>
        profile == PairingPermissionProfile.Full ? Full : ViewOnly;
}
