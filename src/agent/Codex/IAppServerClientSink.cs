namespace CodexControl.Agent.Codex;

/// <summary>
/// AppServerBridge 面向本地 TUI 的有界输出端。Bridge 不直接依赖 WebSocket 类型。
/// </summary>
public interface IAppServerClientSink
{
    bool TryQueue(string message);

    void Abort(string reason);
}
