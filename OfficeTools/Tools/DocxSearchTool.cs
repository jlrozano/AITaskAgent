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
/// LLM tool to search inside WordprocessingML (.docx) documents by keyword or concept, with optional heading filtering.
/// </summary>
public sealed class DocxSearchTool : LlmTool
{
    private readonly IDocxReader? _docxReader;
    private readonly string? _defaultDocPath;
    private readonly ConcurrentDictionary<string, DocumentInfo> _documentCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="DocxSearchTool"/>.
    /// </summary>
    /// <param name="docxReader">Optional Word document reader service.</param>
    /// <param name="defaultDocPath">Optional default file or directory path for Word documents.</param>
    public DocxSearchTool(IDocxReader? docxReader = null, string? defaultDocPath = null)
    {
        _docxReader = docxReader;
        _defaultDocPath = defaultDocPath;
    }

    /// <inheritdoc />
    public override string Name => "docx_search";

    /// <inheritdoc />
    public override string Description => "Busca términos, conceptos o contenido relevante dentro de documentos Word (.docx), permitiendo opcionalmente filtrar por el título de la sección o encabezado ('heading').";

    /// <inheritdoc />
    public override bool IsReadOnly => true;

    /// <inheritdoc />
    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "file_path": {
          "type": "string",
          "description": "Ruta específica al archivo Word (.docx). Opcional si ya se detectaron documentos en el contexto o en el repositorio."
        },
        "query": {
          "type": "string",
          "description": "Término, palabra o concepto a buscar dentro de las secciones, párrafos y tablas del documento."
        },
        "heading": {
          "type": "string",
          "description": "Filtro opcional para limitar la búsqueda a secciones cuyos títulos o encabezados contengan este texto."
        }
      },
      "required": ["query"]
    }
    """);

    /// <inheritdoc />
    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? requestedFilePath = null;
        string? query = null;
        string? heading = null;

        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("file_path", out var fProp) || root.TryGetProperty("filePath", out fProp))
                {
                    requestedFilePath = fProp.GetString();
                }
                if (root.TryGetProperty("query", out var qProp))
                {
                    query = qProp.GetString();
                }
                if (root.TryGetProperty("heading", out var hProp))
                {
                    heading = hProp.GetString();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error parsing arguments in {ToolName}", Name);
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return "TOOL_ERROR: El parámetro 'query' es obligatorio para realizar la búsqueda en documentos Word.";
        }

        var (documents, error) = await ResolveDocumentsAsync(requestedFilePath, context, logger, cancellationToken);
        if (documents.Count == 0)
        {
            return error ?? "TOOL_ERROR: No se encontraron documentos Word (.docx) para realizar la búsqueda.";
        }

        return SearchDocuments(documents, query.Trim(), heading?.Trim());
    }

    private async Task<(List<DocumentInfo> Documents, string? Error)> ResolveDocumentsAsync(
        string? requestedPath,
        PipelineContext context,
        ILogger logger,
        CancellationToken ct)
    {
        var docs = new List<DocumentInfo>();
        var reader = _docxReader ??
                     context.ServiceProvider.GetService<IDocxReader>() ??
                     new DocxReader();

        // 1. If explicit file path is requested
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            var resolvedFile = ResolveFilePath(requestedPath, context);
            if (string.IsNullOrWhiteSpace(resolvedFile) || !File.Exists(resolvedFile))
            {
                return (docs, $"TOOL_ERROR: El archivo Word especificado no existe en la ruta: '{requestedPath}'.");
            }

            if (!string.Equals(Path.GetExtension(resolvedFile), ".docx", StringComparison.OrdinalIgnoreCase))
            {
                return (docs, $"TOOL_ERROR: El archivo '{resolvedFile}' no es un archivo .docx compatible.");
            }

            var doc = await GetOrLoadDocumentAsync(resolvedFile, reader, ct);
            if (doc != null) docs.Add(doc);
            return (docs, null);
        }

        // 2. Check if documents already exist in context
        if (context.Metadata.TryGetValue("Office:Documents", out var ctxObj) && ctxObj is IEnumerable<DocumentInfo> ctxDocs)
        {
            docs.AddRange(ctxDocs);
            if (docs.Count > 0) return (docs, null);
        }

        // 3. Check default path
        if (!string.IsNullOrWhiteSpace(_defaultDocPath))
        {
            if (File.Exists(_defaultDocPath) && string.Equals(Path.GetExtension(_defaultDocPath), ".docx", StringComparison.OrdinalIgnoreCase))
            {
                var doc = await GetOrLoadDocumentAsync(Path.GetFullPath(_defaultDocPath), reader, ct);
                if (doc != null) docs.Add(doc);
                return (docs, null);
            }
            if (Directory.Exists(_defaultDocPath))
            {
                foreach (var file in FindDocxFiles(Path.GetFullPath(_defaultDocPath)))
                {
                    var doc = await GetOrLoadDocumentAsync(file, reader, ct);
                    if (doc != null) docs.Add(doc);
                }
                if (docs.Count > 0) return (docs, null);
            }
        }

        // 4. Scan repository or current directory
        var rootDir = context.Metadata.TryGetValue("RepositoryPath", out var repo) && repo is string r && Directory.Exists(r)
            ? r
            : Directory.GetCurrentDirectory();

        var discoveredFiles = FindDocxFiles(rootDir);
        foreach (var file in discoveredFiles)
        {
            var doc = await GetOrLoadDocumentAsync(file, reader, ct);
            if (doc != null) docs.Add(doc);
        }

        if (docs.Count == 0)
        {
            return (docs, $"TOOL_ERROR: No se encontraron documentos Word (.docx) en el directorio '{rootDir}'.");
        }

        // Cache in context metadata for future use
        context.Metadata["Office:Documents"] = docs;

        return (docs, null);
    }

    private async Task<DocumentInfo?> GetOrLoadDocumentAsync(string filePath, IDocxReader reader, CancellationToken ct)
    {
        if (_documentCache.TryGetValue(filePath, out var cached))
        {
            return cached;
        }

        try
        {
            var loaded = await reader.ReadDocumentAsync(filePath, ct);
            _documentCache[filePath] = loaded;
            return loaded;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveFilePath(string requestedPath, PipelineContext context)
    {
        if (File.Exists(requestedPath)) return Path.GetFullPath(requestedPath);

        if (context.Metadata.TryGetValue("RepositoryPath", out var r) && r is string repoDir && Directory.Exists(repoDir))
        {
            var combined = Path.Combine(repoDir, requestedPath);
            if (File.Exists(combined)) return Path.GetFullPath(combined);
        }

        var localCombined = Path.Combine(Directory.GetCurrentDirectory(), requestedPath);
        if (File.Exists(localCombined)) return Path.GetFullPath(localCombined);

        return null;
    }

    private static List<string> FindDocxFiles(string dirPath)
    {
        var list = new List<string>();
        if (!Directory.Exists(dirPath)) return list;

        try
        {
            foreach (var f in Directory.EnumerateFiles(dirPath, "*.docx", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("~$") || name.StartsWith(".")) continue;

                var norm = f.Replace('/', '\\');
                if (norm.Contains("\\.git\\") || norm.Contains("\\bin\\") || norm.Contains("\\obj\\"))
                    continue;

                list.Add(Path.GetFullPath(f));
            }
        }
        catch
        {
            // Ignore access errors
        }

        return list;
    }

    private static string SearchDocuments(List<DocumentInfo> documents, string query, string? headingFilter)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Resultados de Búsqueda Docx para: \"{query}\"");
        if (!string.IsNullOrWhiteSpace(headingFilter))
        {
            sb.AppendLine($"*(Filtrado por encabezado: '{headingFilter}')*");
        }
        sb.AppendLine();

        int totalMatches = 0;

        foreach (var doc in documents)
        {
            var matchedSections = new List<DocumentSection>();
            var matchedTables = new List<DocumentTable>();

            // 1. Search in Sections
            foreach (var section in doc.Sections)
            {
                if (!string.IsNullOrWhiteSpace(headingFilter) &&
                    !section.Title.Contains(headingFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool titleMatches = section.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool contentMatches = section.Content.Contains(query, StringComparison.OrdinalIgnoreCase);

                if (titleMatches || contentMatches)
                {
                    matchedSections.Add(section);
                }
            }

            // 2. Search in Tables (if not heading-filtered, or if heading is not restrictive)
            if (string.IsNullOrWhiteSpace(headingFilter))
            {
                foreach (var table in doc.Tables)
                {
                    bool headerMatches = table.Headers.Any(h => h.Contains(query, StringComparison.OrdinalIgnoreCase));
                    bool cellMatches = table.Rows.Any(row => row.Any(c => c.Contains(query, StringComparison.OrdinalIgnoreCase)));

                    if (headerMatches || cellMatches)
                    {
                        matchedTables.Add(table);
                    }
                }
            }

            if (matchedSections.Count > 0 || matchedTables.Count > 0)
            {
                totalMatches += matchedSections.Count + matchedTables.Count;
                sb.AppendLine($"## Documento: `{doc.Name}`");
                sb.AppendLine($"**Ruta:** `{doc.FilePath}`");
                sb.AppendLine();

                if (matchedSections.Count > 0)
                {
                    sb.AppendLine($"### Secciones Coincidentes ({matchedSections.Count})");
                    sb.AppendLine();

                    foreach (var s in matchedSections)
                    {
                        var headingPrefix = new string('#', Math.Clamp(s.Level + 2, 3, 6));
                        sb.AppendLine($"{headingPrefix} {s.Title}");
                        sb.AppendLine();
                        sb.AppendLine(s.Content);
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.AppendLine();
                    }
                }

                if (matchedTables.Count > 0)
                {
                    sb.AppendLine($"### Tablas Coincidentes ({matchedTables.Count})");
                    sb.AppendLine();

                    foreach (var t in matchedTables)
                    {
                        sb.AppendLine($"#### Tabla {t.TableIndex}");
                        sb.AppendLine();
                        sb.AppendLine(t.ToMarkdownTable());
                        sb.AppendLine();
                    }
                }
            }
        }

        if (totalMatches == 0)
        {
            var filterMsg = !string.IsNullOrWhiteSpace(headingFilter)
                ? $" con filtro de encabezado '{headingFilter}'"
                : string.Empty;
            return $"No se encontraron coincidencias para el término '{query}'{filterMsg} en {documents.Count} documento(s) Word inspeccionado(s).";
        }

        return sb.ToString();
    }
}
