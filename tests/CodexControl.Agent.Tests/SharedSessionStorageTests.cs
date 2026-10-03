using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Control;
using CodexControl.Agent.Relay;
using CodexControl.Protocol;

namespace CodexControl.Agent.Tests;

internal static class SharedSessionStorageTests
{
    public static Task RunAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "cc-shared-storage-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(testRoot, "workspace");
        var codexHome = Path.Combine(testRoot, "codex");
        const string id = "01a10081-78c0-7851-a412-58e2b1febd88";
        Directory.CreateDirectory(workspace);
        var artifact = ThreadWorkspacePolicy.VisualizationRoot(id, codexHome)!;
        Directory.CreateDirectory(artifact);
        try
        {
            Assert(artifact.EndsWith(Path.Combine("2026", "10", "03", id)), "Thread-derived artifact date");
            Assert(ThreadWorkspacePolicy.IsAllowedRoot(artifact, workspace, id, codexHome), "exact Thread artifact allowed");
            Assert(!ThreadWorkspacePolicy.IsAllowedRoot(codexHome, workspace, id, codexHome), "whole home forbidden");
            Assert(!ThreadWorkspacePolicy.IsAllowedRoot(artifact, workspace, "01a10081-78c0-7851-a412-58e2b1febd89", codexHome), "other Thread forbidden");
            Assert(!ThreadWorkspacePolicy.IsAllowedRoot(testRoot, workspace, id, codexHome), "parent project forbidden");
            var journal = new SessionEventJournal();
            journal.SetConnection("first");
            var cursor = journal.Cursor;
            var ev = new CodexEventPayload("a", 1, "AgentMessageDelta", id, "turn", "item", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                JsonSerializer.SerializeToElement(new { delta = "test" }));
            var first = journal.Append(ev, "service");
            var second = journal.Append(ev with { EventId = "b" }, "service");
            Assert(second.Sequence == first.Sequence + 1 && first.Revision == second.Revision, "event sequence independent of state revision");
            Assert(journal.Read(cursor.Epoch, cursor.Sequence, id).Events.Count == 2, "complete replay");
            journal.SetConnection("next");
            Assert(!journal.Read(cursor.Epoch, cursor.Sequence, id).Complete, "old connection cursor rejected");
            Assert(!journal.Read(journal.Cursor.Epoch, 10, id).Complete, "future cursor rejected");
            var ledger = new ControlSubmissionLedger(testRoot);
            var request = RelayEnvelope.Create(RelayMessageTypes.ControlThreadSend, new ThreadSendControlPayload(id, "private-test-prompt"),
                "request", "device", "controller");
            Assert(ledger.Begin(request) is null, "new submission");
            Assert(ledger.Begin(request)?.Code == "CONTROL_OUTCOME_UNKNOWN", "pending is not resent");
            var changed = request with { Payload = JsonSerializer.SerializeToElement(new ThreadSendControlPayload(id, "changed"), RelayJson.Options) };
            Assert(ledger.Begin(changed)?.Code == "REQUEST_ID_CONFLICT", "different input cannot reuse ID");
            ledger.Complete(request, new(ControlResultStatus.Succeeded, null, null, null));
            Assert(ledger.Begin(request)?.Status == ControlResultStatus.Succeeded, "cached result");
            Assert(new ControlSubmissionLedger(testRoot).Begin(request)?.Code == "CONTROL_OUTCOME_UNKNOWN", "restart cannot resend uncertain side effect");
            Assert(!File.ReadAllText(Path.Combine(testRoot, "control-submissions.json")).Contains("private-test-prompt"), "no prompt persistence");
            File.WriteAllText(Path.Combine(testRoot, "control-submissions.json"), "invalid");
            var unavailable = new ControlSubmissionLedger(testRoot);
            Assert(unavailable.Begin(request)?.Code == "SUBMISSION_LEDGER_UNAVAILABLE", "unavailable ledger blocks new work");
            var stop = RelayEnvelope.Create(RelayMessageTypes.ControlInterrupt, new InterruptControlPayload(id, "turn"),
                "stop", "device", "controller");
            Assert(unavailable.Begin(stop) is null, "journal failure must not prevent stopping a bound Turn");
            unavailable.Complete(stop, new(ControlResultStatus.Succeeded, null, null, null));
            Assert(unavailable.Begin(stop)?.Status == ControlResultStatus.Succeeded, "restrictive duplicate is cached");
            var decline = RelayEnvelope.Create(RelayMessageTypes.ControlApproval,
                new ApprovalControlPayload("approval", JsonSerializer.SerializeToElement("decline")), "decline", "device", "controller");
            Assert(unavailable.Begin(decline) is null, "journal failure must not prevent declining approval");
            var accept = decline with { RequestId = "accept", Payload = JsonSerializer.SerializeToElement(
                new ApprovalControlPayload("approval", JsonSerializer.SerializeToElement("accept")), RelayJson.Options) };
            Assert(unavailable.Begin(accept)?.Code == "SUBMISSION_LEDGER_UNAVAILABLE", "approval must not bypass durable submission");

            var longText = "  " + new string('文', 40_000) + "  ";
            var page = CodexThreadHistoryMapper.MapItemsPage(JsonSerializer.SerializeToElement(new { data = new[] {
                new { turnId = "turn", item = new { id = "long", type = "agentMessage", text = longText } } } }));
            Assert(page.Entries.Single().Text == longText && !page.Entries.Single().Truncated,
                "resync preserves text longer than the former 20k limit and whitespace");
            var largeText = new string('文', 128_001);
            page = CodexThreadHistoryMapper.MapItemsPage(JsonSerializer.SerializeToElement(new { data = new[] {
                new { turnId = "turn", item = new { id = "large", type = "agentMessage", text = largeText } } } }));
            Assert(page.Entries.Single().Truncated && page.Entries.Single().OriginalLength == largeText.Length,
                "over-limit history carries explicit item completeness");
        }
        finally { Directory.Delete(testRoot, true); }
        return Task.CompletedTask;
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
