using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Configuration;
using CodexControl.Agent.Control;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.State;

namespace CodexControl.Agent.Tests;

internal static class SharedSessionPolicyTests
{
    public static async Task RunAsync()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "codex-control-shared-policy-" + Guid.NewGuid().ToString("N"));
        var project = Directory.CreateDirectory(Path.Combine(testRoot, "project")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(project, "child")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(testRoot, "other")).FullName;
        const string threadId = "01a10081-78c0-7851-a412-58e2b1febd88";
        try
        {
            Check(!AgentSettings.Default.SharedAllowStandardTemporaryDirectories, "standard temp grant must default off in settings");
            Check(!AgentOptions.Parse([]).SharedAllowStandardTemporaryDirectories, "standard temp grant must default off in CLI");
            Check(AgentOptions.Parse(["--shared-allow-standard-temp"]).SharedAllowStandardTemporaryDirectories, "standard temp requires explicit local option");
            using var strict = Context(project, excludeTemporary: true);
            using var desktop = Context(project, excludeTemporary: false);
            Check(ThreadWorkspacePolicy.RejectionReason(strict.RootElement, project, threadId) is null, "strict workspace needs no extra temp grant");
            Check(ThreadWorkspacePolicy.RejectionReason(desktop.RootElement, project, threadId) == "TEMPORARY_DIRECTORY_REQUIRES_REVIEW", "Desktop temp policy is not granted from Agent environment alone");
            var expectedTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            var tmpdir = Environment.GetEnvironmentVariable("TMPDIR");
            var conservativeLocalCheck = OperatingSystem.IsWindows() && ThreadWorkspacePolicy.SafeDirectory(expectedTemp) &&
                ThreadWorkspacePolicy.SamePath(Path.GetTempPath(), expectedTemp) &&
                (tmpdir is null || ThreadWorkspacePolicy.SamePath(tmpdir, expectedTemp));
            var granted = ThreadWorkspacePolicy.RejectionReason(desktop.RootElement, project, threadId, allowDesktopTemporaryDirectories: true);
            Check(conservativeLocalCheck ? granted is null : granted == "TEMPORARY_DIRECTORY_REQUIRES_REVIEW",
                "an explicit grant still requires standard local temp constraints; it does not prove upstream environment");
            using var unknown = JsonDocument.Parse("{}");
            Check(ThreadWorkspacePolicy.RejectionReason(unknown.RootElement, project, threadId, true) == "EFFECTIVE_POLICY_UNKNOWN", "explicit temp choice cannot approve unknown sandbox context");

            using var log = new AgentLog(Path.Combine(testRoot, "logs"));
            var options = AgentOptions.ForTests("not-started.exe", testRoot) with
            {
                SharedEndpoint = new Uri("ws://127.0.0.1:9023"), SharedManifestPath = Path.Combine(testRoot, "not-opened.json"),
                SharedProjectRoots = [project],
            };
            await using var bridge = new AppServerBridge(options, log, new CodexStateManager());
            var dispatcher = new RemoteControlDispatcher(bridge, new CodexStateManager());
            var outside = await dispatcher.StartThreadAsync(other, "test", CancellationToken.None);
            Check(outside.ErrorCode == "PROJECT_NOT_AUTHORIZED", "shared create must reject arbitrary remote cwd before any transport request");
            var inside = await dispatcher.StartThreadAsync(child, "test", CancellationToken.None);
            Check(inside.ErrorCode == AgentErrorCodes.AppServerDisconnected, "explicit local project child passes scope check then requires connected transport");
            await using var readOnly = new AppServerBridge(options with { SharedProjectRoots = [] }, log, new CodexStateManager());
            var readOnlyDispatcher = new RemoteControlDispatcher(readOnly, new CodexStateManager());
            Check((await readOnlyDispatcher.StartThreadAsync(project, "test", CancellationToken.None)).ErrorCode == "PROJECT_NOT_AUTHORIZED",
                "no project root means read-only, even if remote cwd exists");
            await using var independent = new AppServerBridge(options with { SharedEndpoint = null, SharedManifestPath = null }, log, new CodexStateManager());
            Check((await new RemoteControlDispatcher(independent, new CodexStateManager()).StartThreadAsync(other, "test", CancellationToken.None)).ErrorCode == AgentErrorCodes.AppServerDisconnected,
                "explicit independent stdio mode retains original project creation behavior");

            using var accept = JsonDocument.Parse("\"accept\"");
            ObserveApproval(readOnly.Approvals, threadId, "unobserved-positive");
            var approval = readOnly.Approvals.PendingApprovals.Single();
            var positive = await readOnlyDispatcher.ResolveApprovalAsync(approval.ApprovalId, accept.RootElement, "test", CancellationToken.None);
            Check(positive.ErrorCode == "STEER_PERMISSION_REQUIRED" && !readOnly.Approvals.PendingApprovals.Single().IsResolving,
                "positive approval cannot implicitly join an unobserved Thread");
            foreach (var decision in new[] { "cancel", "decline" })
            {
                readOnly.Approvals.ResetConnection();
                ObserveApproval(readOnly.Approvals, threadId, "negative-" + decision);
                using var chosen = JsonDocument.Parse(JsonSerializer.Serialize(decision));
                var negative = await readOnlyDispatcher.ResolveApprovalAsync(readOnly.Approvals.PendingApprovals.Single().ApprovalId, chosen.RootElement, "test", CancellationToken.None);
                Check(negative.Succeeded && readOnly.Approvals.PendingApprovals.Single().IsResolving,
                    "negative decision is allowed without project/temp grant and still waits for service resolution");
            }
        }
        finally { Directory.Delete(testRoot, recursive: true); }
    }

    private static JsonDocument Context(string cwd, bool excludeTemporary) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        cwd, approvalPolicy = "untrusted", approvalsReviewer = "user", runtimeWorkspaceRoots = new[] { cwd },
        sandbox = new { type = "workspaceWrite", networkAccess = false, writableRoots = Array.Empty<string>(),
            excludeTmpdirEnvVar = excludeTemporary, excludeSlashTmp = excludeTemporary },
    }));

    private static void ObserveApproval(ApprovalCoordinator coordinator, string threadId, string requestId)
    {
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            method = "item/commandExecution/requestApproval", id = requestId,
            @params = new { threadId, turnId = "01a10081-78c0-7851-a412-58e2b1febd89", itemId = "cmd", availableDecisions = new[] { "accept", "decline", "cancel" } },
        }));
        coordinator.ObserveServerMessage(request.RootElement);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
