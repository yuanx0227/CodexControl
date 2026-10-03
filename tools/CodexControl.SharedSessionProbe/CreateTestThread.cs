using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexControl.SharedSessionProbe;

internal static class CreateTestThread
{
    public static async Task RunAsync(Options options)
    {
        var workspace = options.Workspace!;
        TestThreadPolicy.RequireEmptyWorkspace(workspace);
        await using var client = await Program.ConnectAsync(options, "create_test");
        var model = await SelectDefaultModelAsync(client);
        TestThreadPolicy.RequireEmptyWorkspace(workspace);
        var started = await Program.RequestAsync(client, "thread/start", new
        {
            model,
            cwd = workspace,
            runtimeWorkspaceRoots = new[] { workspace },
            ephemeral = false,
            historyMode = "paginated",
            approvalPolicy = "untrusted",
            approvalsReviewer = "user",
            sandbox = "workspace-write",
            config = new
            {
                sandbox_workspace_write = new
                {
                    writable_roots = new[] { workspace },
                    network_access = false,
                    exclude_tmpdir_env_var = true,
                    exclude_slash_tmp = true,
                },
            },
            serviceName = "codex_control_shared_probe",
        });
        var threadId = Program.SafeId(Program.Text(started, "thread", "id"));
        try
        {
            var gates = new
            {
                idsValid = threadId is not null && Guid.TryParseExact(threadId, "D", out _),
                cwdMatch = SafeGate(() => TestThreadPolicy.SameDirectory(Program.Text(started, "cwd"), workspace)),
                threadCwdMatch = SafeGate(() => TestThreadPolicy.SameDirectory(Program.Text(started, "thread", "cwd"), workspace)),
                modelMatch = Program.Text(started, "model") == model,
                approvalPolicyAllowed = Program.Text(started, "approvalPolicy") == "untrusted",
                reviewerUser = Program.Text(started, "approvalsReviewer") == "user",
                notEphemeral = Program.At(started, "thread", "ephemeral").ValueKind == JsonValueKind.False,
                paginated = Program.Text(started, "thread", "historyMode") == "paginated",
                idle = Program.Text(started, "thread", "status", "type") == "idle",
                hasNoTurns = HasNoTurns(started),
                isolatedSandbox = SafeGate(() => TestThreadPolicy.IsIsolatedSandbox(started, workspace)),
                exactRoots = SafeGate(() => TestThreadPolicy.HasExactWorkspaceRoots(started, workspace)),
            };
            if (!gates.idsValid || !gates.cwdMatch || !gates.threadCwdMatch || !gates.modelMatch ||
                !gates.approvalPolicyAllowed || !gates.reviewerUser || !gates.notEphemeral || !gates.paginated || !gates.idle ||
                !gates.hasNoTurns || !gates.isolatedSandbox || !gates.exactRoots)
            {
                Program.Write(new
                {
                    kind = "test_thread_policy_diagnostics", gates,
                    sandboxType = Program.Text(started, "sandbox", "type") switch
                    {
                        "workspaceWrite" => "workspaceWrite", "readOnly" => "readOnly",
                        "dangerFullAccess" => "dangerFullAccess", "externalSandbox" => "externalSandbox",
                        _ => "unknown",
                    },
                    networkAccess = BooleanOrNull(Program.At(started, "sandbox", "networkAccess")),
                    excludeTmpdirEnvVar = BooleanOrNull(Program.At(started, "sandbox", "excludeTmpdirEnvVar")),
                    excludeSlashTmp = BooleanOrNull(Program.At(started, "sandbox", "excludeSlashTmp")),
                    writableRoots = DescribeRoots(Program.At(started, "sandbox", "writableRoots"), workspace),
                    runtimeWorkspaceRoots = DescribeRoots(Program.At(started, "runtimeWorkspaceRoots"), workspace),
                });
                throw new ProbeRejectedException("CREATED_THREAD_EFFECTIVE_POLICY_REJECTED");
            }

            await Program.RequestAsync(client, "thread/name/set", new { threadId, name = options.Marker });
            // In 0.160, reading turns persists a loaded paginated thread even before its first input.
            // Naming alone does not guarantee that another connection can resume the empty thread.
            var metadata = await Program.RequestAsync(client, "thread/read", new { threadId, includeTurns = true });
            if (Program.Text(metadata, "thread", "id") != threadId ||
                !TestThreadPolicy.SameDirectory(Program.Text(metadata, "thread", "cwd"), workspace) ||
                Program.Text(metadata, "thread", "name") != options.Marker ||
                Program.Text(metadata, "thread", "status", "type") != "idle" ||
                Program.Text(metadata, "thread", "historyMode") != "paginated" || !HasNoTurns(metadata))
                throw new ProbeRejectedException("CREATED_THREAD_NAME_READBACK_REJECTED");

            Program.Write(new
            {
                kind = "test_thread_created", threadId, marker = options.Marker, workspace,
                effectivePolicyVerified = true, inputSubmitted = false, desktopVerified = false,
            });
        }
        catch
        {
            Program.Write(new
            {
                kind = "test_thread_created_but_unverified", threadId, marker = options.Marker,
                inputSubmitted = false, automaticRetry = false,
            });
            throw;
        }
        if (options.Mode == "create-observe")
        {
            // Retain the creating subscription until this observer exits. Both connections belong
            // to this probe process and close together; no permission overrides are sent on resume.
            Program.Write(new { kind = "creation_connection_retained", threadId, inputSubmitted = false });
            var exitCode = await Program.RunObserverAsync(options with { Mode = "observe", Thread = threadId });
            if (exitCode != 0) throw new ProbeRejectedException("CREATED_THREAD_OBSERVER_DISCONNECTED");
        }
    }

    private static bool HasNoTurns(JsonElement started)
    {
        var turns = Program.At(started, "thread", "turns");
        return turns.ValueKind == JsonValueKind.Array && turns.GetArrayLength() == 0;
    }

    private static bool SafeGate(Func<bool> gate)
    {
        try { return gate(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool? BooleanOrNull(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static object DescribeRoots(JsonElement roots, string workspace)
    {
        var array = roots.ValueKind == JsonValueKind.Array;
        return new
        {
            isArray = array,
            count = array ? roots.GetArrayLength() : (int?)null,
            equalsWorkspace = array && roots.GetArrayLength() == 1 && roots[0].ValueKind == JsonValueKind.String &&
                              SafeGate(() => TestThreadPolicy.SameDirectory(roots[0].GetString(), workspace)),
            inScope = array && SafeGate(() => TestThreadPolicy.IsIsolatedSandbox(JsonSerializer.SerializeToElement(new
            {
                sandbox = new
                {
                    type = "workspaceWrite", networkAccess = false, excludeTmpdirEnvVar = true, excludeSlashTmp = true,
                    writableRoots = roots,
                },
                runtimeWorkspaceRoots = new[] { workspace },
            }), workspace)),
        };
    }

    private static async Task<string> SelectDefaultModelAsync(SharedRpcClient client)
    {
        var defaults = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var result = await Program.RequestAsync(client, "model/list", new { cursor, limit = 100, includeHidden = false });
            var data = Program.At(result, "data");
            if (data.ValueKind != JsonValueKind.Array) throw new ProbeRejectedException("MODEL_CATALOG_INVALID");
            foreach (var entry in data.EnumerateArray())
            {
                if (Program.At(entry, "isDefault").ValueKind != JsonValueKind.True ||
                    Program.At(entry, "hidden").ValueKind == JsonValueKind.True) continue;
                var model = Program.Text(entry, "model");
                if (model is null || !Regex.IsMatch(model, @"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,199}\z"))
                    throw new ProbeRejectedException("DEFAULT_MODEL_INVALID");
                defaults.Add(model);
            }

            var nextCursor = Program.At(result, "nextCursor");
            if (nextCursor.ValueKind == JsonValueKind.Null)
            {
                if (defaults.Count != 1) throw new ProbeRejectedException("UNIQUE_DEFAULT_MODEL_REQUIRED");
                return defaults.Single();
            }
            if (nextCursor.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(nextCursor.GetString()) ||
                nextCursor.GetString()!.Length > 4096 || !cursors.Add(nextCursor.GetString()!))
                throw new ProbeRejectedException("MODEL_PAGINATION_INVALID");
            cursor = nextCursor.GetString();
        }
        throw new ProbeRejectedException("MODEL_PAGINATION_LIMIT_REACHED");
    }
}
