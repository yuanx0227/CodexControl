using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexControl.Agent.Diagnostics;

/// <summary>
/// 默认不记录协议正文的 rolling log。调用方只传事件名和脱敏后的短消息。
/// </summary>
public sealed partial class AgentLog : IDisposable
{
    private const long DefaultMaxBytes = 5 * 1024 * 1024;
    private const int DefaultRetainedFiles = 5;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private StreamWriter _writer;
    private bool _disposed;

    public AgentLog(
        string directory,
        long maxBytes = DefaultMaxBytes,
        int retainedFiles = DefaultRetainedFiles)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "codex-control-agent.log");
        _maxBytes = maxBytes;
        _retainedFiles = retainedFiles;
        _writer = OpenWriter();
    }

    public void Info(string eventName, string message) => Write("info", eventName, message, null);

    public void Warning(string eventName, string message) => Write("warning", eventName, message, null);

    public void Error(string eventName, string message, Exception? exception = null) =>
        Write("error", eventName, message, exception);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private void Write(string level, string eventName, string message, Exception? exception)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RotateIfNeeded();

            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow,
                level,
                @event = Sanitize(eventName, 96),
                message = Sanitize(message, 1024),
                exceptionType = exception?.GetType().FullName,
            };

            _writer.WriteLine(JsonSerializer.Serialize(entry));
            _writer.Flush();
        }
    }

    private void RotateIfNeeded()
    {
        if (_writer.BaseStream.Length < _maxBytes)
        {
            return;
        }

        _writer.Dispose();

        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            var destination = $"{_path}.{index + 1}";
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        if (File.Exists(_path))
        {
            File.Move(_path, $"{_path}.1", overwrite: true);
        }

        _writer = OpenWriter();
    }

    private StreamWriter OpenWriter()
    {
        var stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        return new StreamWriter(stream) { AutoFlush = true };
    }

    private static string Sanitize(string value, int maxLength)
    {
        var sanitized = BearerTokenRegex().Replace(value, "$1[REDACTED]");
        sanitized = ApiKeyRegex().Replace(sanitized, "[REDACTED_API_KEY]");
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= maxLength ? sanitized : string.Concat(sanitized.AsSpan(0, maxLength), "...");
    }

    [GeneratedRegex("(?i)(bearer\\s+)[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)\\b(sk-[A-Za-z0-9_-]{12,})\\b")]
    private static partial Regex ApiKeyRegex();
}
