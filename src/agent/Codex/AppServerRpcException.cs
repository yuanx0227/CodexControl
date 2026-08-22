using System.Text.Json;

namespace CodexControl.Agent.Codex;

public sealed class AppServerRpcException : Exception
{
    public AppServerRpcException(string method, JsonElement error)
        : base(BuildMessage(method, error))
    {
        Method = method;
        Error = error.Clone();
        Code = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value)
            ? value
            : null;
    }

    public string Method { get; }

    public int? Code { get; }

    public JsonElement Error { get; }

    private static string BuildMessage(string method, JsonElement error)
    {
        var message = error.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : "unknown app-server error";
        return $"{method} failed: {message}";
    }
}
