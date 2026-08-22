using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.Relay.WebSockets;

internal static class WebSocketJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<RelayEnvelope?> ReceiveAsync(
        WebSocket socket,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new JsonException("Relay accepts text WebSocket messages only.");
                }

                if (stream.Length + result.Count > maxMessageBytes)
                {
                    throw new JsonException("Relay message exceeds configured limit.");
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var json = StrictUtf8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
                    var envelope = JsonSerializer.Deserialize<RelayEnvelope>(json, RelayJson.Options) ??
                                   throw new JsonException("Relay envelope is null.");
                    if (envelope.Version != ProtocolVersion.Current ||
                        string.IsNullOrWhiteSpace(envelope.Type) ||
                        string.IsNullOrWhiteSpace(envelope.MessageId))
                    {
                        throw new JsonException("Relay envelope header is invalid.");
                    }

                    return envelope;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
