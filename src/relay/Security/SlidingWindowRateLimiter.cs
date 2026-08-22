using System.Collections.Concurrent;

namespace CodexControl.Relay.Security;

public sealed class SlidingWindowRateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public bool TryAcquire(string key, int limit, TimeSpan window)
    {
        var bucket = _buckets.GetOrAdd(key, static _ => new Bucket());
        var threshold = DateTimeOffset.UtcNow - window;
        lock (bucket.Gate)
        {
            while (bucket.Events.TryPeek(out var timestamp) && timestamp < threshold)
            {
                bucket.Events.Dequeue();
            }

            if (bucket.Events.Count >= limit)
            {
                return false;
            }

            bucket.Events.Enqueue(DateTimeOffset.UtcNow);
            return true;
        }
    }

    private sealed class Bucket
    {
        public object Gate { get; } = new();
        public Queue<DateTimeOffset> Events { get; } = new();
    }
}
