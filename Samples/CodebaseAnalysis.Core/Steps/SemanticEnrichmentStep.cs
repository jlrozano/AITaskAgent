using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Support;
using Microsoft.Data.Sqlite;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Steps;

public class SemanticEnrichmentStep : IStep<GraphStatusResult, GraphStatusResult>
{
    /// <summary>Marca <c>enriched_by</c> de lo que escribe el fallback del LLM (misma convención que P12: una lista).</summary>
    public const string EnricherName = "SemanticEnricherAgent";

    /// <summary><c>origin</c> de los nodos que crea el fallback del LLM.</summary>
    public const string OriginAiEnricher = "ai_enricher";

    /// <summary>
    /// Versión del fallback en <c>enrichment_runs</c>: cambiarla obliga a repetir la pasada aunque la indexación y los
    /// ficheros enviados sean los mismos.
    /// </summary>
    public const string EnricherVersion = "1";

    /// <summary>
    /// Propiedades que el modelo puede escribir en un nodo: solo semánticas (rutas, seguridad declarada, DI, contratos,
    /// descripción). Las métricas deterministas (<c>cyclomatic</c>, <c>fan_in</c>, <c>fan_out</c>, <c>loc_*</c>,
    /// <c>afferent_coupling</c>, <c>in_cycle</c>…) y las marcas de origen no están: las primeras las calcula el grafo o un
    /// enriquecedor nativo y las facetas las tratan como medidas; las segundas las pone el código (auditoría H-3).
    /// </summary>
    public static IReadOnlySet<string> AllowedNodeProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "presentational", "framework", "http_verb", "http_method", "route_path", "auth_required", "auth_roles", "auth_policies",
        "request_type_fqn", "response_type_fqn", "di_lifetime", "service_type", "implementation_type", "entry_method",
        "config_section", "is_async", "accessibility", "docstring", "description"
    };

    /// <summary>Propiedades que el modelo puede escribir en una arista: las de orden y rama de <c>FLOW_NEXT</c> y la de DI.</summary>
    public static IReadOnlySet<string> AllowedEdgeProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "presentational", "order", "path_type", "condition", "di_lifetime"
    };

    public string Name => "SemanticEnrichmentStep";
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 500;
    public TimeSpan? Timeout => TimeSpan.FromMinutes(20);
    public Type InputType => typeof(GraphStatusResult);
    public Type OutputType => typeof(GraphStatusResult);

    private readonly CustomStatelessTemplateLlmStep<EnrichmentBatchInput, LlmStringStepResult> _llmStep;

    public SemanticEnrichmentStep(CustomStatelessTemplateLlmStep<EnrichmentBatchInput, LlmStringStepResult> llmStep)
    {
        _llmStep = llmStep;
    }

    public async Task<GraphStatusResult> ExecuteAsync(
        GraphStatusResult input, 
        PipelineContext context, 
        int attempt, 
        GraphStatusResult? lastStepResult, 
        CancellationToken cancellationToken)
    {
        if (input.IsEnriched)
        {
            context.Report(Name, AnalysisLogLevel.Info, "⚡ El grafo ya cuenta con enriquecimiento semántico previo. Omitiendo Fase 2...");
            return input;
        }

        var config = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput) 
            ?? new AnalysisPipelineInput { DatabasePath = input.DatabasePath };

        context.Report(Name, AnalysisLogLevel.Info, "🧠 Iniciando enriquecimiento semántico asistido por IA...");

        // 1. Recopilar archivos clave de controladores, rutas y configuración de DI
        var (filesBatch, language, frameworks, filesFound) = ScanKeyFiles(config.RepositoryPath);

        // Sin ficheros de entrada no hay nada que mandar: el modelo solo podría inventar el grafo. Es el caso de un
        // repositorio solo con Access, que la ingesta de Office ya no marca como enriquecido (auditoría, H-10).
        if (filesFound == 0)
        {
            context.Report(Name, AnalysisLogLevel.Info, "ℹ️ No hay ficheros de rutas, controladores ni DI que enviar al enriquecedor semántico; se omite.");
            return input;
        }

        // Como P3: con una pasada ok sobre la misma indexación de cbm y los mismos ficheros no se vuelve a llamar al modelo,
        // porque su salida sigue en el grafo. Desde H-3 el fallback no marca el grafo como enriquecido, y sin esto un
        // repositorio sin enriquecedor nativo llamaba al modelo en cada análisis. ForceReindex, o que cbm vuelva a indexar
        // (cambia la huella), lo repiten.
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var configFingerprint = EnrichmentRuns.ConfigFingerprint([language, frameworks, filesBatch]);
        var indexFingerprint = await IndexFingerprintAsync(config.DatabasePath, cancellationToken);
        if (!config.ForceReindex && await HasSuccessfulRunAsync(config.DatabasePath, indexFingerprint, configFingerprint, cancellationToken))
        {
            context.Report(Name, AnalysisLogLevel.Info,
                "ℹ️ Enriquecimiento semántico (LLM): el grafo ya tiene la pasada de esta indexación y de estos ficheros; se omite. "
                + "Se repite si cbm vuelve a indexar el repositorio (p. ej. con «Forzar reindexación») o si cambian los ficheros que se envían.");
            return input;
        }

        var batchInput = new EnrichmentBatchInput
        {
            RepositoryPath = config.RepositoryPath,
            FilesBatch = filesBatch,
            ProjectLanguage = language,
            FrameworksDetected = frameworks,
            CurrentTimestamp = DateTime.UtcNow.ToString("o")
        };

        Task RecordAsync(string status, EnrichmentOutcome? outcome, string? message) =>
            RecordRunAsync(config.DatabasePath, new EnrichmentRun
            {
                Enricher = EnricherName,
                EnricherVersion = EnricherVersion,
                IndexFingerprint = indexFingerprint ?? "",
                ConfigFingerprint = configFingerprint,
                StartedAt = startedAt,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Status = status,
                Outcome = outcome,
                Message = message
            }, cancellationToken);

        // 2. Invocar Agente Enriquecedor
        var llmResult = await _llmStep.ExecuteStepAsync(batchInput, context, attempt, cancellationToken);
        var rawContent = (llmResult as LlmStringStepResult)?.Content
            ?? (llmResult as LlmStepResult<string>)?.Value
            ?? llmResult?.Value?.ToString()
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(rawContent))
        {
            context.Report(Name, AnalysisLogLevel.Warning, "⚠️ El agente enriquecedor no produjo contenido. Continuando...");
            await RecordAsync(EnrichmentRuns.StatusFailed, null, "El agente enriquecedor no produjo contenido.");
            return input;
        }

        // 3. Limpiar fences markdown y validar JSON
        var cleanJson = CleanJsonFences(rawContent);
        try
        {
            using var doc = JsonDocument.Parse(cleanJson);
            // 4. Volcado transaccional en SQLite. No pone IsEnriched: lo que escribe el modelo es una estimación y no
            // sustituye al enriquecimiento de código; en la siguiente ejecución vuelve a intentarse Roslyn (auditoría H-3).
            var written = await WriteEnrichedDataToSqliteAsync(config.DatabasePath, doc, DateTimeOffset.UtcNow, cancellationToken);
            if (written.DroppedProperties.Count > 0)
            {
                context.Report(Name, AnalysisLogLevel.Warning,
                    $"⚠️ El enriquecedor semántico propuso propiedades que no puede escribir (métricas o marcas de origen) y se han descartado: {string.Join(", ", written.DroppedProperties)}.");
            }
            await RecordAsync(EnrichmentRuns.StatusOk, new EnrichmentOutcome
            {
                FilesAnalyzed = filesFound,
                NodesCreated = written.NodesCreated,
                NodesUpdated = written.NodesUpdated,
                EdgesCreated = written.EdgesCreated,
                Details = written.DroppedProperties.Count > 0
                    ? JsonSerializer.Serialize(new { dropped_properties = written.DroppedProperties })
                    : null
            }, null);
        }
        catch (Exception ex)
        {
            context.Report(Name, AnalysisLogLevel.Warning, $"⚠️ Error al procesar JSON del enriquecedor semántico: {ex.Message}. Continuando...");
            await RecordAsync(EnrichmentRuns.StatusFailed, null, ex.Message);
        }

        // Recalcular conteos de nodos y aristas
        using (var conn = new SqliteConnection($"Data Source={config.DatabasePath}"))
        {
            await conn.OpenAsync(cancellationToken);
            using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(1) FROM nodes;";
            input.TotalNodes = Convert.ToInt32(await cmd1.ExecuteScalarAsync(cancellationToken));

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(1) FROM edges;";
            input.TotalEdges = Convert.ToInt32(await cmd2.ExecuteScalarAsync(cancellationToken));
        }

        context.Report(Name, AnalysisLogLevel.Success, $"✅ Enriquecimiento semántico completado. Total Nodos: {input.TotalNodes}, Total Aristas: {input.TotalEdges}");

        return input;
    }

    private static (string FilesBatch, string Language, string Frameworks, int FilesFound) ScanKeyFiles(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            return (
                "// No se encontraron archivos en la ruta especificada. Se asume repositorio de muestra C# / ASP.NET Core.",
                "C# / .NET",
                "ASP.NET Core / DependencyInjection",
                0
            );
        }

        var sb = new StringBuilder();
        var filesFound = 0;
        const int maxFiles = 15;
        const int maxCharsPerFile = 4000;

        string language = "C# / .NET";
        string frameworks = "ASP.NET Core";

        // Priorizar archivos de rutas, controladores y DI
        var patterns = new[]
        {
            "*Controller*.cs", "*Routes*.cs", "*Program.cs", "*Startup.cs", "*Module*.cs", "*ServiceCollection*.cs",
            "*Controller*.ts", "*routes*.ts", "*router*.ts", "*app.ts", "*index.ts",
            "*Controller*.java", "*Application*.java", "*Config*.java",
            "*router*.py", "*views*.py", "*main*.py", "*app*.py"
        };

        var matchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            if (filesFound >= maxFiles) break;

            try
            {
                var files = Directory.GetFiles(repoPath, pattern, SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    if (filesFound >= maxFiles) break;
                    if (RepositoryPathFilter.IsExcluded(repoPath, f))
                    {
                        continue;
                    }

                    if (matchedFiles.Add(f))
                    {
                        var relPath = Path.GetRelativePath(repoPath, f);
                        var content = File.ReadAllText(f);
                        if (content.Length > maxCharsPerFile)
                        {
                            content = content[..maxCharsPerFile] + "\n// ... [truncado por límite de contexto]";
                        }

                        sb.AppendLine($"// === File: {relPath} ===");
                        sb.AppendLine(content);
                        sb.AppendLine();
                        filesFound++;

                        if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        {
                            language = "C# / .NET";
                            frameworks = "ASP.NET Core / Microsoft.Extensions.DependencyInjection";
                        }
                        else if (f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        {
                            language = "TypeScript / JavaScript";
                            frameworks = "Express / NestJS / Fastify";
                        }
                        else if (f.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                        {
                            language = "Java";
                            frameworks = "Spring Boot / Spring MVC";
                        }
                        else if (f.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                        {
                            language = "Python";
                            frameworks = "FastAPI / Django / Flask";
                        }
                    }
                }
            }
            catch
            {
                // Ignorar excepciones al enumerar carpetas con restricciones de acceso
            }
        }

        if (filesFound == 0)
        {
            sb.AppendLine("// Repositorio vacío o sin archivos de entrada evidentes detectados.");
        }

        return (sb.ToString(), language, frameworks, filesFound);
    }

    private static string CleanJsonFences(string content)
    {
        var cleaned = LlmHelpers.CleanJsonResponse(content).Trim();
        var match = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)\s*```");
        return match.Success ? match.Groups[1].Value.Trim() : cleaned;
    }

    /// <summary>Huella de la indexación de cbm (<see cref="EnrichmentRuns.IndexFingerprintAsync"/>), o null si no se puede leer.</summary>
    private static async Task<string?> IndexFingerprintAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync(ct);
            return await EnrichmentRuns.IndexFingerprintAsync(conn, ct);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>
    /// Si ya hay una pasada <c>ok</c> del fallback con esta versión, esta indexación y estos ficheros. Sin huella de
    /// indexación (sin tabla <c>projects</c>) no se sabe si cambió, y se ejecuta siempre.
    /// </summary>
    private static async Task<bool> HasSuccessfulRunAsync(string dbPath, string? indexFingerprint, string configFingerprint, CancellationToken ct)
    {
        if (indexFingerprint == null)
            return false;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync(ct);
            return await EnrichmentRuns.HasSuccessfulRunAsync(conn, EnricherName, EnricherVersion, indexFingerprint, configFingerprint, ct);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>La fila de la pasada en <c>enrichment_runs</c>; si no se puede escribir, la siguiente ejecución la repite.</summary>
    private static async Task RecordRunAsync(string dbPath, EnrichmentRun run, CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync(ct);
            await EnrichmentRuns.InsertAsync(conn, run, ct);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // La BD es la del propio análisis: si no se puede escribir, lo dirá el paso siguiente.
        }
    }

    /// <summary>Lo que escribió el fallback: nodos creados y completados, aristas, y las claves de propiedad descartadas.</summary>
    internal sealed record FallbackWriteResult(int NodesCreated, int NodesUpdated, int EdgesCreated, IReadOnlyList<string> DroppedProperties);

    /// <summary>
    /// Vuelca el JSON del modelo en la copia del grafo con las marcas impuestas por el código: los nodos nuevos llevan
    /// <c>origin:"ai_enricher"</c>, <c>enriched_by:["SemanticEnricherAgent"]</c> y <c>enriched_at</c>; las aristas y las
    /// propiedades completadas en nodos que ya existían, <c>enriched_by</c> (P12). De lo que escribe el modelo solo pasan las
    /// claves de <see cref="AllowedNodeProperties"/> y <see cref="AllowedEdgeProperties"/>. Antes se retira la salida de la
    /// pasada anterior (nodos creados y aristas marcadas), porque el fallback se repite cuando cambian la indexación o los
    /// ficheros enviados, o con <c>ForceReindex</c>. Una arista
    /// que ya existe (de cbm) no se toca, y todo se limita al proyecto del grafo.
    /// </summary>
    internal static async Task<FallbackWriteResult> WriteEnrichedDataToSqliteAsync(string dbPath, JsonDocument doc, DateTimeOffset enrichedAt, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var project = await GraphProject.ResolveAsync(conn, "Codebase", ct, tx);
        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        int created = 0, updated = 0, edgesCreated = 0;

        // 0. Salida de la pasada anterior del fallback.
        await Enrichment.EnrichmentMarks.RemoveNodesAsync(conn, tx,
            $"SELECT id FROM nodes WHERE project = @project AND json_extract({SafeJson("properties")}, '$.origin') = '{OriginAiEnricher}'",
            p => p.AddWithValue("@project", project), ct);
        using (var cleanup = conn.CreateCommand())
        {
            cleanup.Transaction = tx;
            cleanup.CommandText = $"DELETE FROM edges WHERE project = @project AND {Enrichment.EnrichmentMarks.EnrichedByPredicate()}";
            cleanup.Parameters.AddWithValue("@project", project);
            cleanup.Parameters.AddWithValue("@enricher", EnricherName);
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        // 1. Nodos: nuevos con las marcas de nodo creado; los que ya existen se completan con las propiedades permitidas.
        if (doc.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                var label = Text(node, "label");
                var name = Text(node, "name");
                var qname = Text(node, "qualified_name");
                var file = Text(node, "file");
                var line = node.TryGetProperty("line", out var ln) && ln.TryGetInt32(out var li) ? li : 0;
                var props = Filter(node, AllowedNodeProperties, dropped);

                if (string.IsNullOrWhiteSpace(qname))
                    continue;

                if (await ExistingPropertiesAsync(conn, tx, project, qname, ct) is { } existing)
                {
                    if (props.Count > 0)
                    {
                        await UpdatePropertiesAsync(conn, tx, existing.Id, Enrichment.EnrichmentMarks.Merge(existing.Properties, props, EnricherName), ct);
                        updated++;
                    }
                    continue;
                }

                var properties = Enrichment.EnrichmentMarks.CreatedNodeProperties(EnricherName, enrichedAt, props);
                properties["origin"] = OriginAiEnricher;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO nodes (project, project_id, label, name, qualified_name, file_path, file, start_line, line_start, properties)
                    VALUES (@project, 1, @label, @name, @qname, @file, @file, @line, @line, @props);";

                cmd.Parameters.AddWithValue("@project", project);
                cmd.Parameters.AddWithValue("@label", label);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@qname", qname);
                cmd.Parameters.AddWithValue("@file", file);
                cmd.Parameters.AddWithValue("@line", line);
                cmd.Parameters.AddWithValue("@props", properties.ToJsonString());
                created += await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // 2. Aristas nuevas entre nodos del proyecto; una que ya existe no se toca.
        if (doc.RootElement.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (var edge in edges.EnumerateArray())
            {
                var sourceQName = Text(edge, "source_qualified_name");
                var targetQName = Text(edge, "target_qualified_name");
                var type = Text(edge, "type");
                var props = Enrichment.EnrichmentMarks.EdgeProperties(EnricherName, Filter(edge, AllowedEdgeProperties, dropped));

                if (string.IsNullOrWhiteSpace(sourceQName) || string.IsNullOrWhiteSpace(targetQName) || string.IsNullOrWhiteSpace(type))
                    continue;

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO edges (project, project_id, source_id, target_id, type, properties)
                    SELECT @project, 1, s.id, t.id, @type, @props
                    FROM nodes s, nodes t
                    WHERE s.project = @project AND s.qualified_name = @sourceQName
                      AND t.project = @project AND t.qualified_name = @targetQName
                    ON CONFLICT(source_id, target_id, type) DO NOTHING;";

                cmd.Parameters.AddWithValue("@project", project);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@props", props.ToJsonString());
                cmd.Parameters.AddWithValue("@sourceQName", sourceQName);
                cmd.Parameters.AddWithValue("@targetQName", targetQName);
                edgesCreated += await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // 3. Actualización de Propiedades de Nodos Existentes (del proyecto, y solo las permitidas)
        if (doc.RootElement.TryGetProperty("node_properties_updates", out var propUpdates) && propUpdates.ValueKind == JsonValueKind.Array)
        {
            foreach (var update in propUpdates.EnumerateArray())
            {
                var qname = Text(update, "qualified_name");
                var props = Filter(update, AllowedNodeProperties, dropped);

                if (string.IsNullOrWhiteSpace(qname) || props.Count == 0)
                    continue;

                if (await ExistingPropertiesAsync(conn, tx, project, qname, ct) is { } existing)
                {
                    await UpdatePropertiesAsync(conn, tx, existing.Id, Enrichment.EnrichmentMarks.Merge(existing.Properties, props, EnricherName), ct);
                    updated++;
                }
            }
        }

        await tx.CommitAsync(ct);
        return new FallbackWriteResult(created, updated, edgesCreated, [.. dropped]);
    }

    private static string SafeJson(string column) => $"(CASE WHEN json_valid({column}) THEN {column} ELSE '{{}}' END)";

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>Las propiedades de <paramref name="element"/> cuya clave está en <paramref name="allowed"/>; el resto va a <paramref name="dropped"/>.</summary>
    private static System.Text.Json.Nodes.JsonObject Filter(JsonElement element, IReadOnlySet<string> allowed, ISet<string> dropped)
    {
        var result = new System.Text.Json.Nodes.JsonObject();
        if (!element.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in properties.EnumerateObject())
        {
            if (allowed.Contains(property.Name))
                result[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            else
                dropped.Add(property.Name);
        }
        return result;
    }

    private static async Task<(long Id, string? Properties)?> ExistingPropertiesAsync(
        SqliteConnection conn, SqliteTransaction tx, string project, string qualifiedName, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, properties FROM nodes WHERE project = @project AND qualified_name = @qname LIMIT 1;";
        cmd.Parameters.AddWithValue("@project", project);
        cmd.Parameters.AddWithValue("@qname", qualifiedName);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task UpdatePropertiesAsync(SqliteConnection conn, SqliteTransaction tx, long id, System.Text.Json.Nodes.JsonObject properties, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE nodes SET properties = @props WHERE id = @id;";
        cmd.Parameters.AddWithValue("@props", properties.ToJsonString());
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is GraphStatusResult, (string?)null));
}
