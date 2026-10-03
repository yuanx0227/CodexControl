using System.Text.Json;
using CodexControl.Protocol;

namespace CodexControl.Agent.Relay;

/// <summary>Memory-only replay window. Epoch changes invalidate all old cursors.</summary>
public sealed class SessionEventJournal(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Queue<(CodexEventPayload Event, int Bytes, DateTimeOffset RetainedAt)> _events = new();
    private string _connection = "";
    private string _epoch = Guid.NewGuid().ToString("N");
    private long _sequence;
    private int _bytes;
    public (string Epoch, long Sequence) Cursor { get { lock (_gate) return (_epoch, _sequence); } }

    public void SetConnection(string connection)
    {
        lock (_gate)
        {
            if (_connection == connection) return;
            _connection = connection;
            _epoch = Guid.NewGuid().ToString("N");
            _sequence = 0;
            _events.Clear();
            _bytes = 0;
        }
    }

    public CodexEventPayload Append(CodexEventPayload value, string instance)
    {
        lock (_gate)
        {
            var item = value with { ServiceInstanceId = instance, StreamEpoch = _epoch, Sequence = ++_sequence };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item, RelayJson.Options).Length;
            _events.Enqueue((item, bytes, _timeProvider.GetUtcNow()));
            _bytes += bytes;
            PruneExpired();
            while (_events.Count > 10_000 || _bytes > 32 * 1024 * 1024)
                _bytes -= _events.Dequeue().Bytes;
            return item;
        }
    }

    public (bool Complete, IReadOnlyList<CodexEventPayload> Events) Read(string? epoch, long? after, string threadId)
    {
        lock (_gate)
        {
            PruneExpired();
            var complete = epoch == _epoch && after is >= 0 && after <= _sequence &&
                (_events.Count == 0 ? after == _sequence : after >= _events.Peek().Event.Sequence - 1);
            return (complete, complete
                ? _events.Where(entry => entry.Event.Sequence > after && entry.Event.ThreadId == threadId)
                    .Select(entry => entry.Event).ToArray()
                : []);
        }
    }

    private void PruneExpired()
    {
        var minimumTime = _timeProvider.GetUtcNow().AddMinutes(-5);
        while (_events.Count > 0 && _events.Peek().RetainedAt <= minimumTime)
            _bytes -= _events.Dequeue().Bytes;
    }
}
