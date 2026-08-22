using CodexControl.Relay;

var app = RelayApplication.Build(args);
await RelayApplication.InitializeDatabaseAsync(app, CancellationToken.None).ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);
