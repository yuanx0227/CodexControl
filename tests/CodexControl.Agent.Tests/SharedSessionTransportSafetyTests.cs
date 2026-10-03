using System.Text.Json;
using CodexControl.Agent.Relay;
using CodexControl.Protocol;

namespace CodexControl.Agent.Tests;

internal static class SharedSessionTransportSafetyTests
{
    public static async Task DispatchAsync()
    {
        var startedOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var survived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var running = 0;
        var scheduler = new BoundedControlDispatcher(async (request, token) =>
        {
            if (request.Type == RelayMessageTypes.ControlThreadWatch)
            {
                var count = Interlocked.Increment(ref started);
                Interlocked.Increment(ref running);
                if (count == 1) startedOne.TrySetResult();
                if (count == 2) startedTwo.TrySetResult();
                try { await hold.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref running); }
            }
            else if (request.RequestId == "throws") throw new InvalidOperationException("test failure");
            else if (request.RequestId == "cancel") cancelled.TrySetResult();
            else if (request.RequestId == "survives") survived.TrySetResult();
        }, (_, _) => failed.TrySetResult(), CancellationToken.None,
            regularWorkers: 2, restrictiveWorkers: 1, regularCapacity: 1, restrictiveCapacity: 2);
        try
        {
            Check(scheduler.TryDispatch(Watch("one")), "first watch accepted");
            await startedOne.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(scheduler.TryDispatch(Watch("two")), "second watch accepted");
            await startedTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(scheduler.TryDispatch(Watch("queued")), "bounded waiting slot accepted");
            Check(!scheduler.TryDispatch(Watch("full")), "normal queue refuses excess work instead of unbounded tasks");
            Check(scheduler.TryDispatch(RelayEnvelope.Create(RelayMessageTypes.ControlApproval,
                new { approvalId = "pending", decision = "cancel" }, "cancel")), "cancel has reserved capacity");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(Volatile.Read(ref running) == 2, "cancel does not wait for the two blocked watches");
            Check(scheduler.TryDispatch(RelayEnvelope.Create(RelayMessageTypes.ControlInterrupt, new { }, "throws")), "failure test accepted");
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(scheduler.TryDispatch(RelayEnvelope.Create(RelayMessageTypes.ControlInterrupt, new { }, "survives")), "worker survives failed request");
            await survived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!BoundedControlDispatcher.IsRestrictive(RelayEnvelope.Create(RelayMessageTypes.ControlApproval,
                new { decision = "accept" })), "positive approval cannot consume restriction-only capacity");
        }
        finally { await scheduler.DisposeAsync(); }
        Check(Volatile.Read(ref running) == 0 && !scheduler.TryDispatch(Watch("disposed")),
            "shutdown cancels active work, waits for workers and rejects further work");
    }

    public static Task BudgetAsync()
    {
        var snapshot = new CodexSnapshotPayload(1, "Idle", null, null, null, 0, null, null, null, [], 0, null, null);
        var emptyHistory = new CodexThreadReadResultPayload("thread", null, null, [], [], false);
        var response = new ThreadWatchResultPayload("thread", "service", "epoch", 10, emptyHistory, snapshot, [], [], false, true);
        Check(RelayClient.TrySerializeWatchResult(response, out var small) &&
            small.GetProperty("sequence").GetInt64() == 10 && !small.GetProperty("resyncRequired").GetBoolean(),
            "normal replay fits and preserves cursor start contract");
        var largeEvent = new CodexEventPayload("large", 1, "AgentMessageDelta", "thread", "turn", "item", 0,
            JsonSerializer.SerializeToElement(new { delta = new string('x', 270_000) }));
        Check(!RelayClient.TrySerializeWatchResult(response with { Events = [largeEvent] }, out var rejectedReplay) &&
            rejectedReplay.ValueKind == JsonValueKind.Undefined, "oversized replay cannot be sent as successful response");
        var largeHistory = emptyHistory with { Entries = [new("item", "turn", "assistant", new string('中', 70_000), null, [], [])] };
        Check(!RelayClient.TrySerializeWatchResult(response with { History = largeHistory, ResyncRequired = true }, out _),
            "snapshot budget counts serialized UTF-8 bytes including JSON escaping");

        var multiImage = new CodexEventPayload("images", 1, "UserMessageCompleted", "thread", "turn", "item", 0,
            JsonSerializer.SerializeToElement(new
            {
                text = new string('中', 100_000),
                attachments = Enumerable.Range(0, 4).Select(index => new
                {
                    kind = "image", name = "image-" + index, mimeType = "image/png",
                    dataUrl = "data:image/png;base64," + new string('A', 300_000),
                }).ToArray(),
            }, RelayJson.Options), "service", "epoch", long.MaxValue);
        var bounded = RelayClient.ConstrainDomainEvent(multiImage, "device");
        Check(bounded.Kind == "ContentIncomplete" && bounded.ThreadId == "thread" && bounded.TurnId == "turn" &&
            bounded.ItemId == "item" && bounded.Data.GetProperty("resyncRequired").GetBoolean() &&
            !bounded.Data.TryGetProperty("attachments", out _) && !bounded.Data.TryGetProperty("text", out _),
            "oversized image/unicode event retains attribution and explicitly replaces content with recovery metadata");
        Check(JsonSerializer.SerializeToUtf8Bytes(RelayEnvelope.Create(RelayMessageTypes.CodexEvent, bounded,
            deviceId: "device"), RelayJson.Options).Length <= 900 * 1024,
            "actual event envelope including stream metadata must stay below its wire budget");
        var shortEvent = multiImage with { Data = JsonSerializer.SerializeToElement(new { text = "正文保持完整" }, RelayJson.Options) };
        Check(RelayClient.ConstrainDomainEvent(shortEvent, "device").Data.GetProperty("text").GetString() == "正文保持完整",
            "within-budget content remains complete");

        var clock = new ManualClock();
        var journal = new SessionEventJournal(clock);
        var initial = journal.Cursor;
        var retained = journal.Append(bounded, "service");
        Check(journal.Read(initial.Epoch, initial.Sequence, "thread").Complete, "fresh event is replayable");
        clock.Advance(TimeSpan.FromMinutes(5));
        Check(!journal.Read(initial.Epoch, initial.Sequence, "thread").Complete,
            "read must expire five-minute-old content even without another append");
        Check(journal.Read(initial.Epoch, retained.Sequence, "thread").Events.Count == 0,
            "expired content must not be returned from the journal");
        return Task.CompletedTask;
    }

    private static RelayEnvelope Watch(string id) => RelayEnvelope.Create(RelayMessageTypes.ControlThreadWatch,
        new ThreadWatchControlPayload("thread"), id);
    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException(description); }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
