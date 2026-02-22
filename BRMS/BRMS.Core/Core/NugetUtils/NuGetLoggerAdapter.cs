using Microsoft.Extensions.Logging;
using NuGet.Common;

namespace BRMS.Core.Core.NugetUtils;

// Adaptador para convertir Microsoft.Extensions.Logging.ILogger a NuGet.Common.ILogger
internal class NuGetLoggerAdapter(Microsoft.Extensions.Logging.ILogger? microsoftLogger) : global::NuGet.Common.ILogger
{
    private readonly Microsoft.Extensions.Logging.ILogger? _microsoftLogger = microsoftLogger;//?? throw new ArgumentNullException(nameof(microsoftLogger));

    public void LogDebug(string data)
    {
        if (_microsoftLogger == null)
        {
            Console.WriteLine($"[NugetLogger - Debug: {DateTime.UtcNow}] {data}");
        }
        else
        {
            _microsoftLogger.LogDebug("{Data}", data);
        }
    }
    public void LogVerbose(string data)
    {
        if (_microsoftLogger == null)
        {
            Console.WriteLine($"[NugetLogger - Trace: {DateTime.UtcNow}] {data}");
        }
        else
        {
            _microsoftLogger.LogTrace("{Data}", data);
        }
    }
    public void LogInformation(string data)
    {
        if (_microsoftLogger == null)
        {
            Console.WriteLine($"[NugetLogger - Info: {DateTime.UtcNow}] {data}");
        }
        else
        {
            _microsoftLogger.LogInformation("{Data}", data);
        }
    }
    public void LogMinimal(string data)
    {
        if (_microsoftLogger == null)
        {
            Console.WriteLine($"[NugetLogger - Info: {DateTime.UtcNow}] {data}");
        }
        else
        {
            _microsoftLogger.LogInformation("{Data}", data);
        }
    }
    public void LogWarning(string data)
    {
        if (_microsoftLogger == null)
        {
            Console.WriteLine($"[NugetLogger - Warning: {DateTime.UtcNow}] {data}");
        }
        else
        {
            _microsoftLogger.LogWarning("{Data}", data);
        }
    }
    public void LogError(string data)
    {
        if (_microsoftLogger == null)
        {
            Console.WriteLine($"[NugetLogger - Error: {DateTime.UtcNow}] {data}");
        }
        else
        {
            _microsoftLogger.LogError("{Data}", data);
        }
    }
    public void LogInformationSummary(string data)
    {
        if (_microsoftLogger == null)
        {
            Console.WriteLine($"[NugetLogger - Info: {DateTime.UtcNow}] {data}");
        }
        else
        {
            _microsoftLogger.LogInformation("{Data}", data);
        }
    }
    public void LogErrorSummary(string data)
    {
        if (_microsoftLogger == null)
        {
            Console.WriteLine($"[NugetLogger - Error: {DateTime.UtcNow}] {data}");
        }
        else
        {
            _microsoftLogger.LogError("{Data}", data);
        }
    }

    public void Log(global::NuGet.Common.LogLevel level, string data)
    {
        switch (level)
        {
            case global::NuGet.Common.LogLevel.Debug:
                LogDebug(data);
                break;
            case global::NuGet.Common.LogLevel.Verbose:
                LogVerbose(data);
                break;
            case global::NuGet.Common.LogLevel.Information:
                LogInformation(data);
                break;
            case global::NuGet.Common.LogLevel.Minimal:
                LogMinimal(data);
                break;
            case global::NuGet.Common.LogLevel.Warning:
                LogWarning(data);
                break;
            case global::NuGet.Common.LogLevel.Error:
                LogError(data);
                break;
        }
    }

    public Task LogAsync(global::NuGet.Common.LogLevel level, string data)
    {
        Log(level, data);
        return Task.CompletedTask;
    }

    public void Log(ILogMessage message) => Log(message.Level, message.Message);
    public Task LogAsync(ILogMessage message) => LogAsync(message.Level, message.Message);
}
