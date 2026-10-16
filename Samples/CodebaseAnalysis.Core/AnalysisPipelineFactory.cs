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
using CodebaseAnalysis.Core.Notes;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Extensions.Configuration;
using OpenAILLmService;
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

    private const string InvestigationStepPrefix = "Investigation_";

    /// <summary>
    /// Nombre del sub-step LLM que investiga un subsistema para un informe en modo B: <c>Investigation_&lt;ReportType&gt;_&lt;subsistemaId&gt;</c>.
    /// El envoltorio del informe y su consolidación se siguen llamando <see cref="AuditorStepName"/>: cuentan en el mismo carril.
    /// </summary>
    /// <exception cref="ArgumentException">Si <paramref name="subsystemId"/> está vacío.</exception>
    public static string InvestigationStepName(ReportType type, string subsystemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystemId);
        return $"{InvestigationStepPrefix}{type}_{subsystemId}";
    }

    /// <summary>
    /// Inverso de <see cref="InvestigationStepName"/>; false si el nombre no es el de una investigación (<paramref name="subsystemId"/> queda
    /// vacío). El tipo de informe es el tramo hasta el primer <c>_</c> (los nombres del enumerado no llevan ninguno), así que el
    /// identificador del subsistema puede llevarlo.
    /// </summary>
    public static bool TryParseInvestigationStepName(string stepName, out ReportType type, out string subsystemId)
    {
        type = default;
        subsystemId = string.Empty;
        if (!stepName.StartsWith(InvestigationStepPrefix, StringComparison.Ordinal))
            return false;

        var rest = stepName[InvestigationStepPrefix.Length..];
        var separator = rest.IndexOf('_');
        if (separator <= 0 || separator == rest.Length - 1)
            return false;
        if (!Enum.TryParse(rest[..separator], out type) || !Enum.IsDefined(type))
        {
            type = default;
            return false;
        }

        subsystemId = rest[(separator + 1)..];
        return true;
    }

    private const string VerifyStepPrefix = "Verify_";
    private const string VerifierStepPrefix = "Verifier_";

    /// <summary>Nombre del paralelo de los verificadores (no es un paso de fase: la verificación no mueve el avance del análisis).</summary>
    public const string ParallelVerificationStepName = "ParallelVerificationExecution";

    /// <summary>Nombre del pipeline de verificación en los eventos (<c>pipeline.started</c>).</summary>
    public const string VerificationPipelineName = "CodebaseAnalysisVerification";

    /// <summary>Nombre del paso que verifica un informe: <c>Verify_&lt;ReportType&gt;</c>.</summary>
    public static string VerifyStepName(ReportType type) => $"{VerifyStepPrefix}{type}";

    /// <summary>Nombre del sub-step LLM del verificador de un informe: <c>Verifier_&lt;ReportType&gt;</c>.</summary>
    public static string VerifierLlmStepName(ReportType type) => $"{VerifierStepPrefix}{type}";

    /// <summary>Inverso de <see cref="VerifyStepName"/> y de <see cref="VerifierLlmStepName"/>; false si el nombre no es de ninguno.</summary>
    public static bool TryParseVerificationStepName(string stepName, out ReportType type)
    {
        type = default;
        foreach (var prefix in new[] { VerifyStepPrefix, VerifierStepPrefix })
        {
            if (stepName.StartsWith(prefix, StringComparison.Ordinal) && Enum.TryParse(stepName[prefix.Length..], out type) && Enum.IsDefined(type))
                return true;
        }

        type = default;
        return false;
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
        var dotNetEnricherStep = new DotNetEnricherStep(semanticEnrichmentStep) { Timeout = timeouts.Enrich };

        // 4. Pre-extracción de Facetas SQL
        var facetExtractorStep = new SqlFacetExtractorStep();

        // 5. Construcción Dinámica del ParallelStep. Los informes se crean antes que el presupuesto de tiempo: este escribe el
        //    Timeout de cada uno a través del registro que se crea con ellos.
        var requestedList = input.RequestedReports.Count > 0
            ? input.RequestedReports
            : Enum.GetValues<ReportType>().ToList();
        var preparation = PreparationOptions.FromServices(serviceProvider);
        var (parallelReportsStep, registry) = CreateReportsStep(
            templateProvider, roles, input, requestedList, timeouts, preparation, NoteProtocolOptions.FromServices(serviceProvider));

        // 6. Agregador y Resumen Ejecutivo
        var executiveSynthesizerStep = CreateSynthesisStep(templateProvider, roles, timeouts);

        // Enlace secuencial de los pasos (5 fases: los dos enriquecedores comparten la fase enrich). Con la preparación por
        // subsistema (CodebaseAnalysis:Preparation:Mode = Subsystems) van cuatro más entre las facetas y los informes.
        List<IStep> steps = [detectStep, languageEnricherStep, dotNetEnricherStep, facetExtractorStep];
        steps.AddRange(CreatePreparationSteps(templateProvider, roles, registry, preparation, reuseSavedDomainMap: false));
        // La pasada compartida (CodebaseAnalysis:SharedPass:Enabled), tras el mapa de dominios y antes de los informes; apagada, no
        // hay paso ni se piden sus roles.
        if (SharedPass.SharedPassOptions.FromServices(serviceProvider) is { Enabled: true } sharedPass)
            steps.Add(CreateSharedPassStep(templateProvider, roles, sharedPass));
        steps.Add(parallelReportsStep);
        steps.Add(executiveSynthesizerStep);

        return new CodebaseAnalysisPipeline(ResolveEngine(serviceProvider, engine), steps, timeouts.Pipeline);
    }

    /// <summary>El paso de la pasada compartida con sus dos roles (<see cref="SharedPass.SharedPassOptions.ReaderRole"/> y <see cref="SharedPass.SharedPassOptions.JudgeRole"/>).</summary>
    internal static SharedPassStep CreateSharedPassStep(ITemplateProvider templateProvider, Dictionary<string, LlmRole> roles, SharedPass.SharedPassOptions options) =>
        new(ResolveRole(roles, options.ReaderRole), ResolveRole(roles, options.JudgeRole), templateProvider, options);

    /// <summary>Nombre del pipeline de regeneración en los eventos (<c>pipeline.started</c>).</summary>
    public const string RegenerationPipelineName = "CodebaseAnalysisRegeneration";

    /// <summary>
    /// Pipeline reducido que vuelve a generar los informes de <c>input.RequestedReports</c> sobre el grafo que ya existe:
    /// <see cref="PrepareRegenerationStep"/>, <see cref="SqlFacetExtractorStep"/>, los pasos de la preparación por subsistema (con
    /// <c>Mode = Subsystems</c>), los auditores pedidos y, con <paramref name="synthesis"/>, la síntesis ejecutiva. No detecta, indexa
    /// ni enriquece. Las facetas se recalculan porque solo viven en memoria y son consultas SQL de segundos; de la preparación se
    /// recalculan el inventario, el plan y el presupuesto (deterministas y baratos) y se reutiliza el mapa de dominios del disco si
    /// coincide con la huella del grafo, porque nombrarlo cuesta una llamada al LLM.
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

        var preparation = PreparationOptions.FromServices(serviceProvider);
        var (reportsStep, registry) = CreateReportsStep(
            templateProvider, roles, input, input.RequestedReports, timeouts, preparation, NoteProtocolOptions.FromServices(serviceProvider));

        List<IStep> steps = [new PrepareRegenerationStep(), new SqlFacetExtractorStep()];
        // Los mismos pasos de preparación, pero el mapa de dominios del disco se reutiliza (sin volver a nombrar con el LLM)
        // mientras coincida con la huella del grafo actual.
        steps.AddRange(CreatePreparationSteps(templateProvider, roles, registry, preparation, reuseSavedDomainMap: true));
        steps.Add(reportsStep);
        if (synthesis)
            steps.Add(CreateSynthesisStep(templateProvider, roles, timeouts));

        return new CodebaseAnalysisPipeline(ResolveEngine(serviceProvider, engine), steps, timeouts.Pipeline, RegenerationPipelineName);
    }

    /// <summary>
    /// Pipeline que verifica los informes de <c>input.RequestedReports</c> ya escritos en disco: <see cref="PrepareRegenerationStep"/>
    /// (comprueba el grafo y retira del grafo los informes, que vuelven en el <c>finally</c> del pipeline), un paso
    /// <see cref="VerifyReportStep"/> por informe en paralelo y, con <paramref name="synthesis"/>, la síntesis ejecutiva. No
    /// calcula facetas: el verificador no las consume.
    /// </summary>
    /// <exception cref="ArgumentException">Si <c>input.RequestedReports</c> está vacío.</exception>
    public static CodebaseAnalysisPipeline CreateVerificationPipeline(
        ITemplateProvider templateProvider,
        Dictionary<string, LlmRole> roles,
        AnalysisPipelineInput input,
        bool synthesis,
        IServiceProvider? serviceProvider = null,
        IPipeline? engine = null,
        AnalysisTimeouts? timeouts = null)
    {
        if (input.RequestedReports.Count == 0)
            throw new ArgumentException("La verificación necesita al menos un informe.", nameof(input));

        timeouts ??= AnalysisTimeouts.FromServices(serviceProvider);
        var verifierRole = ResolveRole(roles, VerifierRoleName);
        // Un índice de ficheros del repositorio para todos los informes, y solo si hay alguno que verificar.
        var validator = new Lazy<ReportValidator.ReportContentValidator>(() =>
            ReportValidator.CreateContentValidator(new ReportValidationRequest(input.RepositoryPath, input.OutputReportsPath, input.DatabasePath)));

        var branches = input.RequestedReports.Select(type => (IStep)new VerifyReportStep(
            type,
            CreateVerifierStep(templateProvider, verifierRole, type, input.GetAuditorMaxToolIterations(VerifierRoleName), timeouts.Verifier),
            validator,
            timeouts.Verifier)
        {
            // El límite del paso cubre su LLM, la validación y la escritura; el del LLM lo aplica el propio paso.
            Timeout = timeouts.Verifier == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : timeouts.Verifier + TimeSpan.FromMinutes(2)
        }).ToArray();

        List<IStep> steps =
        [
            new PrepareRegenerationStep(),
            new TolerantReportsParallelStep(ParallelVerificationStepName, branches)
            {
                Timeout = timeouts.Reports,
                MaxDegreeOfParallelism = input.MaxParallelAuditors
            }
        ];
        if (synthesis)
            steps.Add(CreateSynthesisStep(templateProvider, roles, timeouts));

        return new CodebaseAnalysisPipeline(ResolveEngine(serviceProvider, engine), steps, timeouts.Pipeline, VerificationPipelineName);
    }

    /// <summary>Rol LLM del verificador (<c>AITaskAgent:LlmProviders:Roles:ReportVerifier</c>).</summary>
    public const string VerifierRoleName = "ReportVerifier";

    /// <summary>
    /// Tools del verificador: leer el código, buscar texto y los vecinos de un nodo del grafo. No lleva las demás del grafo ni la
    /// documentación aportada: comprueba afirmaciones concretas del informe, no las descubre.
    /// </summary>
    public static IReadOnlyList<string> VerifierToolNames { get; } =
        ["view_file", "view_code_item", "grep_search", Tools.GraphNeighborsTool.ToolName];

    /// <summary>
    /// Mensaje de la última llamada, sin tools, del verificador que agota su tope de iteraciones: devuelve ya el resultado con
    /// lo que ha comprobado.
    /// </summary>
    public const string VerifierToolLimitPrompt =
        "Has agotado las consultas a herramientas disponibles. No pidas más herramientas: responde ya con el informe corregido y su apartado " +
        "«Cambios de la verificación», o con SIN CAMBIOS si no has encontrado nada que corregir, limitándote a lo que has comprobado.";

    private static CustomStatelessTemplateLlmStep<VerificationInput, LlmStringStepResult> CreateVerifierStep(
        ITemplateProvider templateProvider, LlmRole role, ReportType type, int maxToolIterations, TimeSpan timeout) =>
        new(
            name: VerifierLlmStepName(type),
            role: role,
            templateProvider: templateProvider,
            promptTemplateName: "report_verifier_prompt",
            systemPromptTemplateName: role.SystemPrompt ?? "report_verifier_system",
            tools: [.. VerifierToolNames])
        {
            // Se invoca sin middleware: el límite real lo aplica VerifyReportStep con su propio token.
            Timeout = timeout,
            MaxToolIterations = maxToolIterations,
            ToolLimitFinalAnswerPrompt = VerifierToolLimitPrompt
        };

    private static IPipeline ResolveEngine(IServiceProvider? serviceProvider, IPipeline? engine) =>
        engine ?? (serviceProvider?.GetService(typeof(IPipeline)) as IPipeline) ?? new Pipeline();

    /// <summary>
    /// El rol pedido, o un error que lo nombra. Antes devolvía el primero configurado, y un auditor podía acabar con el
    /// prompt de sistema y el modelo de otro rol sin aviso (auditoría, H-11). No rompe ninguna configuración válida:
    /// <see cref="Hosting.LlmRoleFactory.BuildRoles"/> ya falla si falta alguno de los
    /// <see cref="Hosting.LlmRoleFactory.RequiredRoles"/>, y la CLI y la web construyen los roles con él.
    /// </summary>
    internal static LlmRole ResolveRole(Dictionary<string, LlmRole> roles, string roleName)
    {
        if (roles.TryGetValue(roleName, out var role))
            return role;

        throw new InvalidOperationException(
            $"No hay rol LLM '{roleName}' (roles recibidos: {(roles.Count == 0 ? "ninguno" : string.Join(", ", roles.Keys.Order(StringComparer.OrdinalIgnoreCase)))}). "
            + $"Decláralo en {Hosting.LlmRoleFactory.SectionName}:Roles.");
    }

    /// <summary>Rol LLM que nombra los subsistemas (el del informe funcional: no obliga a configurar un rol nuevo).</summary>
    public const string DomainNamingRoleName = "FunctionalAuditor";

    /// <summary>
    /// Los cuatro pasos de la preparación por subsistema, en su orden: inventario, descubrimiento de dominios, plan de cobertura y
    /// presupuesto de tiempo. Vacío con <c>Mode = Off</c>. El presupuesto recibe el <paramref name="registry"/> con los pasos de los
    /// informes, por constructor y sin estáticos.
    /// </summary>
    private static IReadOnlyList<IStep> CreatePreparationSteps(
        ITemplateProvider templateProvider,
        Dictionary<string, LlmRole> roles,
        AuditorStepRegistry registry,
        PreparationOptions preparation,
        bool reuseSavedDomainMap)
    {
        if (preparation.Mode == PreparationMode.Off)
            return [];

        var namingStep = DomainDiscoveryStep.CreateNamingStep(templateProvider, ResolveRole(roles, DomainNamingRoleName));
        return
        [
            new RepositoryInventoryStep(),
            new DomainDiscoveryStep(namingStep, reuseSavedMap: reuseSavedDomainMap),
            new CoveragePlanningStep(templateProvider, registry),
            new TimeBudgetStep(registry, CallLimit(roles.Where(r => AuditorRoleNames.Contains(r.Key, StringComparer.OrdinalIgnoreCase)).Select(r => r.Value)))
        ];
    }

    /// <summary>Roles de los informes (los que llaman al LLM dentro del paralelo de informes y sus investigaciones).</summary>
    private static readonly string[] AuditorRoleNames =
    [
        "FunctionalAuditor", "CodeSmellsAuditor", "DeadCodeAuditor", "FlowsAuditor", "SecurityBugAuditor",
        "ArchitectureAuditor", "ApiSecurityAuditor", "TechnicalDebtAuditor", "DiAuditor"
    ];

    /// <summary>
    /// Límite de llamadas LLM simultáneas con que se presupuesta el tiempo: el menor de los efectivos de los roles
    /// (<c>MaxConcurrentRequests</c> del rol, si no del proveedor, si no el del servicio). Null si ninguno tiene límite (0 = sin límite).
    /// </summary>
    internal static int? CallLimit(IEnumerable<LlmRole> roles)
    {
        var limits = roles
            .Select(r => r.Inference?.GetOption<int?>(OpenAILlmService.MaxConcurrentRequestsKey)
                ?? r.Provider.Inference?.GetOption<int?>(OpenAILlmService.MaxConcurrentRequestsKey)
                ?? OpenAILlmService.DefaultMaxConcurrentRequests)
            .Where(limit => limit > 0)
            .ToList();
        return limits.Count == 0 ? null : limits.Min();
    }

    /// <summary>
    /// El paralelo tolerante con un paso por informe, cada uno con su límite, y el <see cref="AuditorStepRegistry"/> de este pipeline
    /// con todos ellos (y el propio paralelo) para que <see cref="TimeBudgetStep"/> les escriba el <c>Timeout</c>. Con
    /// <c>Mode = Subsystems</c>, los informes en modo B (<see cref="CoveragePlanner.ModeBReports"/>) son un
    /// <see cref="SubsystemInvestigationReportStep"/> que investiga por subsistema y consolida con el auditor de siempre; los demás, y
    /// todos con <c>Off</c>, son el auditor.
    /// </summary>
    private static (TolerantReportsParallelStep Step, AuditorStepRegistry Registry) CreateReportsStep(
        ITemplateProvider templateProvider,
        Dictionary<string, LlmRole> roles,
        AnalysisPipelineInput input,
        IEnumerable<ReportType> reports,
        AnalysisTimeouts timeouts,
        PreparationOptions preparation,
        NoteProtocolOptions noteProtocol)
    {
        var registry = new AuditorStepRegistry();
        var parallelStepsList = new List<IStep>();
        foreach (var reportType in reports)
        {
            var roleName = AnalysisPipelineInput.GetRoleName(reportType);
            var templatePrefix = AnalysisPipelineInput.GetTemplatePrefix(reportType);
            var role = ResolveRole(roles, roleName);
            var maxToolIterations = input.GetAuditorMaxToolIterations(roleName);

            var auditor = CreateReportStep(templateProvider, role, templatePrefix, reportType, maxToolIterations, timeouts.Auditor, noteProtocol.Auditors, noteProtocol);
            IStep step = auditor;
            if (preparation.Mode == PreparationMode.Subsystems && CoveragePlanner.ModeBReports.Contains(reportType))
            {
                // La consolidación lleva su propio tope, más corto; el auditor completo queda para cuando no haya nada que investigar.
                var consolidation = CreateReportStep(
                    templateProvider, role, templatePrefix, reportType,
                    ConsolidationMaxToolIterations(maxToolIterations, preparation.ConsolidationMaxToolIterations), timeouts.Auditor, noteProtocol.Auditors, noteProtocol);
                step = CreateInvestigationReportStep(
                    templateProvider, role, templatePrefix, reportType, maxToolIterations, timeouts.Auditor, preparation, consolidation, auditor,
                    noteProtocol.Investigations, noteProtocol);
            }

            registry.Register(reportType, step);
            parallelStepsList.Add(step);
        }

        var parallel = new TolerantReportsParallelStep(ParallelReportsStepName, parallelStepsList.ToArray())
        {
            Timeout = timeouts.Reports,
            // CodebaseAnalysis:Reports:MaxParallelAuditors; 0 = sin tope propio (uno por procesador, lo de siempre).
            MaxDegreeOfParallelism = input.MaxParallelAuditors
        };
        registry.RegisterReportsParallel(parallel);
        return (parallel, registry);
    }

    /// <summary>
    /// El envoltorio de un informe en modo B. Hasta que <see cref="TimeBudgetStep"/> le escribe el límite calculado (o si no llega a
    /// calcularse), cubre lo que cubrirían sus tandas de investigaciones y la consolidación con el límite de un auditor cada una, no
    /// el de un auditor solo: con un informe de varios subsistemas ese límite lo cortaría a medias.
    /// </summary>
    private static SubsystemInvestigationReportStep CreateInvestigationReportStep(
        ITemplateProvider templateProvider,
        LlmRole role,
        string templatePrefix,
        ReportType type,
        int maxToolIterations,
        TimeSpan auditorTimeout,
        PreparationOptions preparation,
        CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult> consolidation,
        CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult> degradedAuditor,
        bool investigationNoteProtocol,
        NoteProtocolOptions noteOptions)
    {
        var parallel = Math.Max(preparation.MaxParallelInvestigations, 1);
        var waves = (preparation.MaxSubsystemsPerReport + parallel - 1) / parallel;
        return new SubsystemInvestigationReportStep(
            type,
            consolidation,
            subsystemId => SubsystemInvestigationReportStep.CreateInvestigationStep(
                templateProvider, role, templatePrefix, type, subsystemId, maxToolIterations, auditorTimeout, investigationNoteProtocol, noteOptions),
            auditorTimeout,
            preparation,
            degradedAuditor)
        {
            Timeout = auditorTimeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : auditorTimeout * (waves + 1)
        };
    }

    /// <summary>
    /// Tope de consultas a herramientas de la consolidación de un informe en modo B: el configurado
    /// (<c>CodebaseAnalysis:Preparation:ConsolidationMaxToolIterations</c>), sin pasar nunca del tope del auditor del informe, y el del auditor
    /// si el configurado es 0 («sin acotar»).
    /// </summary>
    internal static int ConsolidationMaxToolIterations(int auditorMaxToolIterations, int configured) =>
        configured <= 0 ? auditorMaxToolIterations : Math.Min(configured, auditorMaxToolIterations);

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
    /// Tools de cada auditor: el código (<c>view_file</c>, <c>view_code_item</c>), la búsqueda textual en el repositorio
    /// (<c>grep_search</c>, para lo que el grafo no ve: nombres de clase en cadenas o en XML y factorías por reflexión), el
    /// grafo (<c>graph_search</c>, los vecinos de un nodo con la procedencia de cada arista en <c>graph_neighbors</c>, y rutas,
    /// DI y atributos en <c>graph_domain</c>) y la documentación aportada. <c>graph_map</c>, <c>graph_health</c> y
    /// <c>graph_flows</c> no están: ninguna plantilla las usa; <c>find_by_name</c> y <c>list_dir</c> tampoco, porque
    /// <c>grep_search</c> con <c>Includes</c> ya localiza ficheros. Cómo usar el grafo y la búsqueda lo dice
    /// <c>templates/_graph_rules.md</c>.
    /// </summary>
    public static IReadOnlyList<string> AuditorToolNames { get; } =
    [
        "view_file", "view_code_item", "grep_search", "graph_search", Tools.GraphNeighborsTool.ToolName, "graph_domain", Tools.ReadDocumentTool.ToolName
    ];

    private static CustomStatelessTemplateLlmStep<ReportFacetInput, SingleReportResult> CreateReportStep(
        ITemplateProvider templateProvider,
        LlmRole role,
        string templatePrefix,
        ReportType type,
        int maxToolIterations,
        TimeSpan timeout,
        bool noteProtocol,
        NoteProtocolOptions noteOptions)
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
            ToolLimitFinalAnswerPrompt = AuditorToolLimitPrompt,
            // CodebaseAnalysis:NoteProtocol:Auditors: cada resultado de herramienta se sustituye por la nota del modelo.
            TagHandlers = noteProtocol ? [NoteTagHandler.Instance] : [],
            NoteOptions = noteOptions
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
