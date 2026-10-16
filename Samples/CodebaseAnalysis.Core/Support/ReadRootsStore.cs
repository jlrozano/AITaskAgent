using System.Text.Json;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Las rutas de lectura adicionales que el usuario ha añadido para un repositorio, en
/// <c>&lt;repo&gt;/.codebase/chat-read-roots.json</c> (<see cref="KnowledgePaths.ChatReadRootsFile"/>). Solo guarda rutas:
/// no indexa ni copia nada de ellas y nunca escribe en ellas.
/// </summary>
public sealed class ReadRootsStore(KnowledgePaths paths, bool allowUnc = false)
{
    public const int MaxRoots = 20;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>Lo guardado, tal cual (sin revalidar). Un fichero ilegible o inexistente es una lista vacía.</summary>
    public IReadOnlyList<string> List()
    {
        lock (Gate)
        {
            return Read();
        }
    }

    /// <summary>Valida y guarda la ruta; añadir una que ya estaba no hace nada. Devuelve la ruta canónica.</summary>
    /// <exception cref="ReadRootException">La ruta no vale o ya hay demasiadas.</exception>
    public string Add(string? path)
    {
        var full = AllowedReadRoots.Validate(path, paths, allowUnc);
        lock (Gate)
        {
            var roots = Read().ToList();
            if (roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                return full;
            if (roots.Count >= MaxRoots)
                throw new ReadRootException($"Ya hay {MaxRoots} rutas de lectura adicionales: quita alguna antes de añadir otra.");
            roots.Add(full);
            Write(roots);
            return full;
        }
    }

    /// <summary>Quita la ruta (se compara sin distinguir mayúsculas ni separador final); false si no estaba.</summary>
    public bool Remove(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        lock (Gate)
        {
            var roots = Read().ToList();
            var key = Path.TrimEndingDirectorySeparator(path.Trim());
            var removed = roots.RemoveAll(r => Path.TrimEndingDirectorySeparator(r).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return false;
            Write(roots);
            return true;
        }
    }

    private List<string> Read()
    {
        try
        {
            if (!File.Exists(paths.ChatReadRootsFile))
                return [];
            return JsonSerializer.Deserialize<RootsFile>(File.ReadAllBytes(paths.ChatReadRootsFile), JsonOptions)?.Roots?
                .Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(List<string> roots) =>
        ReportHistoryStore.WriteAtomic(paths.ChatReadRootsFile, JsonSerializer.SerializeToUtf8Bytes(new RootsFile(1, roots), JsonOptions));

    private sealed record RootsFile(int Version, List<string>? Roots);
}
