using CodexControl.Relay;
using CodexControl.Relay.Pairing;
using CodexControl.SystemHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

var testRoot = args.Length == 1
    ? Path.GetFullPath(args[0])
    : Path.Combine(
        Path.GetTempPath(),
        string.Concat("codex-control-system-host-", Guid.NewGuid().ToString("N")));
if (args.Length == 1)
{
    var parent = Directory.GetParent(testRoot);
    if (!string.Equals(Path.GetFileName(testRoot), "system-host", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(parent?.Name, "out", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Explicit system-test root must be an out/system-host directory.");
    }

    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

Directory.CreateDirectory(testRoot);
var app = RelayApplication.Build(
[
    "--environment=Development",
    "--urls=http://127.0.0.1:5080",
    $"--CODEX_CONTROL_DB={Path.Combine(testRoot, "relay.db")}",
    "--CODEX_CONTROL_PAIRING_SECRET=system-test-secret",
    "--Logging:LogLevel:Default=Warning",
]);
var state = new SystemHostState();
app.MapGet("/test/pairing-code", async (
    string? token,
    PairingService pairing,
    CancellationToken cancellationToken) =>
{
    if (state.DeviceId is null || string.IsNullOrWhiteSpace(token))
    {
        return Results.Json(new { status = "starting" }, statusCode: 503);
    }

    var created = await state.GetOrCreatePairingAsync(
        token,
        () => pairing.CreateAsync(state.DeviceId, cancellationToken)).ConfigureAwait(false);
    return created.Succeeded
        ? Results.Json(new { code = created.Value!.Code, deviceId = state.DeviceId })
        : Results.Json(new { code = created.ErrorCode }, statusCode: 500);
});

try
{
    await RelayApplication.InitializeDatabaseAsync(app, CancellationToken.None).ConfigureAwait(false);
    await app.StartAsync().ConfigureAwait(false);
    await using var device = new SystemDeviceClient(new Uri("http://127.0.0.1:5080"));
    await device.StartAsync(CancellationToken.None).ConfigureAwait(false);
    state.DeviceId = device.DeviceId;
    Console.WriteLine($"SYSTEM_READY {device.DeviceId}");
    await app.WaitForShutdownAsync().ConfigureAwait(false);
}
finally
{
    await app.StopAsync().ConfigureAwait(false);
    await app.DisposeAsync().ConfigureAwait(false);
    try
    {
        Directory.Delete(testRoot, recursive: true);
    }
    catch (IOException)
    {
    }
}

internal sealed class SystemHostState
{
    public string? DeviceId { get; set; }
    private readonly Dictionary<string, CodexControl.Relay.Services.ServiceResult<CodexControl.Protocol.PairingCreatedPayload>> _pairings = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CodexControl.Relay.Services.ServiceResult<CodexControl.Protocol.PairingCreatedPayload>> GetOrCreatePairingAsync(
        string token,
        Func<Task<CodexControl.Relay.Services.ServiceResult<CodexControl.Protocol.PairingCreatedPayload>>> factory)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pairings.TryGetValue(token, out var existing))
            {
                return existing;
            }

            var created = await factory().ConfigureAwait(false);
            _pairings[token] = created;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }
}
