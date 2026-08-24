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
    long? RecencyAt,
    string Status,
    string? SourceKind,
    string? ProjectId);

public sealed record CodexProjectSummaryPayload(
    string ProjectId,
    string Name,
    long Position,
    IReadOnlyList<string> Roots);

public sealed record CodexThreadListResultPayload(
    IReadOnlyList<CodexThreadSummaryPayload> Threads,
    IReadOnlyList<CodexProjectSummaryPayload> Projects,
    string? NextCursor);

public sealed record CodexThreadHistoryEntryPayload(
    string ItemId,
    string TurnId,
    string Role,
    string Text,
    string? Phase,
    IReadOnlyList<CodexThreadHistoryAttachmentPayload> Attachments,
    IReadOnlyList<CodexThreadHistoryFileChangePayload> Changes);

public sealed record CodexThreadHistoryAttachmentPayload(
    string Kind,
    string Name,
    string MimeType,
    string DataUrl);

public sealed record CodexThreadHistoryFileChangePayload(
    string Path,
    string? Kind,
    int? Additions,
    int? Deletions);

public sealed record CodexTurnTimingPayload(
    string TurnId,
    string? Status,
    long? StartedAt,
    long? CompletedAt,
    long? DurationMs);

public sealed record CodexThreadReadResultPayload(
    string ThreadId,
    string? Name,
    string? Cwd,
    IReadOnlyList<CodexThreadHistoryEntryPayload> Entries,
    IReadOnlyList<CodexTurnTimingPayload> Turns,
    bool Truncated);

public sealed record CodexThreadActionResultPayload(
    string ThreadId,
    string TurnId);

public sealed record CodexModelOptionPayload(
    string Id,
    string Model,
    string DisplayName,
    string Description,
    bool IsDefault);

public sealed record CodexApprovalPolicyOptionPayload(
    string Id,
    string DisplayName,
    string Description,
    bool IsDefault);

public sealed record CodexSessionOptionsPayload(
    IReadOnlyList<CodexModelOptionPayload> Models,
    IReadOnlyList<CodexApprovalPolicyOptionPayload> ApprovalPolicies);

public sealed record CodexActiveTurnPayload(
    string ThreadId,
    string TurnId,
    string Status,
    long StartedAt,
    long LastActivityAt,
    string? CurrentProject,
    string? CurrentActivity,
    string? RunningCommand,
    IReadOnlyList<string> ChangedFiles,
    int PendingApprovalCount,
    string? LastAgentMessage,
    string? LastError);

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
    string? LastError,
    string? AgentVersion = null,
    IReadOnlyList<CodexActiveTurnPayload>? ActiveTurns = null);

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
