using System.Net;
using System.Net.WebSockets;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexControl.Agent.Proxy;

/// <summary>
/// 只绑定 IPv4 loopback 的本地 WebSocket Server。第一版只允许一个 Codex TUI。
/// </summary>
public sealed class LocalCodexProxyServer : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly AppServerBridge _bridge;
    private readonly AgentLog _log;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private readonly object _sessionGate = new();

    private WebApplication? _application;
    private TuiWebSocketSession? _activeSession;
    private int _disposed;

    public LocalCodexProxyServer(AgentOptions options, AppServerBridge bridge, AgentLog log)
    {
        _options = options;
        _bridge = bridge;
        _log = log;
    }

    public int BoundPort { get; private set; }

    public Uri WebSocketUri => BoundPort > 0
        ? new Uri($"ws://127.0.0.1:{BoundPort}/", UriKind.Absolute)
        : throw new InvalidOperationException("Local proxy 尚未启动。");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_application is not null)
        {
            throw new InvalidOperationException("Local proxy 只能启动一次。");
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(LocalCodexProxyServer).Assembly.FullName,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.AddServerHeader = false;
            serverOptions.Limits.MaxRequestBodySize = 64 * 1024;
            serverOptions.Listen(IPAddress.Loopback, _options.Port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        var application = builder.Build();
        application.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        });

        application.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
        application.MapGet("/readyz", () => _bridge.IsReady
            ? Results.Json(new { status = "ready" })
            : Results.Json(new { status = "starting" }, statusCode: StatusCodes.Status503ServiceUnavailable));
        application.MapFallback(HandleRequestAsync);

        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        _application = application;
        BoundPort = ResolveBoundPort(application);
        _log.Info("local_proxy_started", $"local proxy listening on 127.0.0.1:{BoundPort}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        TuiWebSocketSession? session;
        lock (_sessionGate)
        {
            session = _activeSession;
            _activeSession = null;
        }

        session?.Abort("Local proxy is shutting down");
        if (_application is not null)
        {
            await _application.StopAsync(cancellationToken).ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
            _application = null;
            _log.Info("local_proxy_stopped", "local proxy stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _clientGate.Dispose();
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        if (!await _clientGate.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var session = new TuiWebSocketSession(socket, _bridge, _log, _options.MaxMessageBytes);
            lock (_sessionGate)
            {
                _activeSession = session;
            }

            try
            {
                await session.RunAsync(context.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                lock (_sessionGate)
                {
                    if (ReferenceEquals(_activeSession, session))
                    {
                        _activeSession = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            _log.Warning("tui_websocket_error", $"TUI WebSocket ended; nativeCode={exception.NativeErrorCode}");
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private static int ResolveBoundPort(WebApplication application)
    {
        var server = application.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
                IPAddress.TryParse(uri.Host, out var host) &&
                IPAddress.IsLoopback(host))
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException("无法确定 Local proxy 实际监听端口。");
    }
}
