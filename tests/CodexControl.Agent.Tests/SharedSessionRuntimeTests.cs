using CodexControl.Agent.Control;
using CodexControl.Protocol;

namespace CodexControl.Agent.Tests;

internal static class SharedSessionRuntimeTests
{
    public static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-shared-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runtime = new SharedSessionRuntimeContext();
            var before = runtime.GetSubmissionLedger(root);
            var request = RelayEnvelope.Create(RelayMessageTypes.ControlThreadSend,
                new ThreadSendControlPayload("thread", "runtime-private-prompt"), "request", "device", "controller");
            Check(before.Begin(request) is null, "first submission may execute");
            before.Complete(request, new(ControlResultStatus.Succeeded, null, null, null));
            var reconnected = runtime.GetSubmissionLedger(Path.Combine(root, "."));
            Check(ReferenceEquals(before, reconnected) && reconnected.Begin(request)?.Status == ControlResultStatus.Succeeded,
                "same runtime and normalized data directory retain known outcome across new Relay clients");
            Check(!ReferenceEquals(before, runtime.GetSubmissionLedger(Path.Combine(root, "other"))), "different data directory does not share a submission ledger");
            var restarted = new SharedSessionRuntimeContext().GetSubmissionLedger(root);
            Check(restarted.Begin(request)?.Code == "CONTROL_OUTCOME_UNKNOWN", "new process/runtime never invents the lost outcome or resends");
            Check(!File.ReadAllText(Path.Combine(root, "control-submissions.json")).Contains("runtime-private-prompt", StringComparison.Ordinal), "runtime preservation adds no prompt persistence");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
