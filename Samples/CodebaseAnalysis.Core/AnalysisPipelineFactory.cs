using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Results;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace CodebaseAnalysis.Core;

public static class AnalysisPipelineFactory
{
    /// <summary>Nombre del step que ejecuta los auditores en paralelo.</summary>
    public const string ParallelReportsStepName = "ParallelReportsExecution";

    /// <summary>Sub-step LLM del enriquecimiento semántico (fase <c>enrich</c>); se invoca sin middleware.</summary>
    public const string SemanticEnricherStepName = "SemanticEnricherLlm";

    /// <summary>Sub-step LLM de la síntesis ejecutiva (fase <c>synthesis</c>); se invoca sin middleware.</summary>
    public const string SynthesizerStepName = "SynthesizerLlm";

    private const string AuditorStepPrefix = "Auditor_";

    /// <summary>Nombre del step de un auditor: <c>Auditor_&lt;ReportType&gt;</c>.</summary>
    public static string AuditorStepName(ReportType type) => $"{AuditorStepPrefix}{type}";

    /// <summary>Inverso de <see cref="AuditorStepName"/>; false si el nombre no es el de un auditor.</summary>
    public static bool TryParseAuditorStepName(string stepName, out ReportType type)
    {
        type = default;
        return stepName.StartsWith(AuditorStepPrefix, StringComparison.Ordinal)
            && Enum.TryParse(stepName[AuditorStepPrefix.Length..], out type);
    }

    /// <summary>
    /// Las plantillas viven en la carpeta <c>templates/</c> del proyecto y el .csproj las copia junto
    /// al ejecutable: esa es la única ubicación.
    /// </summary>
    public static ITemplateProvider CreateDefaultTemplateProvider(string? customTemplatesDir = null)
    {
        var templatesDir = Path.GetFullPath(customTemplatesDir ?? Path.Combine(AppContext.BaseDirectory, "templates"));
        if (!Directory.Exists(templatesDir))
        {
            throw new DirectoryNotFoundException($"No existe la carpeta de plantillas '{templatesDir}'.");
        }

        return new AnalysisTemplateProvider(templatesDir);
    }

    /// <summary>
    /// <see cref="LanguageEnricherStep"/> con su límite leído de la configuración del <paramref name="serviceProvider"/>
    /// (<c>JavaAnalyzer:StepTimeoutSeconds</c>), o con el de por defecto si no hay configuración.
    /// </summary>
    internal static LanguageEnricherStep CreateLanguageEnricherStep(IServiceProvider? serviceProvider) =>
        new(serviceProvider?.GetService(typeof(IConfiguration)) is IConfiguration configuration
            ? JavaAnalyzerOptions.FromConfiguration(configuration)
            : new JavaAnalyzerOptions());

    /// <summary>
    /// El pipeline completo. Los límites de tiempo salen de <paramref name="timeouts"/> o, sin él, de
    /// <c>CodebaseAnalysis:Timeouts</c> en la configuración del <paramref name="serviceProvider"/>.
    /// </summary>
    public static CodebaseAnalysisPipeline CreatePipeline(
        ITemplateProvider templateProvider,
        Dictionary<string, LlmRole> roles,
        AnalysisPipelineInput input,
        IServiceProvider? serviceProvider = null,
        IPipeline? engine = null,
        AnalysisTimeouts? timeouts = null)
    {
        timeouts ??= AnalysisTimeouts.FromServices(serviceProvider);

        // 1. Detección e Indexación de Grafo
        var detectStep = new DetectAndIndexGraphStep { Timeout = timeouts.Detect };

        // 2. Enriquecimiento nativo por lenguaje (analizador Java y plugin Struts/JSP), en la fase enrich
        var languageEnricherStep = CreateLanguageEnricherStep(serviceProvider);

        // 3. Enriquecimiento Semántico Supervisado (Roslyn CLI con fallback a LLM), también en la fase enrich
        var enricherRole = ResolveRole(roles, "Enricher");
        var enricherLlmStep = new CustomStatelessTemplateLlmStep<EnrichmentBatchInput, LlmStringStepResult>(
            name: SemanticEnricherStepName,
            role: enricherRole,
            templateProvider: templateProvider,
            promptTemplateName: "enricher_prompt",
            systemPromptTemplateName: enricherRole.SystemPrompt ?? "enricher_system"
        );
        var semanticEnrichmentStep = new SemanticEnrichmentStep(enricherLlmStep);
        var bdpEnricherStep = new BdpEnricherStep(semanticEnrichmentStep) { Timeout = timeouts.Enrich };

        // 4. Pre-extracción de Facetas SQL
        var facetExtractorStep = new SqlFacetExtractorStep();

        // 5. Construcción Dinámica del ParallelStep
        var requestedList = input.RequestedReports.Count > 0
            ? input.RequestedReports
            : Enum.GetValues<ReportType>().ToList();
        var parallelReportsStep = CreateReportsStep(templateProvider, roles, input, requestedList, timeouts);

        // 6. Agregador y Resumen Ejecutivo
        var executiveSynthesizerStep = CreateSynthesisStep(templateProvider, roles, timeouts);

        // Enlace secuencial de los 6 pasos (5 fases: los dos enriquecedores comparten la fase enrich)
        return new CodebaseAnalysisPipeline(
            ResolveEngine(serviceProvider, engine),
            [
                detectStep,
                languageEnricherStep,
                bdpEnricherStep,
                facetExtractorStep,
                parallelReportsStep,
                executiveSynthesizerStep
            ],
            timeouts.Pipeline);
    }

    /// <summary>Nombre del pipeline de regeneración en los eventos (<c>pipeline.started</c>).</summary>
    public const string RegenerationPipelineName = "CodebaseAnalysisRegeneration";

    /// <summary>
    /// Pipeline reducido que vuelve a generar los informes de <c>input.RequestedReports</c> sobre el grafo que ya existe:
    /// <see cref="PrepareRegenerationStep"/>, <see cref="SqlFacetExtractorStep"/>, los auditores pedidos y, con
    /// <paramref name="synthesis"/>, la síntesis ejecutiva. No detecta, indexa ni enriquece. Las facetas se recalculan porque
    /// solo viven en memoria y son consultas SQL de segundos.
    /// </summary>
    /// <exception cref="ArgumentException">Si <c>input.RequestedReports</c> está vacío.</exception>
    public static CodebaseAnalysisPipeline CreateRegenerationPipeline(
        ITemplateProvider templateProvider,
        Dictionary<string, LlmRole> roles,
        AnalysisPipelineInput input,
        bool synthesis,
        IServiceProvider? serviceProvider = null,
        IPipeline? engine = null,
        AnalysisTimeouts? timeouts = null)
    {
        if (input.RequestedReports.Count == 0)
            throw new ArgumentException("La regeneración necesita al menos un informe.", nameof(input));

        timeouts ??= AnalysisTimeouts.FromServices(serviceProvider);

        List<IStep> steps =
        [
            new PrepareRegenerationStep(),
            new SqlFacetExtractorStep(),
            CreateReportsStep(templateProvider, roles, input, input.RequestedReports, timeouts)
        ];
        if (synthesis)
            steps.Add(CreateSynthesisStep(templateProvider, roles, timeouts));

        return new CodebaseAnalysisPipeline(ResolveEngine(serviceProvider, engine), steps, timeouts.Pipeline, RegenerationPipelineName);
    }

    private static IPipeline ResolveEngine(IServiceProvider? serviceProvider, IPipeline? engine) =>
        engine ?? (serviceProvider?.GetService(typeof(IPipeline)) as IPipeline) ?? new Pipeline();

    /// <summary>
    /// El rol pedido, o un error que lo nombra. Antes devolvía el primero configurado, y un auditor podía acabar con el
    /// prompt de sistema y el modelo de otro rol sin aviso (auditoría, H-11). No rompe ninguna configuración válida:
    /// <see cref="Hosting.LlmRoleFactory.BuildRoles"/> crea con el proveedor por defecto los
    /// <see cref="Hosting.LlmRoleFactory.RequiredRoles"/> que falten, y la CLI y la web construyen los roles con él.
    /// </summary>
    internal static LlmRole ResolveRole(Dictionary<string, LlmRole> roles, string roleName)
    {
        if (roles.TryGetValue(roleName, out var role))
            return role;

        throw new InvalidOperationException(
            $"No hay rol LLM '{roleName}' (roles recibidos: {(roles.Count == 0 ? "ninguno" : string.Join(", ", roles.Keys.Order(StringComparer.OrdinalIgnoreCase)))}). "
            + $"Decláralo en LlmConfiguration:Roles o construye los roles con LlmRoleFactory.BuildRoles, que crea los que faltan "
            + $"con el proveedor por defecto.");
    }

    /// <summary>El paralelo tolerante con un auditor por informe, cada uno con su límite.</summary>
    private static TolerantReportsParallelStep CreateReportsStep(
        ITemplateProvider templateProvider,
        Dictionary<string, LlmRole> roles,
        AnalysisPipelineInput input,
        IEnumerable<ReportType> reports,
        AnalysisTimeouts timeouts)
    {
        var parallelStepsList = new List<IStep>();
        foreach (var reportType in reports)
        {
            var roleName = AnalysisPipelineInput.GetRoleName(reportType);
            var templatePrefix = AnalysisPipelineInput.GetTemplatePrefix(reportType);

            parallelStepsList.Add(CreateReportStep(
                templateProvider: templateProvider,
                role: ResolveRole(roles, roleName),
                templatePrefix: templatePrefix,
                type: reportType,
                maxToolIterations: input.GetAuditorMaxToolIterations(roleName),
                timeout: timeouts.Auditor));
        }

        return new TolerantReportsParallelStep(ParallelReportsStepName, parallelStepsList.ToArray())
        {
            Timeout = timeouts.Reports,
            // CodebaseAnalysis:Reports:MaxParallelAuditors; 0 = sin tope propio (uno por procesador, lo de siempre).
            MaxDegreeOfParallelism = input.MaxParallelAuditors
        };
    }

    private static ExecutiveSynthesizerStep CreateSynthesisStep(
        ITemplateProvider templateProvider,
        Dictionary<string, LlmRole> roles,
        AnalysisTimeouts timeouts)
    {
        var synthRole = ResolveRole(roles, "ExecutiveSynthesizer");
        // El sub-step se invoca sin middleware, así que su Timeout no corta nada; se deja igual que el del paso.
        var synthesizerLlmStep = new CustomStatelessTemplateLlmStep<SynthesisInput, LlmStringStepResult>(
            name: SynthesizerStepName,
            role: synthRole,
            templateProvider: templateProvider,
            promptTemplateName: "report_sintesis_prompt",
            systemPromptTemplateName: synthRole.SystemPrompt ?? "report_sintesis_system"
        )
        {
            Timeout = timeouts.Synthesis
        };
        return new ExecutiveSynthesizerStep(synthesizerLlmStep) { Timeout = timeouts.Synthesis };
    }

    /// <summary>
    /// Mensaje de la última llamada, sin tools, de un auditor que agota su tope de iteraciones: en lugar de fallar
    /// («Maximum tool iterations»), redacta el informe con lo que ya ha consultado.
    /// </summary>
    public const string AuditorToolLimitPrompt =
        "Has agotado las consultas a herramientas disponibles para este informe. No pidas más herramientas: redacta ya el " +
        "informe completo con la información que has reunido, con la estructura pedida y su línea analysis-meta al final, " +
        "y señala en él lo que no hayas podido verificar.";

    /// <summary>
    /// Tools de cada auditor: el código (<c>view_file</c>, <c>view_code_item</c>), el grafo (<c>graph_search</c>, los vecinos de
    /// un nodo con la procedencia de cada arista en <c>graph_neighbors</c>, y rutas, DI y atributos en <c>graph_domain</c>) y
    /// la documentación aportada. <c>graph_map</c>, <c>graph_health</c> y <c>graph_flows</c> no están: ninguna plantilla las
    /// usa. Cómo usar el grafo lo dice <c>templates/_graph_rules.md</c>.
    /// </summary>
    public static IReadOnlyList<string> AuditorToolNames { get; } =
    [
        "view_file", "view_code_item", "graph_search", Tools.GraphNeighborsTool.ToolName, "graph_domain", Tools.ReadDocumentTool.ToolName
    ];

    private static IStep CreateReportStep(
        ITemplateProvider templateProvider,
        LlmRole role,
        string templatePrefix,
        ReportType type,
        int maxToolIterations,
        TimeSpan timeout)
    {
        return new CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult>(
            name: AuditorStepName(type),
            role: role,
            templateProvider: templateProvider,
            promptTemplateName: $"{templatePrefix}_prompt",
            systemPromptTemplateName: role.SystemPrompt ?? $"{templatePrefix}_system",
            reportType: type,
            outputFileName: AnalysisPipelineInput.GetReportFileName(type),
            tools: [.. AuditorToolNames]
        )
        {
            Timeout = timeout,
            MaxToolIterations = maxToolIterations,
            ToolLimitFinalAnswerPrompt = AuditorToolLimitPrompt
        };
    }
}

public class CodebaseAnalysisPipeline
{
    public const string DefaultName = "CodebaseAnalysisPipeline";

    private readonly IPipeline _engine;
    private readonly List<IStep> _steps;

    /// <summary>
    /// <paramref name="pipelineTimeout"/> es el límite del pipeline entero (<c>CodebaseAnalysis:Timeouts:PipelineMinutes</c>).
    /// Sin límite es <see cref="Timeout.InfiniteTimeSpan"/>; null deja el timeout por defecto del motor, que es global al
    /// framework.
    /// </summary>
    public CodebaseAnalysisPipeline(IPipeline engine, List<IStep> steps, TimeSpan? pipelineTimeout = null, string name = DefaultName)
    {
        _engine = engine;
        _steps = steps;
        PipelineTimeout = pipelineTimeout;
        Name = name;
    }

    public string Name { get; }

    /// <summary>Pasos de primer nivel, en orden.</summary>
    public IReadOnlyList<IStep> Steps => _steps;

    public TimeSpan? PipelineTimeout { get; }

    /// <summary>Error del run que supera <paramref name="limit"/>: dice el límite y la clave que lo controla.</summary>
    public static string TimeoutMessage(TimeSpan limit) =>
        $"El análisis superó el límite de {limit.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)} min " +
        $"({AnalysisTimeouts.SectionName}:{nameof(AnalysisTimeouts.PipelineMinutes)}); súbelo o pon 0 para quitarlo.";

    /// <summary>
    /// Un <see cref="PipelineTimeout"/> finito lo aplica este wrapper con su propio token, y al motor le llega sin límite.
    /// Así el timeout se distingue de la cancelación del llamante por el token que ha saltado, sin comparar tiempos, y el
    /// error dice qué límite se ha superado: el del motor sale como «A task was canceled.».
    /// </summary>
    public async Task<IStepResult> ExecuteAsync(AnalysisPipelineInput input, PipelineContext context, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = PipelineTimeout is { } limit && limit != Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource(limit)
            : null;
        using var linkedCts = timeoutCts != null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
            : null;
        bool TimedOut() => timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested;

        // Se informa en cuanto vence y no al volver del motor, que para entonces ya ha cerrado el canal de eventos.
        using var onTimeout = (timeoutCts?.Token ?? CancellationToken.None).Register(() =>
        {
            if (TimedOut())
                context.Report(Name, AnalysisLogLevel.Error, $"❌ {TimeoutMessage(PipelineTimeout!.Value)}");
        });

        try
        {
            var result = await _engine.ExecuteAsync(Name, _steps, input, context,
                timeoutCts != null ? Timeout.InfiniteTimeSpan : PipelineTimeout, linkedCts?.Token ?? cancellationToken);
            return result.HasError && TimedOut() ? ErrorStepResult.FromMessage(new EmptyStep(), TimeoutMessage(PipelineTimeout!.Value)) : result;
        }
        catch (Exception) when (TimedOut())
        {
            return ErrorStepResult.FromMessage(new EmptyStep(), TimeoutMessage(PipelineTimeout!.Value));
        }
        finally
        {
            await RestoreRetiredCatalogReportsAsync(input, context);
        }
    }

    /// <summary>
    /// Plan v2 §8.1.4: la detección retira del grafo los informes del catálogo marcados y la síntesis los reincorpora.
    /// Si el run no llega a la síntesis (fallo o cancelación), vuelven aquí con el contenido que haya en disco, para que
    /// no queden fuera del grafo hasta el siguiente análisis. Sin cancelación (el run ya terminó) y sin lanzar.
    /// </summary>
    private static async Task RestoreRetiredCatalogReportsAsync(AnalysisPipelineInput input, PipelineContext context)
    {
        if (context.GetMetadata(AnalysisContextKeys.CatalogReportsRetired) is not true)
            return;

        try
        {
            var paths = new Knowledge.KnowledgePaths(input.RepositoryPath, input.OutputReportsPath);
            var restored = await Knowledge.Graph.KnowledgeGraphIngestor.ReincorporateAsync(input.DatabasePath, paths,
                new HashSet<Knowledge.SourceKind> { Knowledge.SourceKind.Report }, CancellationToken.None);
            context.SetMetadata(AnalysisContextKeys.CatalogReportsRetired, false);
            if (restored > 0)
            {
                context.Report(ExecutiveSynthesizerStep.StepName, AnalysisLogLevel.Info,
                    $"🧩 Reincorporados al grafo {restored} informes del análisis sin llegar a la síntesis.");
            }
        }
        catch (Exception ex)
        {
            context.Report(ExecutiveSynthesizerStep.StepName, AnalysisLogLevel.Warning,
                $"⚠️ No se pudieron reincorporar al grafo los informes del análisis: {ex.Message}");
        }
    }
}
