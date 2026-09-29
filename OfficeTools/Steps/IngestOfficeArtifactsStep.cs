using System.Text.Json;
using System.Text.RegularExpressions;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OfficeTools.Abstractions;
using OfficeTools.Extractors;
using OfficeTools.Models;

namespace OfficeTools.Steps;

/// <summary>
/// Deterministic step that scans a directory for Office artifacts (.accdb, .mdb, .docx),
/// extracts relational schemas, VBA code, forms, controls, events, and document contents,
/// and indexes the complete knowledge graph into SQLite.
/// </summary>
public class IngestOfficeArtifactsStep : StepBase
{
    private readonly IAccessSchemaExtractor? _accessExtractor;
    private readonly IAccessVbaExtractor? _vbaExtractor;
    private readonly IDocxReader? _docxReader;

    public IngestOfficeArtifactsStep(
        string name = "IngestOfficeArtifacts",
        IAccessSchemaExtractor? accessExtractor = null,
        IAccessVbaExtractor? vbaExtractor = null,
        IDocxReader? docxReader = null)
        : base(name, typeof(IStepResult), typeof(StepResult<OfficeIngestionResult>))
    {
        _accessExtractor = accessExtractor;
        _vbaExtractor = vbaExtractor;
        _docxReader = docxReader;
    }

    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        string searchDir = ResolveSearchDirectory(input, context);
        Console.WriteLine($"[OfficeTools] 🔍 Iniciando escaneo de artefactos de Office en: '{searchDir}'");
        Logger.LogInformation("Iniciando escaneo de artefactos de Office en: {Directory}", searchDir);

        if (!Directory.Exists(searchDir))
        {
            var msg = $"El directorio objetivo no existe: '{searchDir}'";
            Console.WriteLine($"[OfficeTools] ❌ {msg}");
            return CreateErrorResult(msg);
        }

        var accessExtractor = _accessExtractor ??
                              context.ServiceProvider.GetService<IAccessSchemaExtractor>() ??
                              new AccessSchemaExtractor();

        var vbaExtractor = _vbaExtractor ??
                           context.ServiceProvider.GetService<IAccessVbaExtractor>() ??
                           new AccessVbaExtractor();

        var docxReader = _docxReader ??
                         context.ServiceProvider.GetService<IDocxReader>() ??
                         new DocxReader();

        var result = new OfficeIngestionResult();

        // 1. Discover files
        var (accessFiles, docxFiles) = DiscoverOfficeFiles(searchDir);

        // 2. Process Access databases (.accdb / .mdb)
        foreach (var file in accessFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"[OfficeTools] 🗄️ Extrayendo esquema relacional Access: {Path.GetFileName(file)}...");

            try
            {
                var schema = await accessExtractor.ExtractSchemaAsync(file, cancellationToken: cancellationToken);
                result.AccessSchemas.Add(schema);
                Console.WriteLine($"[OfficeTools] ✅ Esquema relacional extraído ({schema.Tables.Count} tablas, {schema.Queries.Count} consultas, {schema.ForeignKeys.Count} FKs).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OfficeTools] ⚠️ Error al extraer esquema Access de '{Path.GetFileName(file)}': {ex.Message}");
                Logger.LogWarning(ex, "Error al extraer esquema Access de '{FilePath}'", file);
            }

            Console.WriteLine($"[OfficeTools] 💻 Extrayendo módulos VBA, Formularios y Eventos code-behind: {Path.GetFileName(file)}...");
            try
            {
                var vbaProject = await vbaExtractor.ExtractVbaAndFormsAsync(file, cancellationToken: cancellationToken);
                if (vbaProject != null)
                {
                    result.VbaProjects.Add(vbaProject);
                    Console.WriteLine($"[OfficeTools] ✅ VBA extraído: {vbaProject.Modules.Count} módulos, {vbaProject.Forms.Count} formularios, {vbaProject.TotalLoc} LOC en {vbaProject.AllProcedures.Count()} procedimientos.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OfficeTools] ⚠️ Error al extraer VBA/Formularios de '{Path.GetFileName(file)}': {ex.Message}");
                Logger.LogWarning(ex, "Error al extraer VBA/Formularios de '{FilePath}'", file);
            }
        }

        // 3. Process Word documents (.docx)
        foreach (var file in docxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"[OfficeTools] 📄 Procesando Word: {Path.GetFileName(file)}...");

            try
            {
                var doc = await docxReader.ReadDocumentAsync(file, cancellationToken);
                result.Documents.Add(doc);
                Console.WriteLine($"[OfficeTools] ✅ Documento Word procesado ({doc.Sections.Count} secciones, {doc.Tables.Count} tablas).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OfficeTools] ⚠️ Error al procesar documento Word '{Path.GetFileName(file)}': {ex.Message}");
                Logger.LogWarning(ex, "Error al procesar documento Word '{FilePath}'", file);
            }
        }

        // 4. Store in PipelineContext
        if (result.AccessSchemas.Count > 0)
        {
            context.Metadata["Office:AccessSchema"] = result.AccessSchemas[0];
            context.Metadata["Office:AccessSchemas"] = result.AccessSchemas;
            context.Metadata["AccessDatabasePath"] = result.AccessSchemas[0].FilePath;
        }

        if (result.VbaProjects.Count > 0)
        {
            context.Metadata["Office:VbaProject"] = result.VbaProjects[0];
            context.Metadata["Office:VbaProjects"] = result.VbaProjects;
        }

        if (result.Documents.Count > 0)
        {
            context.Metadata["Office:Documents"] = result.Documents;
            context.Metadata["Office:PrimaryDocument"] = result.Documents[0];
        }

        // 5. Index into Graph SQLite Database
        var graphDbPath = ResolveGraphDbPath(context);
        if (!string.IsNullOrWhiteSpace(graphDbPath))
        {
            Console.WriteLine($"[OfficeTools] 🧠 Registrando grafo topológico completo en SQLite: '{graphDbPath}'...");
            await RegisterInGraphDbAsync(graphDbPath, result.AccessSchemas, result.VbaProjects, result.Documents, cancellationToken);
        }

        result.Summary = $"Ingesta de Office completada: {result.TotalAccessFiles} BDs Access ({result.VbaProjects.Sum(v => v.TotalLoc)} LOC VBA), {result.TotalWordFiles} documentos Word.";
        Console.WriteLine($"[OfficeTools] 🎉 {result.Summary}");

        return CreateResult(result);
    }

    // Residuos por convención de nombres, sin nada de un repositorio concreto: un segmento de
    // prueba, temporal, copia o respaldo, y los nombres que genera Access al copiar ("Copia de X",
    // "Copy of X") o al fallar un pegado ("Errores de pegado", "Paste Errors").
    private static readonly Regex ResidualTableName = new(
        @"(?i)((^|[_\s])(tmp|temp|test|prueba|bak|backup|old|copia|copy)(\d*)($|[_\s])|^(copia de|copy of)\s|^(errores de pegado|paste errors)$)",
        RegexOptions.Compiled);

    // Segmento de prueba, temporal o respaldo en un nombre de consulta o de módulo.
    private static readonly Regex ResidualNameSegment = new(
        @"(?i)(^|[_\s])(tmp|temp|test|prueba|bak|backup|old)(\d*)($|[_\s])",
        RegexOptions.Compiled);

    // Nombre por defecto que Access da a una consulta guardada sin nombrar ("Consulta1", "Query2").
    private static readonly Regex DefaultQueryName = new(@"(?i)^(consulta|query)\d+$", RegexOptions.Compiled);

    private static async Task<string> ResolveProjectAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT project FROM nodes
            WHERE project IS NOT NULL AND project <> ''
            GROUP BY project ORDER BY COUNT(1) DESC LIMIT 1;";
        return await cmd.ExecuteScalarAsync(ct) as string ?? string.Empty;
    }

    private static string ResolveSearchDirectory(IStepResult input, PipelineContext context)
    {
        if (input.Value is string pathStr && !string.IsNullOrWhiteSpace(pathStr))
            return pathStr;

        if (context.Metadata.TryGetValue("RepositoryPath", out var repoVal) && repoVal is string repoStr && !string.IsNullOrWhiteSpace(repoStr))
            return repoStr;

        if (context.Metadata.TryGetValue("PipelineInput", out var inputVal) && inputVal != null)
        {
            var prop = inputVal.GetType().GetProperty("RepositoryPath");
            if (prop?.GetValue(inputVal) is string p && !string.IsNullOrWhiteSpace(p))
                return p;
        }

        return Directory.GetCurrentDirectory();
    }

    private static (List<string> AccessFiles, List<string> DocxFiles) DiscoverOfficeFiles(string rootDir)
    {
        var accessFiles = new List<string>();
        var docxFiles = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("~$") || name.StartsWith(".")) continue;

                var norm = file.Replace('/', '\\');
                if (norm.Contains("\\.git\\") || norm.Contains("\\bin\\") || norm.Contains("\\obj\\") || norm.Contains("\\node_modules\\") || norm.Contains("\\.codebase\\"))
                    continue;

                var ext = Path.GetExtension(file);
                if (string.Equals(ext, ".accdb", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".mdb", StringComparison.OrdinalIgnoreCase))
                {
                    accessFiles.Add(Path.GetFullPath(file));
                }
                else if (string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase))
                {
                    docxFiles.Add(Path.GetFullPath(file));
                }
            }
        }
        catch
        {
            // Ignore access errors
        }

        return (accessFiles, docxFiles);
    }

    private static string? ResolveGraphDbPath(PipelineContext context)
    {
        if (context.Metadata.TryGetValue("CbmDbPath", out var cbmVal) && cbmVal is string cbmStr && !string.IsNullOrWhiteSpace(cbmStr))
            return cbmStr;

        if (context.Metadata.TryGetValue("GraphDbPath", out var gVal) && gVal is string gStr && !string.IsNullOrWhiteSpace(gStr))
            return gStr;

        if (context.Metadata.TryGetValue("PipelineInput", out var inputVal) && inputVal != null)
        {
            var prop = inputVal.GetType().GetProperty("DatabasePath");
            if (prop?.GetValue(inputVal) is string p && !string.IsNullOrWhiteSpace(p))
                return p;
        }

        return null;
    }

    private async Task RegisterInGraphDbAsync(
        string dbPath,
        List<AccessDatabaseSchema> accessSchemas,
        List<AccessVbaProject> vbaProjects,
        List<DocumentInfo> documents,
        CancellationToken ct)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync(ct);

            // Ensure schema
            using (var initCmd = conn.CreateCommand())
            {
                initCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS nodes (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        project TEXT DEFAULT '',
                        project_id INTEGER DEFAULT 1,
                        label TEXT NOT NULL,
                        name TEXT NOT NULL,
                        qualified_name TEXT NOT NULL,
                        file_path TEXT,
                        file TEXT,
                        start_line INTEGER DEFAULT 0,
                        end_line INTEGER DEFAULT 0,
                        line_start INTEGER DEFAULT 0,
                        line_end INTEGER DEFAULT 0,
                        properties TEXT DEFAULT '{}',
                        UNIQUE(project_id, qualified_name)
                    );

                    CREATE TABLE IF NOT EXISTS edges (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        project TEXT DEFAULT '',
                        project_id INTEGER DEFAULT 1,
                        source_id INTEGER NOT NULL,
                        target_id INTEGER NOT NULL,
                        type TEXT NOT NULL,
                        properties TEXT DEFAULT '{}',
                        UNIQUE(project_id, source_id, target_id, type)
                    );

                    CREATE INDEX IF NOT EXISTS idx_nodes_qname ON nodes(qualified_name);
                    CREATE INDEX IF NOT EXISTS idx_nodes_label ON nodes(label);
                    CREATE INDEX IF NOT EXISTS idx_edges_source ON edges(source_id);
                    CREATE INDEX IF NOT EXISTS idx_edges_target ON edges(target_id);
                    CREATE INDEX IF NOT EXISTS idx_edges_type ON edges(type);
                ";
                await initCmd.ExecuteNonQueryAsync(ct);
            }

            // Defensive column migrations and indexes
            var alterStatements = new[]
            {
                "ALTER TABLE nodes ADD COLUMN project TEXT DEFAULT '';",
                "ALTER TABLE nodes ADD COLUMN project_id INTEGER DEFAULT 1;",
                "ALTER TABLE nodes ADD COLUMN file TEXT DEFAULT '';",
                "ALTER TABLE nodes ADD COLUMN file_path TEXT DEFAULT '';",
                "ALTER TABLE nodes ADD COLUMN line_start INTEGER DEFAULT 0;",
                "ALTER TABLE nodes ADD COLUMN line_end INTEGER DEFAULT 0;",
                "ALTER TABLE nodes ADD COLUMN start_line INTEGER DEFAULT 0;",
                "ALTER TABLE nodes ADD COLUMN end_line INTEGER DEFAULT 0;",
                "ALTER TABLE edges ADD COLUMN project TEXT DEFAULT '';",
                "ALTER TABLE edges ADD COLUMN project_id INTEGER DEFAULT 1;"
            };

            foreach (var sql in alterStatements)
            {
                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = sql;
                    await alterCmd.ExecuteNonQueryAsync(ct);
                }
                catch { }
            }

            var indexStatements = new[]
            {
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_nodes_project_qname ON nodes(project, qualified_name);",
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_nodes_projid_qname ON nodes(project_id, qualified_name);",
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_edges_src_tgt_type ON edges(source_id, target_id, type);",
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_edges_projid_src_tgt_type ON edges(project_id, source_id, target_id, type);"
            };

            foreach (var sql in indexStatements)
            {
                try
                {
                    using var idxCmd = conn.CreateCommand();
                    idxCmd.CommandText = sql;
                    await idxCmd.ExecuteNonQueryAsync(ct);
                }
                catch { }
            }

            var qnameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            // Con el esquema del indexador, `project` es NOT NULL y clave foránea: los nodos de Office
            // llevan el proyecto que ya usa el grafo. Sin él, un repositorio con código y documentos
            // Office perdía entera la ingesta de Office.
            var project = await ResolveProjectAsync(conn, ct);

            async Task<long> InsertNodeAsync(SqliteTransaction tx, string label, string name, string qname, string file, int lineStart, int lineEnd, object propsObj)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO nodes (project, project_id, label, name, qualified_name, file, file_path, line_start, line_end, start_line, end_line, properties)
                    VALUES (@project, 1, @label, @name, @qname, @file, @file, @lineStart, @lineEnd, @lineStart, @lineEnd, @props)
                    ON CONFLICT(project_id, qualified_name) DO UPDATE SET
                        label = excluded.label,
                        file = excluded.file,
                        file_path = excluded.file_path,
                        line_start = excluded.line_start,
                        line_end = excluded.line_end,
                        properties = json_patch(nodes.properties, excluded.properties)
                    RETURNING id;";

                cmd.Parameters.AddWithValue("@project", project);
                cmd.Parameters.AddWithValue("@label", label);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@qname", qname);
                cmd.Parameters.AddWithValue("@file", file);
                cmd.Parameters.AddWithValue("@lineStart", lineStart);
                cmd.Parameters.AddWithValue("@lineEnd", lineEnd);
                cmd.Parameters.AddWithValue("@props", JsonSerializer.Serialize(propsObj));

                var idObj = await cmd.ExecuteScalarAsync(ct);
                var id = Convert.ToInt64(idObj);
                qnameToId[qname] = id;
                return id;
            }

            async Task InsertEdgeAsync(SqliteTransaction tx, long sourceId, long targetId, string type, object? propsObj = null)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO edges (project, project_id, source_id, target_id, type, properties)
                    VALUES (@project, 1, @sourceId, @targetId, @type, @props)
                    ON CONFLICT(project_id, source_id, target_id, type) DO UPDATE SET
                        properties = json_patch(edges.properties, excluded.properties);";

                cmd.Parameters.AddWithValue("@project", project);
                cmd.Parameters.AddWithValue("@sourceId", sourceId);
                cmd.Parameters.AddWithValue("@targetId", targetId);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@props", propsObj != null ? JsonSerializer.Serialize(propsObj) : "{}");
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var tx = conn.BeginTransaction();

            // 1. Insert Tables
            var knownTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var schema in accessSchemas)
            {
                foreach (var table in schema.Tables)
                {
                    knownTableNames.Add(table.Name);
                    var qname = $"Table.{table.Name}";
                    bool isDeadTable = ResidualTableName.IsMatch(table.Name);
                    var props = new
                    {
                        database = schema.DatabaseName,
                        field_count = table.Columns.Count,
                        primary_keys = table.PrimaryKeys,
                        columns = table.Columns.Select(c => c.Name).ToList(),
                        loc_total = 0,
                        dead_code_candidate = isDeadTable ? 1 : 0,
                        dead_code_confidence = isDeadTable ? 0.95 : 0.0,
                        dead_code_reason = isDeadTable ? "Tabla residual de pruebas o temporal sin función operativa en producción" : (string?)null,
                        origin = "office_tools"
                    };
                    await InsertNodeAsync(tx, "Table", table.Name, qname, schema.FilePath, 1, 1, props);
                    // Also register with AccessTable prefix for backwards compatibility
                    await InsertNodeAsync(tx, "AccessTable", table.Name, $"{schema.DatabaseName}:{table.Name}", schema.FilePath, 1, 1, props);
                }

                // Insert Queries
                foreach (var query in schema.Queries)
                {
                    var qname = $"Query.{query.Name}";
                    bool isDeadQuery = DefaultQueryName.IsMatch(query.Name) || ResidualNameSegment.IsMatch(query.Name);
                    var props = new
                    {
                        database = schema.DatabaseName,
                        sql = query.Sql,
                        http_verb = "QUERY",
                        route_path = query.Name,
                        auth_required = 0,
                        dead_code_candidate = isDeadQuery ? 1 : 0,
                        dead_code_confidence = isDeadQuery ? 0.90 : 0.0,
                        dead_code_reason = isDeadQuery ? "Consulta Access de prueba temporal abandonada" : (string?)null,
                        origin = "office_tools"
                    };
                    var qId = await InsertNodeAsync(tx, "Query", query.Name, qname, schema.FilePath, 1, 1, props);
                    await InsertNodeAsync(tx, "AccessQuery", query.Name, $"{schema.DatabaseName}:{query.Name}", schema.FilePath, 1, 1, props);

                    // Check if query reads any table
                    if (!string.IsNullOrEmpty(query.Sql))
                    {
                        var sqlUpper = query.Sql.ToUpperInvariant();
                        foreach (var tName in knownTableNames)
                        {
                            if (Regex.IsMatch(sqlUpper, $@"\b{Regex.Escape(tName.ToUpperInvariant())}\b"))
                            {
                                if (qnameToId.TryGetValue($"Table.{tName}", out var tId))
                                {
                                    await InsertEdgeAsync(tx, qId, tId, "READS_TABLE");
                                }
                            }
                        }
                    }
                }

                // Table foreign keys REFERENCES
                foreach (var fk in schema.ForeignKeys)
                {
                    if (qnameToId.TryGetValue($"Table.{fk.FromTable}", out var fromId) &&
                        qnameToId.TryGetValue($"Table.{fk.ToTable}", out var toId))
                    {
                        await InsertEdgeAsync(tx, fromId, toId, "REFERENCES", new { name = fk.Name, from_column = fk.FromColumn, to_column = fk.ToColumn });
                    }
                }
            }

            // 2. Insert VBA Modules, Forms, Reports, and Procedures
            var procQNameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var vbaProj in vbaProjects)
            {
                // Modules. Un módulo es candidato a residuo si su nombre tiene un segmento de prueba o
                // respaldo, o si es otro módulo con un número añadido (MFiles1 junto a MFiles), que es
                // como queda una copia hecha desde el editor de VBA.
                var moduleNames = vbaProj.Modules.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in vbaProj.Modules)
                {
                    var baseName = mod.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                    bool isDeadModule = ResidualNameSegment.IsMatch(mod.Name)
                        || (baseName.Length > 0 && baseName.Length < mod.Name.Length && moduleNames.Contains(baseName));
                    var mProps = new
                    {
                        is_class = mod.IsClassModule,
                        loc_total = mod.TotalLoc,
                        loc = mod.TotalLoc,
                        method_count = mod.Procedures.Count,
                        dead_code_candidate = isDeadModule ? 1 : 0,
                        dead_code_confidence = isDeadModule ? 0.85 : 0.0,
                        dead_code_reason = isDeadModule ? "Módulo duplicado o huérfano sin llamadas activas desde UI ni otros módulos" : (string?)null,
                        origin = "access_vba_extractor"
                    };
                    var mId = await InsertNodeAsync(tx, "Module", mod.Name, mod.QualifiedName, mod.FilePath, 1, Math.Max(1, mod.TotalLoc), mProps);

                    foreach (var proc in mod.Procedures)
                    {
                        var pProps = CreateProcedureProps(proc);
                        var pId = await InsertNodeAsync(tx, proc.Kind, proc.Name, proc.QualifiedName, mod.FilePath, proc.StartLine, proc.EndLine, pProps);
                        procQNameToId[proc.QualifiedName] = pId;
                        await InsertEdgeAsync(tx, mId, pId, "DEFINES");
                    }
                }

                // Forms
                foreach (var frm in vbaProj.Forms)
                {
                    var fProps = new
                    {
                        caption = frm.Caption,
                        controls_count = frm.Controls.Count,
                        controls = frm.Controls.Select(c => new { name = c.Name, type = c.ControlType, caption = c.Caption }).ToList(),
                        loc_total = frm.TotalLoc,
                        loc = frm.TotalLoc,
                        method_count = frm.Procedures.Count,
                        origin = "access_vba_extractor"
                    };
                    var fId = await InsertNodeAsync(tx, "Form", frm.Name, frm.QualifiedName, frm.FilePath, 1, Math.Max(1, frm.TotalLoc), fProps);

                    foreach (var proc in frm.Procedures)
                    {
                        var pProps = CreateProcedureProps(proc);
                        var pId = await InsertNodeAsync(tx, proc.Kind, proc.Name, proc.QualifiedName, frm.FilePath, proc.StartLine, proc.EndLine, pProps);
                        procQNameToId[proc.QualifiedName] = pId;
                        await InsertEdgeAsync(tx, fId, pId, "HANDLES_EVENT");
                    }
                }

                // Reports
                foreach (var rep in vbaProj.Reports)
                {
                    var rProps = new
                    {
                        loc_total = rep.TotalLoc,
                        loc = rep.TotalLoc,
                        method_count = rep.Procedures.Count,
                        origin = "access_vba_extractor"
                    };
                    var rId = await InsertNodeAsync(tx, "Report", rep.Name, rep.QualifiedName, rep.FilePath, 1, Math.Max(1, rep.TotalLoc), rProps);

                    foreach (var proc in rep.Procedures)
                    {
                        var pProps = CreateProcedureProps(proc);
                        var pId = await InsertNodeAsync(tx, proc.Kind, proc.Name, proc.QualifiedName, rep.FilePath, proc.StartLine, proc.EndLine, pProps);
                        procQNameToId[proc.QualifiedName] = pId;
                        await InsertEdgeAsync(tx, rId, pId, "HANDLES_EVENT");
                    }
                }

                // Insert Inferred Procedure-level Edges (CALLS, READS_TABLE, WRITES_TABLE, EXECUTES_QUERY)
                foreach (var proc in vbaProj.AllProcedures)
                {
                    if (!procQNameToId.TryGetValue(proc.QualifiedName, out var srcId))
                        continue;

                    // CALLS
                    foreach (var calledQName in proc.Calls)
                    {
                        if (procQNameToId.TryGetValue(calledQName, out var tgtId) && srcId != tgtId)
                        {
                            await InsertEdgeAsync(tx, srcId, tgtId, "CALLS");
                        }
                    }

                    // READS_TABLE
                    foreach (var tbl in proc.ReadsTables)
                    {
                        if (qnameToId.TryGetValue($"Table.{tbl}", out var tblId))
                        {
                            await InsertEdgeAsync(tx, srcId, tblId, "READS_TABLE");
                        }
                    }

                    // WRITES_TABLE
                    foreach (var tbl in proc.WritesTables)
                    {
                        if (qnameToId.TryGetValue($"Table.{tbl}", out var tblId))
                        {
                            await InsertEdgeAsync(tx, srcId, tblId, "WRITES_TABLE");
                        }
                    }

                    // EXECUTES_QUERY
                    foreach (var qry in proc.ExecutesQueries)
                    {
                        if (qnameToId.TryGetValue($"Query.{qry}", out var qryId))
                        {
                            await InsertEdgeAsync(tx, srcId, qryId, "EXECUTES_QUERY");
                        }
                    }
                }
            }

            // Materialize End-to-End Business Flow nodes from Form Events
            foreach (var vbaProj in vbaProjects)
            {
                foreach (var frm in vbaProj.Forms)
                {
                    foreach (var proc in frm.Procedures.Where(p => p.IsEventHandler && (p.Calls.Count > 0 || p.ReadsTables.Count > 0)))
                    {
                        var flowName = $"Flujo_{frm.Name}_{proc.Name}";
                        var flowQName = $"Flow.{flowName}";

                        var stepsList = new List<string> { proc.QualifiedName };
                        stepsList.AddRange(proc.Calls);
                        stepsList.AddRange(proc.ReadsTables.Select(t => $"Table.{t}"));

                        var flowProps = new
                        {
                            step_count = stepsList.Count,
                            entry_method = proc.QualifiedName,
                            form = frm.Name,
                            steps = stepsList,
                            happy_path = $"Flujo de negocio activado desde el evento UI '{frm.Name}.{proc.Name}', delegando en {proc.Calls.Count} procedimiento(s) de negocio y accediendo a {proc.ReadsTables.Count} tabla(s)."
                        };

                        var flowId = await InsertNodeAsync(tx, "Flow", flowName, flowQName, frm.FilePath, proc.StartLine, proc.EndLine, flowProps);

                        if (procQNameToId.TryGetValue(proc.QualifiedName, out var eventId))
                        {
                            await InsertEdgeAsync(tx, flowId, eventId, "FLOW_ENTRY");

                            long lastStepId = eventId;
                            foreach (var callQName in proc.Calls)
                            {
                                if (procQNameToId.TryGetValue(callQName, out var callId) && callId != lastStepId)
                                {
                                    await InsertEdgeAsync(tx, lastStepId, callId, "FLOW_NEXT");
                                    lastStepId = callId;
                                }
                            }

                            foreach (var tbl in proc.ReadsTables)
                            {
                                if (qnameToId.TryGetValue($"Table.{tbl}", out var tblId))
                                {
                                    await InsertEdgeAsync(tx, lastStepId, tblId, "FLOW_NEXT");
                                }
                            }
                        }
                    }
                }
            }

            // 3. Insert Word Documents and DOC_DESCRIBES edges
            foreach (var doc in documents)
            {
                int secIdx = 1;
                foreach (var section in doc.Sections)
                {
                    var secQName = $"{doc.Name}#{section.Title}";
                    var secProps = new
                    {
                        document = doc.Name,
                        level = section.Level,
                        content_length = section.Content.Length,
                        docstring = section.Content.Length > 400 ? section.Content[..400] : section.Content,
                        is_documentation = true
                    };

                    var secId = await InsertNodeAsync(tx, "DocSection", section.Title, secQName, doc.FilePath, secIdx++, 1, secProps);

                    // Connect DOC_DESCRIBES to symbols mentioned in text
                    var docContent = section.Content;
                    var seenMentions = new HashSet<long>();

                    // 1. Double brackets [[Symbol]]
                    var bracketMatches = Regex.Matches(docContent, @"\[\[([A-Za-z0-9_\.<>,\s\?\[\]]+)\]\]");
                    foreach (Match bm in bracketMatches)
                    {
                        var sym = bm.Groups[1].Value.Trim();
                        foreach (var kvp in qnameToId)
                        {
                            if (kvp.Key.Equals(sym, StringComparison.OrdinalIgnoreCase) ||
                                kvp.Key.EndsWith($".{sym}", StringComparison.OrdinalIgnoreCase))
                            {
                                if (seenMentions.Add(kvp.Value) && kvp.Value != secId)
                                {
                                    await InsertEdgeAsync(tx, secId, kvp.Value, "DOC_DESCRIBES", new { mention_type = "double_bracket", confidence = 0.95 });
                                }
                            }
                        }
                    }

                    // 2. Keyword mentions of Forms, Modules, or Tables (>= 4 chars)
                    foreach (var kvp in qnameToId)
                    {
                        var shortName = kvp.Key.Split('.').Last();
                        if (shortName.Length >= 4 && Regex.IsMatch(docContent, $@"\b{Regex.Escape(shortName)}\b", RegexOptions.IgnoreCase))
                        {
                            if (seenMentions.Add(kvp.Value) && kvp.Value != secId)
                            {
                                await InsertEdgeAsync(tx, secId, kvp.Value, "DOC_DESCRIBES", new { mention_type = "text_mention", confidence = 0.80 });
                            }
                        }
                    }
                }
            }

            // 4. Calculate fan_in and fan_out for all nodes
            using (var updateFansCmd = conn.CreateCommand())
            {
                updateFansCmd.Transaction = tx;
                updateFansCmd.CommandText = @"
                    UPDATE nodes 
                    SET properties = json_set(
                        json_set(properties, '$.fan_in', (SELECT COUNT(1) FROM edges WHERE target_id = nodes.id)),
                        '$.fan_out', (SELECT COUNT(1) FROM edges WHERE source_id = nodes.id)
                    );

                    UPDATE nodes 
                    SET properties = json_set(
                        json_set(
                            json_set(properties, '$.dead_code_candidate', 1),
                            '$.dead_code_confidence', 0.90
                        ),
                        '$.dead_code_reason', 'Procedimiento o clase huérfana con fan-in 0 sin llamadas activas'
                    )
                    WHERE label IN ('Sub', 'Function', 'Module') 
                      AND CAST(json_extract(properties, '$.fan_in') AS INTEGER) = 0
                      AND coalesce(json_extract(properties, '$.is_event_handler'), 0) = 0
                      AND (name LIKE '%test%' OR name LIKE '%prueba%' OR name LIKE '%tmp%' OR name LIKE '%backup%');
                ";
                await updateFansCmd.ExecuteNonQueryAsync(ct);
            }

            // 5. Populate virtual FTS table if available
            try
            {
                using var ftsCmd = conn.CreateCommand();
                ftsCmd.Transaction = tx;
                ftsCmd.CommandText = @"
                    CREATE VIRTUAL TABLE IF NOT EXISTS nodes_fts USING fts5(
                        name, qualified_name, file, content='nodes', content_rowid='id'
                    );
                    INSERT INTO nodes_fts(nodes_fts) VALUES('rebuild');
                ";
                await ftsCmd.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                // FTS is optional
            }

            await tx.CommitAsync(ct);
            Console.WriteLine($"[OfficeTools] ✅ Grafo enriquecido exitosamente: {qnameToId.Count} símbolos indexados con relaciones completas.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OfficeTools] ⚠️ Error al registrar artefactos en el grafo SQLite: {ex.Message}");
            Logger.LogWarning(ex, "Error al registrar artefactos de Office en el grafo SQLite");
        }
    }

    internal static object CreateProcedureProps(AccessVbaProcedure proc)
    {
        int cyclomatic = 1;
        var body = string.Join("\n", proc.BodyLines);
        cyclomatic += Regex.Matches(body, @"\b(If|ElseIf|For|Do|While|Case|Catch|And|Or)\b", RegexOptions.IgnoreCase).Count;

        // Heurísticas genéricas de defectos: ninguna depende de nombres de un repositorio concreto. Las que buscaban
        // identificadores del repositorio de prueba (auditoría 2026-09-29, H-6) daban las respuestas precargadas.
        if (body.Contains("LIKE %'", StringComparison.OrdinalIgnoreCase) || body.Contains("LIKE '%''", StringComparison.OrdinalIgnoreCase))
        {
            proc.DetectedIssues.Add("Defecto sintáctico en cláusula LIKE con comilla mal posicionada");
        }
        if (body.Contains("On Error Resume Next", StringComparison.OrdinalIgnoreCase) && !body.Contains("On Error GoTo", StringComparison.OrdinalIgnoreCase))
        {
            proc.DetectedIssues.Add("Manejo de excepciones ciego: 'On Error Resume Next' sin captura estructurada");
        }

        string codeSnippet = proc.BodyLines.Count <= 50
            ? body
            : string.Join("\n", proc.BodyLines.Take(25)) + "\n... [código truncado para brevedad] ...\n" + string.Join("\n", proc.BodyLines.TakeLast(10));

        return new
        {
            args = proc.Args,
            return_type = proc.ReturnType,
            loc = proc.Loc,
            cyclomatic = cyclomatic,
            is_event_handler = proc.IsEventHandler,
            docstring = proc.Docstring,
            hotspot = proc.Loc > 50 || cyclomatic > 10 ? 6.5 : 1.0,
            anti_pattern = proc.Loc > 100 ? "God Procedure / Bloated Method" : null,
            detected_issues = proc.DetectedIssues.Count > 0 ? proc.DetectedIssues : null,
            code = codeSnippet,
            origin = "access_vba_extractor"
        };
    }
}
