namespace CodexControl.Protocol;

public enum PrincipalRole
{
    Device,
    Controller,
}

public sealed record AuthHelloPayload(
    PrincipalRole Role,
    string PrincipalId,
    string ClientVersion);

public sealed record AuthChallengePayload(
    string ChallengeId,
    string Nonce,
    long ExpiresAt);

public sealed record AuthResponsePayload(
    string ChallengeId,
    string Signature);

public sealed record AuthOkPayload(
    PrincipalRole Role,
    string PrincipalId,
    string ConnectionId,
    string? ServerVersion = null);

public sealed record PublicIdentityPayload(
    string PrincipalId,
    string Name,
    string PublicKey);

public sealed record DeviceRegisterPayload(
    string DeviceId,
    string Name,
    string PublicKey);

public sealed record DeviceRegisteredPayload(
    string DeviceId);

public static class AuthCanonicalPayload
{
    public static string Build(
        PrincipalRole role,
        string principalId,
        string challengeId,
        string nonce,
        long expiresAt) =>
        string.Join(
            '\n',
            "codex-control-auth-v1",
            role.ToString().ToLowerInvariant(),
            principalId,
            challengeId,
            nonce,
            expiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
