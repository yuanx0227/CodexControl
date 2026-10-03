using CodexControl.Agent.Codex;
using CodexControl.Agent.Diagnostics;

namespace CodexControl.Agent.Control;

/// <summary>
/// Non-static Agent-runtime memory. Transport reconnects retain authorized observations and known
/// mutation results; process restarts deliberately fall back to the durable submission tombstones.
/// </summary>
public sealed class SharedSessionRuntimeContext
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Service, string Thread), string> _observedWorkspaces = new();
    private readonly Dictionary<string, ControlSubmissionLedger> _ledgers = new(StringComparer.OrdinalIgnoreCase);

    public ControlSubmissionLedger GetSubmissionLedger(string dataDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        lock (_gate)
        {
            if (!_ledgers.TryGetValue(directory, out var ledger))
                _ledgers.Add(directory, ledger = new ControlSubmissionLedger(directory));
            return ledger;
        }
    }

    internal string? ObservedWorkspace(string serviceInstanceId, string threadId)
    {
        if (string.IsNullOrEmpty(serviceInstanceId)) return null;
        lock (_gate) return _observedWorkspaces.GetValueOrDefault((serviceInstanceId, threadId));
    }

    internal void RememberObservation(string serviceInstanceId, string threadId, string workspace)
    {
        if (string.IsNullOrEmpty(serviceInstanceId))
            throw new AgentException("SERVICE_IDENTITY_UNKNOWN", "Cannot retain an observation without a verified service identity.");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        lock (_gate)
        {
            var key = (serviceInstanceId, threadId);
            if (_observedWorkspaces.TryGetValue(key, out var existing) && !ThreadWorkspacePolicy.SamePath(normalized, existing))
                throw new AgentException("PROJECT_SCOPE_CHANGED", "The observed Thread workspace changed.");
            _observedWorkspaces[key] = normalized;
        }
    }
}
