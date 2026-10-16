using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>
/// Sección <c>CodebaseAnalysis:Timeouts</c>: los límites de tiempo del análisis, en minutos. Dependen del tamaño del
/// repositorio y del modelo, así que se ajustan por configuración (fichero, entorno, user secrets o línea de comandos).
/// En todos, 0 (o un valor negativo) quita el límite. El analizador Java tiene los suyos en la sección <c>JavaAnalyzer</c>.
/// </summary>
public sealed record AnalysisTimeouts
{
    public const string SectionName = "CodebaseAnalysis:Timeouts";

    /// <summary>
    /// Mayor límite que admite <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> (<c>uint.MaxValue - 1</c> ms,
    /// unos 49 días), en minutos. Por encima se trata como sin límite en lugar de lanzar al arrancar el paso.
    /// </summary>
    public const int MaxMinutes = (int)((uint.MaxValue - 1) / 60_000);

    /// <summary>
    /// El pipeline entero, que se pasa como <c>pipelineTimeout</c> a <c>IPipeline.ExecuteAsync</c>. Sin él manda el timeout
    /// global del framework (<c>AITaskAgent:Timeouts:DefaultPipelineTimeout</c>, 10 min), que también usan el chat y otros
    /// pipelines y que cortaba los análisis grandes.
    /// </summary>
    public int PipelineMinutes { get; init; } = 240;

    /// <summary>Detección e indexado del grafo (<c>DetectAndIndexGraphStep</c>).</summary>
    public int DetectMinutes { get; init; } = 15;

    /// <summary>Enriquecimiento de <c>DotNetEnricherStep</c>, con su fallback del LLM. El proceso del CLI tiene su propio límite (<c>DotNetEnricher:TimeoutSeconds</c>) y el enriquecimiento Java el suyo.</summary>
    public int EnrichMinutes { get; init; } = 20;

    /// <summary>El paralelo de los auditores entero (<c>ParallelReportsExecution</c>).</summary>
    public int ReportsMinutes { get; init; } = 60;

    /// <summary>Cada auditor, con sus reintentos.</summary>
    public int AuditorMinutes { get; init; } = 15;

    /// <summary>La síntesis ejecutiva (<c>ExecutiveSynthesizerStep</c>).</summary>
    public int SynthesisMinutes { get; init; } = 15;

    /// <summary>La llamada del verificador de un informe, con sus consultas a las tools (<c>VerifyReportStep</c>).</summary>
    public int VerifierMinutes { get; init; } = 10;

    public TimeSpan Pipeline => ToTimeSpan(PipelineMinutes);
    public TimeSpan Detect => ToTimeSpan(DetectMinutes);
    public TimeSpan Enrich => ToTimeSpan(EnrichMinutes);
    public TimeSpan Reports => ToTimeSpan(ReportsMinutes);
    public TimeSpan Auditor => ToTimeSpan(AuditorMinutes);
    public TimeSpan Synthesis => ToTimeSpan(SynthesisMinutes);
    public TimeSpan Verifier => ToTimeSpan(VerifierMinutes);

    /// <summary>
    /// Minutos a <c>Timeout</c> de paso o de pipeline. Sin límite es <see cref="Timeout.InfiniteTimeSpan"/> y no null: con
    /// null el framework aplica su timeout por defecto (de paso o de pipeline).
    /// </summary>
    public static TimeSpan ToTimeSpan(int minutes) =>
        minutes > 0 && minutes <= MaxMinutes ? TimeSpan.FromMinutes(minutes) : Timeout.InfiniteTimeSpan;

    public static AnalysisTimeouts FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new AnalysisTimeouts();
        return new AnalysisTimeouts
        {
            PipelineMinutes = section.GetValue<int?>(nameof(PipelineMinutes)) ?? defaults.PipelineMinutes,
            DetectMinutes = section.GetValue<int?>(nameof(DetectMinutes)) ?? defaults.DetectMinutes,
            EnrichMinutes = section.GetValue<int?>(nameof(EnrichMinutes)) ?? defaults.EnrichMinutes,
            ReportsMinutes = section.GetValue<int?>(nameof(ReportsMinutes)) ?? defaults.ReportsMinutes,
            AuditorMinutes = section.GetValue<int?>(nameof(AuditorMinutes)) ?? defaults.AuditorMinutes,
            SynthesisMinutes = section.GetValue<int?>(nameof(SynthesisMinutes)) ?? defaults.SynthesisMinutes,
            VerifierMinutes = section.GetValue<int?>(nameof(VerifierMinutes)) ?? defaults.VerifierMinutes
        };
    }

    /// <summary>Los de la configuración del <paramref name="serviceProvider"/>, o los de por defecto si no la hay.</summary>
    public static AnalysisTimeouts FromServices(IServiceProvider? serviceProvider) =>
        serviceProvider?.GetService(typeof(IConfiguration)) is IConfiguration configuration
            ? FromConfiguration(configuration)
            : new AnalysisTimeouts();
}
