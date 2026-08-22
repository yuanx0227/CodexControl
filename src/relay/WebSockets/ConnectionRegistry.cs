using System.Collections.Concurrent;
using CodexControl.Protocol;

namespace CodexControl.Relay.WebSockets;

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, RelayPeer> _devices = new();
    private readonly ConcurrentDictionary<(string PrincipalId, string ConnectionId), RelayPeer> _controllers = new();

    public IReadOnlyCollection<RelayPeer> Devices => _devices.Values.ToArray();
    public IReadOnlyCollection<RelayPeer> Controllers => _controllers.Values.ToArray();

    public void Register(RelayPeer peer)
    {
        var id = peer.PrincipalId ?? throw new InvalidOperationException("Peer must be authenticated.");
        if (peer.Role == PrincipalRole.Controller)
        {
            _controllers[(id, peer.ConnectionId)] = peer;
            return;
        }

        _devices.AddOrUpdate(id, peer, (_, previous) =>
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

        return peer.Role == PrincipalRole.Device
            ? _devices.TryRemove(new KeyValuePair<string, RelayPeer>(peer.PrincipalId, peer))
            : _controllers.TryRemove(
                new KeyValuePair<(string PrincipalId, string ConnectionId), RelayPeer>(
                    (peer.PrincipalId, peer.ConnectionId),
                    peer));
    }

    public RelayPeer? GetDevice(string deviceId) =>
        _devices.TryGetValue(deviceId, out var peer) ? peer : null;

    public IReadOnlyCollection<RelayPeer> GetControllers(string controllerId) =>
        _controllers
            .Where(entry => entry.Key.PrincipalId == controllerId)
            .Select(entry => entry.Value)
            .ToArray();
}
