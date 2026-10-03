using CodexControl.Agent.State;

namespace CodexControl.Agent.Lifecycle;

public enum RuntimeCoreStatus
{
    Stopped,
    Discovering,
    Probing,
    Starting,
    Ready,
    RestartPending,
    Failed,
    Stopping,
}

public enum RuntimeRelayStatus
{
    Disabled,
    Paused,
    Offline,
    Connecting,
    Online,
    Failed,
}

public sealed record AgentRuntimeSnapshot(
    long Revision,
    RuntimeCoreStatus CoreStatus,
    RuntimeRelayStatus RelayStatus,
    string Summary,
    string? CodexPath,
    string? CodexVersion,
    Uri? LocalProxyUri,
    bool LocalTuiConnected,
    bool RestartPending,
    CodexStateSnapshot Codex,
    string? LastError)
{
    public bool IsSharedSession { get; init; }
    public string? ServiceInstanceId { get; init; }
    public static AgentRuntimeSnapshot Initial { get; } = new(
        Revision: 0,
        RuntimeCoreStatus.Stopped,
        RuntimeRelayStatus.Disabled,
        Summary: "尚未启动",
        CodexPath: null,
        CodexVersion: null,
        LocalProxyUri: null,
        LocalTuiConnected: false,
        RestartPending: false,
        CodexStateSnapshot.Initial,
        LastError: null);
}
