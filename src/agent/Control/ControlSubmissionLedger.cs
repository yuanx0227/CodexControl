using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.Agent.Control;

/// <summary>Write-ahead submission tombstones. No prompt, reply, command, or credentials are stored.</summary>
public sealed class ControlSubmissionLedger(string dataDirectory)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _payloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ControlResultPayload> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Type, string Digest, ControlResultPayload? Result)> _restrictive = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _submitted;
    private string FileName => Path.Combine(dataDirectory, "control-submissions.json");

    public ControlResultPayload? Begin(RelayEnvelope request)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.ControllerId))
                return Failed("REQUEST_ID_REQUIRED");
            var key = request.ControllerId + ":" + request.RequestId;
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Payload.GetRawText())));
            // These operations bind an exact Turn/approval and cannot start work. Keep them available
            // if the durable submission journal is full/unavailable. A restart may repeat the same
            // restrictive action; the server rejects an already-ended Turn/resolved approval.
            if (IsRestrictive(request))
            {
                if (_restrictive.TryGetValue(key, out var entry))
                    return entry.Type != request.Type || entry.Digest != digest ? Failed("REQUEST_ID_CONFLICT")
                        : entry.Result ?? Failed("CONTROL_OUTCOME_UNKNOWN");
                if (_payloads.TryGetValue(key, out var previousDigest) && previousDigest != digest)
                    return Failed("REQUEST_ID_CONFLICT");
                if (_restrictive.Count >= 1024) _restrictive.Remove(_restrictive.Keys.First());
                _restrictive.Add(key, (request.Type, digest, null));
                return null;
            }
            try
            {
                _submitted ??= File.Exists(FileName)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FileName))
                      ?? throw new JsonException()
                    : new(StringComparer.Ordinal);
                if (_restrictive.ContainsKey(key)) return Failed("REQUEST_ID_CONFLICT");
                if (_submitted.TryGetValue(key, out var kind))
                {
                    if (kind != request.Type || (_payloads.TryGetValue(key, out var previous) && previous != digest))
                        return Failed("REQUEST_ID_CONFLICT");
                    return _results.GetValueOrDefault(key) ?? Failed("CONTROL_OUTCOME_UNKNOWN");
                }
                if (_submitted.Count >= 10_000) return Failed("SUBMISSION_LEDGER_FULL");
                _submitted.Add(key, request.Type);
                Directory.CreateDirectory(dataDirectory);
                var temporary = FileName + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_submitted));
                File.Move(temporary, FileName, true);
                _payloads[key] = digest;
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { return Failed("SUBMISSION_LEDGER_UNAVAILABLE"); }
        }
    }

    public void Complete(RelayEnvelope request, ControlResultPayload result)
    {
        lock (_gate)
        {
            var key = request.ControllerId + ":" + request.RequestId;
            if (_restrictive.TryGetValue(key, out var entry)) _restrictive[key] = (entry.Type, entry.Digest, result);
            else _results[key] = result;
        }
    }

    public static bool IsRestrictive(RelayEnvelope request) => request.Type == RelayMessageTypes.ControlInterrupt ||
        (request.Type == RelayMessageTypes.ControlApproval && request.Payload.ValueKind == JsonValueKind.Object &&
         request.Payload.TryGetProperty("decision", out var decision) && decision.ValueKind == JsonValueKind.String &&
         decision.GetString() is "cancel" or "decline");

    private static ControlResultPayload Failed(string code) => new(ControlResultStatus.Failed, code,
        code == "CONTROL_OUTCOME_UNKNOWN" ? "此请求已提交，结果待核实；没有自动重发。" : "请求被拒绝，未重复执行。", null);

    public static bool IsMutation(string type) => type is RelayMessageTypes.ControlThreadSend or
        RelayMessageTypes.ControlThreadStart or RelayMessageTypes.ControlThreadResume or RelayMessageTypes.ControlSteer or
        RelayMessageTypes.ControlInterrupt or RelayMessageTypes.ControlApproval;
}
