using CodebaseAnalysis.Core.Reports;
using System.Collections.Concurrent;

namespace CodebaseAnalysis.Core.Knowledge;

/// <summary>
/// SHA-256 de un fichero recordado por <c>(ruta, tamaño, fecha de modificación)</c>. El estado de grafo y embeddings de
/// cada fuente compara su SHA actual, y el listado de documentos y el prompt del chat lo piden para todas las fuentes en
/// cada petición: sin caché, cada listado volvía a leer y hashear todos los <c>content.md</c>. Cualquier escritura
/// cambia la fecha (o el tamaño) y el siguiente acceso recalcula.
/// </summary>
public static class FileShaCache
{
    private sealed record Entry(long Length, DateTime LastWriteUtc, string Sha256);

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SHA-256 del fichero, o null si no existe.</summary>
    public static string? Get(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            Entries.TryRemove(info.FullName, out _);
            return null;
        }

        if (Entries.TryGetValue(info.FullName, out var cached) && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached.Sha256;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(info.FullName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        var sha = ReportHistoryStore.Sha256(bytes);
        // Solo se recuerda si nadie escribió mientras se leía: tamaño y fecha tienen que ser los de antes de leer.
        var (length, lastWrite) = (info.Length, info.LastWriteTimeUtc);
        info.Refresh();
        if (info.Exists && info.Length == length && info.LastWriteTimeUtc == lastWrite && length == bytes.LongLength)
            Entries[info.FullName] = new Entry(length, lastWrite, sha);
        return sha;
    }
}
