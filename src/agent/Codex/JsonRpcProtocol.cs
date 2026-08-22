using System.Buffers;
using System.Text;
using System.Text.Json;
using CodexControl.Agent.Diagnostics;

namespace CodexControl.Agent.Codex;

public static class JsonRpcProtocol
{
    public const string BridgeRequestIdPrefix = "bridge:";

    public static JsonDocument Parse(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new AgentException(
                    AgentErrorCodes.AppServerProtocolError,
                    "JSON-RPC 消息根节点必须是对象。");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new AgentException(
                AgentErrorCodes.AppServerProtocolError,
                "收到无效 JSON-RPC JSON。",
                exception);
        }
    }

    public static bool TryGetMethod(JsonElement message, out string method)
    {
        method = string.Empty;
        if (!message.TryGetProperty("method", out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = property.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        method = value;
        return true;
    }

    public static bool TryGetId(JsonElement message, out JsonElement id)
    {
        if (message.TryGetProperty("id", out var property) &&
            property.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            id = property;
            return true;
        }

        id = default;
        return false;
    }

    public static bool IsResponse(JsonElement message) =>
        TryGetId(message, out _) &&
        !TryGetMethod(message, out _) &&
        (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _));

    public static bool HasReservedBridgeId(JsonElement message) =>
        TryGetId(message, out var id) &&
        id.ValueKind == JsonValueKind.String &&
        id.GetString()?.StartsWith(BridgeRequestIdPrefix, StringComparison.Ordinal) == true;

    public static string? GetStringId(JsonElement message)
    {
        return TryGetId(message, out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    public static string GetIdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => string.Concat("s:", id.GetString()),
        JsonValueKind.Number => string.Concat("n:", id.GetRawText()),
        _ => throw new ArgumentException("JSON-RPC ID 必须是 string 或 number。", nameof(id)),
    };

    public static string BuildRequest(string id, string method, object? parameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("method", method);
        writer.WriteString("id", id);
        writer.WritePropertyName("params");
        JsonSerializer.Serialize(writer, parameters, parameters?.GetType() ?? typeof(object));
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildNotification(string method, object? parameters = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("method", method);
        if (parameters is not null)
        {
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters, parameters.GetType());
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildResultResponse(JsonElement id, JsonElement result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writer.WritePropertyName("result");
        result.WriteTo(writer);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildErrorResponse(JsonElement id, int code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
