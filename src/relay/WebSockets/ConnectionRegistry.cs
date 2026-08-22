using System.Collections.Concurrent;
using CodexControl.Protocol;

namespace CodexControl.Relay.WebSockets;

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, RelayPeer> _devices = new();
    private readonly ConcurrentDictionary<string, RelayPeer> _controllers = new();

    public IReadOnlyCollection<RelayPeer> Devices => _devices.Values.ToArray();
    public IReadOnlyCollection<RelayPeer> Controllers => _controllers.Values.ToArray();

    public void Register(RelayPeer peer)
    {
        var id = peer.PrincipalId ?? throw new InvalidOperationException("Peer must be authenticated.");
        var collection = peer.Role == PrincipalRole.Device ? _devices : _controllers;
        collection.AddOrUpdate(id, peer, (_, previous) =>
        {
            if (!ReferenceEquals(previous, peer))
            {
                previous.Abort();
            }

            return peer;
        });
    }

    public bool Remove(RelayPeer peer)
    {
        if (peer.PrincipalId is null || peer.Role is null)
        {
            return false;
        }

        var collection = peer.Role == PrincipalRole.Device ? _devices : _controllers;
        return collection.TryRemove(new KeyValuePair<string, RelayPeer>(peer.PrincipalId, peer));
    }

    public RelayPeer? GetDevice(string deviceId) =>
        _devices.TryGetValue(deviceId, out var peer) ? peer : null;

    public RelayPeer? GetController(string controllerId) =>
        _controllers.TryGetValue(controllerId, out var peer) ? peer : null;
}
