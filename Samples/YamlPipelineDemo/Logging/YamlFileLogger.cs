using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace YamlPipelineDemo.Logging;

/// <summary>
/// Logger provider that writes structured YAML log entries to a daily-rotating file.
/// Each log entry is emitted as a YAML document separator + block, making the file
/// a valid multi-document YAML stream.
/// </summary>
public sealed class YamlFileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDirectory;
    private readonly LogLevel _minimumLevel;
    private readonly ConcurrentDictionary<string, YamlFileLogger> _loggers = new();
    private readonly object _writeLock = new();
    private StreamWriter? _writer;
    private string _currentFilePath = string.Empty;

    public YamlFileLoggerProvider(string logsDirectory, LogLevel minimumLevel = LogLevel.Debug)
    {
        _logsDirectory = logsDirectory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(logsDirectory);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new YamlFileLogger(name, this));

    internal bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (level < _minimumLevel) return;

        lock (_writeLock)
        {
            EnsureWriter();

            // YAML multi-document separator
            _writer!.WriteLine("---");
            _writer.WriteLine($"timestamp: \"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fffzzz}\"");
            _writer.WriteLine($"level: {level}");
            _writer.WriteLine($"category: \"{EscapeYaml(category)}\"");

            if (eventId.Id != 0 || !string.IsNullOrEmpty(eventId.Name))
                _writer.WriteLine($"eventId: {{ id: {eventId.Id}, name: \"{EscapeYaml(eventId.Name ?? "")}\" }}");

            _writer.WriteLine($"message: \"{EscapeYaml(message)}\"");

            if (exception != null)
            {
                _writer.WriteLine("exception:");
                _writer.WriteLine($"  type: \"{EscapeYaml(exception.GetType().FullName ?? exception.GetType().Name)}\"");
                _writer.WriteLine($"  message: \"{EscapeYaml(exception.Message)}\"");
                if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                {
                    _writer.WriteLine("  stackTrace: |");
                    foreach (var line in exception.StackTrace.Split('\n'))
                        _writer.WriteLine($"    {line.TrimEnd()}");
                }
            }

            _writer.Flush();
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_logsDirectory, $"{today}.yaml");

        if (_currentFilePath == filePath && _writer != null) return;

        _writer?.Dispose();
        _writer = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8);
        _currentFilePath = filePath;
    }

    private static string EscapeYaml(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>
/// Individual logger that delegates writes to the shared provider (and its file writer).
/// </summary>
internal sealed class YamlFileLogger(string category, YamlFileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
    }
}
