using System.Collections.Concurrent;
using CodexControl.Relay.WebSockets;

namespace CodexControl.Relay.Pairing;

public sealed record PendingPairingConnection(
    string PairingRequestId,
    string OriginalRequestId,
    string ControllerId,
    RelayPeer Peer);

public sealed class PendingPairingConnections
{
    private readonly ConcurrentDictionary<string, PendingPairingConnection> _connections = new(StringComparer.Ordinal);

    public void Add(PendingPairingConnection connection) =>
        _connections[connection.PairingRequestId] = connection;

    public bool TryRemove(string pairingRequestId, out PendingPairingConnection connection) =>
        _connections.TryRemove(pairingRequestId, out connection!);

    public IReadOnlyList<PendingPairingConnection> RemoveMany(IEnumerable<string> requestIds)
    {
        var removed = new List<PendingPairingConnection>();
        foreach (var requestId in requestIds)
        {
            if (_connections.TryRemove(requestId, out var connection))
            {
                removed.Add(connection);
            }
        }

        return removed;
    }

    public void RemovePeer(RelayPeer peer)
    {
        foreach (var entry in _connections)
        {
            if (ReferenceEquals(entry.Value.Peer, peer))
            {
                _connections.TryRemove(entry.Key, out _);
            }
        }
    }
}
