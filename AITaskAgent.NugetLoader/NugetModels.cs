using NuGet.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AITaskAgent.NugetLoader;

/// <summary>
/// A NuGet package a workflow package (<c>manifest.requires.nuget</c>) or an extension (<c>extension.json</c>, section <c>nuget</c>)
/// declares (architecture §16.1). <see cref="Version"/> is a NuGet version or range as NuGet writes them (<c>1.2.3</c> = «1.2.3 or
/// higher», <c>[1.2.3]</c> exact, <c>[1.0,2.0)</c>); floating versions (<c>1.*</c>) are not accepted: what runs is what the lock says.
/// </summary>
public sealed record NugetRequirement(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version)
{
    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$", RegexOptions.Compiled);

    /// <summary>What is wrong with this declaration (shown to the user as it is); null when it is valid.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Id) || !IdPattern.IsMatch(Id.Trim()))
        {
            return $"'{Id}' is not a valid NuGet package id.";
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            return $"Package '{Id}' has no version.";
        }

        if (!VersionRange.TryParse(Version.Trim(), allowFloating: false, out _))
        {
            return Version.Contains('*', StringComparison.Ordinal)
                ? $"Package '{Id}': floating versions ('{Version}') are not allowed; write an exact version or a range."
                : $"Package '{Id}': '{Version}' is not a NuGet version or range.";
        }

        return null;
    }

    /// <summary>The parsed range (call <see cref="Problem"/> first).</summary>
    public VersionRange Range => VersionRange.Parse(Version.Trim());

    /// <summary>The problems of a whole list: each one, plus duplicated ids (case-insensitive).</summary>
    public static IReadOnlyList<string> Problems(IEnumerable<NugetRequirement> requirements)
    {
        List<string> problems = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (NugetRequirement requirement in requirements)
        {
            if (requirement.Problem() is { } problem)
            {
                problems.Add(problem);
            }
            else if (!seen.Add(requirement.Id.Trim()))
            {
                problems.Add($"Package '{requirement.Id}' is declared more than once.");
            }
        }

        return problems;
    }

    /// <summary>SHA-256 (hex) of the canonical form of a list: ids and versions, lower-cased and sorted. What the lock records to know it is stale.</summary>
    public static string HashOf(IEnumerable<NugetRequirement> requirements)
    {
        string canonical = string.Join('\n', requirements
            .Select(r => r.Id.Trim().ToLowerInvariant() + "@" + r.Version.Trim())
            .Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>The <c>nuget</c> section of an extension's <c>extension.json</c> (consumed by the designer's extension loader of phase E; folder plugins ignore it).</summary>
public static class ExtensionNuget
{
    /// <summary>
    /// The declarations of <paramref name="extensionJson"/>: <c>{ "nuget": [ { "id": "…", "version": "…" } ] }</c>. A missing section is an empty list.
    /// </summary>
    /// <exception cref="FormatException">The section is not a list of objects with <c>id</c> and <c>version</c>, or a declaration is not valid.</exception>
    public static IReadOnlyList<NugetRequirement> Parse(JsonObject extensionJson)
    {
        ArgumentNullException.ThrowIfNull(extensionJson);
        JsonNode? section = extensionJson.FirstOrDefault(p => string.Equals(p.Key, "nuget", StringComparison.OrdinalIgnoreCase)).Value;
        if (section == null)
        {
            return [];
        }

        if (section is not JsonArray array)
        {
            throw new FormatException("extension.json: 'nuget' must be a list.");
        }

        List<NugetRequirement> list = [];
        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject obj
                || obj["id"] is not JsonValue id || !id.TryGetValue(out string? idText)
                || obj["version"] is not JsonValue version || !version.TryGetValue(out string? versionText))
            {
                throw new FormatException("extension.json: each 'nuget' entry needs a text 'id' and a text 'version'.");
            }

            list.Add(new NugetRequirement(idText, versionText));
        }

        if (NugetRequirement.Problems(list) is { Count: > 0 } problems)
        {
            throw new FormatException("extension.json: " + string.Join(" ", problems));
        }

        return list;
    }
}

/// <summary>A package of the lock: exact version, SHA-512 of the <c>.nupkg</c>, and what it depends on.</summary>
public sealed record NugetLockPackage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("direct")] bool Direct,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string>? Dependencies = null);

/// <summary>A package left out because the host supplies it (shared set): the version of the host's assembly.</summary>
public sealed record NugetProvidedPackage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("hostAssembly")] string HostAssembly);

/// <summary>
/// <c>nuget.lock.json</c> (architecture §16.2): the exact versions and content hashes a run materializes. Nothing at run time resolves
/// ranges: the lock is the authority, and it is stale when <see cref="RequiresHash"/> no longer matches the declarations.
/// </summary>
public sealed class NugetLock
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Format version of the file.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>The target framework the dependency groups were chosen for.</summary>
    [JsonPropertyName("targetFramework")]
    public string TargetFramework { get; set; } = "net10.0";

    /// <summary>Hash of the declarations the lock was made for (<see cref="NugetRequirement.HashOf"/>).</summary>
    [JsonPropertyName("requiresHash")]
    public string RequiresHash { get; set; } = string.Empty;

    /// <summary>The packages to materialize, ordered by id.</summary>
    [JsonPropertyName("packages")]
    public List<NugetLockPackage> Packages { get; set; } = [];

    /// <summary>The packages the host supplies.</summary>
    [JsonPropertyName("provided")]
    public List<NugetProvidedPackage> Provided { get; set; } = [];

    /// <summary>True when the lock was made for exactly <paramref name="requirements"/>.</summary>
    public bool Matches(IEnumerable<NugetRequirement> requirements) => string.Equals(RequiresHash, NugetRequirement.HashOf(requirements), StringComparison.Ordinal);

    /// <summary>The bytes written to the package (UTF-8, no BOM).</summary>
    public byte[] ToBytes() => new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(this, Json));

    /// <summary>Parses a lock file.</summary>
    /// <exception cref="JsonException">Not a lock file.</exception>
    public static NugetLock Parse(string text)
    {
        NugetLock result = JsonSerializer.Deserialize<NugetLock>(text, Json) ?? throw new JsonException("The lock file is empty.");
        if (result.Version != 1)
        {
            throw new JsonException($"The lock file has version {result.Version}; this build reads version 1.");
        }

        return result;
    }
}

/// <summary>How a restore or a materialization reaches the network and where it keeps what it downloads.</summary>
public sealed record NugetOptions
{
    /// <summary>Folder of the private cache (<c>&lt;data&gt;/nuget</c>).</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>Sources: folder paths or <c>https://…/index.json</c> URLs, in order of preference. None = offline.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Whether the configured sources (folders and URLs) may be read at all. False = offline: only the cache is used.</summary>
    public bool AllowNetwork { get; init; } = true;

    /// <summary>
    /// Whether the <c>http(s)</c> sources may be read when <see cref="AllowNetwork"/> is true (folder sources always may then). A host that
    /// always registers a local folder (a repository of <c>.nupkg</c> files) sets it to «there is a remote source», so a source list with only
    /// folders never tries the network. It never makes a source usable when <see cref="AllowNetwork"/> is false.
    /// </summary>
    public bool AllowRemote { get; init; } = true;

    /// <summary>Most bytes really written for one file extracted from a <c>.nupkg</c> into the cache (the declared size is not trusted).</summary>
    public long MaxExtractedEntryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Most bytes really written for all the files extracted from one <c>.nupkg</c>.</summary>
    public long MaxExtractedPackageBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Most entries a <c>.nupkg</c> may have to be extracted.</summary>
    public int MaxPackageEntries { get; init; } = 20_000;

    /// <summary>Target framework dependency groups and libraries are chosen for.</summary>
    public string TargetFramework { get; init; } = "net10.0";

    /// <summary>Runtime identifier for native libraries; null = the running one.</summary>
    public string? RuntimeIdentifier { get; init; }

    /// <summary>
    /// How the application calls its setting of NuGet sources, quoted by the messages that say none is configured. The default is the
    /// designer's (<c>WorkflowDesigner:NuGetSources</c>), so its messages stay as they were; another application names its own.
    /// </summary>
    public string SourcesOptionName { get; init; } = DefaultSourcesOptionName;

    /// <summary>The designer's setting of NuGet sources, the default of <see cref="SourcesOptionName"/>.</summary>
    public const string DefaultSourcesOptionName = "WorkflowDesigner:NuGetSources";
}

/// <summary>Severity of a <see cref="NugetNote"/>.</summary>
public enum NugetNoteLevel
{
    /// <summary>For the record (a package unified to the host's).</summary>
    Info,

    /// <summary>Something may not work (a package needs a newer major than the host has).</summary>
    Warning
}

/// <summary>A message of a restore or a materialization.</summary>
public sealed record NugetNote(NugetNoteLevel Level, string Message);

/// <summary>A package materialized in the cache: where its files are.</summary>
public sealed record NugetMaterializedPackage(string Id, string Version, string Hash, bool Direct, IReadOnlyList<string> Assemblies, string? NativeDirectory)
{
    /// <summary>The <c>description</c> of the package's nuspec (read for the direct package only; null when it has none or cannot be read).</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The extracted <c>plugin/</c> folder of a package that carries <c>plugin/plugin.json</c> (a plugin shaped as a folder inside its
    /// <c>.nupkg</c>, architecture §25.2): own assemblies, content files and private executables with their relative paths. Null otherwise.
    /// </summary>
    public string? PluginDirectory { get; init; }
}

/// <summary>What a unit needs to compile and load against the packages of a lock (architecture §16.3).</summary>
public sealed record NugetEnvironment(
    IReadOnlyList<string> References,
    IReadOnlyList<string> NativeDirectories,
    IReadOnlyList<NugetMaterializedPackage> Packages,
    IReadOnlyList<NugetNote> Notes)
{
    /// <summary>Nothing (a package without NuGet requirements).</summary>
    public static NugetEnvironment Empty { get; } = new([], [], [], []);
}

/// <summary>What <see cref="NugetResolver.RestoreAsync"/> gave: the new lock and the environment materialized from it.</summary>
public sealed record NugetRestoreResult(NugetLock Lock, NugetEnvironment Environment, IReadOnlyList<NugetNote> Notes);

/// <summary>A package could not be resolved, downloaded or verified; the message names the package and what was tried.</summary>
public sealed class NugetException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Stable code: <c>not_restored</c>, <c>stale_lock</c>, <c>hash_mismatch</c>, <c>not_found</c>, <c>conflict</c>, <c>offline</c>, <c>invalid</c>, <c>too_large</c>.</summary>
    public string Code { get; init; } = "invalid";
}
