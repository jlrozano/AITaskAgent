using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Progress;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Enriquecedor de documentación Markdown (.md) descubierta dinámicamente en el repositorio analizado.
/// No inyecta ni depende de ninguna implementación estática externa.
/// </summary>
public static class WorkflowAndMarkdownEnricher
{
    private const string Source = nameof(WorkflowAndMarkdownEnricher);

    private static readonly Regex HeadingRegex = new(@"^(#{1,3})\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex RoleRegex = new(@"(?:rol|role|auditor|synthesizer)[:\s]+[`""']?([A-Za-z0-9_]+)[`""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // reportsDirectory: carpeta de informes del pipeline; no se ingesta como documentación del repositorio.
    public static async Task EnrichAsync(PipelineContext context, string repositoryPath, string dbPath, string? reportsDirectory, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);

        var repoRoot = Path.GetFullPath(repositoryPath);
        var dirName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var projectName = await GraphProject.ResolveAsync(conn, !string.IsNullOrWhiteSpace(dirName) ? dirName : "Repository", ct);

        context.Report(Source, AnalysisLogLevel.Info, $"[WorkflowAndMarkdownEnricher] 📑 Escaneando documentación Markdown (.md) en '{repoRoot}'...");

        // 1. Ingestar y enriquecer todos los archivos Markdown (.md) que pertenezcan al repositorio
        var mdFiles = Directory.Exists(repoRoot)
            ? Directory.GetFiles(repoRoot, "*.md", SearchOption.AllDirectories)
                .Where(f => !RepositoryPathFilter.IsExcluded(repoRoot, f, reportsDirectory))
                .ToList()
            : [];

        if (mdFiles.Count == 0)
        {
            context.Report(Source, AnalysisLogLevel.Info, "[WorkflowAndMarkdownEnricher] ℹ️ No se detectaron archivos Markdown (.md) en el repositorio.");
            return;
        }

        var promptTemplateIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var mdFile in mdFiles)
        {
            try
            {
                var relPath = Path.GetRelativePath(repoRoot, mdFile).Replace('\\', '/');
                var fileName = Path.GetFileName(mdFile);
                var fileNameNoExt = Path.GetFileNameWithoutExtension(mdFile);
                var content = await File.ReadAllTextAsync(mdFile, ct);
                var lines = content.Split('\n');

                bool isPromptTemplate = relPath.Contains("template", StringComparison.OrdinalIgnoreCase)
                                     || fileName.EndsWith("_prompt.md", StringComparison.OrdinalIgnoreCase)
                                     || fileName.EndsWith("_system.md", StringComparison.OrdinalIgnoreCase);

                string label = isPromptTemplate ? "PromptTemplate" : "DocSection";
                string qualifiedName = isPromptTemplate
                    ? $"template.{fileNameNoExt}"
                    : $"doc.{relPath.Replace('/', '.').Replace(".md", "")}";

                string templateType = fileName.EndsWith("_system.md", StringComparison.OrdinalIgnoreCase)
                    ? "system_instructions"
                    : fileName.EndsWith("_prompt.md", StringComparison.OrdinalIgnoreCase)
                        ? "user_prompt"
                        : "documentation";

                // Extraer rol objetivo si el markdown lo especifica
                string? targetRole = null;
                var roleMatch = RoleRegex.Match(content);
                if (roleMatch.Success)
                {
                    targetRole = roleMatch.Groups[1].Value;
                }

                // Extraer encabezados principales
                var headings = HeadingRegex.Matches(content).Select(m => m.Groups[2].Value.Trim()).Take(5).ToList();
                var purpose = headings.FirstOrDefault() ?? fileNameNoExt;

                // Crear resumen de contenido
                var snippet = content.Length > 800 ? content[..800] + "..." : content;

                var props = new Dictionary<string, object?>
                {
                    ["role"] = targetRole,
                    ["template_type"] = templateType,
                    ["purpose"] = purpose,
                    ["headings"] = headings,
                    ["line_count"] = lines.Length,
                    ["char_count"] = content.Length,
                    ["content_snippet"] = snippet
                };
                var propsJson = JsonSerializer.Serialize(props);

                // Upsert nodo PromptTemplate / DocSection
                long nodeId = await UpsertNodeAsync(conn, projectName, label, fileName, qualifiedName, relPath, 1, lines.Length, propsJson, ct);

                if (isPromptTemplate)
                {
                    promptTemplateIds[fileName] = nodeId;
                    promptTemplateIds[fileNameNoExt] = nodeId;
                }
            }
            catch (Exception ex)
            {
                context.Report(Source, AnalysisLogLevel.Warning, $"[WorkflowAndMarkdownEnricher] ⚠️ Error procesando '{mdFile}': {ex.Message}");
            }
        }

        context.Report(Source, AnalysisLogLevel.Success, $"[WorkflowAndMarkdownEnricher] ✅ {mdFiles.Count} archivos Markdown procesados ({promptTemplateIds.Count / 2} templates catalogados).");
    }

    private static async Task<long> UpsertNodeAsync(
        SqliteConnection conn, string project, string label,
        string name, string qualifiedName, string filePath, int startLine, int endLine,
        string properties, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO nodes (project, project_id, label, name, qualified_name, file_path, file, start_line, end_line, line_start, line_end, properties)
            VALUES (@project, 1, @label, @name, @qualifiedName, @filePath, @filePath, @startLine, @endLine, @startLine, @endLine, @properties)
            ON CONFLICT(project, qualified_name) DO UPDATE SET
                label = excluded.label,
                name = excluded.name,
                file_path = excluded.file_path,
                file = excluded.file,
                start_line = excluded.start_line,
                end_line = excluded.end_line,
                line_start = excluded.line_start,
                line_end = excluded.line_end,
                properties = excluded.properties;
            SELECT id FROM nodes WHERE project = @project AND qualified_name = @qualifiedName LIMIT 1;
        ";
        cmd.Parameters.AddWithValue("@project", project);
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@qualifiedName", qualifiedName);
        cmd.Parameters.AddWithValue("@filePath", filePath);
        cmd.Parameters.AddWithValue("@startLine", startLine);
        cmd.Parameters.AddWithValue("@endLine", endLine);
        cmd.Parameters.AddWithValue("@properties", properties);

        var idRes = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(idRes);
    }
}
