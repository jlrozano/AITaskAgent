using NuGet.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AITaskAgent.NugetLoader;

/// <summary>
/// The manifest of a folder plugin: <c>plugin.json</c> (<c>{ "id", "version"?, "description"?, "contract", "assemblies"?: ["x.dll"], "extension"?: "Full.Type.Name" }</c>), a
/// legacy <c>extension.json</c> read as one, or none (the main DLL is <c>&lt;folder&gt;.dll</c> or the only DLL, the id is the folder name).
/// </summary>
/// <param name="Id">The plugin's id (checked as a NuGet id).</param>
/// <param name="Version">Normalized version: the manifest's, else the main assembly's, else <c>0.0.0</c>.</param>
/// <param name="Description">The manifest's description.</param>
/// <param name="Contract">The manifest's contract (0 = no manifest, «unversioned»).</param>
/// <param name="Assemblies">The plugin's own assemblies (absolute paths inside the folder): only these are scanned for contributions.</param>
/// <param name="Source"><c>plugin.json</c>, <c>extension.json</c> or <c>none</c>.</param>
/// <param name="Extension">Full name of the <c>IExtension</c> class that drives the plugin, when the manifest names it (<c>"extension"</c>); null = the only one found.</param>
public sealed record PluginManifest(
    string Id, string Version, string? Description, int Contract, IReadOnlyList<string> Assemblies, string Source, string? Extension = null);

/// <summary>The files of a folder plugin and their hashes (<see cref="PluginFolder.Hash"/>).</summary>
/// <param name="Folder">The folder, absolute.</param>
/// <param name="Sha512"><c>sha512-&lt;base64&gt;</c> of the tree (see <see cref="PluginFolder.Hash"/>).</param>
/// <param name="Files">Relative path (with <c>/</c>) → lowercase hex SHA-512 of the file.</param>
public sealed record PluginFolderTree(string Folder, string Sha512, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The hex SHA-512 recorded for <paramref name="path"/> (absolute), or null when the file is not part of the tree.</summary>
    public string? HashOf(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Files.TryGetValue(full[root.Length..].Replace('\\', '/'), out string? hash) ? hash : null;
    }
}

/// <summary>Reading a folder plugin: its manifest and the hash of its whole tree.</summary>
public static class PluginFolder
{
    /// <summary>The highest <c>contract</c> of <c>plugin.json</c> this build understands.</summary>
    public const int SupportedContract = 1;

    /// <summary>The manifest's file name.</summary>
    public const string ManifestName = "plugin.json";

    /// <summary>The former extension manifest, still read (with the warning <c>legacy-manifest</c>).</summary>
    public const string LegacyManifestName = "extension.json";

    /// <summary>
    /// Reads the manifest of <paramref name="folder"/>. <paramref name="id"/> (optional) is the id the caller expects: a manifest with another id
    /// is the problem <c>id-mismatch</c>; without a manifest it is the id (else the folder name). Problems that stop the plugin come with a null
    /// manifest; warnings (<c>legacy-manifest</c>) come with it.
    /// </summary>
    public static (PluginManifest? Manifest, IReadOnlyList<LoadProblem> Problems) ReadManifest(string folder, string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        List<LoadProblem> problems = [];
        string root = Path.GetFullPath(folder);
        if (IsNetworkPath(folder) || IsNetworkPath(root))
        {
            return (null, [new LoadProblem("invalid-plugin", $"'{folder}' is a network or device path: a folder plugin must be a local folder.")]);
        }

        if (!Directory.Exists(root))
        {
            return (null, [new LoadProblem("invalid-plugin", $"The plugin folder '{root}' does not exist.")]);
        }

        string? manifestId = null;
        string? version = null;
        string? description = null;
        int contract = 0;
        List<string> assemblies = [];
        string source = "none";
        string? extension = null;
        string manifest = Path.Combine(root, ManifestName);
        string legacy = Path.Combine(root, LegacyManifestName);
        try
        {
            if (File.Exists(manifest))
            {
                source = ManifestName;
                JsonObject json = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject ?? throw new FormatException("it is not a JSON object");
                manifestId = Text(json, "id") ?? throw new FormatException("'id' is required");
                contract = Contract(json);
                version = Text(json, "version");
                description = Text(json, "description");
                extension = Text(json, "extension");
                if (json["assemblies"] is { } list)
                {
                    if (list is not JsonArray array || array.Any(a => a is not JsonValue v || !v.TryGetValue(out string? _)))
                    {
                        throw new FormatException("'assemblies' must be a list of file names");
                    }

                    assemblies.AddRange(array.Select(a => a!.GetValue<string>()));
                }
            }
            else if (File.Exists(legacy))
            {
                source = LegacyManifestName;
                JsonObject json = JsonNode.Parse(File.ReadAllText(legacy)) as JsonObject ?? throw new FormatException("it is not a JSON object");
                manifestId = Text(json, "name") ?? throw new FormatException("'name' is required");
                contract = Contract(json);
                version = Text(json, "version");
                description = Text(json, "description");
                extension = Text(json, "extensionType");
                if (Text(json, "assembly") is { } assembly)
                {
                    assemblies.Add(assembly);
                }

                problems.Add(new LoadProblem("legacy-manifest",
                    $"'{root}' has {LegacyManifestName}, read as {ManifestName} (name → id, assembly → assemblies)" +
                    (json.ContainsKey("nuget") ? "; its 'nuget' section is ignored: a folder plugin ships its dependencies in the folder" : string.Empty) +
                    $". Rename it to {ManifestName}."));
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (null, [new LoadProblem("bad-manifest", $"{(source == "none" ? ManifestName : source)} of '{root}' is not valid: {ex.Message}.")]);
        }

        if (contract > SupportedContract)
        {
            return (null, [new LoadProblem("contract-too-new", $"'{root}' needs plugin contract {contract}; this build supports up to {SupportedContract}.")]);
        }

        string folderName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        string finalId = (manifestId ?? id ?? folderName).Trim();
        if (manifestId != null && id != null && !string.Equals(manifestId.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return (null, [new LoadProblem("id-mismatch", $"The manifest of '{root}' says id '{manifestId}', not '{id}'.")]);
        }

        if (new NugetRequirement(finalId, "1.0.0").Problem() != null)
        {
            return (null, [new LoadProblem("invalid-plugin", $"'{finalId}' is not a valid plugin id (letters, digits, '.', '_' and '-', starting with a letter or digit).")]);
        }

        List<string> paths = [];
        if (assemblies.Count > 0)
        {
            foreach (string assembly in assemblies)
            {
                string? path = Inside(root, assembly);
                if (path == null || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    return (null, [new LoadProblem("bad-manifest", $"The manifest of '{root}' names '{assembly}', which is not a DLL inside the folder.")]);
                }

                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }
        }
        else
        {
            string main = Path.Combine(root, folderName + ".dll");
            string[] dlls = Directory.GetFiles(root, "*.dll", SearchOption.TopDirectoryOnly);
            if (File.Exists(main))
            {
                paths.Add(main);
            }
            else if (dlls.Length == 1)
            {
                paths.Add(dlls[0]);
            }
            else
            {
                return (null, [new LoadProblem("no-assemblies", dlls.Length == 0
                    ? $"'{root}' has no DLL."
                    : $"'{root}' has {dlls.Length} DLLs and none is called {folderName}.dll: list the plugin's own assemblies in {ManifestName} ('assemblies').")]);
            }
        }

        if (version != null && !NuGetVersion.TryParse(version, out _))
        {
            return (null, [new LoadProblem("bad-manifest", $"The manifest of '{root}' has version '{version}', which is not a version.")]);
        }

        version ??= PluginScanner.AssemblyVersionOf(paths[0]);
        string normalized = version != null && NuGetVersion.TryParse(version, out NuGetVersion? parsed) ? parsed.ToNormalizedString() : "0.0.0";
        return (new PluginManifest(finalId, normalized, description, contract, paths, source, extension), problems);
    }

    /// <summary>
    /// Hashes every file below <paramref name="folder"/> (links are not followed): one line per file, <c>&lt;relative path with /&gt;\t&lt;hex
    /// SHA-512 of the file&gt;\n</c>, sorted by path (ordinal), and the SHA-512 of those lines (UTF-8) as <c>sha512-&lt;base64&gt;</c>. Any
    /// added, removed, renamed or changed file changes it.
    /// </summary>
    /// <exception cref="IOException">More than <paramref name="maxFiles"/> files, or a file cannot be read.</exception>
    public static PluginFolderTree Hash(string folder, int maxFiles = 20_000)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false
        };

        SortedDictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            if (files.Count >= maxFiles)
            {
                throw new IOException($"'{root}' has more than {maxFiles} files: it does not look like a plugin folder.");
            }

            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            files[relative] = Convert.ToHexStringLower(SHA512.HashData(stream));
        }

        StringBuilder lines = new();
        foreach ((string relative, string hash) in files)
        {
            lines.Append(relative).Append('\t').Append(hash).Append('\n');
        }

        string sha = "sha512-" + Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
        return new PluginFolderTree(root, sha, files);
    }

    /// <summary>True for UNC (<c>\\server\share</c>) and device (<c>\\?\</c>, <c>\\.\</c>) paths.</summary>
    public static bool IsNetworkPath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static string? Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
        {
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string? Text(JsonObject json, string name)
        => json[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static int Contract(JsonObject json)
        => json["contract"] is JsonValue value && value.TryGetValue(out int contract) && contract >= 1
            ? contract
            : throw new FormatException("'contract' must be an integer from 1");
}
