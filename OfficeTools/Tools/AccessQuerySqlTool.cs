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
/// LLM tool to inspect saved queries and views in Microsoft Access databases (.accdb / .mdb).
/// </summary>
public sealed class AccessQuerySqlTool : LlmTool
{
    private readonly IAccessSchemaExtractor? _extractor;
    private readonly string? _defaultDbPath;
    private readonly ConcurrentDictionary<string, AccessDatabaseSchema> _schemaCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="AccessQuerySqlTool"/>.
    /// </summary>
    /// <param name="extractor">Optional schema extractor service.</param>
    /// <param name="defaultDbPath">Optional default path to an Access database file.</param>
    public AccessQuerySqlTool(IAccessSchemaExtractor? extractor = null, string? defaultDbPath = null)
    {
        _extractor = extractor;
        _defaultDbPath = defaultDbPath;
    }

    /// <inheritdoc />
    public override string Name => "access_query_sql";

    /// <inheritdoc />
    public override string Description => "Obtiene la lista de consultas y vistas guardadas en una base de datos Microsoft Access (.accdb o .mdb) o la definición SQL formateada de una consulta específica si se proporciona 'query_name'.";

    /// <inheritdoc />
    public override bool IsReadOnly => true;

    /// <inheritdoc />
    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "database_path": {
          "type": "string",
          "description": "Ruta al archivo de base de datos Access (.accdb o .mdb). Opcional si ya se resolvió en el contexto o repositorio."
        },
        "query_name": {
          "type": "string",
          "description": "Nombre de la consulta guardada cuya sentencia SQL se desea inspeccionar. Opcional; si se omite, se listan todas las consultas disponibles."
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
        string? queryName = null;

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
                if (root.TryGetProperty("query_name", out var qProp) || root.TryGetProperty("queryName", out qProp))
                {
                    queryName = qProp.GetString();
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

        if (string.IsNullOrWhiteSpace(queryName))
        {
            return RenderQueryList(schema);
        }

        return RenderQuerySql(schema, queryName.Trim());
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

    private static string RenderQueryList(AccessDatabaseSchema schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Consultas Guardadas en `{schema.DatabaseName}` ({schema.Queries.Count})");
        sb.AppendLine($"**Archivo:** `{schema.FilePath}`");
        sb.AppendLine();

        if (schema.Queries.Count == 0)
        {
            sb.AppendLine("*(No se encontraron consultas guardadas en la base de datos)*");
            return sb.ToString();
        }

        sb.AppendLine("| # | Nombre de Consulta | Longitud SQL |");
        sb.AppendLine("|:---:|---|:---:|");

        int index = 1;
        foreach (var query in schema.Queries.OrderBy(q => q.Name))
        {
            var len = !string.IsNullOrWhiteSpace(query.Sql) ? $"{query.Sql.Length} caracteres" : "*(vacía o no legible)*";
            sb.AppendLine($"| {index++} | **`{query.Name}`** | {len} |");
        }

        sb.AppendLine();
        sb.AppendLine("> *Para ver la sentencia SQL completa de alguna de estas consultas, ejecuta `access_query_sql` pasando el parámetro `query_name`.*");

        return sb.ToString();
    }

    private static string RenderQuerySql(AccessDatabaseSchema schema, string queryName)
    {
        var query = schema.Queries.FirstOrDefault(q => string.Equals(q.Name, queryName, StringComparison.OrdinalIgnoreCase));
        if (query == null)
        {
            var available = schema.Queries.Count > 0
                ? string.Join(", ", schema.Queries.Select(q => $"`{q.Name}`"))
                : "*(ninguna)*";
            return $"TOOL_ERROR: La consulta '{queryName}' no fue encontrada en la base de datos '{schema.DatabaseName}'. Consultas disponibles: {available}.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Consulta SQL: `{query.Name}`");
        sb.AppendLine($"**Base de datos:** `{schema.DatabaseName}` (`{schema.FilePath}`)");
        sb.AppendLine();

        if (string.IsNullOrWhiteSpace(query.Sql))
        {
            sb.AppendLine("*(La definición SQL de esta consulta está vacía o no pudo ser extraída)*");
        }
        else
        {
            sb.AppendLine("```sql");
            sb.AppendLine(query.Sql.Trim());
            sb.AppendLine("```");
        }

        return sb.ToString();
    }
}
