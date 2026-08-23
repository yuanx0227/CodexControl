using System.Security.Cryptography;
using CodexControl.Protocol;
using CodexControl.Relay.Persistence;
using CodexControl.Relay.Services;
using CodexControl.Relay.WebSockets;
using Microsoft.EntityFrameworkCore;

namespace CodexControl.Relay.Authentication;

public sealed class AuthService
{
    private readonly IDbContextFactory<RelayDbContext> _dbFactory;
    private readonly ChallengeStore _challenges;

    public AuthService(IDbContextFactory<RelayDbContext> dbFactory, ChallengeStore challenges)
    {
        _dbFactory = dbFactory;
        _challenges = challenges;
    }

    public async Task<bool> IsRegisteredDeviceIdentityAsync(
        DeviceRegisterPayload payload,
        CancellationToken cancellationToken)
    {
        if (!IsValidDeviceRegistration(payload))
        {
            return false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == payload.DeviceId,
            cancellationToken).ConfigureAwait(false);
        return device is not null &&
               device.RevokedAt is null &&
               KeysEqual(device.PublicKey, payload.PublicKey);
    }

    public async Task<ServiceResult<DeviceRegisteredPayload>> RegisterDeviceAsync(
        DeviceRegisterPayload payload,
        CancellationToken cancellationToken)
    {
        if (!IsValidDeviceRegistration(payload))
        {
            return ServiceResult<DeviceRegisteredPayload>.Failure(
                "DEVICE_REGISTRATION_INVALID",
                "Device registration payload is invalid.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var device = await db.Devices.SingleOrDefaultAsync(
            value => value.Id == payload.DeviceId,
            cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            db.Devices.Add(new DeviceEntity
            {
                Id = payload.DeviceId,
                Name = payload.Name,
                PublicKey = payload.PublicKey,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            if (!KeysEqual(device.PublicKey, payload.PublicKey) || device.RevokedAt is not null)
            {
                return ServiceResult<DeviceRegisteredPayload>.Failure(
                    "DEVICE_IDENTITY_CONFLICT",
                    "Device ID is already registered with another key or revoked.");
            }

            device.Name = payload.Name;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ServiceResult<DeviceRegisteredPayload>.Success(new(payload.DeviceId));
    }

    public async Task<ServiceResult<AuthChallengePayload>> BeginAsync(
        RelayPeer peer,
        AuthHelloPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.Role != peer.EndpointRole ||
            !IsValidPrincipalId(payload.PrincipalId, payload.Role == PrincipalRole.Device ? "dev_" : "ctl_"))
        {
            return ServiceResult<AuthChallengePayload>.Failure("AUTH_FAILED", "Principal role or ID is invalid.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var exists = payload.Role == PrincipalRole.Device
            ? await db.Devices.AnyAsync(
                value => value.Id == payload.PrincipalId && value.RevokedAt == null,
                cancellationToken).ConfigureAwait(false)
            : await db.Controllers.AnyAsync(
                value => value.Id == payload.PrincipalId && value.RevokedAt == null,
                cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return ServiceResult<AuthChallengePayload>.Failure("AUTH_FAILED", "Principal is not registered.");
        }

        var issued = _challenges.Create(peer.ConnectionId, payload.Role, payload.PrincipalId);
        peer.PendingPrincipalId = payload.PrincipalId;
        return ServiceResult<AuthChallengePayload>.Success(new(
            issued.ChallengeId,
            issued.Nonce,
            issued.ExpiresAt.ToUnixTimeMilliseconds()));
    }

    public async Task<ServiceResult<AuthOkPayload>> CompleteAsync(
        RelayPeer peer,
        AuthResponsePayload payload,
        CancellationToken cancellationToken)
    {
        var principalId = peer.PendingPrincipalId;
        if (principalId is null ||
            !_challenges.TryConsume(
                payload.ChallengeId,
                peer.ConnectionId,
                peer.EndpointRole,
                principalId,
                out var challenge))
        {
            return ServiceResult<AuthOkPayload>.Failure(
                "AUTH_CHALLENGE_INVALID",
                "Authentication challenge is invalid, expired, or replayed.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var publicKey = peer.EndpointRole == PrincipalRole.Device
            ? await db.Devices.Where(value => value.Id == principalId && value.RevokedAt == null)
                .Select(value => value.PublicKey)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await db.Controllers.Where(value => value.Id == principalId && value.RevokedAt == null)
                .Select(value => value.PublicKey)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (publicKey is null)
        {
            return ServiceResult<AuthOkPayload>.Failure("AUTH_FAILED", "Principal is not active.");
        }

        using var key = P256Keys.ImportPublicKey(publicKey);
        var canonical = AuthCanonicalPayload.Build(
            peer.EndpointRole,
            principalId,
            challenge.ChallengeId,
            challenge.Nonce,
            challenge.ExpiresAt.ToUnixTimeMilliseconds());
        if (!P256Keys.Verify(key, canonical, payload.Signature))
        {
            return ServiceResult<AuthOkPayload>.Failure("AUTH_FAILED", "Signature verification failed.");
        }

        peer.Authenticate(peer.EndpointRole, principalId);
        var now = DateTimeOffset.UtcNow;
        if (peer.EndpointRole == PrincipalRole.Device)
        {
            var device = await db.Devices.FindAsync([principalId], cancellationToken).ConfigureAwait(false);
            device!.LastSeenAt = now;
        }
        else
        {
            var controller = await db.Controllers.FindAsync([principalId], cancellationToken).ConfigureAwait(false);
            controller!.LastSeenAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ServiceResult<AuthOkPayload>.Success(new(
            peer.EndpointRole,
            principalId,
            peer.ConnectionId,
            typeof(AuthService).Assembly.GetName().Version?.ToString(3)));
    }

    private static bool IsValidPrincipalId(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Length is >= 12 and <= 80;

    private static bool IsValidDeviceRegistration(DeviceRegisterPayload payload) =>
        IsValidPrincipalId(payload.DeviceId, "dev_") &&
        !string.IsNullOrWhiteSpace(payload.Name) &&
        payload.Name.Length <= 200 &&
        IsValidPublicKey(payload.PublicKey);

    private static bool IsValidPublicKey(string value)
    {
        try
        {
            using var key = P256Keys.ImportPublicKey(value);
            return key.KeySize == 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool KeysEqual(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Base64Url.Decode(left), Base64Url.Decode(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
