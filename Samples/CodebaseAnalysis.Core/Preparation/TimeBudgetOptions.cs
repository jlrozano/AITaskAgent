using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Una curva <c>clamp(base + k · unidades, mín, máx)</c> de las fórmulas del §4.4 de la spec: el tiempo de una pieza
/// (investigación de un subsistema, consolidación de un informe o auditor en modo A) en función de su tamaño. Las
/// unidades son miles de líneas en la investigación y el auditor, y subsistemas elegidos en la consolidación.
/// </summary>
/// <param name="BaseMinutes">Tiempo fijo, en minutos.</param>
/// <param name="MinutesPerUnit">Minutos que suma cada unidad de tamaño.</param>
/// <param name="MinMinutes">Suelo, en minutos.</param>
/// <param name="MaxMinutes">Techo, en minutos; 0 (o negativo) = sin techo.</param>
public readonly record struct TimeBudgetCurve(double BaseMinutes, double MinutesPerUnit, double MinMinutes, double MaxMinutes)
{
    /// <summary>
    /// El tiempo para <paramref name="units"/>. Sin techo manda el tamaño; solo si el resultado desborda lo que admite
    /// <c>CancelAfter</c> (<see cref="Hosting.AnalysisTimeouts.MaxMinutes"/>) pasa a <see cref="Timeout.InfiniteTimeSpan"/>,
    /// como <see cref="Hosting.AnalysisTimeouts.ToTimeSpan"/>.
    /// </summary>
    public TimeSpan Evaluate(double units)
    {
        var minutes = BaseMinutes + MinutesPerUnit * Math.Max(units, 0);
        minutes = Math.Max(minutes, MinMinutes);
        if (MaxMinutes > 0)
            minutes = Math.Min(minutes, MaxMinutes);
        return minutes >= Hosting.AnalysisTimeouts.MaxMinutes ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(minutes);
    }
}

/// <summary>
/// Sección <c>CodebaseAnalysis:TimeBudget</c>: los coeficientes con los que <c>TimeBudgetStep</c> calcula el tiempo de cada
/// pieza en función del tamaño del código (D6). Los valores por defecto son <b>provisionales</b>: no hay todavía datos con
/// que calibrarlos y se fijarán con las mediciones A7 y A8 (pregunta abierta 6 de la spec). Todo en minutos.
/// </summary>
/// <remarks>
/// Un coeficiente inválido (negativo, no numérico, o un techo menor que el suelo) hace que esa curva entera vuelva a su
/// valor por defecto y deja un texto en <see cref="Warnings"/>.
/// </remarks>
public sealed record TimeBudgetOptions
{
    public const string SectionName = "CodebaseAnalysis:TimeBudget";

    /// <summary>Investigación de un subsistema: unidad = miles de líneas de producción del subsistema.</summary>
    public TimeBudgetCurve Investigation { get; init; } = new(BaseMinutes: 4, MinutesPerUnit: 0.1, MinMinutes: 4, MaxMinutes: 20);

    /// <summary>Consolidación de un informe en modo B: unidad = subsistemas elegidos.</summary>
    public TimeBudgetCurve Consolidation { get; init; } = new(BaseMinutes: 5, MinutesPerUnit: 1, MinMinutes: 5, MaxMinutes: 30);

    /// <summary>Auditor en modo A: unidad = miles de líneas de producción del repositorio entero.</summary>
    public TimeBudgetCurve Auditor { get; init; } = new(BaseMinutes: 10, MinutesPerUnit: 0.03, MinMinutes: 10, MaxMinutes: 40);

    /// <summary>Lo que no se pudo leer de la configuración y se sustituyó por su valor por defecto, para avisar. No se serializa: el presupuesto guarda los coeficientes, no sus avisos.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static TimeBudgetOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new TimeBudgetOptions();
        var warnings = new List<string>();
        return new TimeBudgetOptions
        {
            Investigation = Curve(section, nameof(Investigation), defaults.Investigation, warnings),
            Consolidation = Curve(section, nameof(Consolidation), defaults.Consolidation, warnings),
            Auditor = Curve(section, nameof(Auditor), defaults.Auditor, warnings),
            Warnings = warnings
        };
    }

    /// <summary>Las de la configuración del <paramref name="serviceProvider"/>, o las de por defecto si no la hay.</summary>
    public static TimeBudgetOptions FromServices(IServiceProvider? serviceProvider) =>
        serviceProvider?.GetService(typeof(IConfiguration)) is IConfiguration configuration
            ? FromConfiguration(configuration)
            : new TimeBudgetOptions();

    /// <summary>
    /// Cada curva es una subsección (<c>Investigation</c>, <c>Consolidation</c>, <c>Auditor</c>) con las claves
    /// <c>BaseMinutes</c>, <c>MinutesPerUnit</c>, <c>MinMinutes</c> y <c>MaxMinutes</c>; una clave que falta conserva su valor
    /// por defecto.
    /// </summary>
    private static TimeBudgetCurve Curve(IConfigurationSection section, string name, TimeBudgetCurve fallback, List<string> warnings)
    {
        var curve = section.GetSection(name);
        var local = new List<string>();
        var result = new TimeBudgetCurve(
            PreparationOptions.NonNegative(curve, nameof(TimeBudgetCurve.BaseMinutes), fallback.BaseMinutes, local),
            PreparationOptions.NonNegative(curve, nameof(TimeBudgetCurve.MinutesPerUnit), fallback.MinutesPerUnit, local),
            PreparationOptions.NonNegative(curve, nameof(TimeBudgetCurve.MinMinutes), fallback.MinMinutes, local),
            PreparationOptions.NonNegative(curve, nameof(TimeBudgetCurve.MaxMinutes), fallback.MaxMinutes, local));

        if (result.MaxMinutes > 0 && result.MaxMinutes < result.MinMinutes)
            local.Add($"{curve.Path}: el techo ({result.MaxMinutes}) es menor que el suelo ({result.MinMinutes}).");

        if (local.Count == 0)
            return result;

        warnings.AddRange(local.Select(w => w + $" Se usa la curva por defecto de {name}."));
        return fallback;
    }
}
