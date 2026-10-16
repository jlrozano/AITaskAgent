using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>
/// Sección <c>CodebaseAnalysis:SharedPass</c>: la pasada compartida (leer el código una vez por lote del grafo y registrar los
/// hallazgos). Desactivada por defecto. D1 (lotes y paquetes) usa <see cref="MaxBatchTokens"/>, <see cref="MaxFileChars"/> y
/// <see cref="MaxRelationsPerDirection"/>; el rol, los topes de coste y los reintentos son de la lectura (D2) y la consolidación (D3).
/// </summary>
/// <remarks>
/// <para>Un valor que no se puede leer cae al de por defecto: una errata no para el análisis.</para>
/// <para><b>Severidad de D3</b> (<see cref="SeverityRubric"/>): la rúbrica propone una severidad con
/// <c>S = impact × likelihood × exposición</c> (los dos primeros, 1-5, del lector; la exposición, 1-5, del grafo: 5 punto de
/// entrada, 4 alcanzable desde uno o fan-in transitivo ≥ 20, 3 fan-in transitivo ≥ 5 o API pública sin llamadores internos o sin
/// nodo en el grafo, 2 fan-in transitivo ≥ 1, 1 sin alcanzar (<c>is_reachable = false</c>) o privado sin llamadores; +1 si el
/// método toca APIs sensibles en un hallazgo de seguridad o tiene marcas de concurrencia en uno de concurrencia; tope 5).
/// P0 si <c>S ≥ 60</c> e impacto ≥ 4; P1 si <c>S ≥ 36</c> e impacto ≥ 3; P2 si <c>S ≥ 18</c>; si no, P3. Topes: una activación
/// que exige configuración no por defecto o uso erróneo de la API, como mucho P2; la deuda, como mucho P2 (P3 con impacto ≤ 2).
/// Después, el juez (<see cref="JudgeRole"/>) revisa por tandas de <see cref="JudgeBatchSize"/> los P0/P1, la seguridad y lo de
/// confianza menor que <see cref="JudgeConfidenceThreshold"/>, con <see cref="JudgeContextLines"/> líneas de código alrededor del
/// ancla.</para>
/// </remarks>
public sealed record SharedPassOptions
{
    public const string SectionName = "CodebaseAnalysis:SharedPass";

    public bool Enabled { get; init; }

    /// <summary>
    /// Presupuesto de un lote en tokens estimados (caracteres / 4) de su paquete entero: cabecera, hechos del grafo, relaciones y
    /// código numerado. Un subsistema que lo supera se parte (<see cref="BatchPlanner"/>). 0 o menos: sin límite.
    /// </summary>
    public int MaxBatchTokens { get; init; } = 50_000;

    /// <summary>Rol LLM de la lectura por lote (sección <c>LlmProviders:Roles</c>).</summary>
    public string ReaderRole { get; init; } = DefaultReaderRole;

    /// <summary>El rol de la lectura por lote que trae <c>appsettings.analysis.json</c>.</summary>
    public const string DefaultReaderRole = "SharedPassReader";

    /// <summary>Rol LLM del juez de la consolidación (D3).</summary>
    public string JudgeRole { get; init; } = DefaultJudgeRole;

    /// <summary>El rol del juez que trae <c>appsettings.analysis.json</c>.</summary>
    public const string DefaultJudgeRole = "SharedPassJudge";

    /// <summary>Candidatos que el juez compara en una llamada. 1 o menos: de uno en uno.</summary>
    public int JudgeBatchSize { get; init; } = 20;

    /// <summary>Líneas de código antes y después del ancla que recibe el juez por candidato.</summary>
    public int JudgeContextLines { get; init; } = 15;

    /// <summary>Por debajo de esta confianza del lector, el hallazgo va al juez aunque su severidad sea P2 o P3.</summary>
    public double JudgeConfidenceThreshold { get; init; } = 0.6;

    /// <summary>
    /// Lotes que se leen a la vez (todos, sin esperar por niveles; también las tandas del juez). 1 o menos: de uno en uno.
    /// </summary>
    public int MaxParallelBatches { get; init; } = 4;

    /// <summary>Tope de coste de la lectura de un lote (o de una tanda del juez), en USD; al superarlo se corta. 0 o menos: sin tope.</summary>
    public double MaxCostPerBatchUsd { get; init; } = 2;

    /// <summary>Tope de coste de una etapa entera (lectura, consolidación), en USD. 0 o menos: sin tope.</summary>
    public double MaxCostPerStageUsd { get; init; } = 10;

    /// <summary>Tope de coste de la pasada compartida en una ejecución, en USD. 0 o menos: sin tope.</summary>
    public double MaxCostPerRunUsd { get; init; } = 25;

    /// <summary>
    /// Plazo de la pasada entera (lectura y juez), en minutos. Al vencer se cortan las llamadas en curso, no se hace ninguna más y se
    /// guarda lo que haya (lotes leídos, veredictos del juez) con aviso; la ejecución sigue con los informes. 0 o menos: sin plazo
    /// (queda el del pipeline, que sí la para en seco).
    /// </summary>
    public double MaxMinutes { get; init; } = 90;

    /// <summary>
    /// Tope de llamadas al LLM de la pasada en una ejecución (lectura y juez). Cuenta aunque el modelo no tenga precio: es la defensa
    /// cuando el coste no se conoce. 0 o menos: sin tope.
    /// </summary>
    public int MaxCallsPerRun { get; init; } = 1_000;

    /// <summary>
    /// Tope de tokens (entrada y salida, los que informa el proveedor o, en una llamada cortada, los enviados estimados) de la pasada
    /// en una ejecución. Como <see cref="MaxCallsPerRun"/>, cuenta sin precio. 0 o menos: sin tope.
    /// </summary>
    public long MaxTokensPerRun { get; init; } = 50_000_000;

    /// <summary>Reintentos de lo que falla en la validación de la lectura de un lote antes de partirlo.</summary>
    public int MaxReaderRetries { get; init; } = 2;

    /// <summary>
    /// Caracteres de código de un fichero que entran en el paquete; el resto se omite con aviso. 0 o menos: sin tope.
    /// </summary>
    public int MaxFileChars { get; init; } = 200_000;

    /// <summary>Relaciones entrantes y, aparte, salientes que lista un paquete; el resto se resume con su número.</summary>
    public int MaxRelationsPerDirection { get; init; } = 80;

    public static SharedPassOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new SharedPassOptions();
        return new SharedPassOptions
        {
            Enabled = Bool(section, nameof(Enabled), defaults.Enabled),
            MaxBatchTokens = Int(section, nameof(MaxBatchTokens), defaults.MaxBatchTokens),
            ReaderRole = section[nameof(ReaderRole)] is { Length: > 0 } role && role.Trim().Length > 0 ? role.Trim() : defaults.ReaderRole,
            JudgeRole = section[nameof(JudgeRole)] is { Length: > 0 } judge && judge.Trim().Length > 0 ? judge.Trim() : defaults.JudgeRole,
            JudgeBatchSize = Math.Max(1, Int(section, nameof(JudgeBatchSize), defaults.JudgeBatchSize)),
            JudgeContextLines = Math.Max(0, Int(section, nameof(JudgeContextLines), defaults.JudgeContextLines)),
            JudgeConfidenceThreshold = Double(section, nameof(JudgeConfidenceThreshold), defaults.JudgeConfidenceThreshold),
            MaxCostPerBatchUsd = Double(section, nameof(MaxCostPerBatchUsd), defaults.MaxCostPerBatchUsd),
            MaxCostPerStageUsd = Double(section, nameof(MaxCostPerStageUsd), defaults.MaxCostPerStageUsd),
            MaxCostPerRunUsd = Double(section, nameof(MaxCostPerRunUsd), defaults.MaxCostPerRunUsd),
            MaxMinutes = Double(section, nameof(MaxMinutes), defaults.MaxMinutes),
            MaxCallsPerRun = Int(section, nameof(MaxCallsPerRun), defaults.MaxCallsPerRun),
            MaxTokensPerRun = Long(section, nameof(MaxTokensPerRun), defaults.MaxTokensPerRun),
            MaxReaderRetries = Math.Max(0, Int(section, nameof(MaxReaderRetries), defaults.MaxReaderRetries)),
            MaxParallelBatches = Math.Max(1, Int(section, nameof(MaxParallelBatches), defaults.MaxParallelBatches)),
            MaxFileChars = Int(section, nameof(MaxFileChars), defaults.MaxFileChars),
            MaxRelationsPerDirection = Math.Max(0, Int(section, nameof(MaxRelationsPerDirection), defaults.MaxRelationsPerDirection))
        };
    }

    /// <summary>Las de la configuración del <paramref name="serviceProvider"/>, o las de por defecto si no la hay.</summary>
    public static SharedPassOptions FromServices(IServiceProvider? serviceProvider) =>
        serviceProvider?.GetService(typeof(IConfiguration)) is IConfiguration configuration
            ? FromConfiguration(configuration)
            : new SharedPassOptions();

    private static int Int(IConfigurationSection section, string key, int fallback) =>
        section[key] is { Length: > 0 } raw
        && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static long Long(IConfigurationSection section, string key, long fallback) =>
        section[key] is { Length: > 0 } raw
        && long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double Double(IConfigurationSection section, string key, double fallback) =>
        section[key] is { Length: > 0 } raw
        && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;

    private static bool Bool(IConfigurationSection section, string key, bool fallback) =>
        section[key] is { Length: > 0 } raw && bool.TryParse(raw.Trim(), out var value) ? value : fallback;
}
