using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using CodexControl.Protocol;
using CodexControl.Relay.Authentication;
using CodexControl.Relay.Pairing;
using CodexControl.Relay.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodexControl.Relay.Tests;

internal static class RelayTestRunner
{
    public static async Task<int> RunAsync()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            string.Concat("codex-control-relay-tests-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        WebApplication? app = null;
        try
        {
            var database = Path.Combine(testRoot, "relay.db");
            app = RelayApplication.Build(
            [
                "--environment=Development",
                "--urls=http://127.0.0.1:0",
                $"--CODEX_CONTROL_DB={database}",
                "--CODEX_CONTROL_PAIRING_SECRET=relay-test-secret",
                "--Logging:LogLevel:Default=Warning",
            ]);
            await RelayApplication.InitializeDatabaseAsync(app, CancellationToken.None).ConfigureAwait(false);
            await app.StartAsync().ConfigureAwait(false);
            var baseUri = ResolveBaseUri(app);

            await TestHealthAsync(baseUri).ConfigureAwait(false);
            await TestPairingRoutingAndReconnectAsync(app, baseUri).ConfigureAwait(false);
            await TestSecurityBoundariesAsync(app).ConfigureAwait(false);
            await TestMultiDeviceIsolationAsync(app).ConfigureAwait(false);
            await VerifyDatabaseAsync(app).ConfigureAwait(false);

            Console.WriteLine("PASS Relay_Health_Migration");
            Console.WriteLine("PASS Relay_Auth_Pairing_Routing_Reconnect");
            Console.WriteLine("PASS Relay_Expiry_AttemptLimit_ChallengeReplay");
            Console.WriteLine("PASS Relay_OneController_ThreeDevices_Isolation");
            Console.WriteLine("PASS Relay_Persistence_NoPlaintextCode");
            Console.WriteLine("RESULT total=5 passed=5 failed=0");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL Relay: {exception}");
            return 1;
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync().ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task TestHealthAsync(Uri baseUri)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(baseUri, "/healthz")).ConfigureAwait(false);
        Assert(response.StatusCode == HttpStatusCode.OK, "healthz should return 200");
    }

    private static async Task TestPairingRoutingAndReconnectAsync(WebApplication app, Uri baseUri)
    {
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var device = new RelayTestClient(
            PrincipalRole.Device,
            string.Concat("dev_", Guid.NewGuid().ToString("N")),
            "DEV-PC-01",
            deviceKey);
        await device.ConnectAsync(baseUri).ConfigureAwait(false);
        await device.RegisterAndAuthenticateDeviceAsync().ConfigureAwait(false);

        var pairingRequestId = RelayTestClient.NewRequestId();
        await device.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.PairingCreate,
            new PairingCreatePayload(),
            pairingRequestId,
            device.PrincipalId)).ConfigureAwait(false);
        var pairingCreated = (await device.ReceiveAsync(RelayMessageTypes.PairingCreated, pairingRequestId)
            .ConfigureAwait(false)).ReadPayload<PairingCreatedPayload>();
        Assert(pairingCreated.Code.Length == 6, "pairing code should contain six digits");

        await using (var invalidController = new RelayTestClient(
                         PrincipalRole.Controller,
                         string.Concat("ctl_", Guid.NewGuid().ToString("N")),
                         "Invalid Controller"))
        {
            await invalidController.ConnectAsync(baseUri).ConfigureAwait(false);
            var invalidCode = pairingCreated.Code[0] == '0'
                ? string.Concat("1", pairingCreated.Code[1..])
                : string.Concat("0", pairingCreated.Code[1..]);
            var invalidRequestId = RelayTestClient.NewRequestId();
            await invalidController.SendAsync(RelayEnvelope.Create(
                RelayMessageTypes.PairingClaim,
                invalidController.CreatePairingClaim(invalidCode),
                invalidRequestId,
                controllerId: invalidController.PrincipalId)).ConfigureAwait(false);
            var error = (await invalidController.ReceiveAsync(RelayMessageTypes.Error, invalidRequestId)
                .ConfigureAwait(false)).ReadPayload<ErrorPayload>();
            Assert(error.Code == "PAIRING_INVALID", "invalid pairing code should fail closed");
        }

        await using var controller = new RelayTestClient(
            PrincipalRole.Controller,
            string.Concat("ctl_", Guid.NewGuid().ToString("N")),
            "Yuanx Phone");
        await controller.ConnectAsync(baseUri).ConfigureAwait(false);
        var claimRequestId = RelayTestClient.NewRequestId();
        await controller.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.PairingClaim,
            controller.CreatePairingClaim(pairingCreated.Code),
            claimRequestId,
            controllerId: controller.PrincipalId)).ConfigureAwait(false);
        _ = await controller.ReceiveAsync(RelayMessageTypes.PairingCompleted, claimRequestId).ConfigureAwait(false);
        _ = await device.ReceiveAsync(RelayMessageTypes.PairingCompleted, claimRequestId).ConfigureAwait(false);

        var listRequestId = RelayTestClient.NewRequestId();
        await controller.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.DeviceList,
            new { },
            listRequestId,
            controllerId: controller.PrincipalId)).ConfigureAwait(false);
        var list = (await controller.ReceiveAsync(RelayMessageTypes.DeviceListResult, listRequestId)
            .ConfigureAwait(false)).ReadPayload<DeviceListResultPayload>();
        Assert(list.Devices.Count == 1 && list.Devices[0].Online, "paired online device should be listed");

        var snapshot = new CodexSnapshotPayload(
            1,
            "Thinking",
            "thr-1",
            "turn-1",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "D:\\Projects\\MES",
            "Thinking",
            null,
            [],
            0,
            null,
            null);
        await device.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.CodexSnapshot,
            snapshot,
            deviceId: device.PrincipalId)).ConfigureAwait(false);
        var forwardedSnapshot = (await controller.ReceiveAsync(RelayMessageTypes.CodexSnapshot)
            .ConfigureAwait(false)).ReadPayload<CodexSnapshotPayload>();
        Assert(forwardedSnapshot.Revision == 1, "snapshot should be forwarded");

        var controlRequestId = RelayTestClient.NewRequestId();
        await controller.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.ControlSteer,
            new SteerControlPayload("thr-1", "turn-1", "focus on tests"),
            controlRequestId,
            device.PrincipalId,
            controller.PrincipalId)).ConfigureAwait(false);
        var routedControl = await device.ReceiveAsync(RelayMessageTypes.ControlSteer, controlRequestId)
            .ConfigureAwait(false);
        Assert(routedControl.ControllerId == controller.PrincipalId, "controller identity must be attached");

        await device.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.ControlResult,
            new ControlResultPayload(ControlResultStatus.Succeeded, null, null, null),
            controlRequestId,
            device.PrincipalId,
            controller.PrincipalId)).ConfigureAwait(false);
        var controlResult = (await controller.ReceiveAsync(RelayMessageTypes.ControlResult, controlRequestId)
            .ConfigureAwait(false)).ReadPayload<ControlResultPayload>();
        Assert(controlResult.Status == ControlResultStatus.Succeeded, "control result should route back");

        foreach (var remoteSessionControl in new (string Type, object Payload)[]
                 {
                      (RelayMessageTypes.ControlThreadList, new ThreadListControlPayload()),
                      (RelayMessageTypes.ControlThreadRead, new ThreadReadControlPayload("thr-history")),
                      (RelayMessageTypes.ControlThreadStart, new ThreadStartControlPayload("D:\\Projects\\MES", "new task")),
                     (RelayMessageTypes.ControlThreadResume, new ThreadResumeControlPayload("thr-history", "continue task")),
                 })
        {
            var sessionRequestId = RelayTestClient.NewRequestId();
            await controller.SendAsync(RelayEnvelope.Create(
                remoteSessionControl.Type,
                remoteSessionControl.Payload,
                sessionRequestId,
                device.PrincipalId,
                controller.PrincipalId)).ConfigureAwait(false);
            var routedSessionControl = await device.ReceiveAsync(remoteSessionControl.Type, sessionRequestId)
                .ConfigureAwait(false);
            Assert(
                routedSessionControl.ControllerId == controller.PrincipalId,
                $"{remoteSessionControl.Type} should route to the paired device");
            await device.SendAsync(RelayEnvelope.Create(
                RelayMessageTypes.ControlResult,
                new ControlResultPayload(ControlResultStatus.Succeeded, null, null, null),
                sessionRequestId,
                device.PrincipalId,
                controller.PrincipalId)).ConfigureAwait(false);
            var sessionResult = (await controller.ReceiveAsync(RelayMessageTypes.ControlResult, sessionRequestId)
                .ConfigureAwait(false)).ReadPayload<ControlResultPayload>();
            Assert(
                sessionResult.Status == ControlResultStatus.Succeeded,
                $"{remoteSessionControl.Type} result should route back");
        }

        await controller.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.ControlSteer,
            new SteerControlPayload("thr-1", "turn-1", "duplicate"),
            controlRequestId,
            device.PrincipalId,
            controller.PrincipalId)).ConfigureAwait(false);
        var cachedResult = (await controller.ReceiveAsync(RelayMessageTypes.ControlResult, controlRequestId)
            .ConfigureAwait(false)).ReadPayload<ControlResultPayload>();
        Assert(cachedResult.Status == ControlResultStatus.Succeeded, "duplicate request should return cached result");

        await controller.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.ControlInterrupt,
            new InterruptControlPayload("thr-1", "turn-1"),
            controlRequestId,
            device.PrincipalId,
            controller.PrincipalId)).ConfigureAwait(false);
        var conflict = (await controller.ReceiveAsync(RelayMessageTypes.ControlResult, controlRequestId)
            .ConfigureAwait(false)).ReadPayload<ControlResultPayload>();
        Assert(conflict.Code == "REQUEST_ID_CONFLICT", "request ID reuse for another command must fail");

        var forgedRequestId = RelayTestClient.NewRequestId();
        await device.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.ControlResult,
            new ControlResultPayload(ControlResultStatus.Succeeded, null, null, null),
            forgedRequestId,
            device.PrincipalId,
            controller.PrincipalId)).ConfigureAwait(false);
        var forgedError = (await device.ReceiveAsync(RelayMessageTypes.Error, forgedRequestId)
            .ConfigureAwait(false)).ReadPayload<ErrorPayload>();
        Assert(forgedError.Code == "CONTROL_RESULT_UNEXPECTED", "forged device result must be rejected");

        await device.CloseAsync().ConfigureAwait(false);
        _ = await controller.ReceiveAsync(RelayMessageTypes.DeviceOffline, timeout: TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        await using var reconnectedDevice = new RelayTestClient(
            PrincipalRole.Device,
            device.PrincipalId,
            device.Name,
            deviceKey);
        await reconnectedDevice.ConnectAsync(baseUri).ConfigureAwait(false);
        await reconnectedDevice.AuthenticateAsync().ConfigureAwait(false);
        _ = await controller.ReceiveAsync(RelayMessageTypes.DeviceOnline, timeout: TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        var revokeRequestId = RelayTestClient.NewRequestId();
        await controller.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.PairingRevoked,
            new PairingRevokedPayload(device.PrincipalId, controller.PrincipalId),
            revokeRequestId,
            device.PrincipalId,
            controller.PrincipalId)).ConfigureAwait(false);
        _ = await controller.ReceiveAsync(RelayMessageTypes.PairingRevoked, revokeRequestId).ConfigureAwait(false);

        var deniedRequestId = RelayTestClient.NewRequestId();
        await controller.SendAsync(RelayEnvelope.Create(
            RelayMessageTypes.ControlInterrupt,
            new InterruptControlPayload("thr-1", "turn-1"),
            deniedRequestId,
            device.PrincipalId,
            controller.PrincipalId)).ConfigureAwait(false);
        var denied = (await controller.ReceiveAsync(RelayMessageTypes.ControlResult, deniedRequestId)
            .ConfigureAwait(false)).ReadPayload<ControlResultPayload>();
        Assert(denied.Code == "PERMISSION_DENIED", "revoked controller must lose control permission");

        await reconnectedDevice.CloseAsync().ConfigureAwait(false);
    }

    private static async Task VerifyDatabaseAsync(WebApplication app)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<RelayDbContext>>();
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        Assert(await db.Devices.CountAsync().ConfigureAwait(false) >= 4, "all paired devices should persist");
        Assert(await db.Controllers.CountAsync().ConfigureAwait(false) >= 2, "paired controllers should persist");
        var sessions = await db.PairingSessions.ToListAsync().ConfigureAwait(false);
        Assert(
            sessions.Count > 0 && sessions.All(session => session.CodeHash.Length > 20 && !session.CodeHash.All(char.IsDigit)),
            "pairing codes must be hashed");
        Assert(await db.AuditEvents.AnyAsync().ConfigureAwait(false), "audit metadata should persist");
    }

    private static async Task TestSecurityBoundariesAsync(WebApplication app)
    {
        var challenges = new ChallengeStore();
        var issued = challenges.Create("conn-security", PrincipalRole.Device, "dev_security1234");
        Assert(
            challenges.TryConsume(
                issued.ChallengeId,
                "conn-security",
                PrincipalRole.Device,
                "dev_security1234",
                out _),
            "first challenge consumption should succeed");
        Assert(
            !challenges.TryConsume(
                issued.ChallengeId,
                "conn-security",
                PrincipalRole.Device,
                "dev_security1234",
                out _),
            "challenge replay must fail");

        using var verificationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert(
            !P256Keys.Verify(verificationKey, "payload", Base64Url.Encode(RandomNumberGenerator.GetBytes(64))),
            "invalid signature must fail verification");

        var pairingService = app.Services.GetRequiredService<PairingService>();
        var factory = app.Services.GetRequiredService<IDbContextFactory<RelayDbContext>>();
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var deviceId = await db.Devices.Select(value => value.Id).SingleAsync().ConfigureAwait(false);

        var expired = await pairingService.CreateAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
        Assert(expired.Succeeded, "expired-code setup should create a session");
        var expiredCandidates = await db.PairingSessions
            .Where(value => value.ConsumedAt == null)
            .ToListAsync().ConfigureAwait(false);
        var expiredSession = expiredCandidates.OrderByDescending(value => value.CreatedAt).First();
        expiredSession.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync().ConfigureAwait(false);
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controllerId = string.Concat("ctl_", Guid.NewGuid().ToString("N"));
        var publicKey = P256Keys.ExportPublicKey(controllerKey);
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var expiredPayload = new PairingClaimPayload(
            expired.Value!.Code,
            controllerId,
            "Expired Controller",
            publicKey,
            nonce,
            P256Keys.Sign(
                controllerKey,
                PairingProofCanonicalPayload.Build(expired.Value.Code, controllerId, publicKey, nonce)));
        var expiredResult = await pairingService.ClaimAsync(expiredPayload, CancellationToken.None).ConfigureAwait(false);
        Assert(!expiredResult.Succeeded && expiredResult.ErrorCode == "PAIRING_INVALID", "expired code must fail");

        var limited = await pairingService.CreateAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
        Assert(limited.Succeeded, "attempt-limit setup should create a session");
        var invalidPayload = expiredPayload with
        {
            Code = limited.Value!.Code,
            ProofSignature = Base64Url.Encode(RandomNumberGenerator.GetBytes(64)),
        };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await pairingService.ClaimAsync(invalidPayload, CancellationToken.None).ConfigureAwait(false);
            Assert(!failed.Succeeded, "invalid proof attempt must fail");
        }

        await db.Entry(expiredSession).ReloadAsync().ConfigureAwait(false);
        var limitedCandidates = await db.PairingSessions
            .AsNoTracking()
            .Where(value => value.CodeHash != expiredSession.CodeHash)
            .ToListAsync().ConfigureAwait(false);
        var limitedSession = limitedCandidates.OrderByDescending(value => value.CreatedAt).First();
        Assert(
            limitedSession.AttemptCount == 5 && limitedSession.ConsumedAt is not null,
            "fifth invalid proof must consume the pairing session");

        var concurrent = await pairingService.CreateAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
        Assert(concurrent.Succeeded, "concurrent-claim setup should create a session");
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var firstClaim = CreateClaim(concurrent.Value!.Code, firstKey, "Concurrent One");
        var secondClaim = CreateClaim(concurrent.Value.Code, secondKey, "Concurrent Two");
        var concurrentResults = await Task.WhenAll(
            pairingService.ClaimAsync(firstClaim, CancellationToken.None),
            pairingService.ClaimAsync(secondClaim, CancellationToken.None)).ConfigureAwait(false);
        Assert(
            concurrentResults.Count(result => result.Succeeded) == 1,
            "concurrent claim must produce exactly one successful pairing");
    }

    private static async Task TestMultiDeviceIsolationAsync(WebApplication app)
    {
        var pairingService = app.Services.GetRequiredService<PairingService>();
        var factory = app.Services.GetRequiredService<IDbContextFactory<RelayDbContext>>();
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        var deviceIds = Enumerable.Range(1, 3)
            .Select(_ => string.Concat("dev_", Guid.NewGuid().ToString("N")))
            .ToArray();
        foreach (var deviceId in deviceIds)
        {
            using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            db.Devices.Add(new DeviceEntity
            {
                Id = deviceId,
                Name = deviceId,
                PublicKey = P256Keys.ExportPublicKey(deviceKey),
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        using var controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var controllerId = string.Concat("ctl_", Guid.NewGuid().ToString("N"));
        var publicKey = P256Keys.ExportPublicKey(controllerKey);
        foreach (var deviceId in deviceIds)
        {
            var pairing = await pairingService.CreateAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            Assert(pairing.Succeeded, "multi-device pairing code should be created");
            var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
            var claim = new PairingClaimPayload(
                pairing.Value!.Code,
                controllerId,
                "Multi Device Controller",
                publicKey,
                nonce,
                P256Keys.Sign(
                    controllerKey,
                    PairingProofCanonicalPayload.Build(pairing.Value.Code, controllerId, publicKey, nonce)));
            var claimed = await pairingService.ClaimAsync(claim, CancellationToken.None).ConfigureAwait(false);
            Assert(claimed.Succeeded, "same controller should pair each device");
        }

        var listed = await pairingService.ListDevicesAsync(controllerId, CancellationToken.None).ConfigureAwait(false);
        Assert(listed.Count == 3, "one controller should list three paired devices");
        var isolated = await pairingService.ListDevicesAsync(
            string.Concat("ctl_", Guid.NewGuid().ToString("N")),
            CancellationToken.None).ConfigureAwait(false);
        Assert(isolated.Count == 0, "unpaired controller must not list device data");
    }

    private static Uri ResolveBaseUri(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single() ??
                      throw new InvalidOperationException("Relay address is unavailable.");
        return new Uri(address, UriKind.Absolute);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static PairingClaimPayload CreateClaim(string code, ECDsa key, string name)
    {
        var controllerId = string.Concat("ctl_", Guid.NewGuid().ToString("N"));
        var publicKey = P256Keys.ExportPublicKey(key);
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        return new PairingClaimPayload(
            code,
            controllerId,
            name,
            publicKey,
            nonce,
            P256Keys.Sign(
                key,
                PairingProofCanonicalPayload.Build(code, controllerId, publicKey, nonce)));
    }
}
