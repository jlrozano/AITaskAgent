using AITaskAgent.FileTools;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysisWeb.Workspace;

namespace CodebaseAnalysisWeb.Api;

/// <summary>Un fichero de texto del repositorio para el visor de código.</summary>
public sealed record SourceFileDto(string Path, string Text, int TotalLines, long SizeBytes);

/// <summary>
/// <c>GET /api/source?path=&lt;ruta&gt;</c>: el visor abre las referencias <c>ruta:línea</c> del chat. Solo lectura y solo
/// texto, siempre dentro del repositorio abierto: la ruta se resuelve como en la auditoría del chat, y se rechazan
/// <c>.git</c> y los enlaces simbólicos o uniones (podrían apuntar fuera).
/// </summary>
public static class SourceEndpoints
{
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Bytes que se miran en busca de un NUL para dar el fichero por binario.</summary>
    private const int BinaryProbeBytes = 8192;

    public static void MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/source", async (string? path, WorkspaceState workspace, CancellationToken ct) =>
        {
            var active = workspace.Current;
            if (active == null)
                return ApiErrorsFactory.NoWorkspace().ToResult(StatusCodes.Status409Conflict);

            var root = Path.GetFullPath(active.RepositoryPath);
            var full = string.IsNullOrWhiteSpace(path) ? null : TurnEvidence.ToFullPath(root, path);
            if (full == null)
                return new ApiError(ApiErrorCodes.InvalidPath, "La ruta tiene que estar dentro del repositorio abierto.").ToResult(StatusCodes.Status400BadRequest);

            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (!File.Exists(full) || IsHidden(relative) || HasLink(root, full))
                return ApiErrorsFactory.NotFound($"No existe '{relative}' en el repositorio.").ToResult(StatusCodes.Status404NotFound);

            var info = new FileInfo(full);
            if (info.Length > MaxBytes)
                return new ApiError(ApiErrorCodes.FileTooLarge, $"'{relative}' pesa más de {MaxBytes / (1024 * 1024)} MB.").ToResult(StatusCodes.Status413PayloadTooLarge);

            var bytes = await File.ReadAllBytesAsync(full, ct);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, BinaryProbeBytes)).IndexOf((byte)0) >= 0)
                return new ApiError(ApiErrorCodes.UnsupportedType, $"'{relative}' no es un fichero de texto.").ToResult(StatusCodes.Status400BadRequest);

            var text = Decode(bytes).ReplaceLineEndings("\n");
            var totalLines = text.Length == 0 ? 0 : text.TrimEnd('\n').Split('\n').Length;
            return Results.Ok(new SourceFileDto(relative, text, totalLines, info.Length));
        });
    }

    /// <summary>
    /// La regla de las tools (<see cref="TextFileEncoding"/>, auditoría H-8): UTF-8 (con o sin BOM); si no lo es, la
    /// codificación del repositorio que fijó el registro de tools al abrirlo (<c>JavaAnalyzer:DefaultEncoding</c> o el
    /// <c>pom.xml</c>) o, si no declara ninguna, Latin-1, que es lo que suelen dar las exportaciones de Office y VBA.
    /// </summary>
    private static string Decode(byte[] bytes) => TextFileEncoding.Decode(bytes).Text;

    private static bool IsHidden(string relative) =>
        relative.Split('/').Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase));

    /// <summary>Algún tramo de la ruta, desde el fichero hasta la raíz (sin incluirla), es un enlace o una unión.</summary>
    private static bool HasLink(string root, string full)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(root);
        FileSystemInfo? current = new FileInfo(full);
        while (current != null && !Path.TrimEndingDirectorySeparator(current.FullName).Equals(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo dir => dir.Parent,
                _ => null
            };
        }
        return false;
    }
}
