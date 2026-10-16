using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Logging;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// Envuelve una tool de ficheros de solo lectura para que su descripción diga qué raíces tiene permitidas el chat (el
/// repositorio y las rutas de lectura adicionales). Solo cambia el texto que ve el modelo: el límite real lo sigue
/// poniendo FileTools en cada llamada, con las carpetas de <c>FileToolsConfiguration</c>.
/// </summary>
public sealed class ReadRootsAwareTool(ITool inner, string repositoryPath, IReadOnlyList<string> extraRoots) : ITool
{
    /// <summary>Las tools de FileTools que leen ficheros y reciben rutas.</summary>
    public static IReadOnlySet<string> FileToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_dir", "find_by_name", "grep_search", "view_file", "view_file_outline", "view_code_item"
    };

    /// <summary>Las mismas tools, con la descripción ampliada en las de <see cref="FileToolNames"/>.</summary>
    public static IEnumerable<ITool> Annotate(IEnumerable<ITool> tools, string repositoryPath, IReadOnlyList<string> extraRoots) =>
        tools.Select(t => FileToolNames.Contains(t.Name) ? new ReadRootsAwareTool(t, repositoryPath, extraRoots) : t);

    public string Name => inner.Name;

    public string Description => inner.Description + RootsNote();

    public string? UsageGuidelines => inner.UsageGuidelines;

    public bool IsReadOnly => inner.IsReadOnly;

    public ToolDefinition GetDefinition() => inner.GetDefinition() with { Description = Description };

    public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken = default) =>
        inner.ExecuteAsync(argumentsJson, context, stepName, logger, cancellationToken);

    private string RootsNote() =>
        $"\n\nAllowed roots: the repository '{repositoryPath}' (relative paths resolve against it)"
        + (extraRoots.Count == 0
            ? ". Nothing outside it can be read."
            : $" and, read-only, {string.Join(", ", extraRoots.Select(r => $"'{r}'"))}. For the latter pass absolute paths inside them. Nothing else can be read.");
}
