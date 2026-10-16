using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Results;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Globalization;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Paso 6 del pipeline de preparación (spec §4.2): descubre los subsistemas del repositorio. Lee el inventario del contexto
/// (<see cref="AnalysisContextKeys.RepositoryInventory"/>), el grafo de cbm y las entradas, calcula el mapa de dominios de forma
/// determinista (<see cref="DomainMapBuilder"/>: comunidades del grafo, vocabulario y estructura), pide al LLM que nombre los
/// subsistemas con validación en código (<see cref="DomainNaming"/>), escribe <c>reports/_domain_map.json</c>, deja el mapa en el
/// contexto (<see cref="AnalysisContextKeys.DomainMap"/>) y su versión compacta en <see cref="ReportFacetInput.DomainMap"/>.
/// </summary>
/// <remarks>
/// Devuelve el mismo <see cref="ReportFacetInput"/> que recibe, con los campos nuevos rellenos. No falla duro: sin inventario es un
/// aviso y el input sale intacto; si algo se rompe por dentro, también (solo la cancelación se propaga); si el LLM falla o no
/// responde a tiempo, los subsistemas conservan los nombres derivados del código. Sin LLM (<c>namingStep</c> null) siempre
/// salen derivados. Con <c>CodebaseAnalysis:Preparation:Mode = Off</c> no hace nada. El sub-step LLM se invoca sin middleware,
/// como el del verificador, y el límite lo aplica este paso con su propio token. Determinista salvo el nombrado: mismo grafo y
/// mismo inventario, mismos subsistemas, ficheros e identificadores. En la regeneración (<c>reuseSavedMap</c>) reutiliza el mapa del
/// disco si coincide con la huella del grafo.
/// </remarks>
public sealed class DomainDiscoveryStep : IStep<ReportFacetInput, ReportFacetInput>
{
    public const string StepName = "DomainDiscoveryStep";

    /// <summary>Nombre del sub-step LLM que nombra los subsistemas.</summary>
    public const string NamingStepName = "DomainNaming";

    /// <summary>Límite por defecto de cada tanda de nombrado.</summary>
    public static readonly TimeSpan DefaultNamingTimeout = TimeSpan.FromMinutes(5);

    private readonly CustomStatelessTemplateLlmStep<DomainNamingInput, LlmStringStepResult>? _namingStep;
    private readonly TimeSpan _namingTimeout;
    private readonly bool _reuseSavedMap;

    /// <param name="namingStep">El sub-step que nombra los subsistemas (<see cref="CreateNamingStep"/>); null deja los nombres derivados.</param>
    /// <param name="namingTimeout">Límite de cada tanda de nombrado; <see cref="DefaultNamingTimeout"/> si no se indica.</param>
    /// <param name="reuseSavedMap">
    /// La regeneración lo activa: si <c>_domain_map.json</c> está en la carpeta de informes y se calculó sobre el grafo actual
    /// (<see cref="PreparationArtifacts.MatchesGraph"/>), se carga en lugar de recalcularlo y no se vuelve a llamar al LLM para
    /// nombrar. Si falta, no se puede leer o no coincide con el grafo, se recalcula todo.
    /// </param>
    public DomainDiscoveryStep(
        CustomStatelessTemplateLlmStep<DomainNamingInput, LlmStringStepResult>? namingStep = null, TimeSpan? namingTimeout = null, bool reuseSavedMap = false)
    {
        _namingStep = namingStep;
        _namingTimeout = namingTimeout ?? DefaultNamingTimeout;
        _reuseSavedMap = reuseSavedMap;
    }

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 500;

    /// <summary>El cálculo del mapa (segundos) más las tandas de nombrado; lo fija la factoría si hace falta otro.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(20);
    public Type InputType => typeof(ReportFacetInput);
    public Type OutputType => typeof(ReportFacetInput);

    /// <summary>
    /// El sub-step que nombra los subsistemas: rol <c>FunctionalAuditor</c> (para no obligar a configurar uno nuevo), sin
    /// herramientas, con el prompt de sistema propio y no el del rol, que es el del informe funcional.
    /// </summary>
    public static CustomStatelessTemplateLlmStep<DomainNamingInput, LlmStringStepResult> CreateNamingStep(
        ITemplateProvider templateProvider, LlmRole role, TimeSpan? timeout = null) =>
        new(
            name: NamingStepName,
            role: role,
            templateProvider: templateProvider,
            promptTemplateName: "domain_naming_prompt",
            systemPromptTemplateName: "domain_naming_system")
        {
            // Se invoca sin middleware: el límite real lo aplica DomainDiscoveryStep con su propio token.
            Timeout = timeout ?? DefaultNamingTimeout
        };

    public async Task<ReportFacetInput> ExecuteAsync(
        ReportFacetInput input,
        PipelineContext context,
        int attempt,
        ReportFacetInput? lastStepResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = PreparationOptions.FromServices(context.ServiceProvider);
        if (options.Mode == PreparationMode.Off)
            return input;

        var inventory = context.Get<RepositoryInventory>(AnalysisContextKeys.RepositoryInventory);
        if (inventory == null)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                "⚠️ No hay inventario del repositorio en el contexto: no se descubren subsistemas y los informes trabajan sin mapa de dominios.");
            return input;
        }

        var reportsPath = input.OutputReportsPath is { Length: > 0 } fromInput
            ? fromInput
            : context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput)?.OutputReportsPath ?? "";

        if (_reuseSavedMap && TryReuseSavedMap(input, context, inventory, reportsPath))
        {
            await WriteSubsystemGraphAsync(input, context, cancellationToken);
            return input;
        }

        context.Report(StepName, AnalysisLogLevel.Info, "🧭 Descubriendo los subsistemas del repositorio (estructura, grafo y vocabulario)...");
        try
        {
            var map = await DiscoverAsync(input, context, options, inventory, attempt, cancellationToken);

            if (PreparationArtifacts.TryWrite(reportsPath, map) is { } writeWarning)
                context.Report(StepName, AnalysisLogLevel.Warning, writeWarning);

            context.Set(AnalysisContextKeys.DomainMap, map);
            input.DomainMap = DomainMapSummary.ToJson(map);

            foreach (var warning in map.Warnings)
                context.Report(StepName, AnalysisLogLevel.Warning, warning.StartsWith("⚠️", StringComparison.Ordinal) ? warning : $"⚠️ {warning}");
            context.Report(StepName, AnalysisLogLevel.Info,
                $"🧭 Mapa de dominios ({map.Basis}): {map.Subsystems.Count} subsistema(s), " +
                $"{map.Subsystems.Count(s => s.NameOrigin == SubsystemNameOrigins.Llm)} nombrado(s) por el LLM, " +
                $"{map.UnassignedFiles.Count} fichero(s) sin asignar.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ No se pudo construir el mapa de dominios: {ex.Message}. Los pasos siguientes siguen sin él.");
        }

        await WriteSubsystemGraphAsync(input, context, cancellationToken);
        return input;
    }

    /// <summary>
    /// E1: el mapa del contexto, como nodos <c>Subsystem</c> en la copia del grafo (<see cref="Enrichment.AnalysisGraph.SubsystemGraphWriter"/>),
    /// para que el chat lo consulte. Sin mapa o sin BD no hace nada; un fallo es un aviso y el análisis sigue.
    /// </summary>
    private static async Task WriteSubsystemGraphAsync(ReportFacetInput input, PipelineContext context, CancellationToken ct)
    {
        var map = context.Get<DomainMap>(AnalysisContextKeys.DomainMap);
        var databasePath = input.DatabasePath is { Length: > 0 } db ? db : context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput)?.DatabasePath ?? "";
        if (map == null || string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return;

        try
        {
            var stats = await Enrichment.AnalysisGraph.SubsystemGraphWriter.WriteAsync(databasePath, map, ct);
            context.Report(StepName, AnalysisLogLevel.Info,
                $"🧭 Subsistemas en el grafo: {stats.Subsystems} nodo(s), {stats.ContainsMembers + stats.ContainsRoutes + stats.ContainsFiles} CONTAINS, " +
                $"{stats.DependsOn} DEPENDS_ON.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Report(StepName, AnalysisLogLevel.Warning,
                $"⚠️ No se pudieron escribir los subsistemas en el grafo: {ex.Message}. El chat no tendrá la vista 'subsystems'.");
        }
    }

    /// <summary>
    /// El mapa de dominios del disco, si es del grafo actual: lo deja en el contexto y en <see cref="ReportFacetInput.DomainMap"/> como
    /// si lo acabara de calcular, sin tocar el fichero (ni llamar al LLM). False si hay que descubrirlo de nuevo, con el motivo dicho.
    /// </summary>
    private bool TryReuseSavedMap(ReportFacetInput input, PipelineContext context, RepositoryInventory inventory, string reportsPath)
    {
        var saved = PreparationArtifacts.TryRead<DomainMap>(reportsPath, out var readWarning);
        if (saved == null)
        {
            var exists = !string.IsNullOrWhiteSpace(reportsPath) && File.Exists(Path.Combine(reportsPath, DomainMap.FileName));
            context.Report(StepName, exists ? AnalysisLogLevel.Warning : AnalysisLogLevel.Info,
                exists ? $"{readWarning} Se descubren los subsistemas de nuevo." : "ℹ️ No hay mapa de dominios guardado: se descubren los subsistemas.");
            return false;
        }

        if (!PreparationArtifacts.MatchesGraph(saved, inventory.GraphFingerprint))
        {
            context.Report(StepName, AnalysisLogLevel.Info,
                "ℹ️ El mapa de dominios guardado no es del grafo actual (otra indexación, o sin huella con que compararlo): se descubren los subsistemas de nuevo.");
            return false;
        }

        // Un mapa guardado antes del escaneo del SQL de las cadenas (o con otro resultado) recibe ahora las tablas de este inventario; el fichero no se toca.
        saved = DomainMapBuilder.WithSqlTables(saved, context.Get<IReadOnlyList<SqlTableReference>>(AnalysisContextKeys.InventorySqlTables) ?? []);
        context.Set(AnalysisContextKeys.DomainMap, saved);
        input.DomainMap = DomainMapSummary.ToJson(saved);
        context.Report(StepName, AnalysisLogLevel.Info,
            $"♻️ Se reutiliza el mapa de dominios guardado, que es del grafo actual: {saved.Subsystems.Count} subsistema(s), sin volver a nombrarlos con el LLM.");
        return true;
    }

    private async Task<DomainMap> DiscoverAsync(
        ReportFacetInput input, PipelineContext context, PreparationOptions options, RepositoryInventory inventory, int attempt, CancellationToken ct)
    {
        var pipelineInput = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput);
        var databasePath = input.DatabasePath is { Length: > 0 } db ? db : pipelineInput?.DatabasePath ?? "";

        // Solo los ficheros de producción del inventario: los tipos de test, qa y generado no entran en el mapa.
        var production = new HashSet<string>(inventory.ProductionLoc.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var graph = await TypeGraphBuilder.ReadAsync(databasePath, production.Contains, ct);

        var map = DomainMapBuilder.Build(new DomainMapInput
        {
            ProductionFiles = inventory.ProductionLoc.Files,
            Graph = graph,
            // Todas las entradas, no la lista recortada del artefacto: son la señal «entradas» de la puntuación de cada subsistema.
            EntryPoints = context.Get<IReadOnlyList<InventoryEntryPoint>>(AnalysisContextKeys.InventoryEntryPoints) ?? inventory.EntryPoints,
            SqlTables = context.Get<IReadOnlyList<SqlTableReference>>(AnalysisContextKeys.InventorySqlTables) ?? [],
            GraphFingerprint = inventory.GraphFingerprint,
            GeneratedAt = DateTimeOffset.UtcNow
        }, options);

        if (_namingStep == null || map.Subsystems.Count == 0)
            return map;

        var warnings = new List<string>();
        map = await DomainNaming.NameAsync(map, (text, token) => AskAsync(text, context, attempt, token), warnings, ct);
        return warnings.Count == 0 ? map : map with { Warnings = [.. map.Warnings, .. warnings] };
    }

    /// <summary>Una tanda de nombrado: el texto de la respuesta, o una excepción con el motivo si el LLM falla o agota su límite.</summary>
    private async Task<string?> AskAsync(string text, PipelineContext context, int attempt, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_namingTimeout != System.Threading.Timeout.InfiniteTimeSpan)
            limit.CancelAfter(_namingTimeout);

        IStepResult result;
        try
        {
            result = await _namingStep!.ExecuteStepAsync(new DomainNamingInput(text), context, attempt, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"superó el límite de {_namingTimeout.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)} min.");
        }

        if (result.HasError)
            throw new InvalidOperationException(result.Error?.Message ?? "error sin mensaje.");
        if (result is ILlmStepResult { FinishReason: FinishReason.Length })
            throw new InvalidOperationException("la respuesta se cortó al alcanzar MaxOutputTokens.");
        return (result as LlmStringStepResult)?.Content ?? result.Value?.ToString();
    }
}
