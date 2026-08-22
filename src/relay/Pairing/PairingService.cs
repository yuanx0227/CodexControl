using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodexControl.Protocol;
using CodexControl.Relay.Configuration;
using CodexControl.Relay.Persistence;
using CodexControl.Relay.Services;
using Microsoft.EntityFrameworkCore;

namespace CodexControl.Relay.Pairing;

public sealed record PairingClaimResult(
    string DeviceId,
    string ControllerId,
    PairingPermissions Permissions);

public sealed class PairingService
{
    private static readonly TimeSpan PairingTtl = TimeSpan.FromMinutes(3);
    private const int MaxAttempts = 5;

    private readonly IDbContextFactory<RelayDbContext> _dbFactory;
    private readonly RelayOptions _options;
    private readonly SemaphoreSlim[] _claimLocks = Enumerable.Range(0, 256)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();

    public PairingService(IDbContextFactory<RelayDbContext> dbFactory, RelayOptions options)
    {
        _dbFactory = dbFactory;
        _options = options;
    }

    public async Task<ServiceResult<PairingCreatedPayload>> CreateAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var allUnconsumed = await db.PairingSessions
            .Where(value => value.ConsumedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var expired in allUnconsumed.Where(value => value.ExpiresAt <= now))
        {
            expired.ConsumedAt = now;
        }

        var existing = allUnconsumed
            .Where(value => value.DeviceId == deviceId && value.ExpiresAt > now)
            .ToArray();
        foreach (var session in existing)
        {
            session.ConsumedAt = now;
        }

        var activeLookups = allUnconsumed
            .Where(value => value.ExpiresAt > now)
            .Select(value => value.CodeLookupHash)
            .ToHashSet(StringComparer.Ordinal);
        string code;
        string lookupHash;
        do
        {
            code = RandomNumberGenerator.GetInt32(1_000_000)
                .ToString("D6", CultureInfo.InvariantCulture);
            lookupHash = ComputeLookupHash(code);
        }
        while (activeLookups.Contains(lookupHash));
        var id = string.Concat("pair_", Guid.NewGuid().ToString("N"));
        var expiresAt = now + PairingTtl;
        db.PairingSessions.Add(new PairingSessionEntity
        {
            Id = id,
            CodeHash = ComputeCodeHash(id, code),
            CodeLookupHash = lookupHash,
            DeviceId = deviceId,
            ExpiresAt = expiresAt,
            AttemptCount = 0,
            CreatedAt = now,
        });
        db.AuditEvents.Add(new AuditEventEntity
        {
            EventType = RelayMessageTypes.PairingCreate,
            DeviceId = deviceId,
            Outcome = "created",
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ServiceResult<PairingCreatedPayload>.Success(new(
            code,
            expiresAt.ToUnixTimeMilliseconds(),
            MaxAttempts));
    }

    public async Task<ServiceResult<PairingClaimResult>> ClaimAsync(
        PairingClaimPayload payload,
        CancellationToken cancellationToken)
    {
        if (payload.Code.Length != 6 || payload.Code.Any(static value => !char.IsAsciiDigit(value)) ||
            !payload.ControllerId.StartsWith("ctl_", StringComparison.Ordinal) ||
            payload.ControllerId.Length is < 12 or > 80 ||
            string.IsNullOrWhiteSpace(payload.ControllerName) || payload.ControllerName.Length > 200 ||
            payload.ProofNonce.Length is < 16 or > 200)
        {
            return ServiceResult<PairingClaimResult>.Failure("PAIRING_INVALID", "Pairing code is invalid.");
        }

        var lookupHash = ComputeLookupHash(payload.Code);
        var lockIndex = (StringComparer.Ordinal.GetHashCode(lookupHash) & int.MaxValue) % _claimLocks.Length;
        var claimLock = _claimLocks[lockIndex];
        await claimLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ClaimLockedAsync(payload, lookupHash, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            claimLock.Release();
        }
    }

    private async Task<ServiceResult<PairingClaimResult>> ClaimLockedAsync(
        PairingClaimPayload payload,
        string lookupHash,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.PairingSessions
            .Where(value => value.CodeLookupHash == lookupHash &&
                            value.ConsumedAt == null && value.AttemptCount < MaxAttempts)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var active = candidates.Where(value => value.ExpiresAt > now).ToArray();
        var session = active.Length == 1 && HashMatches(active[0], payload.Code) ? active[0] : null;
        if (session is null)
        {
            return ServiceResult<PairingClaimResult>.Failure("PAIRING_INVALID", "Pairing code is invalid.");
        }

        if (!VerifyProof(payload))
        {
            session.AttemptCount++;
            if (session.AttemptCount >= MaxAttempts)
            {
                session.ConsumedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ServiceResult<PairingClaimResult>.Failure("PAIRING_INVALID", "Pairing proof is invalid.");
        }

        var controller = await db.Controllers.SingleOrDefaultAsync(
            value => value.Id == payload.ControllerId,
            cancellationToken).ConfigureAwait(false);
        if (controller is null)
        {
            controller = new ControllerEntity
            {
                Id = payload.ControllerId,
                Name = payload.ControllerName,
                PublicKey = payload.PublicKey,
                CreatedAt = now,
                LastSeenAt = now,
            };
            db.Controllers.Add(controller);
        }
        else if (!KeysEqual(controller.PublicKey, payload.PublicKey) || controller.RevokedAt is not null)
        {
            return ServiceResult<PairingClaimResult>.Failure(
                "CONTROLLER_IDENTITY_CONFLICT",
                "Controller ID is registered with another key or revoked.");
        }
        else
        {
            controller.Name = payload.ControllerName;
            controller.LastSeenAt = now;
        }

        var pairing = await db.Pairings.SingleOrDefaultAsync(
            value => value.DeviceId == session.DeviceId && value.ControllerId == payload.ControllerId,
            cancellationToken).ConfigureAwait(false);
        if (pairing is null)
        {
            pairing = new PairingEntity
            {
                DeviceId = session.DeviceId,
                ControllerId = payload.ControllerId,
                CreatedAt = now,
            };
            db.Pairings.Add(pairing);
        }

        pairing.RevokedAt = null;
        pairing.ViewPermission = true;
        pairing.SteerPermission = true;
        pairing.InterruptPermission = true;
        pairing.ApprovalPermission = true;
        session.ConsumedAt = now;
        db.AuditEvents.Add(new AuditEventEntity
        {
            EventType = RelayMessageTypes.PairingClaim,
            DeviceId = session.DeviceId,
            ControllerId = payload.ControllerId,
            Outcome = "completed",
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ServiceResult<PairingClaimResult>.Success(new(
            session.DeviceId,
            payload.ControllerId,
            PairingPermissions.Full));
    }

    public async Task<PairingEntity?> GetActivePairingAsync(
        string deviceId,
        string controllerId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Pairings.AsNoTracking().SingleOrDefaultAsync(
            value => value.DeviceId == deviceId &&
                     value.ControllerId == controllerId &&
                     value.RevokedAt == null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeviceEntity>> ListDevicesAsync(
        string controllerId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Pairings.AsNoTracking()
            .Where(value => value.ControllerId == controllerId && value.RevokedAt == null && value.ViewPermission)
            .Select(value => value.Device)
            .Where(value => value.RevokedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(
        string deviceId,
        string controllerId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pairing = await db.Pairings.SingleOrDefaultAsync(
            value => value.DeviceId == deviceId && value.ControllerId == controllerId && value.RevokedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (pairing is null)
        {
            return false;
        }

        pairing.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(new AuditEventEntity
        {
            EventType = RelayMessageTypes.PairingRevoked,
            DeviceId = deviceId,
            ControllerId = controllerId,
            Outcome = "revoked",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private string ComputeCodeHash(string sessionId, string code)
    {
        using var hmac = new HMACSHA256(_options.PairingSecret);
        return Base64Url.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(
            string.Concat("codex-control-pairing-v1\n", sessionId, "\n", code))));
    }

    private string ComputeLookupHash(string code)
    {
        using var hmac = new HMACSHA256(_options.PairingSecret);
        return Base64Url.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(
            string.Concat("codex-control-pairing-lookup-v1\n", code))));
    }

    private bool HashMatches(PairingSessionEntity session, string code)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Base64Url.Decode(session.CodeHash),
                Base64Url.Decode(ComputeCodeHash(session.Id, code)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool VerifyProof(PairingClaimPayload payload)
    {
        try
        {
            using var key = P256Keys.ImportPublicKey(payload.PublicKey);
            return P256Keys.Verify(
                key,
                PairingProofCanonicalPayload.Build(
                    payload.Code,
                    payload.ControllerId,
                    payload.PublicKey,
                    payload.ProofNonce),
                payload.ProofSignature);
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
