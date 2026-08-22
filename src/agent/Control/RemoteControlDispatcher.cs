using System.Text.Json;
using CodexControl.Agent.Codex;
using CodexControl.Agent.Diagnostics;
using CodexControl.Agent.State;

namespace CodexControl.Agent.Control;

public sealed record ControlDispatchResult(
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? Result);

public sealed class RemoteControlDispatcher
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly AppServerBridge _bridge;
    private readonly CodexStateManager _state;

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

    private static ControlDispatchResult Success(JsonElement? result) =>
        new(true, null, null, result?.Clone());

    private static ControlDispatchResult Failure(string code, string message) =>
        new(false, code, message, null);
}
