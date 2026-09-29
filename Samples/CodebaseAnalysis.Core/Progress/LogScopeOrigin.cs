using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Paso que emite una línea de log, sacado de los scopes que abre el pipeline del framework: uno por paso, con
/// <c>{ Path, Step, CorrelationId }</c> (<c>Framework/Core/Execution/Pipeline.cs</c>), también en cada rama de un paralelo. Con
/// Serilog llegan como el texto del objeto (<c>"{ Path = …, Step = Auditor_CodeSmells, CorrelationId = … }"</c>), del más
/// externo al más interno. Sirve para que los mensajes de las tools («Executing Tool: view_file…») digan qué auditor las llama.
/// </summary>
public static partial class LogScopeOrigin
{
    /// <summary>El <c>Step</c> del scope más interno que lo tenga; null si ninguno lo tiene.</summary>
    public static string? InnermostStep(IEnumerable<string?> scopes) =>
        scopes.Select(StepOf).LastOrDefault(step => step != null);

    private static string? StepOf(string? scope)
    {
        if (string.IsNullOrEmpty(scope))
            return null;
        var match = StepPattern().Match(scope);
        return match.Success && match.Groups["step"].Value.Trim() is { Length: > 0 } step ? step : null;
    }

    [GeneratedRegex(@"(?:^|[{,]\s*)Step = (?<step>[^,}]*)")]
    private static partial Regex StepPattern();
}
