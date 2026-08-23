namespace CodexControl.Relay.Persistence;

public sealed class DeviceEntity
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string PublicKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? LatestSnapshotJson { get; set; }
    public long LatestSnapshotRevision { get; set; }
    public ICollection<PairingEntity> Pairings { get; set; } = [];
}

public sealed class ControllerEntity
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string PublicKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public ICollection<PairingEntity> Pairings { get; set; } = [];
}

public sealed class PairingEntity
{
    public long Id { get; set; }
    public required string DeviceId { get; set; }
    public DeviceEntity Device { get; set; } = null!;
    public required string ControllerId { get; set; }
    public ControllerEntity Controller { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public bool ViewPermission { get; set; }
    public bool SteerPermission { get; set; }
    public bool InterruptPermission { get; set; }
    public bool ApprovalPermission { get; set; }
    public string? Alias { get; set; }
}

public sealed class PairingSessionEntity
{
    public required string Id { get; set; }
    public required string CodeHash { get; set; }
    public required string CodeLookupHash { get; set; }
    public required string DeviceId { get; set; }
    public DeviceEntity Device { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class PairingRequestEntity
{
    public required string Id { get; set; }
    public required string SessionId { get; set; }
    public required string DeviceId { get; set; }
    public required string ControllerId { get; set; }
    public required string ControllerName { get; set; }
    public required string PublicKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Outcome { get; set; }
}

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public required string EventType { get; set; }
    public string? DeviceId { get; set; }
    public string? ControllerId { get; set; }
    public string? RequestId { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
