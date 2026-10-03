using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace CodexControl.SharedSessionProbe;

/// <summary>
/// A connection-only JSON-RPC client. It never starts, stops, or reconfigures the server.
/// </summary>
public sealed class SharedRpcClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 8 * 1024 * 1024;
    private readonly ClientWebSocket _socket;
    private readonly HttpMessageInvoker _httpInvoker;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _closedToken;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly string _requestPrefix = "shared_probe_" + Guid.NewGuid().ToString("N") + "_";
    private readonly Task _receiveTask;
    private long _nextRequestId;
    private int _disposed;

    private SharedRpcClient(ClientWebSocket socket, HttpMessageInvoker httpInvoker)
    {
        _socket = socket;
        _httpInvoker = httpInvoker;
        _closedToken = _lifetime.Token;
        _receiveTask = ReceiveLoopAsync();
    }

    /// <summary>
    /// Receives complete method messages, including server requests with an id.
    /// The client does not automatically answer server requests.
    /// </summary>
    public event Action<JsonElement>? Notification;

    public Task Completion => _receiveTask;

    public static async Task<SharedRpcClient> ConnectUnixAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(socketPath) || !Path.IsPathFullyQualified(socketPath))
        {
            throw new ArgumentException("The Unix socket path must be absolute.", nameof(socketPath));
        }

        var handler = CreateHttpHandler();
        handler.ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        // The HTTP upgrade still runs through ClientWebSocket; only the transport is Unix-domain.
        return await ConnectAsync(new Uri("ws://localhost/"), handler, cancellationToken).ConfigureAwait(false);
    }

    public static Task<SharedRpcClient> ConnectLoopbackAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "ws" ||
            !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            endpoint.Port is < 1 or > 65535 ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException(
                "Only ws://127.0.0.1:port/path without user info, query, or fragment is allowed.",
                nameof(endpoint));
        }

        return ConnectAsync(endpoint, CreateHttpHandler(), cancellationToken);
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        ValidateMethod(method);
        ThrowIfClosed();
        var id = _requestPrefix + Interlocked.Increment(ref _nextRequestId).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var pending = new PendingRequest(method);
        if (!_pending.TryAdd(id, pending))
        {
            throw new InvalidOperationException("Unable to allocate a JSON-RPC request id.");
        }

        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken)
                .ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task RespondAsync(JsonElement id, object result, CancellationToken cancellationToken)
    {
        if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            throw new ArgumentException("A server request id must be a string or number.", nameof(id));
        }

        return SendAsync(new { jsonrpc = "2.0", id, result }, cancellationToken);
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ValidateMethod(method);
        return SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // No RPC is sent during disposal, and no process or service is stopped.
        _lifetime.Cancel();
        _socket.Abort();
        FailPending();
        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch
        {
            // Completion remains observable by the caller; disposal only releases the connection.
        }

        _socket.Dispose();
        _httpInvoker.Dispose();
        _lifetime.Dispose();
        // Do not dispose the semaphore while cancelled callers may still be unwinding WaitAsync.
    }

    private static SocketsHttpHandler CreateHttpHandler() => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        Credentials = null,
    };

    private static async Task<SharedRpcClient> ConnectAsync(
        Uri endpoint,
        SocketsHttpHandler handler,
        CancellationToken cancellationToken)
    {
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version11;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        try
        {
            await socket.ConnectAsync(endpoint, invoker, cancellationToken).ConfigureAwait(false);
            return new SharedRpcClient(socket, invoker);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            invoker.Dispose();
            throw;
        }
        catch
        {
            socket.Dispose();
            invoker.Dispose();
            throw new IOException("Unable to establish the shared app-server WebSocket connection.");
        }
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The JSON-RPC message exceeds the 8 MiB limit.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closedToken);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbortConnection();
            throw;
        }
        catch
        {
            AbortConnection();
            throw new IOException("The shared app-server connection is closed.");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_closedToken.IsCancellationRequested)
            {
                var received = await _socket.ReceiveAsync(buffer.AsMemory(), _closedToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("The shared app-server connection was closed by the server.");
                }

                if (received.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("The shared app-server sent a non-text WebSocket message.");
                }

                if (message.Length + received.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("The JSON-RPC message exceeds the 8 MiB limit.");
                }

                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(message.ToArray());
                DispatchMessage(document.RootElement);
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) when (_closedToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch
        {
            // Never retain or expose response bodies, server error text, or endpoint details.
            throw new IOException("The shared app-server connection closed or sent an invalid message.");
        }
        finally
        {
            AbortConnection();
        }
    }

    private void DispatchMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A JSON-RPC message must be an object.");
        }

        if (message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            var handlers = Notification;
            if (handlers is null)
            {
                return;
            }

            var ownedMessage = message.Clone();
            foreach (Action<JsonElement> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(ownedMessage);
                }
                catch
                {
                    // A consumer must not break the reader or cause an implicit server response.
                }
            }

            return;
        }

        if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !_pending.TryRemove(id.GetString()!, out var pending))
        {
            return;
        }

        if (message.TryGetProperty("error", out var error))
        {
            long? code = error.ValueKind == JsonValueKind.Object &&
                         error.TryGetProperty("code", out var value) &&
                         value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                ? number
                : null;
            pending.Completion.TrySetException(new SharedRpcException(code, pending.Method, ClassifyError(error)));
        }
        else if (message.TryGetProperty("result", out var result))
        {
            pending.Completion.TrySetResult(result.Clone());
        }
        else
        {
            pending.Completion.TrySetException(new SharedRpcException(null, pending.Method));
        }
    }

    private void AbortConnection()
    {
        try
        {
            _lifetime.Cancel();
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }

        FailPending();
    }

    private static string ClassifyError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.String) return "UNCLASSIFIED";
        // Return fixed categories only. Never retain or print the server's error text or data.
        var value = message.GetString()!;
        if (value.Contains("mcp_servers.codex_app", StringComparison.OrdinalIgnoreCase)) return "DESKTOP_TOOL_TRANSPORT";
        if (value.Contains("not materialized", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("resolve rollout path", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("no rollout", StringComparison.OrdinalIgnoreCase)) return "THREAD_NOT_MATERIALIZED";
        if (value.Contains("thread not found", StringComparison.OrdinalIgnoreCase)) return "THREAD_NOT_FOUND";
        if (value.Contains("already running", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("active writer", StringComparison.OrdinalIgnoreCase)) return "THREAD_OWNERSHIP_CONFLICT";
        if (value.Contains("closing", StringComparison.OrdinalIgnoreCase)) return "THREAD_CLOSING";
        return "UNCLASSIFIED";
    }

    private void FailPending()
    {
        foreach (var entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out var pending))
            {
                pending.Completion.TrySetException(new IOException("The shared app-server connection is closed."));
            }
        }
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_closedToken.IsCancellationRequested || _socket.State != WebSocketState.Open)
        {
            throw new IOException("The shared app-server connection is closed.");
        }
    }

    private static void ValidateMethod(string method)
    {
        if (string.IsNullOrEmpty(method) || method.Length > 128 ||
            method.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '_' or '-' or '.')))
        {
            throw new ArgumentException("The JSON-RPC method name is invalid.", nameof(method));
        }
    }

    private sealed class PendingRequest(string method)
    {
        public string Method { get; } = method;

        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class SharedRpcException(long? code, string method, string category = "UNCLASSIFIED")
    : Exception($"Shared app-server RPC failed (method={method}, code={code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}).")
{
    public long? Code { get; } = code;

    public string Method { get; } = method;

    public string Category { get; } = category;
}
