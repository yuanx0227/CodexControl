using System.Text.Json;

namespace CodexControl.Protocol;

public sealed record SteerControlPayload(
    string ThreadId,
    string ExpectedTurnId,
    string Text);

public sealed record InterruptControlPayload(
    string ThreadId,
    string TurnId);

public sealed record ApprovalControlPayload(
    string ApprovalId,
    JsonElement Decision);

public enum ControlResultStatus
{
    Accepted,
    Succeeded,
    Failed,
}

public sealed record ControlResultPayload(
    ControlResultStatus Status,
    string? Code,
    string? Message,
    JsonElement? Result);

public sealed record HeartbeatPayload(
    string ConnectionId,
    long SnapshotRevision);

public sealed record ErrorPayload(
    string Code,
    string Message);
