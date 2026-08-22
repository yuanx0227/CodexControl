using System.Collections.Concurrent;
using CodexControl.Protocol;

namespace CodexControl.Relay.Routing;

public enum ControlBeginStatus
{
    New,
    Pending,
    Completed,
    Conflict,
}

public sealed record ControlBeginResult(
    ControlBeginStatus Status,
    RelayEnvelope? CachedResult);

public sealed class ControlRequestTracker
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ControlBeginResult Begin(
        string requestId,
        string deviceId,
        string controllerId,
        string messageType)
    {
        Cleanup();
        var created = new Entry(deviceId, controllerId, messageType, DateTimeOffset.UtcNow);
        var entry = _entries.GetOrAdd(requestId, created);
        if (entry.DeviceId != deviceId || entry.ControllerId != controllerId || entry.MessageType != messageType)
        {
            return new(ControlBeginStatus.Conflict, null);
        }

        lock (entry.Gate)
        {
            return entry.Result is null
                ? ReferenceEquals(entry, created)
                    ? new(ControlBeginStatus.New, null)
                    : new(ControlBeginStatus.Pending, null)
                : new(ControlBeginStatus.Completed, entry.Result);
        }
    }

    public bool TryComplete(
        string requestId,
        string deviceId,
        string controllerId,
        RelayEnvelope result)
    {
        if (!_entries.TryGetValue(requestId, out var entry) ||
            entry.DeviceId != deviceId || entry.ControllerId != controllerId)
        {
            return false;
        }

        lock (entry.Gate)
        {
            entry.Result ??= result;
            entry.CompletedAt ??= DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void Cancel(string requestId, string deviceId, string controllerId)
    {
        if (_entries.TryGetValue(requestId, out var entry) &&
            entry.DeviceId == deviceId && entry.ControllerId == controllerId)
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(requestId, entry));
        }
    }

    private void Cleanup()
    {
        if (_entries.Count < 2048)
        {
            return;
        }

        var threshold = DateTimeOffset.UtcNow.AddMinutes(-5);
        foreach (var entry in _entries)
        {
            if ((entry.Value.CompletedAt ?? entry.Value.CreatedAt) < threshold)
            {
                _entries.TryRemove(entry.Key, out _);
            }
        }
    }

    private sealed class Entry(
        string deviceId,
        string controllerId,
        string messageType,
        DateTimeOffset createdAt)
    {
        public object Gate { get; } = new();
        public string DeviceId { get; } = deviceId;
        public string ControllerId { get; } = controllerId;
        public string MessageType { get; } = messageType;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset? CompletedAt { get; set; }
        public RelayEnvelope? Result { get; set; }
    }
}
