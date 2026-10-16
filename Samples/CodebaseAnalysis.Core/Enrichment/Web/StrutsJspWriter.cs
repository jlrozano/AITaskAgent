using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment.Web;

/// <summary>Recuentos propios del plugin Struts/JSP, además de los de <c>enrichment_runs</c>.</summary>
public sealed record StrutsJspStats
{
    /// <summary>Acciones de Struts con nodo <c>Route</c>.</summary>
    public int Actions { get; init; }

    /// <summary>Acciones con un <c>path</c> ya declarado en otro <c>struts-config</c> (o en el mismo): se queda la primera.</summary>
    public int DuplicateActions { get; init; }

    /// <summary>JSP con nodo <c>Jsp</c>.</summary>
    public int Jsps { get; init; }

    public int JspsWithScriptlets { get; init; }
    public int Scriptlets { get; init; }
    public int Suspicious { get; init; }
    public int TilesDefinitions { get; init; }
    public int DuplicateDefinitions { get; init; }

    /// <summary>Forwards globales: se cuentan, no generan arista (P14).</summary>
    public int GlobalForwards { get; init; }

    /// <summary>Forwards locales, <c>input</c> y atributos <c>forward</c>/<c>include</c> de acción con destino en el grafo.</summary>
    public int ForwardsResolved { get; init; }

    /// <summary>Forwards cuyo destino (JSP, acción o definición) no existe: se cuentan y no generan arista.</summary>
    public int ForwardsUnresolved { get; init; }

    public int HandlesEdges { get; init; }

    /// <summary>Acciones con <c>type</c> cuya clase no está en el grafo (p. ej. las de Struts o de un jar).</summary>
    public int ActionClassesUnresolved { get; init; }

    public int FormsResolved { get; init; }
    public int FormsUnresolved { get; init; }
    public int IncludesResolved { get; init; }
    public int IncludesUnresolved { get; init; }

    /// <summary>Includes y formularios con destino calculado en ejecución: no se pueden resolver sin ejecutar.</summary>
    public int DynamicReferences { get; init; }

    public int RendersEdges { get; init; }
    public int ExtendsEdges { get; init; }

    /// <summary>Plantillas, <c>put</c> de página o <c>extends</c> de Tiles cuyo destino no existe.</summary>
    public int TilesReferencesUnresolved { get; init; }

    public int UnreadableConfigs { get; init; }
    public int UnreadableTiles { get; init; }
    public int UnreadableJsps { get; init; }

    /// <summary>Nodos que no se crearon porque su QN ya lo ocupaba otro nodo del grafo.</summary>
    public int QnConflicts { get; init; }

    public int PreviousNodesRemoved { get; init; }
    public int PreviousEdgesRemoved { get; init; }
}

/// <summary>Resultado de <see cref="StrutsJspWriter.ApplyAsync"/>.</summary>
public sealed record StrutsJspWriteResult(EnrichmentOutcome Outcome, StrutsJspStats Stats);

/// <summary>
/// Plugin estructural de Struts 1.x, Tiles y JSP (P14): lee los <c>struts-config*.xml</c>, las definiciones de Tiles y las
/// JSP sin compilar nada y escribe sobre la copia del grafo, en una transacción, los nodos <c>Route</c>, <c>Jsp</c> y
/// <c>TilesDefinition</c> y las aristas <c>HANDLES</c>, <c>FORWARDS_TO</c>, <c>SUBMITS_TO</c>, <c>INCLUDES</c>,
/// <c>RENDERS</c> y <c>EXTENDS_DEFINITION</c>, con la marca de P12 y la fila <c>ok</c> de <c>enrichment_runs</c>. No
/// depende del analizador Java: la clase de cada acción se busca en los nodos <c>Class</c> de cbm por nombre y ruta.
/// </summary>
public static class StrutsJspWriter
{
    public const string EnricherName = "StrutsJspPlugin";

    /// <summary>
    /// Versión de las reglas de escritura; cambiarla invalida las pasadas <c>ok</c> anteriores (P3). La 2 separa los imports
    /// de negocio, que solo marcan la JSP si los usa un scriptlet.
    /// </summary>
    public const string EnricherVersion = "2";

    public const string RouteLabel = "Route";
    public const string JspLabel = "Jsp";
    public const string TilesDefinitionLabel = "TilesDefinition";

    /// <summary>Ejemplos de forwards sin resolver que se guardan en <c>details</c>.</summary>
    private const int UnresolvedSamples = 200;

    /// <summary>Señal del plugin: está activado y el repositorio tiene algún <c>struts-config*.xml</c>.</summary>
    public static bool IsApplicable(RepositoryScanResult scan, StrutsJspOptions options) =>
        options.Enabled && scan.StrutsConfigs.Count > 0;

    /// <summary>
    /// Huella de las opciones que cambian el resultado: la codificación por defecto, los patrones de import y la última
    /// pasada <c>ok</c> del analizador Java. Lo último hace que el plugin se repita cuando el analizador Java acaba de
    /// reescribir el grafo: sus pasadas borran y recrean nodos, y con ellos las <c>HANDLES</c> que salen de las clases de
    /// acción.
    /// </summary>
    /// <param name="options">Sección <c>StrutsJsp</c>.</param>
    /// <param name="defaultEncoding">Codificación de las JSP que no declaran la suya (P6).</param>
    /// <param name="lastJavaRunId">Id de la última fila <c>ok</c> de <c>JavaAnalyzer</c> en <c>enrichment_runs</c>, o null si no hay.</param>
    public static string ConfigFingerprint(StrutsJspOptions options, Encoding defaultEncoding, long? lastJavaRunId = null) =>
        EnrichmentRuns.ConfigFingerprint([
            defaultEncoding.WebName,
            "java-run:" + (lastJavaRunId?.ToString(CultureInfo.InvariantCulture) ?? "none"),
            .. options.SuspiciousImportPatterns.Select(p => "strong:" + p),
            .. options.BusinessImportPatterns.Select(p => "business:" + p)
        ]);

    /// <param name="dbPath">La copia del análisis; nunca la BD de <c>~/.cache/codebase-memory-mcp</c>.</param>
    /// <param name="scan">Recorrido del repositorio: <c>struts-config</c>, Tiles, JSP y raíces web.</param>
    /// <param name="options">Sección <c>StrutsJsp</c>: los patrones de import sospechosos.</param>
    /// <param name="defaultEncoding">Codificación de las JSP que no declaran la suya y no son UTF-8 válido (P6).</param>
    /// <param name="configFingerprint">Huella de configuración para <c>enrichment_runs</c> (<see cref="ConfigFingerprint"/>).</param>
    /// <param name="startedAt">Inicio de la pasada: <c>started_at</c>, <c>enriched_at</c> y base de <c>duration_ms</c>.</param>
    /// <param name="ct">Cancelación; antes de confirmar la transacción no deja nada escrito.</param>
    /// <exception cref="InvalidOperationException">Si la BD es la de cbm o un patrón de import no es una expresión válida.</exception>
    public static async Task<StrutsJspWriteResult> ApplyAsync(
        string dbPath,
        RepositoryScanResult scan,
        StrutsJspOptions options,
        Encoding defaultEncoding,
        string configFingerprint,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        EnrichmentMarks.AssertWorkingCopy(dbPath);
        var stopwatch = Stopwatch.StartNew();

        // Toda la lectura va antes de abrir la BD: un fichero ilegible se cuenta y no deja media pasada.
        var input = Read(scan, options, defaultEncoding, ct);

        await GraphSchema.EnsureAsync(dbPath, ct);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        await EnrichmentRuns.EnsureAsync(conn, ct);

        using var tx = conn.BeginTransaction();
        using var pass = new Pass(conn, tx, await GraphProject.ResolveAsync(conn, "Codebase", ct, tx), startedAt, scan);
        var result = await pass.RunAsync(input, ct);

        var runId = await EnrichmentRuns.InsertAsync(conn, new EnrichmentRun
        {
            Enricher = EnricherName,
            EnricherVersion = EnricherVersion,
            IndexFingerprint = await EnrichmentRuns.IndexFingerprintAsync(conn, ct, tx) ?? "",
            ConfigFingerprint = configFingerprint,
            StartedAt = startedAt,
            DurationMs = Math.Max(stopwatch.ElapsedMilliseconds, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds),
            Status = EnrichmentRuns.StatusOk,
            Outcome = result.Outcome
        }, ct, tx);

        tx.Commit();
        return result with { Outcome = result.Outcome with { RunId = runId } };
    }

    // ---------------------------------------------------------------- lectura

    private sealed record ReadInput(
        List<StrutsConfigDocument> Configs,
        List<TilesDefinitionsDocument> Tiles,
        List<(string Path, JspScanResult Scan)> Jsps,
        List<string> UnreadableConfigs,
        List<string> UnreadableTiles,
        List<string> UnreadableJsps);

    private static ReadInput Read(RepositoryScanResult scan, StrutsJspOptions options, Encoding defaultEncoding, CancellationToken ct)
    {
        var scanner = new JspScanner(options.SuspiciousImportPatterns, options.BusinessImportPatterns);
        var configs = new List<StrutsConfigDocument>();
        var tiles = new List<TilesDefinitionsDocument>();
        var jsps = new List<(string, JspScanResult)>();
        var unreadableConfigs = new List<string>();
        var unreadableTiles = new List<string>();
        var unreadableJsps = new List<string>();

        foreach (var path in scan.StrutsConfigs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                configs.Add(StrutsConfigParser.ParseFile(path, scan.FullPath(path)));
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                unreadableConfigs.Add($"{path}: {ex.Message}");
            }
        }

        foreach (var path in scan.TilesDefinitions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                tiles.Add(TilesDefinitionsParser.ParseFile(path, scan.FullPath(path)));
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                unreadableTiles.Add($"{path}: {ex.Message}");
            }
        }

        foreach (var path in scan.JspFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                jsps.Add((path, scanner.ScanFile(scan.FullPath(path), defaultEncoding)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadableJsps.Add($"{path}: {ex.Message}");
            }
        }

        return new ReadInput(configs, tiles, jsps, unreadableConfigs, unreadableTiles, unreadableJsps);
    }

    // ---------------------------------------------------------------- escritura

    /// <summary>Datos de una arista acumulados antes de escribirla: <c>edges</c> admite una sola por (origen, destino, tipo).</summary>
    private sealed class EdgeData
    {
        public List<string> Names { get; } = [];
        public List<int> Lines { get; } = [];
        public List<string> Kinds { get; } = [];
        public bool Redirect { get; set; }
        public string? RoutePath { get; set; }

        public static void AddDistinct<T>(List<T> list, T value)
        {
            if (!list.Contains(value))
                list.Add(value);
        }
    }

    private sealed record ClassNode(long Id, string FilePath);

    /// <summary>Las clases Java del grafo por nombre simple y, si las escribió el analizador Java, por FQN exacto de javac.</summary>
    private sealed class ClassIndex
    {
        public Dictionary<string, List<ClassNode>> ByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> ByFqn { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Pass(SqliteConnection conn, SqliteTransaction tx, string project, DateTimeOffset enrichedAt, RepositoryScanResult scan)
        : IDisposable
    {
        private readonly Dictionary<string, long> _jspIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _jspPathsIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _routeIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _definitionIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedQns = new(StringComparer.Ordinal);
        private readonly Dictionary<(long Source, long Target, string Type), EdgeData> _edges = [];
        private readonly List<(long Source, long Target, string Type)> _edgeOrder = [];
        private readonly List<JsonObject> _unresolvedSamples = [];
        private SqliteCommand? _insertNode;
        private SqliteCommand? _insertEdge;

        private int _nodesCreated, _edgesCreated, _qnConflicts;
        private int _actions, _duplicateActions, _definitions, _duplicateDefinitions, _globalForwards;
        private int _forwardsResolved, _forwardsUnresolved, _actionClassesUnresolved;
        private int _formsResolved, _formsUnresolved, _includesResolved, _includesUnresolved, _tilesUnresolved;

        public async Task<StrutsJspWriteResult> RunAsync(ReadInput input, CancellationToken ct)
        {
            // P12: la salida de la pasada anterior de este enriquecedor, con las HANDLES que llegaban a sus Route.
            var (previousNodes, previousEdges) = await EnrichmentMarks.RemovePreviousOutputAsync(conn, tx, EnricherName, ct);

            await CreateJspNodesAsync(input.Jsps, ct);
            var definitions = await CreateDefinitionNodesAsync(input.Tiles, ct);
            var actions = await CreateRouteNodesAsync(input.Configs, ct);

            await LinkActionsAsync(actions, ct);
            LinkJsps(input.Jsps);
            LinkDefinitions(definitions);
            var edgeCounts = await FlushEdgesAsync(ct);

            var jsps = input.Jsps.Where(j => _jspIds.ContainsKey(j.Path)).Select(j => j.Scan).ToList();
            var stats = new StrutsJspStats
            {
                Actions = _actions,
                DuplicateActions = _duplicateActions,
                Jsps = jsps.Count,
                JspsWithScriptlets = jsps.Count(j => j.ScriptletCount > 0),
                Scriptlets = jsps.Sum(j => j.ScriptletCount),
                Suspicious = jsps.Count(j => j.Suspicious),
                TilesDefinitions = _definitions,
                DuplicateDefinitions = _duplicateDefinitions,
                GlobalForwards = _globalForwards,
                ForwardsResolved = _forwardsResolved,
                ForwardsUnresolved = _forwardsUnresolved,
                HandlesEdges = edgeCounts.GetValueOrDefault("HANDLES"),
                ActionClassesUnresolved = _actionClassesUnresolved,
                FormsResolved = _formsResolved,
                FormsUnresolved = _formsUnresolved,
                IncludesResolved = _includesResolved,
                IncludesUnresolved = _includesUnresolved,
                DynamicReferences = jsps.Sum(j => j.DynamicReferences),
                RendersEdges = edgeCounts.GetValueOrDefault("RENDERS"),
                ExtendsEdges = edgeCounts.GetValueOrDefault("EXTENDS_DEFINITION"),
                TilesReferencesUnresolved = _tilesUnresolved,
                UnreadableConfigs = input.UnreadableConfigs.Count,
                UnreadableTiles = input.UnreadableTiles.Count,
                UnreadableJsps = input.UnreadableJsps.Count,
                QnConflicts = _qnConflicts,
                PreviousNodesRemoved = previousNodes,
                PreviousEdgesRemoved = previousEdges
            };

            var unresolved = new JsonObject
            {
                ["forwards"] = _forwardsUnresolved,
                ["action_classes"] = _actionClassesUnresolved,
                ["forms"] = _formsUnresolved,
                ["includes"] = _includesUnresolved,
                ["tiles"] = _tilesUnresolved,
                ["dynamic"] = stats.DynamicReferences
            };
            var diagnostics = new JsonObject
            {
                ["unreadable_configs"] = ToArray(input.UnreadableConfigs),
                ["unreadable_tiles"] = ToArray(input.UnreadableTiles),
                ["unreadable_jsps"] = ToArray(input.UnreadableJsps),
                ["duplicate_actions"] = _duplicateActions,
                ["duplicate_definitions"] = _duplicateDefinitions,
                ["qn_conflicts"] = _qnConflicts
            };
            var edgesByType = new JsonObject();
            foreach (var (type, count) in edgeCounts.OrderBy(e => e.Key, StringComparer.Ordinal))
                edgesByType[type] = count;
            var details = new JsonObject
            {
                ["struts_configs"] = input.Configs.Count,
                ["tiles_files"] = input.Tiles.Count,
                ["actions"] = _actions,
                ["jsps"] = stats.Jsps,
                ["jsps_with_scriptlets"] = stats.JspsWithScriptlets,
                ["scriptlets"] = stats.Scriptlets,
                ["suspicious"] = stats.Suspicious,
                ["tiles_definitions"] = _definitions,
                ["global_forwards"] = _globalForwards,
                ["forwards"] = new JsonObject { ["resolved"] = _forwardsResolved, ["unresolved"] = _forwardsUnresolved },
                ["forms"] = new JsonObject { ["resolved"] = _formsResolved, ["unresolved"] = _formsUnresolved },
                ["includes"] = new JsonObject { ["resolved"] = _includesResolved, ["unresolved"] = _includesUnresolved },
                ["edges"] = edgesByType,
                ["previous_output_removed"] = new JsonObject { ["nodes"] = previousNodes, ["edges"] = previousEdges },
                ["unresolved_forwards_sample"] = new JsonArray(_unresolvedSamples.Select(s => (JsonNode?)s).ToArray())
            };

            var outcome = new EnrichmentOutcome
            {
                FilesAnalyzed = input.Configs.Count + input.Tiles.Count + input.Jsps.Count,
                // Ninguno de estos ficheros tiene nodo en cbm por diseño: cbm no indexa JSP ni XML de Struts.
                FilesUnmatched = 0,
                NodesCreated = _nodesCreated,
                EdgesCreated = _edgesCreated,
                Unresolved = unresolved.ToJsonString(),
                Diagnostics = diagnostics.ToJsonString(),
                Details = details.ToJsonString()
            };
            return new StrutsJspWriteResult(outcome, stats);
        }

        // ------------------------------------------------------------ nodos

        private async Task CreateJspNodesAsync(List<(string Path, JspScanResult Scan)> jsps, CancellationToken ct)
        {
            foreach (var (path, jsp) in jsps)
            {
                // a.jsp y a.jspf darían el mismo QN: la segunda lleva la extensión en el QN.
                var qn = JspQn(path, withExtension: false);
                if (_usedQns.Contains(qn))
                    qn = JspQn(path, withExtension: true);

                var properties = new JsonObject
                {
                    ["scriptlet_count"] = jsp.ScriptletCount,
                    ["scriptlet_lines"] = ToArray(jsp.ScriptletLines),
                    ["expression_count"] = jsp.ExpressionCount,
                    ["declaration_count"] = jsp.DeclarationCount,
                    ["page_imports"] = ToArray(jsp.PageImports),
                    ["business_imports"] = ToArray(jsp.BusinessImports),
                    ["taglibs"] = new JsonArray(jsp.Taglibs
                        .Select(t => (JsonNode?)new JsonObject { ["prefix"] = t.Prefix, ["uri"] = t.Uri }).ToArray()),
                    ["has_sql"] = jsp.HasSql,
                    ["sql_lines"] = ToArray(jsp.SqlLines),
                    ["session_access"] = jsp.SessionAccess,
                    ["suspicious"] = jsp.Suspicious,
                    ["suspicious_reasons"] = ToArray(jsp.SuspiciousReasons),
                    ["encoding"] = jsp.Encoding
                };
                if (await CreateNodeAsync(JspLabel, path, qn, path, 1, jsp.LineCount, properties, ct) is { } id)
                {
                    _jspIds[path] = id;
                    _jspPathsIgnoreCase.TryAdd(path, path);
                }
            }
        }

        private async Task<List<(TilesDefinition Definition, string ConfigFile, long Id)>> CreateDefinitionNodesAsync(
            List<TilesDefinitionsDocument> documents, CancellationToken ct)
        {
            var created = new List<(TilesDefinition, string, long)>();
            foreach (var document in documents)
            {
                foreach (var definition in document.Definitions)
                {
                    if (_definitionIds.ContainsKey(definition.Name))
                    {
                        _duplicateDefinitions++;
                        continue;
                    }

                    var puts = new JsonObject();
                    foreach (var put in definition.Puts)
                        puts[put.Name] = put.Value;
                    var properties = new JsonObject
                    {
                        ["template"] = definition.Template,
                        ["extends"] = definition.Extends,
                        ["puts"] = puts,
                        ["config_file"] = document.ConfigFile
                    };
                    var qn = $"{project}.__tiles__.{definition.Name}";
                    if (await CreateNodeAsync(TilesDefinitionLabel, definition.Name, qn, document.ConfigFile, definition.Line, definition.Line,
                            properties, ct) is not { } id)
                        continue;
                    _definitionIds[definition.Name] = id;
                    _definitions++;
                    created.Add((definition, document.ConfigFile, id));
                }
            }
            return created;
        }

        private async Task<List<(StrutsAction Action, string ConfigFile, string RoutePath, long Id)>> CreateRouteNodesAsync(
            List<StrutsConfigDocument> configs, CancellationToken ct)
        {
            // Los form-bean se buscan primero en su fichero y después en el primero que lo declare.
            var formBeans = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var config in configs)
            {
                foreach (var (name, type) in config.FormBeans)
                    formBeans.TryAdd(name, type);
            }

            var created = new List<(StrutsAction, string, string, long)>();
            foreach (var config in configs)
            {
                _globalForwards += config.GlobalForwards.Count;
                foreach (var action in config.Actions)
                {
                    var routePath = action.Path.StartsWith('/') ? action.Path : "/" + action.Path;
                    if (_routeIds.ContainsKey(routePath))
                    {
                        _duplicateActions++;
                        continue;
                    }

                    string? formType = null;
                    if (action.FormBean != null && !config.FormBeans.TryGetValue(action.FormBean, out formType))
                        formType = formBeans.GetValueOrDefault(action.FormBean);

                    var properties = new JsonObject
                    {
                        ["framework"] = "struts",
                        ["route_path"] = routePath,
                        ["http_verb"] = "ANY",
                        ["http_method"] = "ANY",
                        ["action_type"] = action.Type,
                        ["form_bean"] = action.FormBean,
                        ["form_type"] = formType,
                        ["scope"] = action.Scope,
                        ["input"] = action.Input,
                        ["parameter"] = action.Parameter,
                        ["forward"] = action.Forward,
                        ["include"] = action.Include,
                        ["config_file"] = config.ConfigFile
                    };
                    // Sin prefijo de proyecto, como las Route de cbm y de DotNetEnricher.
                    var qn = $"__route__ANY__{routePath}";
                    if (await CreateNodeAsync(RouteLabel, routePath, qn, config.ConfigFile, action.Line, action.Line, properties, ct) is not { } id)
                        continue;
                    _routeIds[routePath] = id;
                    _actions++;
                    created.Add((action, config.ConfigFile, routePath, id));
                }
            }
            return created;
        }

        // ------------------------------------------------------------ aristas

        private async Task LinkActionsAsync(List<(StrutsAction Action, string ConfigFile, string RoutePath, long Id)> actions, CancellationToken ct)
        {
            var classes = await LoadClassesAsync(ct);
            foreach (var (action, configFile, routePath, routeId) in actions)
            {
                if (action.Type != null)
                {
                    if (FindClass(classes, action.Type) is { } classId)
                        AddEdge(classId, routeId, "HANDLES").RoutePath = routePath;
                    else
                        _actionClassesUnresolved++;
                }

                var forwards = action.Forwards.Select(f => (f.Name, f.Path, f.Redirect, f.Line)).ToList();
                if (action.Input != null)
                    forwards.Add(("input", action.Input, false, action.Line));
                if (action.Forward != null)
                    forwards.Add(("forward", action.Forward, false, action.Line));
                if (action.Include != null)
                    forwards.Add(("include", action.Include, false, action.Line));

                foreach (var (name, path, redirect, line) in forwards)
                {
                    if (ResolveForward(path, configFile) is not { } target)
                    {
                        _forwardsUnresolved++;
                        if (_unresolvedSamples.Count < UnresolvedSamples)
                            _unresolvedSamples.Add(new JsonObject
                            {
                                ["config"] = configFile, ["line"] = line, ["action"] = routePath, ["forward"] = name, ["path"] = path
                            });
                        continue;
                    }

                    _forwardsResolved++;
                    var edge = AddEdge(routeId, target, "FORWARDS_TO");
                    EdgeData.AddDistinct(edge.Names, name);
                    EdgeData.AddDistinct(edge.Lines, line);
                    edge.Redirect |= redirect;
                }
            }
        }

        private void LinkJsps(List<(string Path, JspScanResult Scan)> jsps)
        {
            foreach (var (path, jsp) in jsps)
            {
                if (!_jspIds.TryGetValue(path, out var source))
                    continue;

                foreach (var form in jsp.Forms)
                {
                    if (!_routeIds.TryGetValue(ActionPath(form.Target), out var route))
                    {
                        _formsUnresolved++;
                        continue;
                    }
                    _formsResolved++;
                    EdgeData.AddDistinct(AddEdge(source, route, "SUBMITS_TO").Lines, form.Line);
                }

                foreach (var include in jsp.Includes)
                {
                    if (ResolveJsp(include.Target, path, path) is not { } target)
                    {
                        _includesUnresolved++;
                        continue;
                    }
                    _includesResolved++;
                    var edge = AddEdge(source, target, "INCLUDES");
                    EdgeData.AddDistinct(edge.Kinds, include.Kind);
                    EdgeData.AddDistinct(edge.Lines, include.Line);
                }
            }
        }

        private void LinkDefinitions(List<(TilesDefinition Definition, string ConfigFile, long Id)> definitions)
        {
            foreach (var (definition, configFile, id) in definitions)
            {
                if (definition.Template != null)
                {
                    if (ResolveJsp(definition.Template, null, configFile) is { } template)
                        EdgeData.AddDistinct(AddEdge(id, template, "RENDERS").Names, "template");
                    else
                        _tilesUnresolved++;
                }

                foreach (var put in definition.Puts)
                {
                    if (put.Value == null || string.Equals(put.Type, "string", StringComparison.OrdinalIgnoreCase))
                        continue;

                    long? target = null;
                    var isPage = JspScanner.IsJspPath(put.Value)
                        || put.Type is { } type && (type.Equals("page", StringComparison.OrdinalIgnoreCase) || type.Equals("template", StringComparison.OrdinalIgnoreCase));
                    if (isPage)
                        target = ResolveJsp(put.Value, null, configFile);
                    else if (_definitionIds.TryGetValue(put.Value, out var nested))
                        target = nested;
                    else if (!string.Equals(put.Type, "definition", StringComparison.OrdinalIgnoreCase))
                        continue; // Un valor literal (título, clave de mensajes): no es una referencia.

                    if (target is { } t)
                        EdgeData.AddDistinct(AddEdge(id, t, "RENDERS").Names, $"put:{put.Name}");
                    else
                        _tilesUnresolved++;
                }

                if (definition.Extends != null)
                {
                    if (_definitionIds.TryGetValue(definition.Extends, out var parent))
                        AddEdge(id, parent, "EXTENDS_DEFINITION");
                    else
                        _tilesUnresolved++;
                }
            }
        }

        private EdgeData AddEdge(long source, long target, string type)
        {
            var key = (source, target, type);
            if (!_edges.TryGetValue(key, out var data))
            {
                _edges[key] = data = new EdgeData();
                _edgeOrder.Add(key);
            }
            return data;
        }

        private async Task<Dictionary<string, int>> FlushEdgesAsync(CancellationToken ct)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var key in _edgeOrder)
            {
                var data = _edges[key];
                var properties = key.Type switch
                {
                    "HANDLES" => new JsonObject { ["framework"] = "struts", ["http_method"] = "ANY", ["route_path"] = data.RoutePath },
                    "FORWARDS_TO" => new JsonObject
                    {
                        ["forward_name"] = data.Names[0], ["forward_names"] = ToArray(data.Names), ["redirect"] = data.Redirect, ["lines"] = ToArray(data.Lines)
                    },
                    "SUBMITS_TO" => new JsonObject { ["lines"] = ToArray(data.Lines) },
                    "INCLUDES" => new JsonObject { ["kinds"] = ToArray(data.Kinds), ["lines"] = ToArray(data.Lines) },
                    "RENDERS" => new JsonObject { ["role"] = data.Names[0], ["roles"] = ToArray(data.Names) },
                    _ => new JsonObject()
                };
                if (await InsertEdgeAsync(key.Source, key.Target, key.Type, properties, ct))
                    counts[key.Type] = counts.GetValueOrDefault(key.Type) + 1;
            }
            return counts;
        }

        // ------------------------------------------------------------ resolución

        /// <summary>
        /// Destino de un forward: una JSP (ruta de contexto), otra acción (<c>/x.do</c> o el <c>path</c> de una acción) o una
        /// definición de Tiles (nombre sin barra). Null si no existe en el grafo.
        /// </summary>
        private long? ResolveForward(string path, string configFile)
        {
            var clean = JspScanner.StripQuery(path);
            if (clean.Length == 0)
                return null;
            if (JspScanner.IsJspPath(clean))
                return ResolveJsp(clean, null, configFile);
            if (clean.StartsWith('/') || clean.EndsWith(".do", StringComparison.OrdinalIgnoreCase))
                return _routeIds.TryGetValue(ActionPath(clean), out var route) ? route : null;
            return _definitionIds.TryGetValue(clean, out var definition) ? definition : null;
        }

        /// <summary><c>path</c> de acción de un destino: sin query ni <c>.do</c> y con barra inicial, como lo calcula Struts.</summary>
        private static string ActionPath(string target)
        {
            var clean = JspScanner.StripQuery(target);
            if (clean.EndsWith(".do", StringComparison.OrdinalIgnoreCase))
                clean = clean[..^3];
            return clean.StartsWith('/') ? clean : "/" + clean;
        }

        /// <summary>
        /// JSP de una referencia. Una ruta relativa se resuelve desde la carpeta de <paramref name="currentJsp"/>; una de
        /// contexto (<c>/pages/x.jsp</c>), contra las raíces web, empezando por la que contiene <paramref name="ownerFile"/>.
        /// </summary>
        private long? ResolveJsp(string reference, string? currentJsp, string ownerFile)
        {
            var clean = JspScanner.StripQuery(reference).Replace('\\', '/');
            if (clean.Length == 0)
                return null;

            if (!clean.StartsWith('/') && currentJsp != null)
            {
                var directory = currentJsp.Contains('/') ? currentJsp[..currentJsp.LastIndexOf('/')] : "";
                if (Lookup(Normalize(directory.Length == 0 ? clean : $"{directory}/{clean}")) is { } relative)
                    return relative;
            }

            foreach (var root in WebRootsFor(ownerFile))
            {
                var candidate = root.Length == 0 ? clean.TrimStart('/') : $"{root}/{clean.TrimStart('/')}";
                if (Lookup(Normalize(candidate)) is { } found)
                    return found;
            }
            return null;
        }

        private IEnumerable<string> WebRootsFor(string ownerFile)
        {
            var roots = scan.WebRoots.Count > 0 ? scan.WebRoots : [""];
            // La raíz que contiene el fichero va primero; entre varias, la más profunda.
            return roots
                .OrderByDescending(r => IsUnder(ownerFile, r) ? r.Length + 1 : 0)
                .ThenBy(r => r, StringComparer.Ordinal);
        }

        private static bool IsUnder(string path, string root) =>
            root.Length == 0 || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

        private long? Lookup(string? path)
        {
            if (path == null)
                return null;
            if (_jspIds.TryGetValue(path, out var id))
                return id;
            // En Windows el repositorio no distingue mayúsculas; el servidor de destino quizá sí, pero la referencia existe.
            return _jspPathsIgnoreCase.TryGetValue(path, out var actual) ? _jspIds[actual] : null;
        }

        /// <summary>Resuelve <c>.</c> y <c>..</c>; null si la ruta sale de la raíz del repositorio.</summary>
        private static string? Normalize(string path)
        {
            var parts = new List<string>();
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                    continue;
                if (part == "..")
                {
                    if (parts.Count == 0)
                        return null;
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(part);
            }
            return string.Join('/', parts);
        }

        private async Task<ClassIndex> LoadClassesAsync(CancellationToken ct)
        {
            var classes = new ClassIndex();
            using var select = Command("""
                SELECT id, name, file_path, json_extract(properties, '$.fqn') FROM nodes
                WHERE label = 'Class' AND project = @project AND file_path LIKE '%.java'
                ORDER BY id
                """);
            select.Parameters.AddWithValue("@project", project);
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(1);
                if (!classes.ByName.TryGetValue(name, out var list))
                    classes.ByName[name] = list = [];
                var id = reader.GetInt64(0);
                list.Add(new ClassNode(id, reader.IsDBNull(2) ? "" : reader.GetString(2).Replace('\\', '/')));
                if (!reader.IsDBNull(3) && reader.GetString(3) is { Length: > 0 } fqn)
                    classes.ByFqn.TryAdd(fqn, id);
            }
            return classes;
        }

        /// <summary>
        /// Clase de la acción. Primero por el FQN que javac escribió en la propiedad <c>fqn</c> del nodo (<c>a.B$C</c> es <c>a.B.C</c>);
        /// si el analizador Java no pasó por esa clase, por el camino de antes: <c>label='Class'</c>, <c>name</c> = nombre simple y
        /// <c>file_path</c> terminado en <c>/&lt;paquete como ruta&gt;/&lt;Simple&gt;.java</c> (una clase anidada vive en <c>B.java</c>).
        /// </summary>
        private static long? FindClass(ClassIndex index, string type)
        {
            var fqn = type.Trim();
            if (index.ByFqn.TryGetValue(fqn.Replace('$', '.'), out var exact))
                return exact;
            var classes = index.ByName;
            var nested = fqn.IndexOf('$');
            var outerFqn = nested < 0 ? fqn : fqn[..nested];
            var simple = fqn[(Math.Max(fqn.LastIndexOf('.'), fqn.LastIndexOf('$')) + 1)..];
            var fileName = outerFqn.Replace('.', '/') + ".java";

            if (!classes.TryGetValue(simple, out var candidates))
                return null;
            return candidates.FirstOrDefault(c => c.FilePath == fileName || c.FilePath.EndsWith("/" + fileName, StringComparison.Ordinal))?.Id;
        }

        // ------------------------------------------------------------ SQL

        private string JspQn(string path, bool withExtension)
        {
            var normalized = path.Replace('\\', '/');
            var dot = normalized.LastIndexOf('.');
            if (!withExtension && dot > normalized.LastIndexOf('/'))
                normalized = normalized[..dot];
            return $"{project}.{normalized.Replace('/', '.')}.__jsp__";
        }

        /// <summary>Crea el nodo con la marca de P12; null si su QN ya lo ocupa otro nodo del grafo.</summary>
        private async Task<long?> CreateNodeAsync(string label, string name, string qualifiedName, string filePath, int start, int end,
            JsonObject properties, CancellationToken ct)
        {
            _insertNode ??= Command("""
                INSERT INTO nodes(project, project_id, label, name, qualified_name, file_path, file,
                                  start_line, line_start, end_line, line_end, properties)
                VALUES (@project, 1, @label, @name, @qn, @file, @file, @start, @start, @end, @end, @props)
                ON CONFLICT DO NOTHING
                RETURNING id
                """);
            _insertNode.Parameters.Clear();
            _insertNode.Parameters.AddWithValue("@project", project);
            _insertNode.Parameters.AddWithValue("@label", label);
            _insertNode.Parameters.AddWithValue("@name", name);
            _insertNode.Parameters.AddWithValue("@qn", qualifiedName);
            _insertNode.Parameters.AddWithValue("@file", filePath);
            _insertNode.Parameters.AddWithValue("@start", start);
            _insertNode.Parameters.AddWithValue("@end", Math.Max(end, start));
            _insertNode.Parameters.AddWithValue("@props",
                EnrichmentMarks.CreatedNodeProperties(EnricherName, enrichedAt, properties).ToJsonString());
            _usedQns.Add(qualifiedName);

            var id = await _insertNode.ExecuteScalarAsync(ct);
            if (id is null or DBNull)
            {
                _qnConflicts++;
                return null;
            }
            _nodesCreated++;
            return Convert.ToInt64(id, CultureInfo.InvariantCulture);
        }

        private async Task<bool> InsertEdgeAsync(long source, long target, string type, JsonObject properties, CancellationToken ct)
        {
            _insertEdge ??= Command("""
                INSERT INTO edges(project, project_id, source_id, target_id, type, properties)
                VALUES (@project, 1, @source, @target, @type, @props)
                ON CONFLICT DO NOTHING
                """);
            _insertEdge.Parameters.Clear();
            _insertEdge.Parameters.AddWithValue("@project", project);
            _insertEdge.Parameters.AddWithValue("@source", source);
            _insertEdge.Parameters.AddWithValue("@target", target);
            _insertEdge.Parameters.AddWithValue("@type", type);
            _insertEdge.Parameters.AddWithValue("@props", EnrichmentMarks.EdgeProperties(EnricherName, properties).ToJsonString());
            var inserted = await _insertEdge.ExecuteNonQueryAsync(ct) > 0;
            if (inserted)
                _edgesCreated++;
            return inserted;
        }

        private SqliteCommand Command(string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            return cmd;
        }

        public void Dispose()
        {
            _insertNode?.Dispose();
            _insertEdge?.Dispose();
        }
    }

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonArray ToArray(IEnumerable<int> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
