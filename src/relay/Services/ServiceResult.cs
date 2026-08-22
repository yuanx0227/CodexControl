namespace CodexControl.Relay.Services;

public sealed record ServiceResult<T>(
    bool Succeeded,
    T? Value,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ServiceResult<T> Success(T value) => new(true, value, null, null);

    public static ServiceResult<T> Failure(string code, string message) =>
        new(false, default, code, message);
}
