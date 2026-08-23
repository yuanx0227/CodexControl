using System.Security.Cryptography;
using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.Agent.Security;

public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    private DeviceIdentity(string deviceId, string name, ECDsa key)
    {
        DeviceId = deviceId;
        Name = name;
        _key = key;
        PublicKey = P256Keys.ExportPublicKey(key);
    }

    public string DeviceId { get; }

    public string Name { get; }

    public string PublicKey { get; }

    public static DeviceIdentity LoadOrCreate(string dataDirectory, string deviceName)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "device-identity.json");
        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<StoredIdentity>(
                File.ReadAllText(path),
                RelayJson.Options) ?? throw new CryptographicException("Device identity file is invalid.");
            var privateKey = WindowsDpapi.Unprotect(Convert.FromBase64String(stored.ProtectedPrivateKey));
            try
            {
                var key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(privateKey, out _);
                var effectiveName = string.IsNullOrWhiteSpace(deviceName) ? stored.Name : deviceName.Trim();
                var identity = new DeviceIdentity(stored.DeviceId, effectiveName, key);
                if (!CryptographicOperations.FixedTimeEquals(
                        Base64Url.Decode(identity.PublicKey),
                        Base64Url.Decode(stored.PublicKey)))
                {
                    identity.Dispose();
                    throw new CryptographicException("Stored public key does not match DPAPI private key.");
                }

                if (!string.Equals(stored.Name, effectiveName, StringComparison.Ordinal))
                {
                    var updated = stored with { Name = effectiveName };
                    var updatePath = string.Concat(path, ".tmp-", Guid.NewGuid().ToString("N"));
                    try
                    {
                        File.WriteAllText(updatePath, JsonSerializer.Serialize(updated, RelayJson.Options));
                        File.Move(updatePath, path, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(updatePath)) File.Delete(updatePath);
                    }
                }

                return identity;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        var createdKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = new DeviceIdentity(
            string.Concat("dev_", Guid.NewGuid().ToString("N")),
            deviceName,
            createdKey);
        var exported = createdKey.ExportPkcs8PrivateKey();
        string? temporaryPath = null;
        try
        {
            var stored = new StoredIdentity(
                created.DeviceId,
                created.Name,
                created.PublicKey,
                Convert.ToBase64String(WindowsDpapi.Protect(exported)));
            temporaryPath = string.Concat(path, ".tmp-", Guid.NewGuid().ToString("N"));
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, RelayJson.Options));
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
                temporaryPath = null;
            }
            catch (IOException) when (File.Exists(path))
            {
                created.Dispose();
                return LoadOrCreate(dataDirectory, deviceName);
            }
        }
        catch
        {
            created.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return created;
    }

    public string Sign(string canonicalPayload) => P256Keys.Sign(_key, canonicalPayload);

    public void Dispose() => _key.Dispose();

    private sealed record StoredIdentity(
        string DeviceId,
        string Name,
        string PublicKey,
        string ProtectedPrivateKey);
}
