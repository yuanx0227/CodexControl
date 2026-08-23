using System.Text.Json;

namespace CodexControl.Agent.Configuration;

public sealed record PendingPairingRevocation(long PairingId, string RequestId, DateTimeOffset CreatedAt);

public sealed record AgentPersistentState(
    int SchemaVersion,
    IReadOnlyList<PendingPairingRevocation> PendingPairingRevocations)
{
    public static AgentPersistentState Empty { get; } = new(1, []);
}

public sealed class AgentStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AgentDataPaths _paths;
    private readonly object _gate = new();

    public AgentStateStore(AgentDataPaths paths)
    {
        _paths = paths;
    }

    public AgentPersistentState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_paths.StatePath))
            {
                return AgentPersistentState.Empty;
            }

            try
            {
                var state = JsonSerializer.Deserialize<AgentPersistentState>(
                    File.ReadAllText(_paths.StatePath),
                    JsonOptions);
                return state is { SchemaVersion: 1 } ? state : AgentPersistentState.Empty;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                return AgentPersistentState.Empty;
            }
        }
    }

    public AgentPersistentState AddRevocation(long pairingId)
    {
        lock (_gate)
        {
            var state = Load();
            if (state.PendingPairingRevocations.Any(value => value.PairingId == pairingId))
            {
                return state;
            }

            return Save(state with
            {
                PendingPairingRevocations = [
                    .. state.PendingPairingRevocations,
                    new PendingPairingRevocation(
                        pairingId,
                        string.Concat("req_", Guid.NewGuid().ToString("N")),
                        DateTimeOffset.UtcNow),
                ],
            });
        }
    }

    public AgentPersistentState RemoveRevocation(long pairingId)
    {
        lock (_gate)
        {
            var state = Load();
            return Save(state with
            {
                PendingPairingRevocations = state.PendingPairingRevocations
                    .Where(value => value.PairingId != pairingId)
                    .ToArray(),
            });
        }
    }

    private AgentPersistentState Save(AgentPersistentState state)
    {
        _paths.EnsureWritable();
        var temporary = string.Concat(_paths.StatePath, ".tmp-", Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, _paths.StatePath, overwrite: true);
            return state;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
