namespace CodexControl.Agent.State;

public enum CodexActivityStatus
{
    Offline,
    Starting,
    Idle,
    Thinking,
    Reading,
    Editing,
    RunningCommand,
    RunningTests,
    WaitingApproval,
    WaitingUserInput,
    Completed,
    Interrupted,
    Failed,
    Unknown,
}

public sealed record CodexActiveTurnSnapshot(
    string ThreadId,
    string TurnId,
    CodexActivityStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    string? CurrentProject,
    string? CurrentActivity,
    string? RunningCommand,
    IReadOnlyList<string> ChangedFiles,
    int PendingApprovalCount,
    string? LastAgentMessage,
    string? LastError);

public sealed record CodexThreadStateSnapshot(
    string ThreadId,
    string ThreadState,
    string? ActiveTurnId,
    string? LastTurnId,
    string? LastTurnStatus,
    CodexActivityStatus Activity,
    bool WaitingOnApproval,
    bool WaitingOnUserInput,
    bool RequiresRefresh,
    DateTimeOffset LastActivityAt,
    string? CurrentProject);

public sealed record CodexStateSnapshot(
    long Revision,
    CodexActivityStatus Status,
    string? ActiveThreadId,
    string? ActiveTurnId,
    IReadOnlyList<CodexActiveTurnSnapshot> ActiveTurns,
    DateTimeOffset? StartedAt,
    DateTimeOffset LastActivityAt,
    string? CurrentProject,
    string? CurrentActivity,
    string? RunningCommand,
    IReadOnlyList<string> ChangedFiles,
    int PendingApprovalCount,
    string? LastAgentMessage,
    bool RelayConnected,
    int PairedControllerCount,
    string? LastError)
{
    public string ConnectionState { get; init; } = "offline";
    public IReadOnlyList<CodexThreadStateSnapshot> Threads { get; init; } = [];
    public static CodexStateSnapshot Initial { get; } = new(
        Revision: 0,
        Status: CodexActivityStatus.Offline,
        ActiveThreadId: null,
        ActiveTurnId: null,
        ActiveTurns: [],
        StartedAt: null,
        LastActivityAt: DateTimeOffset.UtcNow,
        CurrentProject: null,
        CurrentActivity: null,
        RunningCommand: null,
        ChangedFiles: [],
        PendingApprovalCount: 0,
        LastAgentMessage: null,
        RelayConnected: false,
        PairedControllerCount: 0,
        LastError: null);
}
