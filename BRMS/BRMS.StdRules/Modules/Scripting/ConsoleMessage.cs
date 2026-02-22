using Microsoft.Extensions.Logging;

namespace BRMS.StdRules.Modules.Scripting;

public class ConsoleMessage
{
    public LogLevel Level { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTime Date { get; set; } = DateTime.UtcNow;
}
