using System.Security.Cryptography;
using System.Text;

namespace CodexControl.Relay.Configuration;

public sealed record RelayOptions(
    string ConnectionString,
    byte[] PairingSecret,
    int MaxMessageBytes,
    TimeSpan HeartbeatTimeout,
    bool AllowInsecureTransport,
    IReadOnlySet<string> AllowedOrigins)
{
    public const int DefaultMaxMessageBytes = 1024 * 1024;

    public static RelayOptions FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var databasePath = configuration["CODEX_CONTROL_DB"] ??
                           Path.Combine(AppContext.BaseDirectory, "data", "codex-control.db");
        var fullDatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDatabasePath)!);

        var secretText = configuration["CODEX_CONTROL_PAIRING_SECRET"];
        byte[] secret;
        if (string.IsNullOrWhiteSpace(secretText))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "CODEX_CONTROL_PAIRING_SECRET is required outside Development.");
            }

            secret = SHA256.HashData(Encoding.UTF8.GetBytes("codex-control-development-secret"));
        }
        else
        {
            secret = SHA256.HashData(Encoding.UTF8.GetBytes(secretText));
        }

        return new RelayOptions(
            $"Data Source={fullDatabasePath};Cache=Shared;Foreign Keys=True;Pooling=False",
            secret,
            DefaultMaxMessageBytes,
            TimeSpan.FromSeconds(120),
            environment.IsDevelopment(),
            ParseAllowedOrigins(configuration, environment));
    }

    private static IReadOnlySet<string> ParseAllowedOrigins(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration["CODEX_CONTROL_ALLOWED_ORIGINS"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return environment.IsDevelopment()
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "http://127.0.0.1:5173",
                    "http://localhost:5173",
                }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
