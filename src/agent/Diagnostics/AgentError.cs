namespace CodexControl.Agent.Diagnostics;

public static class AgentErrorCodes
{
    public const string CodexNotFound = "CODEX_NOT_FOUND";
    public const string CodexNotExecutable = "CODEX_NOT_EXECUTABLE";
    public const string CodexVersionUnsupported = "CODEX_VERSION_UNSUPPORTED";
    public const string AppServerStartFailed = "APP_SERVER_START_FAILED";
    public const string AppServerProtocolError = "APP_SERVER_PROTOCOL_ERROR";
    public const string AppServerDisconnected = "APP_SERVER_DISCONNECTED";
    public const string BridgeOverloaded = "BRIDGE_OVERLOADED";
    public const string RequestIdNamespaceCollision = "REQUEST_ID_NAMESPACE_COLLISION";
    public const string TurnNotActive = "TURN_NOT_ACTIVE";
    public const string TurnMismatch = "TURN_MISMATCH";
    public const string ApprovalNotFound = "APPROVAL_NOT_FOUND";
    public const string ApprovalAlreadyResolved = "APPROVAL_ALREADY_RESOLVED";
    public const string ApprovalDecisionUnavailable = "APPROVAL_DECISION_UNAVAILABLE";
}

public sealed class AgentException : Exception
{
    public AgentException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public AgentException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
