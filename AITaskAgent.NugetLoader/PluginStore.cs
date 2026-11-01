using NuGet.Versioning;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AITaskAgent.NugetLoader;

/// <summary>
/// The files of <see cref="PluginLoader"/> under its install folder: <c>plugins.json</c> (what is installed) and
/// <c>plugin-locks/&lt;id lower&gt;/&lt;version lower&gt;/nuget.lock.json</c> (the lock of each NuGet plugin, format of architecture §16.2).
/// Every write goes to a temporary file that then replaces the old one; a file that cannot be read is copied aside before anything replaces
/// it. A read-modify-write of <c>plugins.json</c> holds <see cref="Lock"/>, so loaders over the same folder do not drop each other's entries.
/// The first read without <c>plugins.json</c> migrates a <c>steps-packages.json</c> of the former step-package loader (and its locks).
/// </summary>
internal sealed class PluginStore
{
    public const string FileName = "plugins.json";

    public const string LegacyFileName = "steps-packages.json";

    public const string LockFileName = "nuget.lock.json";

    public const string LocksFolder = "plugin-locks";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _legacyRoot;

    public PluginStore(string installDirectory, string? legacyDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        Root = Path.GetFullPath(installDirectory);
        _legacyRoot = Path.GetFullPath(legacyDirectory ?? installDirectory);
    }

    /// <summary>The install folder, absolute.</summary>
    public string Root { get; }

    public string FilePath => Path.Combine(Root, FileName);

    private string LegacyPath => Path.Combine(_legacyRoot, LegacyFileName);

    /// <summary>
    /// The installed plugins; an unreadable file gives an empty list and a problem naming the copy kept of it. When <c>plugins.json</c> does
    /// not exist and a <c>steps-packages.json</c> does, it is migrated first (<paramref name="note"/> says so).
    /// </summary>
    public (PluginFile File, LoadProblem? Problem) Read(out string? note)
    {
        note = null;
        if (!File.Exists(FilePath) && File.Exists(LegacyPath))
        {
            using IDisposable folderLock = Lock();
            if (!File.Exists(FilePath) && File.Exists(LegacyPath))
            {
                (PluginFile migrated, LoadProblem? problem) = Migrate(out note);
                if (problem != null)
                {
                    return (migrated, problem);
                }
            }
        }

        return ReadCurrent();
    }

    /// <summary>Reads <c>plugins.json</c> as it is (callers that already hold <see cref="Lock"/>).</summary>
    public (PluginFile File, LoadProblem? Problem) ReadCurrent()
    {
        string path = FilePath;
        if (!File.Exists(path))
        {
            return (new PluginFile(), null);
        }

        try
        {
            PluginFile file = JsonSerializer.Deserialize<PluginFile>(ReadShared(path), Json) ?? throw new JsonException("The file is empty.");
            if (file.Version != 1)
            {
                throw new JsonException($"The file has version {file.Version}; this build reads version 1.");
            }

            file.Plugins ??= [];

            // Valid JSON with values the loader cannot work with: an entry without id, version or hash (or a folder plugin without its folder)
            // is the file being wrong; a version that is not exact (a range, say) is reported for that plugin alone when it is reloaded.
            if (file.Plugins.Any(p => p is null || p.Id is null || p.Version is null || p.Sha512 is null || (p.Origin == PluginOrigin.Folder && string.IsNullOrWhiteSpace(p.Folder))))
            {
                throw new JsonException("An entry has no id, version or sha512 (or a folder plugin has no folder).");
            }

            file.Plugins = [.. file.Plugins.Select(p => p.Steps is null || (p.Origin == PluginOrigin.Nuget && p.Requested is null)
                ? p with { Steps = p.Steps ?? [], Requested = p.Origin == PluginOrigin.Nuget ? p.Requested ?? p.Version : p.Requested }
                : p)];
            return (file, null);
        }
        catch (JsonException ex)
        {
            return (new PluginFile(), Corrupt(path, ex.Message));
        }
    }

    public void Write(PluginFile file) => WriteAtomically(FilePath, Encoding(JsonSerializer.Serialize(file, Json)));

    /// <summary>
    /// Takes the lock file of the install folder (<c>plugins.json.lock</c>, opened with no sharing and deleted when released), so that
    /// a read-modify-write of <c>plugins.json</c> by another loader or process waits; retried for about <see cref="LockTimeout"/>.
    /// </summary>
    /// <exception cref="IOException">The lock could not be taken in that time.</exception>
    public IDisposable Lock()
    {
        Directory.CreateDirectory(Root);
        string path = FilePath + ".lock";
        DateTime deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by another loader (or being deleted by it: access denied while the delete is pending).
                if (DateTime.UtcNow >= deadline)
                {
                    throw new IOException($"The install folder '{Root}' is locked by another operation ({path}): {ex.Message}", ex);
                }

                Thread.Sleep(25);
            }
        }
    }

    /// <summary>How long <see cref="Lock"/> waits for another operation on the install folder.</summary>
    public static TimeSpan LockTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True when every package of <paramref name="lockFile"/> has a valid id, an exact version and a hash: what the loader needs to build
    /// paths and compare hashes. A lock that is valid JSON but fails this is treated as unreadable.
    /// </summary>
    public static bool IsUsable(NugetLock lockFile)
        => lockFile.Packages is not null && lockFile.Packages.All(p => p is not null
            && new NugetRequirement(p.Id, "1.0.0").Problem() == null
            && NuGetVersion.TryParse(p.Version, out _)
            && !string.IsNullOrEmpty(p.Hash));

    /// <summary>The lock of <paramref name="id"/> at <paramref name="version"/>; the id and version are checked before they become a path.</summary>
    public string LockPath(string id, string version) => Under(Root, LocksFolder, FolderName(id), NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant(), LockFileName);

    public void WriteLock(string id, string version, NugetLock lockFile) => WriteAtomically(LockPath(id, version), lockFile.ToBytes());

    /// <summary>A lock of <paramref name="id"/> written for exactly <paramref name="requirement"/> (so installing it again resolves nothing); null when none.</summary>
    public NugetLock? FindLock(string id, NugetRequirement requirement)
    {
        string folder = Folder(id);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        foreach (string candidate in Directory.EnumerateFiles(folder, LockFileName, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            try
            {
                NugetLock lockFile = NugetLock.Parse(File.ReadAllText(candidate));
                if (IsUsable(lockFile) && lockFile.Matches([requirement]) && lockFile.Packages.Any(p => p.Direct && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    return lockFile;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // An unreadable lock is not reused: the package is resolved again and the lock rewritten.
            }
        }

        return null;
    }

    /// <summary>Deletes the locks of <paramref name="id"/> (best effort: a lock left behind is never read without its entry).</summary>
    public void DeleteLocks(string id)
    {
        try
        {
            string folder = Folder(id);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Harmless: the entry is what makes a plugin installed.
        }
    }

    /// <summary>
    /// <c>steps-packages.json</c> → <c>plugins.json</c> (origin <c>nuget</c>), its locks copied to <c>plugin-locks/</c>, the old file renamed to
    /// <c>steps-packages.json.migrated</c>. Called under <see cref="Lock"/>.
    /// </summary>
    private (PluginFile File, LoadProblem? Problem) Migrate(out string? note)
    {
        note = null;
        LegacyFile? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<LegacyFile>(ReadShared(LegacyPath), Json) ?? throw new JsonException("The file is empty.");
            if (legacy.Version != 1 || legacy.Packages is null || legacy.Packages.Any(p => p is null || p.Id is null || p.Version is null || p.Sha512 is null))
            {
                throw new JsonException("It is not a version 1 file with id, version and sha512 in every entry.");
            }
        }
        catch (JsonException ex)
        {
            return (new PluginFile(), Corrupt(LegacyPath, ex.Message, LegacyFileName));
        }

        PluginFile file = new();
        foreach (LegacyEntry old in legacy.Packages)
        {
            file.Plugins.Add(new PluginEntry
            {
                Id = old.Id,
                Origin = PluginOrigin.Nuget,
                Version = old.Version,
                Requested = old.Requested ?? old.Version,
                Sha512 = old.Sha512,
                ApprovedSha512 = old.ApprovedSha512,
                ApprovedClosure = old.ApprovedClosure,
                InstalledAtUtc = old.InstalledAtUtc,
                Steps = old.Steps ?? []
            });

            // The lock of the old layout (<legacy>/<id>/<version>/nuget.lock.json); a value that is not a valid id or version is left for the
            // reload to report (lock-missing / store-corrupt).
            try
            {
                if (new NugetRequirement(old.Id, "1.0.0").Problem() == null && NuGetVersion.TryParse(old.Version, out NuGetVersion? version))
                {
                    string source = Under(_legacyRoot, FolderName(old.Id), version.ToNormalizedString().ToLowerInvariant(), LockFileName);
                    string target = LockPath(old.Id, old.Version);
                    if (File.Exists(source) && !File.Exists(target))
                    {
                        WriteAtomically(target, File.ReadAllBytes(source));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The reload reports the missing lock of that plugin.
            }
        }

        file.Plugins.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
        Write(file);
        string renamed = LegacyPath + ".migrated";
        if (File.Exists(renamed))
        {
            renamed += "-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
        }

        File.Move(LegacyPath, renamed);
        note = $"{LegacyFileName} ({file.Plugins.Count} package(s)) was migrated to {FilePath}; the old file is kept as '{renamed}'.";
        return (file, null);
    }

    private static LoadProblem Corrupt(string path, string message, string name = FileName)
    {
        string copy = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture);
        File.Copy(path, copy, overwrite: true);
        return new LoadProblem("store-corrupt", $"{name} could not be read ({message}); it was copied to '{copy}' and the list starts empty.");
    }

    private string Folder(string id) => Under(Root, LocksFolder, FolderName(id));

    /// <summary>The folder name of a plugin: its id, checked (<see cref="NugetRequirement.Problem"/>) and lower-cased.</summary>
    private static string FolderName(string id)
    {
        if (new NugetRequirement(id, "1.0.0").Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(id));
        }

        return id.Trim().ToLowerInvariant();
    }

    /// <summary><paramref name="parts"/> below <paramref name="root"/>; never outside it (no rooted part, no <c>..</c>).</summary>
    private static string Under(string root, params string[] parts)
    {
        string path = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (parts.Any(p => Path.IsPathRooted(p) || p.Split('/', '\\').Contains(".."))
            || !path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{string.Join("/", parts)}' is not a path below the install folder.", nameof(parts));
        }

        return path;
    }

    private static byte[] Encoding(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>Reads a text file letting another process replace it meanwhile (the atomic write of a loader that holds the lock).</summary>
    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>The former <c>steps-packages.json</c>, read once for the migration.</summary>
    private sealed class LegacyFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }

        [JsonPropertyName("packages")] public List<LegacyEntry>? Packages { get; set; }
    }

    private sealed class LegacyEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = null!;

        [JsonPropertyName("version")] public string Version { get; set; } = null!;

        [JsonPropertyName("requested")] public string? Requested { get; set; }

        [JsonPropertyName("sha512")] public string Sha512 { get; set; } = null!;

        [JsonPropertyName("approvedSha512")] public string? ApprovedSha512 { get; set; }

        [JsonPropertyName("approvedClosure")] public string? ApprovedClosure { get; set; }

        [JsonPropertyName("installedAtUtc")] public DateTimeOffset InstalledAtUtc { get; set; }

        [JsonPropertyName("steps")] public List<string>? Steps { get; set; }
    }
}
