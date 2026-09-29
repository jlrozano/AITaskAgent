using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OfficeTools.Abstractions;
using OfficeTools.Extractors;
using OfficeTools.Models;

namespace OfficeTools.Tools;

/// <summary>
/// LLM tool to inspect Microsoft Access database schemas (.accdb / .mdb).
/// </summary>
public sealed class AccessSchemaTool : LlmTool
{
    private readonly IAccessSchemaExtractor? _extractor;
    private readonly string? _defaultDbPath;
    private readonly ConcurrentDictionary<string, AccessDatabaseSchema> _schemaCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="AccessSchemaTool"/>.
    /// </summary>
    /// <param name="extractor">Optional schema extractor service.</param>
    /// <param name="defaultDbPath">Optional default path to an Access database file.</param>
    public AccessSchemaTool(IAccessSchemaExtractor? extractor = null, string? defaultDbPath = null)
    {
        _extractor = extractor;
        _defaultDbPath = defaultDbPath;
    }

    /// <inheritdoc />
    public override string Name => "access_schema";

    /// <inheritdoc />
    public override string Description => "Obtiene el esquema de una base de datos Microsoft Access (.accdb o .mdb). Si se omite 'table_name', lista todas las tablas, vistas/consultas, conteo de columnas y relaciones foráneas. Si se proporciona 'table_name', devuelve el detalle exhaustivo de columnas, tipos de datos, nullabilidad, índices y relaciones entrantes/salientes de esa tabla.";

    /// <inheritdoc />
    public override bool IsReadOnly => true;

    /// <inheritdoc />
    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "database_path": {
          "type": "string",
          "description": "Ruta absoluta o relativa al archivo de base de datos Access (.accdb o .mdb). Opcional si ya se encuentra en el contexto del pipeline o en el repositorio."
        },
        "table_name": {
          "type": "string",
          "description": "Nombre de la tabla a inspeccionar en detalle. Opcional; si no se especifica, se listan todas las tablas y relaciones."
        }
      }
    }
    """);

    /// <inheritdoc />
    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? requestedDbPath = null;
        string? tableName = null;

        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("database_path", out var dbProp) || root.TryGetProperty("databasePath", out dbProp))
                {
                    requestedDbPath = dbProp.GetString();
                }
                if (root.TryGetProperty("table_name", out var tProp) || root.TryGetProperty("tableName", out tProp))
                {
                    tableName = tProp.GetString();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error parsing arguments in {ToolName}", Name);
            }
        }

        var (schema, error) = await ResolveSchemaAsync(requestedDbPath, context, logger, cancellationToken);
        if (schema == null)
        {
            return error ?? "TOOL_ERROR: No se pudo resolver ni extraer el esquema de la base de datos Access.";
        }

        if (string.IsNullOrWhiteSpace(tableName))
        {
            return RenderDatabaseOverview(schema);
        }

        return RenderTableDetail(schema, tableName.Trim());
    }

    private async Task<(AccessDatabaseSchema? Schema, string? Error)> ResolveSchemaAsync(
        string? requestedPath,
        PipelineContext context,
        ILogger logger,
        CancellationToken ct)
    {
        // 1. Direct match with already stored schema in context if no path or matching path
        if (context.Metadata.TryGetValue("Office:AccessSchema", out var obj) && obj is AccessDatabaseSchema ctxSchema)
        {
            if (string.IsNullOrWhiteSpace(requestedPath) ||
                string.Equals(Path.GetFullPath(requestedPath), Path.GetFullPath(ctxSchema.FilePath), StringComparison.OrdinalIgnoreCase))
            {
                return (ctxSchema, null);
            }
        }

        // 2. Check if a collection of schemas is in context
        if (context.Metadata.TryGetValue("Office:AccessSchemas", out var listObj) && listObj is IEnumerable<AccessDatabaseSchema> schemas)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                var first = schemas.FirstOrDefault();
                if (first != null) return (first, null);
            }
            else
            {
                var match = schemas.FirstOrDefault(s =>
                    string.Equals(Path.GetFullPath(s.FilePath), Path.GetFullPath(requestedPath), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.DatabaseName, requestedPath, StringComparison.OrdinalIgnoreCase));
                if (match != null) return (match, null);
            }
        }

        // 3. Resolve file path
        var resolvedPath = ResolveDatabaseFilePath(requestedPath, context);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return (null, "TOOL_ERROR: No se especificó 'database_path' y no se encontró ninguna base de datos Access (.accdb o .mdb) en el contexto ni en el directorio del repositorio.");
        }

        if (!File.Exists(resolvedPath))
        {
            return (null, $"TOOL_ERROR: El archivo de base de datos Access no existe en la ruta: '{resolvedPath}'.");
        }

        // 4. Check memory cache
        if (_schemaCache.TryGetValue(resolvedPath, out var cached))
        {
            return (cached, null);
        }

        // 5. Extract using extractor
        try
        {
            var extractor = _extractor ??
                            context.ServiceProvider.GetService<IAccessSchemaExtractor>() ??
                            new AccessSchemaExtractor();

            logger.LogInformation("Extrayendo esquema Access desde {FilePath}", resolvedPath);
            var extracted = await extractor.ExtractSchemaAsync(resolvedPath, cancellationToken: ct);
            _schemaCache[resolvedPath] = extracted;

            // Also keep it in context metadata for fast reuse across steps/tools
            if (!context.Metadata.ContainsKey("Office:AccessSchema"))
            {
                context.Metadata["Office:AccessSchema"] = extracted;
            }

            return (extracted, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al extraer esquema de {FilePath}", resolvedPath);
            return (null, $"TOOL_ERROR: Error al extraer el esquema de '{resolvedPath}': {ex.Message}");
        }
    }

    private string? ResolveDatabaseFilePath(string? requestedPath, PipelineContext context)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            if (File.Exists(requestedPath)) return Path.GetFullPath(requestedPath);

            // Try relative to RepositoryPath if available
            if (context.Metadata.TryGetValue("RepositoryPath", out var r) && r is string repoDir && Directory.Exists(repoDir))
            {
                var combined = Path.Combine(repoDir, requestedPath);
                if (File.Exists(combined)) return Path.GetFullPath(combined);
            }

            return Path.GetFullPath(requestedPath);
        }

        if (!string.IsNullOrWhiteSpace(_defaultDbPath) && File.Exists(_defaultDbPath))
        {
            return Path.GetFullPath(_defaultDbPath);
        }

        if (context.Metadata.TryGetValue("AccessDatabasePath", out var p) && p is string ctxPath && !string.IsNullOrWhiteSpace(ctxPath) && File.Exists(ctxPath))
        {
            return Path.GetFullPath(ctxPath);
        }

        // Scan repository directory
        if (context.Metadata.TryGetValue("RepositoryPath", out var repo) && repo is string repoPath && Directory.Exists(repoPath))
        {
            var found = FindAccessFile(repoPath);
            if (found != null) return Path.GetFullPath(found);
        }

        // Scan current directory as fallback
        var localFound = FindAccessFile(Directory.GetCurrentDirectory());
        return localFound != null ? Path.GetFullPath(localFound) : null;
    }

    private static string? FindAccessFile(string rootDir)
    {
        try
        {
            return Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                {
                    var ext = Path.GetExtension(f);
                    if (!string.Equals(ext, ".accdb", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ext, ".mdb", StringComparison.OrdinalIgnoreCase))
                        return false;

                    var name = Path.GetFileName(f);
                    if (name.StartsWith("~$") || name.StartsWith(".")) return false;

                    var norm = f.Replace('/', '\\');
                    if (norm.Contains("\\.git\\") || norm.Contains("\\bin\\") || norm.Contains("\\obj\\"))
                        return false;

                    return true;
                });
        }
        catch
        {
            return null;
        }
    }

    private static string RenderDatabaseOverview(AccessDatabaseSchema schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Esquema de Base de Datos Access: `{schema.DatabaseName}`");
        sb.AppendLine($"**Archivo:** `{schema.FilePath}`");
        sb.AppendLine();

        sb.AppendLine($"## Tablas ({schema.Tables.Count})");
        if (schema.Tables.Count == 0)
        {
            sb.AppendLine("*(No se encontraron tablas de usuario en la base de datos)*");
        }
        else
        {
            sb.AppendLine("| Tabla | Columnas | Clave Primaria | Claves Foráneas Salientes |");
            sb.AppendLine("|---|:---:|---|---|");

            foreach (var table in schema.Tables.OrderBy(t => t.Name))
            {
                var pk = table.PrimaryKeys.Count > 0 ? string.Join(", ", table.PrimaryKeys) : "*(Ninguna)*";
                var outgoingFks = schema.ForeignKeys
                    .Where(fk => string.Equals(fk.FromTable, table.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(fk => $"{fk.FromColumn} → {fk.ToTable}({fk.ToColumn})")
                    .ToList();

                var fkSummary = outgoingFks.Count > 0 ? string.Join("; ", outgoingFks) : "-";
                sb.AppendLine($"| **{table.Name}** | {table.Columns.Count} | `{pk}` | {fkSummary} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"## Consultas / Vistas ({schema.Queries.Count})");
        if (schema.Queries.Count == 0)
        {
            sb.AppendLine("*(No se encontraron consultas guardadas en la base de datos)*");
        }
        else
        {
            sb.AppendLine("| # | Nombre de Consulta |");
            sb.AppendLine("|:---:|---|");
            int qIndex = 1;
            foreach (var query in schema.Queries.OrderBy(q => q.Name))
            {
                sb.AppendLine($"| {qIndex++} | `{query.Name}` |");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"## Relaciones Foráneas Globales ({schema.ForeignKeys.Count})");
        if (schema.ForeignKeys.Count == 0)
        {
            sb.AppendLine("*(No se definieron restricciones de claves foráneas explícitas)*");
        }
        else
        {
            sb.AppendLine("| Restricción | Tabla Origen | Columna Origen | Tabla Destino | Columna Destino |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var fk in schema.ForeignKeys.OrderBy(f => f.FromTable).ThenBy(f => f.Name))
            {
                sb.AppendLine($"| {fk.Name} | `{fk.FromTable}` | `{fk.FromColumn}` | `{fk.ToTable}` | `{fk.ToColumn}` |");
            }
        }

        return sb.ToString();
    }

    private static string RenderTableDetail(AccessDatabaseSchema schema, string tableName)
    {
        var table = schema.Tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
        if (table == null)
        {
            var available = schema.Tables.Count > 0
                ? string.Join(", ", schema.Tables.Select(t => $"`{t.Name}`"))
                : "*(ninguna)*";
            return $"TOOL_ERROR: La tabla '{tableName}' no fue encontrada en la base de datos '{schema.DatabaseName}'. Tablas disponibles: {available}.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Detalle de Tabla: `{table.Name}`");
        sb.AppendLine($"**Base de datos:** `{schema.DatabaseName}` (`{schema.FilePath}`)");
        sb.AppendLine();

        // 1. Columnas
        sb.AppendLine($"## Columnas ({table.Columns.Count})");
        sb.AppendLine("| # | Columna | Tipo | Longitud / Tamaño | Permite Nulos | Valor por Defecto | Clave Primaria |");
        sb.AppendLine("|:---:|---|---|:---:|:---:|---|:---:|");

        var pkSet = new HashSet<string>(table.PrimaryKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var col in table.Columns.OrderBy(c => c.OrdinalPosition))
        {
            var sizeStr = col.Size.HasValue ? col.Size.Value.ToString() : "-";
            var nullableStr = col.Nullable ? "Sí" : "**No**";
            var defaultVal = !string.IsNullOrWhiteSpace(col.DefaultValue) ? $"`{col.DefaultValue}`" : "-";
            var isPk = pkSet.Contains(col.Name) ? "✓ (PK)" : "-";

            sb.AppendLine($"| {col.OrdinalPosition} | **{col.Name}** | `{col.Type}` | {sizeStr} | {nullableStr} | {defaultVal} | {isPk} |");
        }
        sb.AppendLine();

        // 2. Índices
        sb.AppendLine($"## Índices ({table.Indexes.Count})");
        if (table.Indexes.Count == 0)
        {
            sb.AppendLine("*(No hay índices explícitos definidos en esta tabla)*");
        }
        else
        {
            sb.AppendLine("| Nombre de Índice | Columnas | ¿Es Único? | ¿Es Clave Primaria? |");
            sb.AppendLine("|---|---|:---:|:---:|");
            foreach (var idx in table.Indexes.OrderBy(i => i.Name))
            {
                var cols = string.Join(", ", idx.Columns);
                var unique = idx.IsUnique ? "Sí" : "No";
                var pk = idx.IsPrimaryKey ? "✓ Sí" : "No";
                sb.AppendLine($"| `{idx.Name}` | `{cols}` | {unique} | {pk} |");
            }
        }
        sb.AppendLine();

        // 3. Relaciones Salientes
        var outgoingFks = schema.ForeignKeys
            .Where(fk => string.Equals(fk.FromTable, table.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        sb.AppendLine($"## Relaciones Salientes ({outgoingFks.Count})");
        if (outgoingFks.Count == 0)
        {
            sb.AppendLine("*(Esta tabla no tiene claves foráneas hacia otras tablas)*");
        }
        else
        {
            sb.AppendLine("| Restricción | Columna Local | Tabla Destino | Columna Destino |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var fk in outgoingFks)
            {
                sb.AppendLine($"| {fk.Name} | `{fk.FromColumn}` | **`{fk.ToTable}`** | `{fk.ToColumn}` |");
            }
        }
        sb.AppendLine();

        // 4. Relaciones Entrantes
        var incomingFks = schema.ForeignKeys
            .Where(fk => string.Equals(fk.ToTable, table.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        sb.AppendLine($"## Relaciones Entrantes ({incomingFks.Count})");
        if (incomingFks.Count == 0)
        {
            sb.AppendLine("*(Ninguna otra tabla tiene claves foráneas apuntando a esta tabla)*");
        }
        else
        {
            sb.AppendLine("| Restricción | Tabla Origen | Columna Origen | Columna Local |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var fk in incomingFks)
            {
                sb.AppendLine($"| {fk.Name} | **`{fk.FromTable}`** | `{fk.FromColumn}` | `{fk.ToColumn}` |");
            }
        }

        return sb.ToString();
    }
}
