using Microsoft.Extensions.Logging;

namespace GeminiTestApp;

/// <summary>
/// Minimal file-backed logging provider.
///
/// The demos stream model output straight to the console, so console logging would
/// interleave framework messages into the middle of a half-written answer. Logs go
/// to a file instead and the console is left for the model.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public FileLoggerProvider(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        FilePath = path;
    }

    /// <summary>Full path of the file receiving the logs.</summary>
    public string FilePath { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _writer.Dispose();

    private void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            provider.Write($"{timestamp} [{logLevel}] {category}: {formatter(state, exception)}");

            if (exception is not null)
            {
                provider.Write(exception.ToString());
            }
        }
    }
}
