using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Diagnostics;

namespace CodexControl.Agent.Proxy;

public sealed class TuiWebSocketSession : IAppServerClientSink
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly WebSocket _socket;
    private readonly AppServerBridge _bridge;
    private readonly AgentLog _log;
    private readonly int _maxMessageBytes;
    private readonly Channel<string> _outbound;
    private readonly CancellationTokenSource _abort = new();
    private int _aborted;

    public TuiWebSocketSession(
        WebSocket socket,
        AppServerBridge bridge,
        AgentLog log,
        int maxMessageBytes)
    {
        _socket = socket;
        _bridge = bridge;
        _log = log;
        _maxMessageBytes = maxMessageBytes;
        _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _abort.Token);
        var token = linkedCancellation.Token;

        if (!await CompleteLocalHandshakeAsync(token).ConfigureAwait(false))
        {
            return;
        }

        if (!_bridge.TryRegisterClient(this))
        {
            await CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "app-server bridge is not ready or already has a client",
                token).ConfigureAwait(false);
            return;
        }

        try
        {
            var sendTask = SendLoopAsync(token);
            var receiveTask = ReceiveLoopAsync(token);
            await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);
            linkedCancellation.Cancel();
            _outbound.Writer.TryComplete();
            await IgnoreCancellationAsync(Task.WhenAll(sendTask, receiveTask)).ConfigureAwait(false);
        }
        finally
        {
            _bridge.UnregisterClient(this);
            await CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "session closed",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public bool TryQueue(string message)
    {
        return Volatile.Read(ref _aborted) == 0 && _outbound.Writer.TryWrite(message);
    }

    public void Abort(string reason)
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        _log.Warning("tui_aborted", reason);
        _outbound.Writer.TryComplete();
        _abort.Cancel();
        try
        {
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
            // Kestrel 已完成请求释放时，连接已经终止，无需再次 Abort。
        }
    }

    private async Task<bool> CompleteLocalHandshakeAsync(CancellationToken cancellationToken)
    {
        var initializeJson = await ReceiveTextMessageAsync(cancellationToken).ConfigureAwait(false);
        if (initializeJson is null)
        {
            return false;
        }

        using (var document = JsonRpcProtocol.Parse(initializeJson))
        {
            var message = document.RootElement;
            if (!JsonRpcProtocol.TryGetMethod(message, out var method) ||
                method != "initialize" ||
                !JsonRpcProtocol.TryGetId(message, out var id))
            {
                await CloseAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    "first message must be initialize request",
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (JsonRpcProtocol.HasReservedBridgeId(message))
            {
                var error = JsonRpcProtocol.BuildErrorResponse(
                    id,
                    -32600,
                    AgentErrorCodes.RequestIdNamespaceCollision);
                await SendTextAsync(error, cancellationToken).ConfigureAwait(false);
                await CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    AgentErrorCodes.RequestIdNamespaceCollision,
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            var experimentalApi = TryReadBoolean(
                message,
                "params",
                "capabilities",
                "experimentalApi");
            var openAiForm = TryReadBoolean(
                message,
                "params",
                "capabilities",
                "mcpServerOpenaiFormElicitation");
            var extensionCount = CountObjectProperties(
                message,
                "params",
                "capabilities",
                "extensions");
            _log.Info(
                "tui_initialize_capabilities",
                $"experimentalApi={experimentalApi}; openAiForm={openAiForm}; extensions={extensionCount}");

            if (!experimentalApi || openAiForm || extensionCount > 0)
            {
                var error = JsonRpcProtocol.BuildErrorResponse(
                    id,
                    -32602,
                    "TUI_CAPABILITY_UNSUPPORTED");
                await SendTextAsync(error, cancellationToken).ConfigureAwait(false);
                await CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "TUI_CAPABILITY_UNSUPPORTED",
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            var response = JsonRpcProtocol.BuildResultResponse(id, _bridge.InitializeResult);
            await SendTextAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var initializedJson = await ReceiveTextMessageAsync(cancellationToken).ConfigureAwait(false);
        if (initializedJson is null)
        {
            return false;
        }

        using (var document = JsonRpcProtocol.Parse(initializedJson))
        {
            var message = document.RootElement;
            if (!JsonRpcProtocol.TryGetMethod(message, out var method) ||
                method != "initialized" ||
                JsonRpcProtocol.TryGetId(message, out _))
            {
                await CloseAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    "second message must be initialized notification",
                    cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var message = await ReceiveTextMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                break;
            }

            using var document = JsonRpcProtocol.Parse(message);
            var root = document.RootElement;
            if (JsonRpcProtocol.HasReservedBridgeId(root))
            {
                await CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    AgentErrorCodes.RequestIdNamespaceCollision,
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            if (JsonRpcProtocol.TryGetMethod(root, out var method) && method == "initialize")
            {
                await CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "initialize may only be sent once",
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            await _bridge.ForwardFromTuiAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendTextAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> ReceiveTextMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await CloseAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "binary messages are not supported",
                        cancellationToken).ConfigureAwait(false);
                    return null;
                }

                if (stream.Length + result.Count > _maxMessageBytes)
                {
                    await CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "message exceeds configured limit",
                        cancellationToken).ConfigureAwait(false);
                    return null;
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return StrictUtf8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
                }
            }
        }
        catch (DecoderFallbackException)
        {
            await CloseAsync(
                WebSocketCloseStatus.InvalidPayloadData,
                "message is not valid UTF-8",
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendTextAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = StrictUtf8.GetBytes(message);
        await _socket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseAsync(
        WebSocketCloseStatus status,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await _socket.CloseOutputAsync(status, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static bool TryReadBoolean(JsonElement root, params string[] path)
    {
        if (!TryFindElement(root, out var value, path) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        return value.GetBoolean();
    }

    private static int CountObjectProperties(JsonElement root, params string[] path)
    {
        return TryFindElement(root, out var value, path) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().Count()
            : 0;
    }

    private static bool TryFindElement(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }
}
