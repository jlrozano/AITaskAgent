namespace CodebaseAnalysis.Core.Progress;

/// <summary>
/// Un evento del log de un análisis, tal como viaja por SSE (plan §4.3). <c>Seq</c> lo asigna el log de
/// la ejecución al añadirlo; el mapper lo deja a 0.
/// </summary>
/// <remarks>
/// <c>Data</c> es un diccionario con claves ya en camelCase y valores primitivos, listas o diccionarios:
/// nunca lleva objetos del framework (el <c>StepResult</c> de un step, por ejemplo).
/// </remarks>
public sealed record AnalysisEventDto
{
    public long Seq { get; init; }
    public required string RunId { get; init; }
    public DateTimeOffset Ts { get; init; }
    public required string Type { get; init; }
    public string? Source { get; init; }
    public string? Report { get; init; }
    public string? Phase { get; init; }
    public string Level { get; init; } = AnalysisEventLevels.Info;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?>? Data { get; init; }
}

/// <summary>Valores de <see cref="AnalysisEventDto.Level"/>.</summary>
public static class AnalysisEventLevels
{
    public const string Info = "info";
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Error = "error";
}

/// <summary>Valores de <see cref="AnalysisEventDto.Phase"/>.</summary>
public static class AnalysisPhases
{
    public const string Detect = "detect";
    public const string Enrich = "enrich";
    public const string Facets = "facets";
    public const string Reports = "reports";
    public const string Synthesis = "synthesis";
}

/// <summary>Valores de <see cref="AnalysisEventDto.Type"/> (plan §4.3).</summary>
public static class AnalysisEventDtoTypes
{
    public const string RunStarted = "run.started";
    public const string RunCompleted = "run.completed";

    /// <summary>
    /// La ejecución terminada vuelve a correr para regenerar informes, con el mismo <c>runId</c> y el mismo registro de
    /// eventos. <c>data</c>: <c>reports</c> (tipos) y <c>synthesis</c>. Le sigue otro <c>run.completed</c>.
    /// </summary>
    public const string RunRegenerationStarted = "run.regeneration_started";
    public const string PipelineStarted = "pipeline.started";
    public const string PipelineCompleted = "pipeline.completed";
    public const string StepStarted = "step.started";
    public const string StepCompleted = "step.completed";
    public const string StepValidation = "step.validation";
    public const string StepTimeout = "step.timeout";
    public const string BranchStarted = "branch.started";
    public const string BranchCompleted = "branch.completed";
    public const string LlmResponse = "llm.response";
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string Log = "log";
    public const string ReportWritten = "report.written";
    public const string LogTruncated = "log.truncated";
}
