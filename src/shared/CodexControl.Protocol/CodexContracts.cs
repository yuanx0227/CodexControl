using System.Text.Json;

namespace CodexControl.Protocol;

public sealed record DeviceSummaryPayload(
    string DeviceId,
    string Name,
    bool Online,
    CodexSnapshotPayload? Snapshot,
    long? LastSeenAt);

public sealed record DeviceListResultPayload(
    IReadOnlyList<DeviceSummaryPayload> Devices);

public sealed record CodexThreadSummaryPayload(
    string ThreadId,
    string? Name,
    string? Preview,
    string? Cwd,
    long? CreatedAt,
    long? UpdatedAt,
    string Status,
    string? SourceKind);

public sealed record CodexThreadListResultPayload(
    IReadOnlyList<CodexThreadSummaryPayload> Threads,
    string? NextCursor);

public sealed record CodexThreadHistoryEntryPayload(
    string ItemId,
    string TurnId,
    string Role,
    string Text,
    string? Phase);

public sealed record CodexThreadReadResultPayload(
    string ThreadId,
    string? Name,
    string? Cwd,
    IReadOnlyList<CodexThreadHistoryEntryPayload> Entries,
    bool Truncated);

public sealed record CodexThreadActionResultPayload(
    string ThreadId,
    string TurnId);

public sealed record CodexSnapshotPayload(
    long Revision,
    string Status,
    string? ActiveThreadId,
    string? ActiveTurnId,
    long? StartedAt,
    long LastActivityAt,
    string? CurrentProject,
    string? CurrentActivity,
    string? RunningCommand,
    IReadOnlyList<string> ChangedFiles,
    int PendingApprovalCount,
    string? LastAgentMessage,
    string? LastError);

public sealed record CodexEventPayload(
    string EventId,
    long Revision,
    string Kind,
    string? ThreadId,
    string? TurnId,
    string? ItemId,
    long OccurredAt,
    JsonElement Data);

public sealed record ApprovalRequestedPayload(
    string ApprovalId,
    string RequestMethod,
    string? ThreadId,
    string? TurnId,
    string? ItemId,
    string? Command,
    string? Cwd,
    string? Reason,
    IReadOnlyList<JsonElement> AvailableDecisions,
    long RequestedAt);

public sealed record ApprovalResolvedPayload(
    string ApprovalId,
    string? ResolvedBy,
    long ResolvedAt);
