using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using CodexControl.Protocol;

namespace CodexControl.Relay.WebSockets;

public sealed class RelayPeer : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Channel<RelayEnvelope> _outbound;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _sendTask;
    private int _closed;
    private long _lastActivityUnixMilliseconds;

    public RelayPeer(WebSocket socket, IPAddress remoteAddress, PrincipalRole endpointRole)
    {
        _socket = socket;
        RemoteAddress = remoteAddress;
        EndpointRole = endpointRole;
        ConnectionId = string.Concat("conn_", Guid.NewGuid().ToString("N"));
        _lastActivityUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _outbound = Channel.CreateBounded<RelayEnvelope>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _sendTask = SendLoopAsync(_lifetime.Token);
    }

    public string ConnectionId { get; }
    public IPAddress RemoteAddress { get; }
    public PrincipalRole EndpointRole { get; }
    public PrincipalRole? Role { get; private set; }
    public string? PrincipalId { get; private set; }
    public string? PendingPrincipalId { get; set; }
    public DateTimeOffset LastActivityAt =>
        DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastActivityUnixMilliseconds));
    public bool IsAuthenticated => Role is not null && PrincipalId is not null;
    public bool IsReady { get; private set; }
    public WebSocket Socket => _socket;

    public void Authenticate(PrincipalRole role, string principalId)
    {
        if (role != EndpointRole)
        {
            throw new InvalidOperationException("Authenticated role does not match WebSocket endpoint.");
        }

        Role = role;
        PrincipalId = principalId;
        PendingPrincipalId = null;
        Touch();
    }

    public void MarkReady()
    {
        if (!IsAuthenticated || Role != PrincipalRole.Device)
        {
            throw new InvalidOperationException("Only an authenticated Device can become ready.");
        }

        IsReady = true;
        Touch();
    }

    public void Touch() =>
        Interlocked.Exchange(ref _lastActivityUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public bool TrySend(RelayEnvelope envelope)
    {
        if (Volatile.Read(ref _closed) != 0 || !_outbound.Writer.TryWrite(envelope))
        {
            Abort();
            return false;
        }

        return true;
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _outbound.Writer.TryComplete();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(status, reason, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _lifetime.Cancel();
        }
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _outbound.Writer.TryComplete();
        _lifetime.Cancel();
        _socket.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(WebSocketCloseStatus.NormalClosure, "connection closed", CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            await _sendTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }

        _lifetime.Dispose();
        _socket.Dispose();
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, RelayJson.Options);
                await _socket.SendAsync(
                    bytes.AsMemory(),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            Abort();
        }
    }
}
