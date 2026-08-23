using System.Net;
using CodexControl.Protocol;
using CodexControl.Relay.Authentication;
using CodexControl.Relay.Configuration;
using CodexControl.Relay.Pairing;
using CodexControl.Relay.Persistence;
using CodexControl.Relay.Routing;
using CodexControl.Relay.Security;
using CodexControl.Relay.WebSockets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace CodexControl.Relay;

public static class RelayApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var relayOptions = RelayOptions.FromConfiguration(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(relayOptions);
        builder.Services.AddDbContextFactory<RelayDbContext>(options =>
            options.UseSqlite(relayOptions.ConnectionString));
        builder.Services.AddSingleton<ChallengeStore>();
        builder.Services.AddSingleton<SlidingWindowRateLimiter>();
        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddSingleton<ControlRequestTracker>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<PendingPairingConnections>();
        builder.Services.AddSingleton<RelayRouter>();
        builder.Services.AddSingleton<RelayWebSocketHandler>();
        builder.Services.AddHostedService<PresenceMonitor>();

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/ws"))
            {
                if (!relayOptions.AllowInsecureTransport && !context.Request.IsHttps)
                {
                    context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                    return;
                }

                if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                    (origin.Count != 1 || !relayOptions.AllowedOrigins.Contains(origin[0]!)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            await next().ConfigureAwait(false);
        });

        app.MapGet("/healthz", async (IDbContextFactory<RelayDbContext> factory, CancellationToken cancellationToken) =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? Results.Json(new { status = "ok", protocolVersion = ProtocolVersion.Current })
                : Results.Json(new { status = "database-unavailable" }, statusCode: 503);
        });
        app.MapGet("/readyz", () => Results.Json(new { status = "ready" }));
        app.MapGet("/", () => Results.Json(new
        {
            name = "Codex Control Relay",
            protocolVersion = ProtocolVersion.Current,
        }));
        app.Map("/ws/device", context =>
            context.RequestServices.GetRequiredService<RelayWebSocketHandler>()
                .HandleAsync(context, PrincipalRole.Device));
        app.Map("/ws/controller", context =>
            context.RequestServices.GetRequiredService<RelayWebSocketHandler>()
                .HandleAsync(context, PrincipalRole.Controller));
        return app;
    }

    public static async Task InitializeDatabaseAsync(
        WebApplication app,
        CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RelayDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
