using System.Text.Json;
using System.Threading.Channels;
using CodexControl.Protocol;

namespace CodexControl.Agent.Relay;

/// <summary>Fixed workers keep slow reads from blocking restriction requests or the socket reader.</summary>
internal sealed class BoundedControlDispatcher : IAsyncDisposable
{
    private readonly Channel<RelayEnvelope> _regular;
    private readonly Channel<RelayEnvelope> _restrictive;
    private readonly CancellationTokenSource _lifetime;
    private readonly Func<RelayEnvelope, CancellationToken, Task> _execute;
    private readonly Action<RelayEnvelope, Exception> _failed;
    private readonly Task[] _workers;
    private int _disposed;

    public BoundedControlDispatcher(Func<RelayEnvelope, CancellationToken, Task> execute,
        Action<RelayEnvelope, Exception> failed, CancellationToken token,
        int regularWorkers = 16, int restrictiveWorkers = 2, int regularCapacity = 32, int restrictiveCapacity = 16)
    {
        if (regularWorkers < 1 || restrictiveWorkers < 1) throw new ArgumentOutOfRangeException(nameof(regularWorkers));
        _execute = execute;
        _failed = failed;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _regular = Create(regularCapacity);
        _restrictive = Create(restrictiveCapacity);
        _workers = Enumerable.Range(0, regularWorkers).Select(_ => RunAsync(_regular.Reader))
            .Concat(Enumerable.Range(0, restrictiveWorkers).Select(_ => RunAsync(_restrictive.Reader))).ToArray();
    }

    public bool TryDispatch(RelayEnvelope envelope) => Volatile.Read(ref _disposed) == 0 &&
        (IsRestrictive(envelope) ? _restrictive : _regular).Writer.TryWrite(envelope);

    private async Task RunAsync(ChannelReader<RelayEnvelope> reader)
    {
        try
        {
            await foreach (var envelope in reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try { await _execute(envelope, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    try { _failed(envelope, exception); }
                    catch { /* Failure reporting cannot tear down another control worker. */ }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _regular.Writer.TryComplete();
        _restrictive.Writer.TryComplete();
        _lifetime.Cancel();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    internal static bool IsRestrictive(RelayEnvelope envelope) => envelope.Type == RelayMessageTypes.ControlInterrupt ||
        envelope.Type == RelayMessageTypes.ControlApproval && envelope.Payload.ValueKind == JsonValueKind.Object &&
        envelope.Payload.TryGetProperty("decision", out var decision) && decision.ValueKind == JsonValueKind.String &&
        decision.GetString() is "cancel" or "decline";

    private static Channel<RelayEnvelope> Create(int capacity) => Channel.CreateBounded<RelayEnvelope>(
        new BoundedChannelOptions(capacity)
        {
            SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
}
