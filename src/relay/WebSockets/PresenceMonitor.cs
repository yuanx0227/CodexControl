using CodexControl.Relay.Configuration;

namespace CodexControl.Relay.WebSockets;

public sealed class PresenceMonitor(
    ConnectionRegistry connections,
    RelayOptions options,
    ILogger<PresenceMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var threshold = DateTimeOffset.UtcNow - options.HeartbeatTimeout;
            foreach (var peer in connections.Devices.Concat(connections.Controllers))
            {
                if (peer.LastActivityAt < threshold)
                {
                    logger.LogWarning(
                        "Closing stale {Role} connection {PrincipalId}",
                        peer.Role,
                        peer.PrincipalId);
                    peer.Abort();
                }
            }
        }
    }
}
