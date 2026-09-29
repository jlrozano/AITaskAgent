using CodebaseAnalysis.Core.Knowledge;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Reports;

/// <summary>Acciones de una versión (plan v2 §6.4); los valores son los de <c>ReportVersionDto.action</c>.</summary>
public static class ReportVersionActionNames
{
    public const string Baseline = "baseline";
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Restored = "restored";
    public const string Deleted = "deleted";
}

/// <summary>Quién escribió una versión: <c>pipeline</c>, <c>chat</c> (con sesión y turno) o <c>ui</c>.</summary>
public sealed record ReportVersionOrigin(string Type, string? SessionId = null, string? TurnId = null)
{
    public const string PipelineType = "pipeline";
    public const string ChatType = "chat";
    public const string UiType = "ui";

    public static ReportVersionOrigin Pipeline { get; } = new(PipelineType);
    public static ReportVersionOrigin Ui { get; } = new(UiType);
    public static ReportVersionOrigin Chat(string? sessionId, string? turnId) => new(ChatType, sessionId, turnId);
}

/// <summary>Una entrada de <c>history.json</c>.</summary>
public sealed record ReportVersion(
    string VersionId,
    DateTimeOffset CreatedAt,
    string Action,
    ReportVersionOrigin Origin,
    string? Note,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Historial de versiones de los informes (plan v2 §6.4): <c>&lt;repo&gt;/.codebase/report-history/&lt;catalog|custom&gt;/&lt;fichero&gt;/</c>
/// con <c>history.json</c> y una instantánea <c>vNNNN.md</c> por versión, con los mismos bytes que tenía el fichero.
/// Escrituras atómicas (temporal y <c>File.Move</c>). Se conservan <see cref="MaxVersions"/> versiones por informe; la
/// <c>baseline</c> más antigua no se poda.
/// </summary>
public sealed partial class ReportHistoryStore(KnowledgePaths paths, TimeProvider? clock = null)
{
    public const int MaxVersions = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Una sola escritura de historial a la vez en el proceso: bastan milisegundos y evita ids repetidos.</summary>
    private static readonly Lock WriteLock = new();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public KnowledgePaths Paths { get; } = paths;

    [GeneratedRegex(@"^v\d{4,}$", RegexOptions.CultureInvariant)]
    public static partial Regex VersionIdPattern();

    /// <summary>SHA-256 en hexadecimal minúscula.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>SHA-256 de un texto tal como se escribe en disco (UTF-8 sin BOM).</summary>
    public static string Sha256(string content) => Sha256(Encoding.UTF8.GetBytes(content));

    /// <summary>SHA-256 del fichero, o null si no existe.</summary>
    public static string? FileSha256(string path) => File.Exists(path) ? Sha256(File.ReadAllBytes(path)) : null;

    /// <summary>Versiones en orden de creación (la más antigua primero). Vacío si no hay historial o está dañado.</summary>
    public IReadOnlyList<ReportVersion> List(SourceRef report)
    {
        var file = Path.Combine(Paths.ReportHistoryFolder(report), KnowledgePaths.ReportHistoryFileName);
        if (!File.Exists(file))
            return [];

        try
        {
            return JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(file), JsonOptions)?.Versions ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    public ReportVersion? Latest(SourceRef report) => List(report) is { Count: > 0 } versions ? versions[^1] : null;

    /// <summary>Contenido de una versión, o null si el id no es válido o no existe.</summary>
    public string? ReadContent(SourceRef report, string? versionId)
    {
        if (versionId == null || !VersionIdPattern().IsMatch(versionId) || List(report).All(v => v.VersionId != versionId))
            return null;

        var file = Path.Combine(Paths.ReportHistoryFolder(report), versionId + ".md");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>Guarda <paramref name="content"/> como versión nueva y la devuelve.</summary>
    public ReportVersion Append(SourceRef report, string content, string action, ReportVersionOrigin origin, string? note)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        lock (WriteLock)
        {
            var folder = Paths.ReportHistoryFolder(report);
            Directory.CreateDirectory(folder);
            var versions = List(report).ToList();
            var next = versions.Count == 0 ? 1 : versions.Max(v => int.Parse(v.VersionId.AsSpan(1))) + 1;
            var version = new ReportVersion(
                $"v{next:D4}",
                _clock.GetUtcNow(),
                action,
                origin,
                string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                bytes.LongLength,
                Sha256(bytes));

            WriteAtomic(Path.Combine(folder, version.VersionId + ".md"), bytes);
            versions.Add(version);
            Prune(folder, versions);
            WriteAtomic(Path.Combine(folder, KnowledgePaths.ReportHistoryFileName),
                JsonSerializer.SerializeToUtf8Bytes(new HistoryFile(1, versions), JsonOptions));
            return version;
        }
    }

    /// <summary>Borra el historial entero de un informe.</summary>
    public void DeleteAll(SourceRef report)
    {
        var folder = Paths.ReportHistoryFolder(report);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    private static void Prune(string folder, List<ReportVersion> versions)
    {
        var protectedBaseline = versions.FirstOrDefault(v => v.Action == ReportVersionActionNames.Baseline);
        while (versions.Count > MaxVersions)
        {
            var victim = versions.First(v => !ReferenceEquals(v, protectedBaseline));
            versions.Remove(victim);
            var snapshot = Path.Combine(folder, victim.VersionId + ".md");
            if (File.Exists(snapshot))
                File.Delete(snapshot);
        }
    }

    /// <summary>Escritura atómica: temporal en la misma carpeta y <c>File.Move</c> con sobrescritura.</summary>
    internal static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    private sealed record HistoryFile(int SchemaVersion, List<ReportVersion> Versions);
}
