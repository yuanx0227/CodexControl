using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexControl.Protocol;

public sealed record RelayEnvelope(
    int Version,
    string Type,
    string MessageId,
    string? RequestId,
    long Timestamp,
    string? DeviceId,
    string? ControllerId,
    JsonElement Payload)
{
    public static RelayEnvelope Create<TPayload>(
        string type,
        TPayload payload,
        string? requestId = null,
        string? deviceId = null,
        string? controllerId = null) =>
        new(
            ProtocolVersion.Current,
            type,
            Guid.NewGuid().ToString("D"),
            requestId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            deviceId,
            controllerId,
            JsonSerializer.SerializeToElement(payload, RelayJson.Options));

    public TPayload ReadPayload<TPayload>() =>
        Payload.Deserialize<TPayload>(RelayJson.Options) ??
        throw new JsonException($"Payload for {Type} is null or invalid.");
}

public static class ProtocolVersion
{
    public const int Current = 1;
}

public static class RelayJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
