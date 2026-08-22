using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.State;
using CodexControl.Protocol;

namespace CodexControl.Agent.Control;

public sealed record ControlDispatchResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? Result);

public sealed class RemoteControlDispatcher
{
    private const int ThreadItemsPageSize = 100;
    private const int MaxThreadItemPages = 20;
    private const int ProjectPageSize = 100;
    private const int MaxProjectPages = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HistoryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ThreadMutationTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] UserThreadSourceKinds = ["cli", "vscode", "appServer", "exec", "unknown"];
    private const int MaxControlTextLength = 20_000;
    private const int MaxCwdLength = 2_048;

    private readonly AppServerBridge _bridge;
    private readonly CodexStateManager _state;
    private readonly SemaphoreSlim _threadMutationGate = new(1, 1);

    public RemoteControlDispatcher(AppServerBridge bridge, CodexStateManager state)
    {
        _bridge = bridge;
        _state = state;
    }

    public async Task<ControlDispatchResult> SteerAsync(
        string threadId,
        string expectedTurnId,
        string text,
        CancellationToken cancellationToken)
    {
        var snapshot = _state.Snapshot;
        if (snapshot.ActiveThreadId != threadId || string.IsNullOrWhiteSpace(snapshot.ActiveTurnId))
        {
            return Failure("TURN_NOT_ACTIVE", "指定 Thread 当前没有活动 Turn。");
        }

        if (snapshot.ActiveTurnId != expectedTurnId)
        {
            return Failure("TURN_MISMATCH", "expectedTurnId 与当前活动 Turn 不一致。");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Failure("INVALID_CONTROL_PAYLOAD", "Steer 文本不能为空。");
        }

        try
        {
            var result = await _bridge.SendRequestAsync(
                "turn/steer",
                new
                {
                    threadId,
                    expectedTurnId,
                    input = new[] { new { type = "text", text } },
                },
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            return Success(result);
        }
        catch (AppServerRpcException exception)
        {
            return Failure("TURN_NOT_ACTIVE", exception.Message);
        }
        catch (TimeoutException exception)
        {
            return Failure("APP_SERVER_TIMEOUT", exception.Message);
        }
        catch (AgentException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
    }

    public async Task<ControlDispatchResult> ListThreadsAsync(
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100 || cursor?.Length > 2_048)
        {
            return Failure("INVALID_CONTROL_PAYLOAD", "历史会话分页参数无效。");
        }

        try
        {
            var result = await _bridge.SendRequestAsync(
                "thread/list",
                new
                {
                    cursor,
                    limit,
                    sortKey = "recency_at",
                    sortDirection = "desc",
                    sourceKinds = UserThreadSourceKinds,
                    archived = false,
                },
                HistoryTimeout,
                cancellationToken).ConfigureAwait(false);
            var projects = cursor is null
                ? await ListProjectsAsync(cancellationToken).ConfigureAwait(false)
                : [];
            return Success(JsonSerializer.SerializeToElement(
                MapThreadList(result, projects),
                RelayJson.Options));
        }
        catch (JsonException exception)
        {
            return Failure("APP_SERVER_PROTOCOL_ERROR", exception.Message);
        }
        catch (AppServerRpcException exception)
        {
            return Failure("APP_SERVER_PROTOCOL_ERROR", exception.Message);
        }
        catch (TimeoutException exception)
        {
            return Failure("APP_SERVER_TIMEOUT", exception.Message);
        }
        catch (AgentException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
    }

    public async Task<ControlDispatchResult> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        if (!IsValidThreadId(threadId))
        {
            return Failure("THREAD_NOT_FOUND", "历史会话 ID 无效。");
        }

        try
        {
            var metadataResult = await _bridge.SendRequestAsync(
                "thread/read",
                new { threadId, includeTurns = false },
                HistoryTimeout,
                cancellationToken).ConfigureAwait(false);
            var metadata = CodexThreadHistoryMapper.MapMetadata(metadataResult);
            if (!string.Equals(metadata.ThreadId, threadId, StringComparison.Ordinal))
            {
                throw new JsonException("thread/read 返回了不匹配的 thread.id。");
            }

            var entriesNewestFirst = new List<CodexThreadHistoryEntryPayload>();
            string? cursor = null;
            var textWasTruncated = false;
            for (var pageNumber = 0;
                 pageNumber < MaxThreadItemPages && entriesNewestFirst.Count < CodexThreadHistoryMapper.EntryLimit;
                 pageNumber++)
            {
                JsonElement pageResult;
                try
                {
                    pageResult = await _bridge.SendRequestAsync(
                        "thread/items/list",
                        new
                        {
                            threadId,
                            turnId = (string?)null,
                            cursor,
                            limit = ThreadItemsPageSize,
                            sortDirection = "desc",
                        },
                        HistoryTimeout,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AppServerRpcException exception) when (
                    pageNumber == 0 && IsThreadItemsListUnavailable(exception))
                {
                    var legacyResult = await _bridge.SendRequestAsync(
                        "thread/read",
                        new { threadId, includeTurns = true },
                        HistoryTimeout,
                        cancellationToken).ConfigureAwait(false);
                    var legacyHistory = CodexThreadHistoryMapper.MapLegacy(legacyResult);
                    if (!string.Equals(legacyHistory.ThreadId, threadId, StringComparison.Ordinal))
                    {
                        throw new JsonException("thread/read 返回了不匹配的 thread.id。");
                    }

                    return Success(JsonSerializer.SerializeToElement(legacyHistory, RelayJson.Options));
                }
                var page = CodexThreadHistoryMapper.MapItemsPage(pageResult);
                entriesNewestFirst.AddRange(page.Entries);
                textWasTruncated |= page.TextWasTruncated;
                cursor = page.NextCursor;
                if (cursor is null)
                {
                    break;
                }
            }

            var history = CodexThreadHistoryMapper.Build(
                metadata,
                entriesNewestFirst,
                hasMore: cursor is not null,
                textWasTruncated);
            return Success(JsonSerializer.SerializeToElement(
                history,
                RelayJson.Options));
        }
        catch (JsonException exception)
        {
            return Failure("APP_SERVER_PROTOCOL_ERROR", exception.Message);
        }
        catch (AppServerRpcException exception)
        {
            return Failure("THREAD_READ_FAILED", exception.Message);
        }
        catch (TimeoutException exception)
        {
            return Failure("APP_SERVER_TIMEOUT", exception.Message);
        }
        catch (AgentException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
    }

    public Task<ControlDispatchResult> StartThreadAsync(
        string cwd,
        string text,
        CancellationToken cancellationToken) =>
        MutateThreadAsync(
            async token =>
            {
                if (!Path.IsPathFullyQualified(cwd) || cwd.Length > MaxCwdLength)
                {
                    return Failure("PROJECT_PATH_INVALID", "项目目录必须是本机绝对路径。");
                }

                var fullCwd = Path.GetFullPath(cwd);
                if (!Directory.Exists(fullCwd))
                {
                    return Failure("PROJECT_NOT_FOUND", "电脑上不存在指定项目目录。");
                }

                var thread = await _bridge.SendRequestAsync(
                    "thread/start",
                    new
                    {
                        cwd = fullCwd,
                        ephemeral = false,
                        approvalPolicy = "untrusted",
                        sandbox = "workspace-write",
                        serviceName = "codex_control_remote",
                    },
                    ThreadMutationTimeout,
                    token).ConfigureAwait(false);
                var threadId = ReadRequiredId(thread, "thread", "thread/start");
                return await StartTurnCoreAsync(threadId, text, token).ConfigureAwait(false);
            },
            text,
            cancellationToken);

    public Task<ControlDispatchResult> ResumeThreadAsync(
        string threadId,
        string text,
        CancellationToken cancellationToken) =>
        MutateThreadAsync(
            async token =>
            {
                if (!IsValidThreadId(threadId))
                {
                    return Failure("THREAD_NOT_FOUND", "历史会话 ID 无效。");
                }

                var resumed = await _bridge.SendRequestAsync(
                    "thread/resume",
                    new
                    {
                        threadId,
                        approvalPolicy = "untrusted",
                        sandbox = "workspace-write",
                    },
                    ThreadMutationTimeout,
                    token).ConfigureAwait(false);
                var resumedThreadId = ReadRequiredId(resumed, "thread", "thread/resume");
                return await StartTurnCoreAsync(resumedThreadId, text, token).ConfigureAwait(false);
            },
            text,
            cancellationToken);

    public async Task<ControlDispatchResult> InterruptAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken)
    {
        var snapshot = _state.Snapshot;
        if (snapshot.ActiveThreadId != threadId || snapshot.ActiveTurnId != turnId)
        {
            return Failure("TURN_NOT_ACTIVE", "指定 Turn 当前不活动。");
        }

        try
        {
            var result = await _bridge.SendRequestAsync(
                "turn/interrupt",
                new { threadId, turnId },
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            return Success(result);
        }
        catch (AppServerRpcException exception)
        {
            return Failure("TURN_NOT_ACTIVE", exception.Message);
        }
        catch (TimeoutException exception)
        {
            return Failure("APP_SERVER_TIMEOUT", exception.Message);
        }
        catch (AgentException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
    }

    public async Task<ControlDispatchResult> ResolveApprovalAsync(
        string approvalId,
        JsonElement decision,
        string controllerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _bridge.Approvals.ResolveRemoteAsync(
                approvalId,
                decision,
                string.Concat("controller:", controllerId),
                cancellationToken).ConfigureAwait(false);
            return resolution.Succeeded
                ? Success(null)
                : Failure(resolution.ErrorCode!, resolution.ErrorMessage!);
        }
        catch (TimeoutException exception)
        {
            return Failure("APP_SERVER_TIMEOUT", exception.Message);
        }
        catch (AgentException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
    }

    private async Task<ControlDispatchResult> MutateThreadAsync(
        Func<CancellationToken, Task<ControlDispatchResult>> mutation,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxControlTextLength)
        {
            return Failure("INVALID_CONTROL_PAYLOAD", "任务内容不能为空且不能超过 20000 个字符。");
        }

        await _threadMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_state.Snapshot.ActiveTurnId))
            {
                return Failure("TURN_ALREADY_ACTIVE", "已有任务正在运行，请先 Steer 或停止当前任务。");
            }

            try
            {
                return await mutation(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                return Failure("APP_SERVER_PROTOCOL_ERROR", exception.Message);
            }
            catch (AppServerRpcException exception)
            {
                return Failure("THREAD_CONTROL_FAILED", exception.Message);
            }
            catch (TimeoutException exception)
            {
                return Failure("APP_SERVER_TIMEOUT", exception.Message);
            }
            catch (AgentException exception)
            {
                return Failure(exception.Code, exception.Message);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return Failure("PROJECT_PATH_INVALID", exception.Message);
            }
        }
        finally
        {
            _threadMutationGate.Release();
        }
    }

    private async Task<ControlDispatchResult> StartTurnCoreAsync(
        string threadId,
        string text,
        CancellationToken cancellationToken)
    {
        var turn = await _bridge.SendRequestAsync(
            "turn/start",
            new
            {
                threadId,
                approvalPolicy = "untrusted",
                input = new[] { new { type = "text", text = text.Trim() } },
            },
            ThreadMutationTimeout,
            cancellationToken).ConfigureAwait(false);
        var turnId = ReadRequiredId(turn, "turn", "turn/start");
        return Success(JsonSerializer.SerializeToElement(
            new CodexThreadActionResultPayload(threadId, turnId),
            RelayJson.Options));
    }

    private async Task<IReadOnlyList<CodexProjectSummaryPayload>> ListProjectsAsync(
        CancellationToken cancellationToken)
    {
        var projects = new List<CodexProjectSummaryPayload>();
        string? cursor = null;
        for (var page = 0; page < MaxProjectPages; page++)
        {
            JsonElement result;
            try
            {
                result = await _bridge.SendRequestAsync(
                    "project/list",
                    new { cursor, limit = ProjectPageSize },
                    HistoryTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AppServerRpcException exception) when (IsMethodUnavailable(exception, "project/list"))
            {
                return [];
            }

            var mapped = MapProjectPage(result);
            projects.AddRange(mapped.Projects);
            cursor = mapped.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        return projects
            .GroupBy(project => project.ProjectId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(project => project.Position)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CodexThreadListResultPayload MapThreadList(
        JsonElement result,
        IReadOnlyList<CodexProjectSummaryPayload> projects)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("thread/list 未返回 data 数组。");
        }

        var threads = new List<CodexThreadSummaryPayload>();
        foreach (var thread in data.EnumerateArray())
        {
            var threadId = GetString(thread, "id", 256);
            if (string.IsNullOrWhiteSpace(threadId))
            {
                continue;
            }

            threads.Add(new CodexThreadSummaryPayload(
                threadId,
                GetString(thread, "name", 256),
                GetString(thread, "preview", 1_000),
                GetString(thread, "cwd", MaxCwdLength),
                GetInt64(thread, "createdAt"),
                GetInt64(thread, "updatedAt"),
                GetInt64(thread, "recencyAt"),
                GetStatus(thread),
                GetSourceKind(thread),
                GetString(thread, "projectId", 256)));
        }

        return new CodexThreadListResultPayload(
            threads,
            projects,
            GetString(result, "nextCursor", 2_048));
    }

    private static ProjectPage MapProjectPage(JsonElement result)
    {
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("project/list 未返回 data 数组。");
        }

        var projects = new List<CodexProjectSummaryPayload>();
        foreach (var project in data.EnumerateArray())
        {
            var projectId = GetString(project, "id", 256);
            var name = GetString(project, "name", 256);
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var roots = new List<string>();
            if (project.TryGetProperty("roots", out var rootValues) && rootValues.ValueKind == JsonValueKind.Array)
            {
                foreach (var root in rootValues.EnumerateArray())
                {
                    var path = GetString(root, "path", MaxCwdLength);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        roots.Add(path);
                    }
                }
            }

            projects.Add(new CodexProjectSummaryPayload(
                projectId,
                name,
                GetInt64(project, "position") ?? long.MaxValue,
                roots));
        }

        return new ProjectPage(projects, GetString(result, "nextCursor", 2_048));
    }

    private static string ReadRequiredId(JsonElement result, string objectProperty, string method)
    {
        if (result.TryGetProperty(objectProperty, out var value) &&
            value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(id.GetString()))
        {
            return id.GetString()!;
        }

        throw new JsonException($"{method} 未返回 {objectProperty}.id。");
    }

    private static bool IsValidThreadId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

    private static bool IsThreadItemsListUnavailable(AppServerRpcException exception) =>
        IsMethodUnavailable(exception, "thread/items/list");

    private static bool IsMethodUnavailable(AppServerRpcException exception, string method) =>
        exception.Method == method &&
        (exception.Code == -32601 ||
         exception.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase));

    private sealed record ProjectPage(
        IReadOnlyList<CodexProjectSummaryPayload> Projects,
        string? NextCursor);

    private static string? GetString(JsonElement value, string propertyName, int maximumLength)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        return text is null || text.Length <= maximumLength ? text : text[..maximumLength];
    }

    private static long? GetInt64(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt64(out var number) ? number : null;
    }

    private static string GetStatus(JsonElement thread)
    {
        if (!thread.TryGetProperty("status", out var status))
        {
            return "unknown";
        }

        if (status.ValueKind == JsonValueKind.String)
        {
            return status.GetString() ?? "unknown";
        }

        return status.ValueKind == JsonValueKind.Object
            ? GetString(status, "type", 64) ?? "unknown"
            : "unknown";
    }

    private static string? GetSourceKind(JsonElement thread)
    {
        var direct = GetString(thread, "sourceKind", 64);
        if (direct is not null)
        {
            return direct;
        }

        if (!thread.TryGetProperty("source", out var source))
        {
            return null;
        }

        return source.ValueKind switch
        {
            JsonValueKind.String => source.GetString(),
            JsonValueKind.Object => GetString(source, "kind", 64) ?? GetString(source, "type", 64),
            _ => null,
        };
    }

    private static ControlDispatchResult Success(JsonElement? result) =>
        new(true, null, null, result?.Clone());

    private static ControlDispatchResult Failure(string code, string message) =>
        new(false, code, message, null);
}
