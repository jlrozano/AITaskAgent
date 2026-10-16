using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>
/// Sección <c>DotNetEnricher</c>: el enriquecedor Roslyn de soluciones .NET (<c>Samples/DotNetEnricher</c>), que
/// <see cref="Steps.DotNetEnricherStep"/> lanza como proceso aparte. Sustituye a la sección <c>BdpEnricher</c>.
/// </summary>
public sealed record DotNetEnricherOptions
{
    public const string SectionName = "DotNetEnricher";

    public bool Enabled { get; init; } = true;

    /// <summary>Si el enriquecedor no está o falla, el análisis se para en lugar de seguir con el fallback del LLM.</summary>
    public bool Required { get; init; }

    /// <summary>
    /// Ejecutable (<c>.exe</c>) o ensamblado (<c>.dll</c>) del CLI. Vacío = el que el build deja junto a la app, en
    /// <c>dotnet-enricher/</c> o en la propia carpeta de la app si se compiló todo en un <c>OutDir</c> común.
    /// </summary>
    public string CliPath { get; init; } = "";

    /// <summary>Solución, relativa al repositorio. Vacía = el primer <c>.slnx</c>/<c>.sln</c> de la raíz o de sus subcarpetas inmediatas.</summary>
    public string SolutionPath { get; init; } = "";

    /// <summary>Proyecto de cbm en el grafo. Vacío = el mayoritario en <c>nodes</c> (lo resuelve el propio CLI).</summary>
    public string ProjectName { get; init; } = "";

    /// <summary>
    /// Límite del proceso, en segundos; al superarlo se mata con todo su árbol (MSBuild incluido) y el análisis sigue con el
    /// fallback del LLM. 0 = sin límite. Tiene que quedar por debajo de <c>CodebaseAnalysis:Timeouts:EnrichMinutes</c>,
    /// que cubre también el fallback.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 900;

    /// <summary>
    /// <c>auto</c> (por defecto), <c>always</c>/<c>siempre</c> o <c>never</c>/<c>nunca</c>: cuándo restaura el CLI los paquetes
    /// NuGet para que Roslyn resuelva sus tipos. La restauración nunca escribe en el repositorio analizado: va a una caché fuera
    /// de él (<c>.codebase/enrichment/dotnet-restore</c>, junto a la BD).
    /// </summary>
    public DotNetRestoreMode Restore { get; init; } = DotNetRestoreMode.Auto;

    /// <summary>Valor de <c>DotNetEnricher:Restore</c> que no se entiende (se usa <c>auto</c>); null si es válido.</summary>
    public string? InvalidRestoreValue { get; init; }

    /// <summary>Límite de la restauración, en segundos (0 = sin límite propio); al superarlo se mata y el paso sigue sin paquetes. Va dentro de <see cref="TimeoutSeconds"/>.</summary>
    public int RestoreTimeoutSeconds { get; init; } = 300;

    public static DotNetEnricherOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new DotNetEnricherOptions();
        var restoreRaw = section[nameof(Restore)];
        var restoreValid = DotNetRestoreModes.TryParse(restoreRaw, out var restore);
        return new DotNetEnricherOptions
        {
            Enabled = section.GetValue<bool?>(nameof(Enabled)) ?? defaults.Enabled,
            Required = section.GetValue<bool?>(nameof(Required)) ?? defaults.Required,
            CliPath = section[nameof(CliPath)] ?? defaults.CliPath,
            SolutionPath = section[nameof(SolutionPath)] ?? defaults.SolutionPath,
            ProjectName = section[nameof(ProjectName)] ?? defaults.ProjectName,
            TimeoutSeconds = section.GetValue<int?>(nameof(TimeoutSeconds)) ?? defaults.TimeoutSeconds,
            Restore = restore,
            InvalidRestoreValue = restoreValid ? null : restoreRaw,
            RestoreTimeoutSeconds = section.GetValue<int?>(nameof(RestoreTimeoutSeconds)) ?? defaults.RestoreTimeoutSeconds
        };
    }
}
