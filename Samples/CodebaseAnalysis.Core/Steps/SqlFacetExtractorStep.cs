using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using Microsoft.Data.Sqlite;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Steps;

public class SqlFacetExtractorStep : IStep<GraphStatusResult, ReportFacetInput>
{
    private static Documents.DocumentOptions DocumentOptionsFor(PipelineContext context) =>
        context.ServiceProvider.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration)) is Microsoft.Extensions.Configuration.IConfiguration configuration
            ? Documents.DocumentOptions.FromConfiguration(configuration)
            : new Documents.DocumentOptions();

    public const string StepName = "SqlFacetExtractorStep";

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 500;
    public TimeSpan? Timeout => TimeSpan.FromMinutes(5);
    public Type InputType => typeof(GraphStatusResult);
    public Type OutputType => typeof(ReportFacetInput);

    public async Task<ReportFacetInput> ExecuteAsync(
        GraphStatusResult input, 
        PipelineContext context, 
        int attempt, 
        ReportFacetInput? lastStepResult, 
        CancellationToken cancellationToken)
    {
        context.Report(StepName, AnalysisLogLevel.Info, "📊 Extrayendo facetas SQL deterministas del grafo SQLite con referencias de código...");

        var config = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput) 
            ?? new AnalysisPipelineInput { DatabasePath = input.DatabasePath };

        var dbPath = !string.IsNullOrEmpty(input.DatabasePath) ? input.DatabasePath : config.DatabasePath;
        var repoPath = !string.IsNullOrEmpty(config.RepositoryPath) ? config.RepositoryPath : Directory.GetCurrentDirectory();

        var facetResult = new ReportFacetInput
        {
            RepositoryPath = repoPath,
            DatabasePath = dbPath,
            OutputReportsPath = config.OutputReportsPath,
            TotalNodes = input.TotalNodes,
            TotalEdges = input.TotalEdges
        };

        if (!File.Exists(dbPath))
        {
            context.Report(StepName, AnalysisLogLevel.Warning, "⚠️ Base de datos SQLite no encontrada. Se generarán facetas vacías.");
            return facetResult;
        }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(cancellationToken);

        facetResult.DetectedLanguages = await DetectEcosystemAsync(conn, repoPath, cancellationToken);
        facetResult.RawFacets["detected_languages"] = facetResult.DetectedLanguages;
        facetResult.RawFacets["technology_stack"] = facetResult.DetectedLanguages;
        context.Report(StepName, AnalysisLogLevel.Info, $"🔍 Ecosistema Tecnológico Detectado: {facetResult.DetectedLanguages}");

        // 1. Métricas Funcionales y Generales
        facetResult.MetricsSummary = await QueryToJsonAsync(conn, @"
            SELECT 
                COUNT(1) AS total_nodes,
                (SELECT COUNT(1) FROM edges) AS total_edges,
                (SELECT COUNT(1) FROM nodes WHERE label = 'Route') AS total_routes,
                (SELECT COUNT(1) FROM nodes WHERE label = 'Flow') AS total_flows,
                (SELECT COUNT(1) FROM nodes WHERE label = 'DiRegistration') AS total_di,
                (SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.dead_code_candidate') = 1) AS dead_code_count
            FROM nodes;", cancellationToken);

        // Métricas calculadas del grafo cuando faltan las propiedades (auditoría H-2). Sin ellas, cada faceta usa solo las
        // propiedades y un valor desconocido queda como null, nunca como cero.
        Dictionary<string, object> metricParameters;
        try
        {
            metricParameters = await PrepareDerivedMetricsAsync(conn, cancellationToken);
        }
        catch (Exception ex)
        {
            context.Report(StepName, AnalysisLogLevel.Warning, $"⚠️ No se pudieron calcular métricas del grafo (fan_in, fan_out, method_count): {ex.Message}. Las facetas usan solo las propiedades.");
            metricParameters = [];
        }

        // Rutas y consultas. El verbo sale de la propiedad; 'QUERY' solo para las consultas, que no tienen verbo HTTP. Una
        // autenticación sin dato es 'desconocido', no «sin autenticación».
        facetResult.RoutesSummary = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file,
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 1) AS end_line,
                   coalesce(json_extract(properties, '$.http_verb'), json_extract(properties, '$.http_method'),
                            CASE WHEN label IN ('AccessQuery', 'Query') THEN 'QUERY' END) AS verb,
                   coalesce(json_extract(properties, '$.route_path'), name) AS path,
                   coalesce(json_extract(properties, '$.auth_required'), 'desconocido') AS auth_required
            FROM nodes WHERE label IN ('Route', 'AccessQuery', 'Query')
            ORDER BY label, path, qualified_name LIMIT 50;", cancellationToken);

        facetResult.FlowsSummary = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name,
                   json_extract(properties, '$.step_count') AS steps,
                   json_extract(properties, '$.entry_method') AS entry_point,
                   properties
            FROM nodes WHERE label = 'Flow'
            ORDER BY qualified_name LIMIT 30;", cancellationToken);

        // Las entidades más usadas primero (fan_in conocido); el resto, por nombre.
        facetResult.DomainEntitiesSummary = await QueryToJsonAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.label, n.file,
                   {StartLineExpr} AS start_line,
                   coalesce(nullif(n.line_end, 0), n.end_line, 1) AS end_line,
                   {FanInExpr} AS fan_in, {FanInSourceExpr} AS fan_in_source,
                   n.properties
            {MetricsJoin}
            WHERE n.label IN ('Class', 'Record', 'Interface', 'Entity', 'AccessTable', 'Table')
            ORDER BY coalesce({FanInExpr}, -1) DESC, n.qualified_name LIMIT 50;", cancellationToken, parameters: metricParameters);

        // 2. Code Smells (con snippets de código fuente en los métodos más complejos)
        facetResult.HighCyclomaticMethods = await QueryToJsonAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.file,
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   CAST(json_extract(n.properties, '$.cyclomatic') AS INTEGER) AS cyclomatic,
                   {FanInExpr} AS fan_in, {FanInSourceExpr} AS fan_in_source
            {MetricsJoin}
            WHERE n.label IN ('Method', 'Sub', 'Function', 'Property', 'Constructor') AND CAST(json_extract(n.properties, '$.cyclomatic') AS INTEGER) > 0
            ORDER BY cyclomatic DESC, n.qualified_name LIMIT 30;", cancellationToken, repositoryPath: repoPath, attachSnippets: true, maxSnippetLines: 70, maxSnippetItems: 10,
            parameters: metricParameters);

        facetResult.BloatedClasses = await QueryToJsonAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.file,
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   {LocExpr} AS loc_total, {LocSourceExpr} AS loc_source,
                   {MethodCountExpr} AS method_count, {MethodCountSourceExpr} AS method_count_source
            {MetricsJoin}
            WHERE n.label IN ('Class', 'Module', 'Form') AND ({LocExpr} IS NOT NULL OR {MethodCountExpr} IS NOT NULL)
            ORDER BY loc_total DESC, method_count DESC, n.qualified_name LIMIT 25;", cancellationToken, parameters: metricParameters);

        facetResult.CouplingMetrics = await QueryToJsonAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.label, n.file,
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   {FanInExpr} AS fan_in, {FanInSourceExpr} AS fan_in_source,
                   {FanOutExpr} AS fan_out, {FanOutSourceExpr} AS fan_out_source
            {MetricsJoin}
            WHERE n.label IN ('Class', 'Interface', 'Method', 'Module', 'Form', 'Sub', 'Function') AND ({FanInExpr} IS NOT NULL OR {FanOutExpr} IS NOT NULL)
            ORDER BY fan_in DESC, fan_out DESC, n.qualified_name LIMIT 30;", cancellationToken, parameters: metricParameters);

        // 3. Código Muerto
        facetResult.DeadCodeCandidates = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, label, file,
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   json_extract(properties, '$.dead_code_confidence') AS confidence,
                   json_extract(properties, '$.dead_code_reason') AS reason
            FROM nodes
            WHERE json_extract(properties, '$.dead_code_candidate') = 1
            ORDER BY confidence DESC, qualified_name LIMIT 25;", cancellationToken);

        facetResult.UnusedDiRegistrations = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file,
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties
            FROM nodes
            WHERE label = 'DiRegistration' AND (json_extract(properties, '$.unused_registration') = 1 OR json_extract(properties, '$.fan_in') = 0)
            ORDER BY qualified_name LIMIT 25;", cancellationToken);

        facetResult.OrphanRoutes = await QueryToJsonAsync(conn, @"
            SELECT r.name, r.qualified_name, r.file,
                   coalesce(nullif(r.line_start, 0), r.start_line, 1) AS start_line,
                   coalesce(nullif(r.line_end, 0), r.end_line, 1) AS end_line,
                   r.properties
            FROM nodes r
            WHERE r.label = 'Route' AND NOT EXISTS (
                SELECT 1 FROM edges e WHERE e.target_id = r.id AND e.type = 'HANDLES'
            )
            ORDER BY r.file, start_line, r.qualified_name LIMIT 20;", cancellationToken);

        // Solo tipos con fan_in conocido y igual a 0: un fan_in sin dato es uso desconocido, no nulo. Quedan fuera los que
        // atienden una ruta (HANDLES, suyo o de sus métodos) y los que tienen subtipos: esos tienen uso por otra vía. Con el
        // fan_in calculado, además, solo los que declaran constructor: instanciar con el constructor implícito no deja arista
        // CALLS (no hay nodo al que llegar), así que en los demás un cero no prueba nada. Los más grandes primero.
        facetResult.ZeroFaninClasses = await QueryToJsonAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.file, n.label,
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   {FanInExpr} AS fan_in, {FanInSourceExpr} AS fan_in_source,
                   {LocExpr} AS loc_total
            {MetricsJoin}
            WHERE n.label IN ('Class', 'Module', 'Form') AND {FanInExpr} = 0
              AND NOT EXISTS (SELECT 1 FROM edges h WHERE h.source_id = n.id AND h.type = 'HANDLES')
              AND NOT EXISTS (SELECT 1 FROM temp.facet_members m JOIN edges h ON h.source_id = m.method_id AND h.type = 'HANDLES' WHERE m.type_id = n.id)
              AND NOT EXISTS (SELECT 1 FROM edges s WHERE s.target_id = n.id AND s.type IN ('INHERITS', 'IMPLEMENTS'))
              AND (json_extract(n.properties, '$.fan_in') IS NOT NULL
                   OR EXISTS (SELECT 1 FROM temp.facet_members m JOIN nodes c ON c.id = m.method_id
                              WHERE m.type_id = n.id AND (c.label = 'Constructor' OR json_extract(c.properties, '$.is_constructor') = 1)))
            ORDER BY loc_total DESC, n.qualified_name LIMIT 20;", cancellationToken, parameters: metricParameters);

        // Acciones Struts que ninguna clase del grafo atiende (sin HANDLES entrante), con el motivo que da el propio nodo.
        // Primero las que declaran una clase que no está en el grafo; después, por fichero y línea.
        facetResult.StrutsActionsWithoutHandler = await QueryToJsonAsync(conn, $@"
            SELECT n.name AS action_path, n.qualified_name, n.file,
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   json_extract(n.properties, '$.action_type') AS action_type,
                   json_extract(n.properties, '$.forward') AS forward,
                   json_extract(n.properties, '$.include') AS include,
                   CASE WHEN json_extract(n.properties, '$.action_type') IS NULL
                        THEN 'sin atributo type: la acción solo reenvía o incluye'
                        ELSE 'la clase del atributo type no está en el grafo (externa al repositorio o sin indexar)' END AS reason,
                   (SELECT COUNT(1) FROM edges e WHERE e.target_id = n.id AND e.type = 'SUBMITS_TO') AS submitted_from_jsps,
                   (SELECT COUNT(1) FROM edges e WHERE e.source_id = n.id AND e.type = 'FORWARDS_TO') AS forwards_resolved
            FROM nodes n
            WHERE n.label = 'Route' AND json_extract(n.properties, '$.framework') = 'struts'
              AND NOT EXISTS (SELECT 1 FROM edges h WHERE h.target_id = n.id AND h.type = 'HANDLES')
            ORDER BY (json_extract(n.properties, '$.action_type') IS NULL), n.file, start_line, n.qualified_name LIMIT 30;", cancellationToken);

        // 4. Flujos End-to-End
        facetResult.FlowsCatalog = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, properties FROM nodes WHERE label = 'Flow'
            ORDER BY qualified_name LIMIT 50;", cancellationToken);

        facetResult.FlowStepsGraph = await QueryToJsonAsync(conn, @"
            SELECT e.type, s.qualified_name AS source, t.qualified_name AS target, e.properties
            FROM edges e
            JOIN nodes s ON e.source_id = s.id
            JOIN nodes t ON e.target_id = t.id
            WHERE e.type IN ('FLOW_ENTRY', 'FLOW_NEXT')
            ORDER BY source, e.type, target LIMIT 60;", cancellationToken);

        facetResult.RouteHandlersMapping = await QueryToJsonAsync(conn, @"
            SELECT s.qualified_name AS handler_method, t.qualified_name AS route, e.properties
            FROM edges e
            JOIN nodes s ON e.source_id = s.id
            JOIN nodes t ON e.target_id = t.id
            WHERE e.type = 'HANDLES'
            ORDER BY route, handler_method LIMIT 50;", cancellationToken);

        facetResult.UnresolvedForwards = await UnresolvedForwardsAsync(conn, 30, cancellationToken);

        facetResult.DiResolutions = await QueryToJsonAsync(conn, @"
            SELECT s.qualified_name AS source, t.qualified_name AS target, e.type, e.properties
            FROM edges e
            JOIN nodes s ON e.source_id = s.id
            JOIN nodes t ON e.target_id = t.id
            WHERE e.type IN ('DI_RESOLVES', 'IMPLEMENTS')
            ORDER BY e.type, target, source LIMIT 50;", cancellationToken);

        // 5. Errores y Bugs (con fragmentos de código fuente adjuntos)
        facetResult.CriticalHotspotsExceptions = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file, 
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   CAST(json_extract(properties, '$.hotspot') AS REAL) AS hotspot,
                   json_extract(properties, '$.throws') AS throws,
                   json_extract(properties, '$.cyclomatic') AS cyclomatic,
                   json_extract(properties, '$.detected_issues') AS detected_issues
            FROM nodes 
            WHERE CAST(json_extract(properties, '$.hotspot') AS REAL) > 5 
               OR json_extract(properties, '$.throws') IS NOT NULL
               OR json_extract(properties, '$.detected_issues') IS NOT NULL
            ORDER BY hotspot DESC, qualified_name LIMIT 30;", cancellationToken, repositoryPath: repoPath, attachSnippets: true, maxSnippetLines: 70, maxSnippetItems: 10);

        // Solo las rutas que el grafo declara sin autenticación (auth_required = false). Sin el dato (p. ej. las acciones de
        // Struts, cuyo plugin no lo escribe) la validación es desconocida y no se listan como «sin validación».
        facetResult.UnvalidatedRoutes = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file,
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 1) AS end_line,
                   coalesce(json_extract(properties, '$.http_verb'), json_extract(properties, '$.http_method')) AS verb,
                   json_extract(properties, '$.route_path') AS path
            FROM nodes
            WHERE label = 'Route' AND json_extract(properties, '$.auth_required') = 0
            ORDER BY path, qualified_name LIMIT 40;", cancellationToken);

        // JSP marcadas como sospechosas por el plugin Struts/JSP (SQL, imports de acceso a datos o código de negocio usado en
        // un scriptlet; el acceso a sesión va aparte en session_access y no la marca), con sus motivos. Las de más motivos y
        // más scriptlets primero.
        facetResult.SuspiciousJsps = await QueryToJsonAsync(conn, $@"
            SELECT n.name AS jsp, n.qualified_name, n.file,
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   (SELECT group_concat(value, ', ') FROM json_each({SafeJson("n.properties")}, '$.suspicious_reasons')) AS reasons,
                   json_array_length(json_extract({SafeJson("n.properties")}, '$.suspicious_reasons')) AS reason_count,
                   CAST(json_extract(n.properties, '$.scriptlet_count') AS INTEGER) AS scriptlets,
                   CASE WHEN json_type({SafeJson("n.properties")}, '$.business_imports') IS NOT NULL
                        THEN coalesce((SELECT group_concat(value, ', ') FROM json_each({SafeJson("n.properties")}, '$.business_imports')), '') END AS business_imports,
                   json_extract(n.properties, '$.has_sql') AS has_sql,
                   json_extract(n.properties, '$.session_access') AS session_access,
                   (SELECT COUNT(1) FROM edges e WHERE e.target_id = n.id AND e.type IN ('FORWARDS_TO', 'RENDERS', 'INCLUDES')) AS referenced_by
            FROM nodes n
            WHERE n.label = 'Jsp' AND json_extract(n.properties, '$.suspicious') = 1
            ORDER BY reason_count DESC, scriptlets DESC, n.qualified_name LIMIT 25;", cancellationToken);

        facetResult.SuspectPatterns = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file, 
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties,
                   json_extract(properties, '$.detected_issues') AS detected_issues
            FROM nodes 
            WHERE json_extract(properties, '$.anti_pattern') IS NOT NULL 
               OR json_extract(properties, '$.smell') IS NOT NULL
               OR json_extract(properties, '$.detected_issues') IS NOT NULL
            ORDER BY file, start_line, qualified_name LIMIT 30;", cancellationToken);

        // 6. Arquitectura y Acoplamiento
        facetResult.ProjectComponents = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file, 
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties 
            FROM nodes 
            WHERE label IN ('CsProject', 'Module', 'Package', 'Component', 'Form')
            ORDER BY label, qualified_name LIMIT 50;", cancellationToken);

        facetResult.MartinInstabilityMetrics = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name,
                   json_extract(properties, '$.instability') AS instability,
                   json_extract(properties, '$.abstractness') AS abstractness,
                   json_extract(properties, '$.distance') AS distance
            FROM nodes 
            WHERE json_extract(properties, '$.instability') IS NOT NULL
            ORDER BY distance DESC, qualified_name LIMIT 40;", cancellationToken);

        facetResult.DependencyCycles = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file,
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   json_extract(properties, '$.cycle_group') AS cycle_group,
                   json_extract(properties, '$.cycle_size') AS cycle_size,
                   json_extract(properties, '$.cycle_members') AS cycle_members
            FROM nodes 
            WHERE json_extract(properties, '$.in_cycle') = 1
            ORDER BY cycle_size DESC, cycle_group, qualified_name LIMIT 30;", cancellationToken);

        facetResult.LayerViolations = await QueryToJsonAsync(conn, @"
            SELECT s.qualified_name AS source, t.qualified_name AS target, e.properties 
            FROM edges e 
            JOIN nodes s ON e.source_id = s.id 
            JOIN nodes t ON e.target_id = t.id 
            WHERE json_extract(e.properties, '$.layer_violation') = 1
            ORDER BY source, target LIMIT 30;", cancellationToken);

        // Jerarquía (INHERITS/IMPLEMENTS) por supertipo, los más extendidos o implementados primero, con cuántas aristas
        // resolvió un enriquecedor y cuántas son heurística de cbm (herencia decidida por nombre).
        facetResult.TypeHierarchy = await QueryToJsonAsync(conn, $@"
            SELECT t.qualified_name AS supertype, t.label, t.file, e.type AS relation,
                   COUNT(1) AS subtypes,
                   SUM(CASE WHEN {ResolvedEdgePredicate("e.properties")} THEN 1 ELSE 0 END) AS resolved,
                   SUM(CASE WHEN {ResolvedEdgePredicate("e.properties")} THEN 0 ELSE 1 END) AS heuristic_cbm,
                   (SELECT group_concat(sub_name, ', ') FROM (
                        SELECT s2.name AS sub_name FROM edges e2 JOIN nodes s2 ON s2.id = e2.source_id
                        WHERE e2.target_id = t.id AND e2.type = e.type ORDER BY s2.name LIMIT 10)) AS sample_subtypes
            FROM edges e JOIN nodes t ON t.id = e.target_id
            WHERE e.type IN ('INHERITS', 'IMPLEMENTS')
            GROUP BY t.id, e.type
            ORDER BY subtypes DESC, supertype, relation LIMIT 30;", cancellationToken);

        facetResult.ArchitecturalCommunities = await QueryToJsonAsync(conn, @"
            SELECT json_extract(properties, '$.community_id') AS community,
                   COUNT(1) AS members,
                   GROUP_CONCAT(name, ', ') AS sample_members
            FROM nodes 
            WHERE json_extract(properties, '$.community_id') IS NOT NULL 
            GROUP BY community
            ORDER BY members DESC, community LIMIT 20;", cancellationToken);

        // 7. APIs y Seguridad
        facetResult.RoutesCatalog = facetResult.RoutesSummary;
        facetResult.RoutesSecurityMatrix = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name,
                   coalesce(json_extract(properties, '$.http_verb'), json_extract(properties, '$.http_method')) AS verb,
                   json_extract(properties, '$.route_path') AS path,
                   coalesce(json_extract(properties, '$.auth_required'), 'desconocido') AS auth_required,
                   json_extract(properties, '$.auth_roles') AS roles
            FROM nodes WHERE label = 'Route'
            ORDER BY path, qualified_name LIMIT 50;", cancellationToken);

        facetResult.RoutesContracts = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name,
                   json_extract(properties, '$.request_type_fqn') AS request_type,
                   json_extract(properties, '$.response_type_fqn') AS response_type
            FROM nodes WHERE label = 'Route'
            ORDER BY qualified_name LIMIT 50;", cancellationToken);

        facetResult.OrphanRoutesList = facetResult.OrphanRoutes;

        // 8. Deuda Técnica y Hotspots (con fragmentos de código fuente adjuntos)
        facetResult.HotspotsRanking = await QueryToJsonAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.file, 
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   CAST(json_extract(n.properties, '$.hotspot') AS REAL) AS hotspot,
                   CAST(json_extract(n.properties, '$.cyclomatic') AS INTEGER) AS cyclomatic,
                   {FanInExpr} AS fan_in, {FanInSourceExpr} AS fan_in_source
            {MetricsJoin}
            WHERE json_extract(n.properties, '$.hotspot') IS NOT NULL
            ORDER BY hotspot DESC, n.qualified_name LIMIT 30;", cancellationToken, repositoryPath: repoPath, attachSnippets: true, maxSnippetLines: 70, maxSnippetItems: 10,
            parameters: metricParameters);

        facetResult.DocCoverageMetrics = await QueryToJsonAsync(conn, @"
            SELECT label, COUNT(1) AS total,
                   SUM(CASE WHEN json_extract(properties, '$.docstring') IS NOT NULL OR json_extract(properties, '$.xml_doc') IS NOT NULL THEN 1 ELSE 0 END) AS documented
            FROM nodes WHERE label NOT IN ('UserDocument', 'UserDocSection') GROUP BY label;", cancellationToken);

        // [Obsolete] de C# (attributes) y @Deprecated de Java (annotations, que el analizador Java escribe con el nombre simple
        // de cada anotación).
        facetResult.ObsoleteElements = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file,
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties
            FROM nodes
            WHERE json_extract(properties, '$.is_obsolete') = 1 OR json_extract(properties, '$.attributes') LIKE '%Obsolete%'
               OR json_extract(properties, '$.annotations') LIKE '%""Deprecated""%'
            ORDER BY file, start_line, qualified_name LIMIT 30;", cancellationToken);

        // Solo clases con algún dato: sin loc_total ni cyclomatic_total (propios o calculados), 25 clases cualesquiera.
        facetResult.ComplexClasses = await QueryToJsonAsync(conn, $@"
            SELECT n.name, n.qualified_name, n.file,
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   {LocExpr} AS loc_total, {LocSourceExpr} AS loc_source,
                   {CyclomaticTotalExpr} AS cyclomatic_total, {CyclomaticTotalSourceExpr} AS cyclomatic_total_source,
                   {MethodCountExpr} AS method_count
            {MetricsJoin}
            WHERE n.label = 'Class' AND ({CyclomaticTotalExpr} IS NOT NULL OR {LocExpr} IS NOT NULL)
            ORDER BY cyclomatic_total DESC, loc_total DESC, n.qualified_name LIMIT 25;", cancellationToken, parameters: metricParameters);

        // 9. Inyección de Dependencias
        facetResult.DiRegistrationsCatalog = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file, 
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties 
            FROM nodes WHERE label = 'DiRegistration'
            ORDER BY qualified_name LIMIT 50;", cancellationToken);

        facetResult.DiLifetimesMapping = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name,
                   json_extract(properties, '$.di_lifetime') AS di_lifetime,
                   properties
            FROM nodes WHERE label = 'DiRegistration'
            ORDER BY qualified_name LIMIT 50;", cancellationToken);

        facetResult.ExtensionPointsCatalog = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file, 
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties 
            FROM nodes WHERE json_extract(properties, '$.is_extension_point') = 1
            ORDER BY qualified_name LIMIT 30;", cancellationToken);

        facetResult.ConfigAdaptersMapping = await QueryToJsonAsync(conn, @"
            SELECT s.qualified_name AS config, t.qualified_name AS adapter, e.properties 
            FROM edges e 
            JOIN nodes s ON e.source_id = s.id 
            JOIN nodes t ON e.target_id = t.id 
            WHERE e.type = 'RESOLVES_ADAPTER'
            ORDER BY config, adapter LIMIT 30;", cancellationToken);

        // Candidatos creacionales y de composición por nombre, válido para cualquier lenguaje: los
        // catálogos de DI de arriba dependen de labels que solo emiten indexadores de ecosistemas
        // con contenedor (.NET, Spring…). Sin esto, el auditor de DI de un proyecto VBA, Go o C
        // recibía todo vacío y afirmaba que no había factorías aunque existieran.
        facetResult.CreationalCandidates = await QueryToJsonAsync(conn, $@"
            SELECT n.label, n.name, n.qualified_name, n.file,
                   {StartLineExpr} AS start_line,
                   {EndLineExpr} AS end_line,
                   {FanInExpr} AS fan_in, {FanInSourceExpr} AS fan_in_source
            {MetricsJoin}
            WHERE n.label NOT IN ('DocSection', 'PromptTemplate', 'Section', 'Folder', 'File', 'Variable',
                                'Table', 'AccessTable', 'Query', 'AccessQuery', 'Route', 'Flow',
                                'UserDocument', 'UserDocSection')
              AND (n.name LIKE 'Create%' OR n.name LIKE 'Build%' OR n.name LIKE 'Make%' OR n.name LIKE 'New%'
                   OR n.name LIKE '%Factory%' OR n.name LIKE '%Builder%' OR n.name LIKE '%Provider%'
                   OR n.name LIKE '%Container%' OR n.name LIKE '%Registry%' OR n.name LIKE '%Locator%'
                   OR n.name LIKE '%Instance%' OR n.name LIKE '%Connection%' OR n.name LIKE '%Client%')
              AND n.name NOT LIKE '%Test%'
            ORDER BY coalesce({FanInExpr}, 0) DESC, n.label, n.name, n.qualified_name LIMIT 60;", cancellationToken, parameters: metricParameters);

        // Credenciales en claro, leídas del disco en los ficheros que conoce el grafo: ninguna faceta
        // de las anteriores las cubre, y sin esto dependían de que un auditor abriera el fichero.
        var graphFiles = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT file FROM nodes WHERE file IS NOT NULL AND file <> '';";
            using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await rdr.ReadAsync(cancellationToken))
            {
                graphFiles.Add(rdr.GetString(0));
            }
        }
        facetResult.HardcodedSecrets = await HardcodedSecretScanner.ScanAsync(graphFiles, repoPath, cancellationToken);

        // 10. Pasos de Workflow, Templates de Prompts y Documentación Markdown
        facetResult.WorkflowStepsCatalog = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file, 
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties 
            FROM nodes 
            WHERE label = 'WorkflowStep'
            ORDER BY file, start_line, qualified_name LIMIT 50;", cancellationToken);

        facetResult.PromptTemplatesCatalog = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file, 
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties 
            FROM nodes 
            WHERE label = 'PromptTemplate'
            ORDER BY file, qualified_name LIMIT 50;", cancellationToken);

        facetResult.DocumentationAndPrompts = await QueryToJsonAsync(conn, @"
            SELECT name, qualified_name, file, 
                   coalesce(nullif(line_start, 0), start_line, 1) AS start_line,
                   coalesce(nullif(line_end, 0), end_line, 0) AS end_line,
                   properties 
            FROM nodes 
            WHERE label IN ('DocSection', 'PromptTemplate')
            ORDER BY file, start_line, qualified_name LIMIT 50;", cancellationToken);

        // Documentación aportada por el usuario (plan v2 §7.8).
        facetResult.ProvidedDocumentation = await ProvidedDocumentationFacet.BuildAsync(
            new Knowledge.KnowledgePaths(repoPath, string.IsNullOrWhiteSpace(config.OutputReportsPath) ? Path.Combine(repoPath, "reports") : config.OutputReportsPath),
            conn, DocumentOptionsFor(context).AnalysisIndexMaxChars, cancellationToken);

        // 11. Totales Agregados
        // Los defectos críticos ya no se cuentan aquí: "hotspot > 10" no es un bug. Los declara el
        // informe de errores en su línea analysis-meta y los consolida ExecutiveSynthesizerStep.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT
                    (SELECT COUNT(1) FROM nodes WHERE label = 'Route') AS routes,
                    (SELECT COUNT(1) FROM nodes WHERE label = 'Flow') AS flows,
                    (SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.dead_code_candidate') = 1) AS dead
            ;";
            using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            await rdr.ReadAsync(cancellationToken);
            facetResult.TotalRoutes = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
            facetResult.TotalFlows = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
            facetResult.DeadCodeCount = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2);
        }
        facetResult.TotalLoc = await ComputeTotalLocAsync(conn, cancellationToken);

        context.Set("ReportFacetInput", facetResult);
        context.Report(StepName, AnalysisLogLevel.Success, $"✅ Extracción de facetas finalizada. Rutas: {facetResult.TotalRoutes}, Flujos: {facetResult.TotalFlows}, Candidatos Código Muerto: {facetResult.DeadCodeCount}");

        return facetResult;
    }

    /// <summary>
    /// LOC de los ficheros de código del grafo, sin depender de qué label y qué propiedades usa cada indexador.
    /// Por fichero, lo primero que haya de:
    /// <list type="number">
    /// <item><c>loc_total</c> de su contenedor (<c>Module</c>/<c>Form</c>/<c>File</c>: VBA, Python...);</item>
    /// <item>la suma de <c>loc_total</c> de sus tipos (<c>Class</c>/<c>Interface</c>...: C#, Java...), sin
    /// contar dos veces un módulo y las clases que contiene;</item>
    /// <item>el rango de líneas de su contenedor, o la última línea de sus símbolos: codebase-memory no da
    /// <c>loc_total</c> en C# y el grafo de un repositorio C# sumaba 0.</item>
    /// </list>
    /// Los dos últimos solo si el fichero tiene símbolos de código (tipos, funciones, métodos): Markdown,
    /// JSON o YAML también tienen contenedor y no son código.
    /// </summary>
    public static async Task<int> ComputeTotalLocAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT SUM(file_loc) FROM (
                SELECT COALESCE(
                    MAX(CASE WHEN label IN ('Module', 'Form', 'File') THEN CAST(json_extract(properties, '$.loc_total') AS INTEGER) END),
                    SUM(CASE WHEN label IN ('Class', 'Interface', 'Struct', 'Record', 'Enum', 'Trait') THEN CAST(json_extract(properties, '$.loc_total') AS INTEGER) END),
                    CASE WHEN SUM(CASE WHEN label IN ('Class', 'Interface', 'Struct', 'Record', 'Enum', 'Trait', 'Function', 'Method', 'Sub', 'Property', 'Constructor') THEN 1 ELSE 0 END) > 0
                        THEN COALESCE(
                            MAX(CASE WHEN label IN ('Module', 'Form', 'File')
                                      AND COALESCE(NULLIF(end_line, 0), NULLIF(line_end, 0)) >= COALESCE(NULLIF(start_line, 0), NULLIF(line_start, 0), 1)
                                 THEN COALESCE(NULLIF(end_line, 0), NULLIF(line_end, 0)) - COALESCE(NULLIF(start_line, 0), NULLIF(line_start, 0), 1) + 1 END),
                            MAX(COALESCE(NULLIF(end_line, 0), NULLIF(line_end, 0))))
                    END
                ) AS file_loc
                FROM nodes
                WHERE file IS NOT NULL AND file <> ''
                GROUP BY file);";
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------ métricas calculadas del grafo (auditoría H-2)

    /// <summary>Etiquetas de método para las métricas calculadas por <c>CALLS</c>.</summary>
    private const string MethodLabels = "'Method', 'Function', 'Constructor', 'Sub'";

    /// <summary>Etiquetas de tipo para las métricas calculadas por <c>DEFINES_METHOD</c>.</summary>
    private const string TypeLabels = "'Class', 'Interface', 'Struct', 'Record', 'Enum', 'Trait'";

    /// <summary>Procedencia de un valor que es propiedad del nodo en el grafo.</summary>
    public const string SourceGraph = "grafo";

    /// <summary>Procedencia de <c>method_count</c> calculado.</summary>
    public const string SourceDefinesMethod = "calculado: aristas DEFINES_METHOD";

    /// <summary>Procedencia de la LOC calculada.</summary>
    public const string SourceLineRange = "calculado: rango de líneas del nodo (incluye comentarios y líneas en blanco)";

    /// <summary>Procedencia de <c>cyclomatic_total</c> calculado.</summary>
    public const string SourceCyclomaticSum = "calculado: suma de cyclomatic de sus métodos (DEFINES_METHOD)";

    /// <summary>Procedencia de <c>fan_in</c>/<c>fan_out</c> calculados; <c>{0}</c> son los enriquecedores de las aristas.</summary>
    public const string SourceResolvedCallsFormat = "calculado: aristas CALLS resueltas por {0}";

    /// <summary>Propiedades del nodo o <c>{}</c> si no son JSON válido, para <c>json_each</c>, que falla con JSON inválido.</summary>
    private static string SafeJson(string column) => $"(CASE WHEN json_valid({column}) THEN {column} ELSE '{{}}' END)";

    /// <summary>
    /// Arista resuelta por un enriquecedor: lleva <c>enriched_by</c> con alguno que no sea el fallback del LLM, cuyas
    /// aristas son una estimación (H-3). Las de cbm no llevan la marca.
    /// </summary>
    private static string ResolvedEdgePredicate(string column) =>
        $"EXISTS (SELECT 1 FROM json_each({SafeJson(column)}, '$.enriched_by') m WHERE m.value <> '{SemanticEnrichmentStep.EnricherName}')";

    // fan_in / fan_out / method_count / LOC / cyclomatic_total efectivos: la propiedad del grafo si existe; si no, el cálculo.
    private const string FanInExpr = "coalesce(CAST(json_extract(n.properties, '$.fan_in') AS INTEGER), fm.calc_fan_in)";
    private const string FanInSourceExpr = "CASE WHEN json_extract(n.properties, '$.fan_in') IS NOT NULL THEN @src_graph WHEN fm.calc_fan_in IS NOT NULL THEN @src_calls END";
    private const string FanOutExpr = "coalesce(CAST(json_extract(n.properties, '$.fan_out') AS INTEGER), fm.calc_fan_out)";
    private const string FanOutSourceExpr = "CASE WHEN json_extract(n.properties, '$.fan_out') IS NOT NULL THEN @src_graph WHEN fm.calc_fan_out IS NOT NULL THEN @src_calls END";
    private const string MethodCountExpr = "coalesce(CAST(json_extract(n.properties, '$.method_count') AS INTEGER), fm.calc_method_count)";
    private const string MethodCountSourceExpr = "CASE WHEN json_extract(n.properties, '$.method_count') IS NOT NULL THEN @src_graph WHEN fm.calc_method_count IS NOT NULL THEN @src_members END";
    private const string LineSpanExpr =
        "CASE WHEN coalesce(nullif(n.line_start, 0), n.start_line, 0) > 0 AND coalesce(nullif(n.line_end, 0), n.end_line, 0) >= coalesce(nullif(n.line_start, 0), n.start_line) " +
        "THEN coalesce(nullif(n.line_end, 0), n.end_line) - coalesce(nullif(n.line_start, 0), n.start_line) + 1 END";
    private const string LocExpr = "coalesce(CAST(json_extract(n.properties, '$.loc_total') AS INTEGER), " + LineSpanExpr + ")";
    private const string LocSourceExpr = "CASE WHEN json_extract(n.properties, '$.loc_total') IS NOT NULL THEN @src_graph WHEN (" + LineSpanExpr + ") IS NOT NULL THEN @src_lines END";
    private const string CyclomaticTotalExpr = "coalesce(CAST(json_extract(n.properties, '$.cyclomatic_total') AS INTEGER), fm.calc_cyclomatic_total)";
    private const string CyclomaticTotalSourceExpr = "CASE WHEN json_extract(n.properties, '$.cyclomatic_total') IS NOT NULL THEN @src_graph WHEN fm.calc_cyclomatic_total IS NOT NULL THEN @src_cyclomatic END";
    private const string StartLineExpr = "coalesce(nullif(n.line_start, 0), n.start_line, 1)";
    private const string EndLineExpr = "coalesce(nullif(n.line_end, 0), n.end_line, 0)";
    private const string MetricsJoin = "FROM nodes n LEFT JOIN temp.facet_metrics fm ON fm.node_id = n.id";

    /// <summary>
    /// Calcula en tablas temporales de la conexión (no toca el fichero) las métricas que el grafo permite derivar cuando
    /// faltan las propiedades: <c>fan_in</c>/<c>fan_out</c> de métodos y tipos por <c>CALLS</c> resueltas (solo de los nodos
    /// que marcó el mismo enriquecedor que escribió esas aristas: en el resto, una llamada sin arista no prueba nada),
    /// <c>method_count</c> por <c>DEFINES_METHOD</c> y <c>cyclomatic_total</c> como suma de los métodos, si todos la tienen.
    /// La LOC por rango de líneas se calcula en cada consulta. Devuelve los parámetros de procedencia de las facetas.
    /// Un tipo cuenta como llamante o llamado solo si el método es de otro tipo: sus llamadas internas no son acoplamiento.
    /// </summary>
    internal static async Task<Dictionary<string, object>> PrepareDerivedMetricsAsync(SqliteConnection conn, CancellationToken ct)
    {
        await ExecuteAsync(conn, """
            DROP TABLE IF EXISTS temp.facet_metrics;
            CREATE TEMP TABLE facet_metrics(node_id INTEGER PRIMARY KEY, calc_fan_in INTEGER, calc_fan_out INTEGER,
                                            calc_method_count INTEGER, calc_cyclomatic_total INTEGER);
            DROP TABLE IF EXISTS temp.facet_members;
            CREATE TEMP TABLE facet_members AS SELECT DISTINCT source_id AS type_id, target_id AS method_id FROM edges WHERE type = 'DEFINES_METHOD';
            CREATE INDEX temp.facet_members_method ON facet_members(method_id);
            CREATE INDEX temp.facet_members_type ON facet_members(type_id);
            """, ct);

        // method_count y cyclomatic_total (este, solo si todos los métodos del tipo tienen cyclomatic).
        await ExecuteAsync(conn, $"""
            INSERT INTO facet_metrics(node_id, calc_method_count, calc_cyclomatic_total)
            SELECT m.type_id, COUNT(1),
                   CASE WHEN COUNT(json_extract({SafeJson("n.properties")}, '$.cyclomatic')) = COUNT(1)
                        THEN SUM(CAST(json_extract({SafeJson("n.properties")}, '$.cyclomatic') AS INTEGER)) END
            FROM facet_members m JOIN nodes n ON n.id = m.method_id
            WHERE 1
            GROUP BY m.type_id;
            """, ct);

        var enrichers = new List<string>();
        await ExecuteAsync(conn, $"""
            DROP TABLE IF EXISTS temp.facet_calls;
            CREATE TEMP TABLE facet_calls AS
                SELECT DISTINCT e.source_id, e.target_id FROM edges e WHERE e.type = 'CALLS' AND {ResolvedEdgePredicate("e.properties")};
            CREATE INDEX temp.facet_calls_target ON facet_calls(target_id);
            CREATE INDEX temp.facet_calls_source ON facet_calls(source_id);
            DROP TABLE IF EXISTS temp.facet_call_enrichers;
            CREATE TEMP TABLE facet_call_enrichers AS
                SELECT DISTINCT m.value AS enricher FROM edges e, json_each({SafeJson("e.properties")}, '$.enriched_by') m
                WHERE e.type = 'CALLS' AND m.value <> '{SemanticEnrichmentStep.EnricherName}';
            """, ct);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT enricher FROM temp.facet_call_enrichers ORDER BY enricher;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                enrichers.Add(reader.GetString(0));
        }

        if (enrichers.Count > 0)
        {
            await ExecuteAsync(conn, $"""
                DROP TABLE IF EXISTS temp.facet_covered;
                CREATE TEMP TABLE facet_covered AS
                    SELECT n.id, n.label FROM nodes n
                    WHERE n.label IN ({MethodLabels}, {TypeLabels})
                      AND EXISTS (SELECT 1 FROM json_each({SafeJson("n.properties")}, '$.enriched_by') m
                                  WHERE m.value IN (SELECT enricher FROM temp.facet_call_enrichers));

                INSERT INTO facet_metrics(node_id, calc_fan_in, calc_fan_out)
                SELECT c.id,
                       (SELECT COUNT(1) FROM temp.facet_calls f WHERE f.target_id = c.id),
                       (SELECT COUNT(1) FROM temp.facet_calls f WHERE f.source_id = c.id)
                FROM temp.facet_covered c WHERE c.label IN ({MethodLabels})
                ON CONFLICT(node_id) DO UPDATE SET calc_fan_in = excluded.calc_fan_in, calc_fan_out = excluded.calc_fan_out;

                INSERT INTO facet_metrics(node_id, calc_fan_in, calc_fan_out)
                SELECT c.id,
                       (SELECT COUNT(DISTINCT f.source_id) FROM temp.facet_members m JOIN temp.facet_calls f ON f.target_id = m.method_id
                        WHERE m.type_id = c.id AND f.source_id NOT IN (SELECT method_id FROM temp.facet_members WHERE type_id = c.id)),
                       (SELECT COUNT(DISTINCT f.target_id) FROM temp.facet_members m JOIN temp.facet_calls f ON f.source_id = m.method_id
                        WHERE m.type_id = c.id AND f.target_id NOT IN (SELECT method_id FROM temp.facet_members WHERE type_id = c.id))
                FROM temp.facet_covered c WHERE c.label IN ({TypeLabels})
                ON CONFLICT(node_id) DO UPDATE SET calc_fan_in = excluded.calc_fan_in, calc_fan_out = excluded.calc_fan_out;
                """, ct);
        }

        return new Dictionary<string, object>
        {
            ["@src_graph"] = SourceGraph,
            ["@src_calls"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, SourceResolvedCallsFormat,
                enrichers.Count > 0 ? string.Join(", ", enrichers) : "—"),
            ["@src_members"] = SourceDefinesMethod,
            ["@src_lines"] = SourceLineRange,
            ["@src_cyclomatic"] = SourceCyclomaticSum
        };
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Forwards de Struts sin destino en el grafo: no son aristas, así que salen de la última pasada <c>ok</c> del plugin en
    /// <c>enrichment_runs</c> (<c>details.forwards.unresolved</c> y la muestra <c>details.unresolved_forwards_sample</c>, en
    /// orden de lectura de la configuración). <c>[]</c> sin tabla o sin pasada.
    /// </summary>
    internal static async Task<string> UnresolvedForwardsAsync(SqliteConnection conn, int limit, CancellationToken ct)
    {
        try
        {
            if (!await Enrichment.EnrichmentRuns.TableExistsAsync(conn, "enrichment_runs", ct))
                return "[]";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT details FROM enrichment_runs WHERE enricher = @enricher AND status = @ok ORDER BY id DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("@enricher", Enrichment.Web.StrutsJspWriter.EnricherName);
            cmd.Parameters.AddWithValue("@ok", Enrichment.EnrichmentRuns.StatusOk);
            if (await cmd.ExecuteScalarAsync(ct) is not string details
                || System.Text.Json.Nodes.JsonNode.Parse(details) is not System.Text.Json.Nodes.JsonObject root)
                return "[]";

            var total = root["forwards"]?["unresolved"]?.GetValue<int>() ?? 0;
            if (total == 0)
                return "[]";
            var sample = root["unresolved_forwards_sample"] as System.Text.Json.Nodes.JsonArray ?? [];
            var shown = sample.Take(limit).Select(s => s?.DeepClone()).ToArray();
            var result = new System.Text.Json.Nodes.JsonObject
            {
                ["total_unresolved"] = total,
                ["shown"] = shown.Length,
                ["source"] = $"enrichment_runs: última pasada ok de {Enrichment.Web.StrutsJspWriter.EnricherName}; muestra en orden de lectura de la configuración",
                ["forwards"] = new System.Text.Json.Nodes.JsonArray(shown)
            };
            return result.ToJsonString(FacetJsonOptions);
        }
        catch (Exception ex)
        {
            return FacetErrorNote(ex);
        }
    }

    /// <summary>
    /// Serialización de las facetas: indentada, como siempre, y sin escapar los caracteres no ASCII (tildes de nombres y de
    /// los motivos), que van a un prompt y no a HTML.
    /// </summary>
    private static readonly JsonSerializerOptions FacetJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Lo que recibe el auditor si una consulta falla: no es un conjunto vacío y lo dice.</summary>
    private static string FacetErrorNote(Exception ex) =>
        $"[] /* Faceta no disponible por un error SQL ({ex.Message}): no es un conjunto vacío, es un dato desconocido */";

    private static async Task<string> QueryToJsonAsync(
        SqliteConnection conn,
        string query,
        CancellationToken ct,
        string? repositoryPath = null,
        bool attachSnippets = false,
        int maxSnippetLines = 60,
        int maxSnippetItems = 10,
        IReadOnlyDictionary<string, object>? parameters = null)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = query;
            foreach (var (name, value) in parameters ?? new Dictionary<string, object>())
            {
                if (query.Contains(name, StringComparison.Ordinal))
                    cmd.Parameters.AddWithValue(name, value);
            }
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Dictionary<string, object?>>();
            int snippetsAttached = 0;

            while (await reader.ReadAsync(ct))
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    dict[name] = val;
                }

                int startLine = 0;
                if (dict.TryGetValue("start_line", out var slVal) && slVal != null)
                {
                    _ = int.TryParse(slVal.ToString(), out startLine);
                }
                else if (dict.TryGetValue("line_start", out var lsVal) && lsVal != null)
                {
                    _ = int.TryParse(lsVal.ToString(), out startLine);
                    dict["start_line"] = startLine;
                }

                int endLine = 0;
                if (dict.TryGetValue("end_line", out var elVal) && elVal != null)
                {
                    _ = int.TryParse(elVal.ToString(), out endLine);
                }
                else if (dict.TryGetValue("line_end", out var leVal) && leVal != null)
                {
                    _ = int.TryParse(leVal.ToString(), out endLine);
                    dict["end_line"] = endLine;
                }

                if (endLine <= 0 && startLine > 0)
                {
                    endLine = startLine + 35;
                    dict["end_line"] = endLine;
                }

                // Adjuntar code_snippet real del disco si se solicita
                if (attachSnippets && snippetsAttached < maxSnippetItems && !string.IsNullOrWhiteSpace(repositoryPath))
                {
                    var codeSnippet = dict.TryGetValue("code_snippet", out var csVal) ? csVal?.ToString() : null;
                    if (string.IsNullOrWhiteSpace(codeSnippet) && dict.TryGetValue("file", out var fVal) && fVal != null)
                    {
                        var fileStr = fVal.ToString();
                        if (!string.IsNullOrWhiteSpace(fileStr) && startLine > 0)
                        {
                            var snippet = await TryReadSnippetAsync(repositoryPath, fileStr, startLine, endLine, maxSnippetLines, ct);
                            if (!string.IsNullOrWhiteSpace(snippet))
                            {
                                dict["code_snippet"] = snippet;
                                snippetsAttached++;
                            }
                        }
                    }
                }

                list.Add(dict);
            }

            return list.Count > 0 
                ? JsonSerializer.Serialize(list, FacetJsonOptions) 
                : "[]";
        }
        catch (Exception ex)
        {
            return FacetErrorNote(ex);
        }
    }

    private static async Task<string?> TryReadSnippetAsync(
        string repoPath, 
        string relOrFullPath, 
        int startLine, 
        int endLine, 
        int maxLines, 
        CancellationToken ct)
    {
        try
        {
            var candidates = new[]
            {
                relOrFullPath,
                Path.Combine(repoPath, relOrFullPath),
                Path.Combine(Directory.GetCurrentDirectory(), relOrFullPath)
            };

            var targetPath = candidates.FirstOrDefault(File.Exists);
            if (targetPath == null) return null;

            var allLines = await AITaskAgent.FileTools.TextFileEncoding.ReadAllLinesAsync(targetPath, ct);
            if (allLines.Length == 0) return null;

            int s = Math.Max(1, startLine) - 1;
            int effectiveEnd = endLine > 0 ? endLine : (startLine + maxLines);
            int e = Math.Min(allLines.Length, Math.Min(effectiveEnd, startLine + maxLines - 1)) - 1;

            if (s > e || s >= allLines.Length) return null;

            var sb = new StringBuilder();
            for (int i = s; i <= e; i++)
            {
                sb.AppendLine($"{i + 1}: {allLines[i]}");
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> DetectEcosystemAsync(SqliteConnection conn, string repoPath, CancellationToken ct)
    {
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Analizar extensiones de archivos en la tabla nodes
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT file FROM nodes WHERE file IS NOT NULL AND file != '';";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var filePath = reader.GetString(0);
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                switch (ext)
                {
                    case ".cs":
                        languages.Add("C# (.NET)");
                        break;
                    case ".accdb":
                    case ".mdb":
                        languages.Add("Microsoft Access Database");
                        break;
                    case ".bas":
                    case ".cls":
                    case ".frm":
                    case ".vba":
                        languages.Add("VBA (Visual Basic for Applications)");
                        break;
                    case ".py":
                        languages.Add("Python");
                        break;
                    case ".ts":
                    case ".tsx":
                        languages.Add("TypeScript");
                        break;
                    case ".js":
                    case ".jsx":
                        languages.Add("JavaScript");
                        break;
                    case ".java":
                        languages.Add("Java");
                        break;
                    case ".go":
                        languages.Add("Go");
                        break;
                    case ".cpp":
                    case ".c":
                    case ".h":
                    case ".hpp":
                        languages.Add("C/C++");
                        break;
                    case ".sql":
                        languages.Add("SQL / T-SQL");
                        break;
                    case ".docx":
                    case ".xlsx":
                        languages.Add("Microsoft Office (Word / Excel)");
                        break;
                    case ".php":
                        languages.Add("PHP");
                        break;
                    case ".rs":
                        languages.Add("Rust");
                        break;
                }
            }
        }
        catch { }

        // 2. Analizar etiquetas (labels) de nodos
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT label FROM nodes WHERE label IS NOT NULL;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var label = reader.GetString(0);
                if (label.StartsWith("Access", StringComparison.OrdinalIgnoreCase))
                {
                    languages.Add("Microsoft Access Database");
                }
                else if (label.StartsWith("Vba", StringComparison.OrdinalIgnoreCase))
                {
                    languages.Add("VBA (Visual Basic for Applications)");
                }
                else if (label.Equals("CsProject", StringComparison.OrdinalIgnoreCase))
                {
                    languages.Add("C# (.NET)");
                }
            }
        }
        catch { }

        // 3. Fallback: Inspeccionar sistema de archivos si el grafo no tiene extensiones
        if (languages.Count == 0 && Directory.Exists(repoPath))
        {
            try
            {
                if (Directory.EnumerateFiles(repoPath, "*.cs", SearchOption.AllDirectories).Any())
                    languages.Add("C# (.NET)");
                if (Directory.EnumerateFiles(repoPath, "*.accdb", SearchOption.AllDirectories).Any() ||
                    Directory.EnumerateFiles(repoPath, "*.mdb", SearchOption.AllDirectories).Any())
                    languages.Add("Microsoft Access / VBA");
                if (Directory.EnumerateFiles(repoPath, "*.py", SearchOption.AllDirectories).Any())
                    languages.Add("Python");
                if (Directory.EnumerateFiles(repoPath, "*.ts", SearchOption.AllDirectories).Any())
                    languages.Add("TypeScript");
                if (Directory.EnumerateFiles(repoPath, "*.js", SearchOption.AllDirectories).Any())
                    languages.Add("JavaScript");
            }
            catch { }
        }

        return languages.Count > 0 ? string.Join(", ", languages) : "Políglota / No determinado";
    }

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is ReportFacetInput, (string?)null));
}
