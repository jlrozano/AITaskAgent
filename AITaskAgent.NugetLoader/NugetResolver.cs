using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AITaskAgent.NugetLoader;

/// <summary>
/// The one resolver of the NuGet packages that workflow packages and extensions declare (architecture §16), shared by the CLI and the
/// Web. <see cref="RestoreAsync"/> turns declarations into a <see cref="NugetLock"/> (exact versions and content hashes) and fills the
/// private cache; <see cref="MaterializeAsync"/> turns a lock into assembly paths, offline from the cache when it can. Neither is
/// called by a run to resolve anything: a run only materializes the lock.
/// </summary>
/// <remarks>
/// A simplified NuGet algorithm over the official client libraries (<c>NuGet.Protocol</c> for the sources, <c>NuGet.Packaging</c>
/// to read packages, <c>NuGet.Frameworks</c> to pick the nearest target framework): the lowest version that satisfies every range
/// that mentions the package, and only the dependency group of the nearest target framework. See the deviations of phase D.
/// </remarks>
public sealed class NugetResolver
{
    private const string RootKey = "<requires>";

    // One writer at a time to the cache: two restores of the same package would race on the same files.
    private static readonly SemaphoreSlim CacheGate = new(1, 1);

    /// <summary>Resolves <paramref name="requirements"/> against the sources of <paramref name="options"/> and materializes the result.</summary>
    /// <exception cref="NugetException">A declaration is not valid, a package or version is not found, ranges cannot be satisfied together, or a download fails.</exception>
    public async Task<NugetRestoreResult> RestoreAsync(IReadOnlyList<NugetRequirement> requirements, NugetOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(options);
        if (NugetRequirement.Problems(requirements) is { Count: > 0 } problems)
        {
            throw new NugetException(string.Join(" ", problems)) { Code = "invalid" };
        }

        NuGetFramework target = NuGetFramework.Parse(options.TargetFramework);
        List<NugetNote> notes = [];
        NugetNote? Conflict(string id, VersionRange range, Version host)
            => range.MinVersion is { } min && min.Major > host.Major
                ? new NugetNote(NugetNoteLevel.Warning, $"'{id}' {range.PrettyPrint()} wants a newer major than the host's {host}; the host's version is used.")
                : null;

        using SourceCacheContext cache = new() { NoCache = true, DirectDownload = true };
        SourceSet sources = new(options, cache);
        Dictionary<string, Dictionary<string, VersionRange>> constraints = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Node> nodes = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, NugetProvidedPackage> provided = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> casing = new(StringComparer.OrdinalIgnoreCase);

        foreach (NugetRequirement requirement in requirements)
        {
            string id = requirement.Id.Trim();
            casing.TryAdd(id, id);
            Constrain(constraints, id, RootKey, requirement.Range);
        }

        bool changed = true;
        for (int round = 0; changed; round++)
        {
            if (round > 500)
            {
                throw new NugetException("The NuGet dependency graph does not settle (too many version changes); check the declared ranges.") { Code = "conflict" };
            }

            changed = false;
            foreach (string id in constraints.Keys.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (provided.ContainsKey(id))
                {
                    continue;
                }

                if (SharedAssemblySet.HostProvides(id))
                {
                    Version? hostVersion = SharedAssemblySet.IsMetapackage(id) ? null : SharedAssemblySet.HostVersion(id);
                    provided[id] = new NugetProvidedPackage(casing[id], hostVersion?.ToString() ?? "platform", hostVersion != null ? id : "(platform)");
                    foreach (VersionRange range in constraints[id].Values)
                    {
                        if (hostVersion != null && Conflict(casing[id], range, hostVersion) is { } note && !notes.Contains(note))
                        {
                            notes.Add(note);
                        }
                    }

                    notes.Add(new NugetNote(NugetNoteLevel.Info, $"'{casing[id]}' is supplied by the host ({provided[id].Version}); the package's own copy is not used."));
                    continue;
                }

                IReadOnlyDictionary<NuGetVersion, FindPackageByIdResource> versions = await sources.VersionsAsync(id, cancellationToken);
                List<VersionRange> ranges = [.. constraints[id].Values];
                NuGetVersion? pick = versions.Keys.Where(v => ranges.All(r => r.Satisfies(v))).Order().FirstOrDefault();
                if (pick == null)
                {
                    string wants = string.Join("; ", constraints[id].Select(c => $"{(c.Key == RootKey ? "the package" : c.Key)} wants {c.Value.PrettyPrint()}"));
                    throw new NugetException(
                        $"No version of '{casing[id]}' satisfies every requirement ({wants}). Available: {string.Join(", ", versions.Keys.Order().Select(v => v.ToNormalizedString()))}.")
                    { Code = "conflict" };
                }

                if (nodes.TryGetValue(id, out Node? node) && node.Version == pick)
                {
                    continue;
                }

                node ??= nodes[id] = new Node(casing[id]);
                foreach (Dictionary<string, VersionRange> contributions in constraints.Values)
                {
                    contributions.Remove(node.Id);
                }

                node.Version = pick;
                node.Source = versions[pick];
                node.Dependencies.Clear();
                foreach (PackageDependency dependency in await sources.DependenciesAsync(node.Id, pick, target, cancellationToken))
                {
                    casing.TryAdd(dependency.Id, dependency.Id);
                    node.Dependencies.Add(dependency.Id);
                    Constrain(constraints, dependency.Id, node.Id, dependency.VersionRange ?? VersionRange.All);
                }

                changed = true;
            }
        }

        // Only what the declarations reach through the versions finally chosen.
        HashSet<string> reachable = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new(requirements.Select(r => r.Id.Trim()));
        while (pending.Count > 0)
        {
            string id = pending.Pop();
            if (reachable.Add(id) && nodes.TryGetValue(id, out Node? node))
            {
                foreach (string dependency in node.Dependencies)
                {
                    pending.Push(dependency);
                }
            }
        }

        HashSet<string> direct = new(requirements.Select(r => r.Id.Trim()), StringComparer.OrdinalIgnoreCase);
        NugetLock result = new()
        {
            TargetFramework = options.TargetFramework,
            RequiresHash = NugetRequirement.HashOf(requirements)
        };

        foreach (Node node in nodes.Values.Where(n => reachable.Contains(n.Id)).OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase))
        {
            string hash = await Cache.EnsureNupkgAsync(options, sources, node.Id, node.Version!, expectedHash: null, node.Source, cancellationToken);
            result.Packages.Add(new NugetLockPackage(
                node.Id, node.Version!.ToNormalizedString(), hash, direct.Contains(node.Id),
                [.. node.Dependencies
                    .Where(d => nodes.ContainsKey(d))
                    .Select(d => $"{nodes[d].Id}/{nodes[d].Version!.ToNormalizedString()}")
                    .Order(StringComparer.OrdinalIgnoreCase)]));
        }

        result.Provided = [.. provided.Values.Where(p => reachable.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];
        NugetEnvironment environment = await MaterializeAsync(result, options, cancellationToken);
        return new NugetRestoreResult(result, environment, [.. notes.Distinct(), .. environment.Notes]);
    }

    /// <summary>
    /// The assemblies and native folders of the packages of <paramref name="lockFile"/>, from the cache. A package missing from the cache
    /// is downloaded only when the options allow it and a source has it; the <c>.nupkg</c> is always checked against the lock's hash.
    /// </summary>
    /// <exception cref="NugetException">A package is not in the cache and cannot be downloaded (<c>not_restored</c>/<c>offline</c>), or its hash is not the lock's (<c>hash_mismatch</c>).</exception>
    public async Task<NugetEnvironment> MaterializeAsync(NugetLock lockFile, NugetOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockFile);
        ArgumentNullException.ThrowIfNull(options);
        NuGetFramework target = NuGetFramework.Parse(options.TargetFramework);
        List<NugetNote> notes = [];
        List<string> references = [];
        List<string> native = [];
        List<NugetMaterializedPackage> packages = [];
        using SourceCacheContext cache = new() { NoCache = true, DirectDownload = true };
        SourceSet sources = new(options, cache);

        foreach (NugetProvidedPackage provided in lockFile.Provided)
        {
            notes.Add(new NugetNote(NugetNoteLevel.Info, $"'{provided.Id}' comes from the host ({provided.Version})."));
        }

        foreach (NugetLockPackage package in lockFile.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NuGetVersion version = NuGetVersion.Parse(package.Version);
            await Cache.EnsureNupkgAsync(options, sources, package.Id, version, package.Hash, source: null, cancellationToken);
            Cache.Extracted content = await Cache.ExtractAsync(options, package.Id, version, package.Hash, target, cancellationToken);

            List<string> assemblies = [];
            foreach (string dll in content.Assemblies)
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (SharedAssemblySet.IsShared(name) && SharedAssemblySet.HostVersion(name) is { } hostVersion)
                {
                    notes.Add(new NugetNote(NugetNoteLevel.Info, $"'{package.Id}': assembly '{name}' is supplied by the host ({hostVersion}); the package's copy is not loaded."));
                    continue;
                }

                assemblies.Add(dll);
            }

            references.AddRange(assemblies);
            if (content.NativeDirectory != null)
            {
                native.Add(content.NativeDirectory);
            }

            packages.Add(new NugetMaterializedPackage(package.Id, package.Version, package.Hash, package.Direct, assemblies, content.NativeDirectory)
            {
                Description = package.Direct ? Cache.DescriptionOf(options, package.Id, version) : null,
                PluginDirectory = content.PluginDirectory
            });
        }

        return new NugetEnvironment([.. references.Distinct(StringComparer.OrdinalIgnoreCase)], [.. native.Distinct(StringComparer.OrdinalIgnoreCase)], packages, notes);
    }

    /// <summary>
    /// Where the entry <c>plugin/&lt;relative&gt;</c> of a package is extracted below <paramref name="root"/> (the package's <c>content/plugin</c>);
    /// null when it would leave it (<c>..</c>, rooted, a drive, empty segments): such a package is not extracted.
    /// </summary>
    public static string? PluginEntryPath(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        string[] segments = relative.Split('/', '\\');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':') || segments.Any(s => s is "" or "." or ".."))
        {
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static void Constrain(Dictionary<string, Dictionary<string, VersionRange>> constraints, string id, string by, VersionRange range)
    {
        if (!constraints.TryGetValue(id, out Dictionary<string, VersionRange>? contributions))
        {
            constraints[id] = contributions = new Dictionary<string, VersionRange>(StringComparer.OrdinalIgnoreCase);
        }

        contributions[by] = range;
    }

    private sealed class Node(string id)
    {
        public string Id { get; } = id;

        public NuGetVersion? Version { get; set; }

        public FindPackageByIdResource? Source { get; set; }

        public List<string> Dependencies { get; } = [];
    }

    /// <summary>The configured sources, opened lazily.</summary>
    private sealed class SourceSet(NugetOptions options, SourceCacheContext cache)
    {
        private readonly Dictionary<string, Task<FindPackageByIdResource?>> _resources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyDictionary<NuGetVersion, FindPackageByIdResource>> _versions = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsRemote(string source) => source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The sources a package may be read from: none when the options do not allow reading sources (offline = the cache only); otherwise the
        /// folders, and the <c>http(s)</c> ones too when <see cref="NugetOptions.AllowRemote"/>. A UNC or device path (<c>\\server\share</c>,
        /// <c>\\?\…</c>) reaches the network: it counts as remote, never as a local folder.
        /// </summary>
        public IEnumerable<string> Usable => options.AllowNetwork
            ? options.Sources.Where(s => !string.IsNullOrWhiteSpace(s) && (options.AllowRemote || !(IsRemote(s) || PluginFolder.IsNetworkPath(s))))
            : [];

        public bool HasUsable => Usable.Any();

        /// <summary>Why the sources that could not be opened (unreachable feed, broken folder) were left out; reported when no source has the package.</summary>
        public List<string> OpenProblems { get; } = [];

        public async Task<IReadOnlyList<FindPackageByIdResource>> ResourcesAsync(CancellationToken cancellationToken)
        {
            List<FindPackageByIdResource> list = [];
            foreach (string source in Usable)
            {
                if (!_resources.TryGetValue(source, out Task<FindPackageByIdResource?>? task))
                {
                    _resources[source] = task = OpenAsync(source, cancellationToken);
                }

                try
                {
                    if (await task is { } resource)
                    {
                        list.Add(resource);
                    }
                }
                catch (NugetException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // One unreachable source must not hide the others (the local repository answers with no network): it is skipped and reported.
                    if (!OpenProblems.Contains(ex.Message))
                    {
                        OpenProblems.Add(ex.Message);
                    }
                }
            }

            return list;
        }

        private static async Task<FindPackageByIdResource?> OpenAsync(string source, CancellationToken cancellationToken)
        {
            try
            {
                SourceRepository repository = Repository.Factory.GetCoreV3(new PackageSource(IsRemote(source) ? source : Path.GetFullPath(source)));
                return await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
            }
            catch (Exception ex) when (ex is FatalProtocolException or InvalidOperationException or ArgumentException or IOException)
            {
                throw new NugetException($"The NuGet source '{source}' cannot be opened: {ex.Message}", ex) { Code = "not_found" };
            }
        }

        public async Task<IReadOnlyDictionary<NuGetVersion, FindPackageByIdResource>> VersionsAsync(string id, CancellationToken cancellationToken)
        {
            if (_versions.TryGetValue(id, out IReadOnlyDictionary<NuGetVersion, FindPackageByIdResource>? known))
            {
                return known;
            }

            IReadOnlyList<FindPackageByIdResource> resources = await ResourcesAsync(cancellationToken);
            if (resources.Count == 0 && OpenProblems.Count > 0)
            {
                throw new NugetException(string.Join(" ", OpenProblems)) { Code = "not_found" };
            }

            if (resources.Count == 0)
            {
                throw new NugetException(
                    options.Sources.Count == 0
                        ? $"Package '{id}' needs a NuGet source and none is configured ({options.SourcesOptionName})."
                        : $"Package '{id}': none of the configured NuGet sources can be used (remote sources are off).")
                { Code = "offline" };
            }

            Dictionary<NuGetVersion, FindPackageByIdResource> found = [];
            List<string> problems = [.. OpenProblems];
            foreach (FindPackageByIdResource resource in resources)
            {
                try
                {
                    foreach (NuGetVersion version in await resource.GetAllVersionsAsync(id, cache, NullLogger.Instance, cancellationToken))
                    {
                        found.TryAdd(version, resource);
                    }
                }
                catch (Exception ex) when (ex is FatalProtocolException or HttpRequestException or IOException)
                {
                    problems.Add(ex.Message);
                }
            }

            if (found.Count == 0)
            {
                throw new NugetException(
                    $"Package '{id}' was not found in the NuGet sources ({string.Join(", ", Usable)})" + (problems.Count > 0 ? ": " + string.Join(" ", problems) : "."))
                { Code = "not_found" };
            }

            return _versions[id] = found;
        }

        public async Task<IReadOnlyList<PackageDependency>> DependenciesAsync(string id, NuGetVersion version, NuGetFramework target, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<NuGetVersion, FindPackageByIdResource> versions = await VersionsAsync(id, cancellationToken);
            FindPackageByIdDependencyInfo? info = await versions[version].GetDependencyInfoAsync(id, version, cache, NullLogger.Instance, cancellationToken)
                ?? throw new NugetException($"Package '{id}' {version.ToNormalizedString()} cannot be read from its source.") { Code = "not_found" };

            List<PackageDependencyGroup> groups = [.. info.DependencyGroups];
            NuGetFramework? nearest = new FrameworkReducer().GetNearest(target, groups.Select(g => g.TargetFramework));
            return nearest == null
                ? []
                : [.. groups.First(g => g.TargetFramework.Equals(nearest)).Packages];
        }

        /// <summary>Copies the <c>.nupkg</c> to <paramref name="destination"/> from <paramref name="preferred"/> or the first source that has it.</summary>
        public async Task<bool> DownloadAsync(string id, NuGetVersion version, string destination, FindPackageByIdResource? preferred, CancellationToken cancellationToken)
        {
            IEnumerable<FindPackageByIdResource> candidates = preferred != null ? [preferred] : await ResourcesAsync(cancellationToken);
            foreach (FindPackageByIdResource resource in candidates)
            {
                try
                {
                    await using FileStream stream = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                    if (await resource.CopyNupkgToStreamAsync(id, version, stream, cache, NullLogger.Instance, cancellationToken))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is FatalProtocolException or HttpRequestException or IOException)
                {
                    throw new NugetException($"Package '{id}' {version.ToNormalizedString()} could not be downloaded: {ex.Message}", ex) { Code = "not_found" };
                }
            }

            return false;
        }
    }

    /// <summary>The private cache: <c>&lt;cache&gt;/packages/&lt;id&gt;/&lt;version&gt;/</c>.</summary>
    private static class Cache
    {
        public sealed record Extracted(IReadOnlyList<string> Assemblies, string? NativeDirectory, string? PluginDirectory);

        /// <summary>The folder of a package shaped as a plugin folder: every entry below it is extracted with its relative path.</summary>
        public const string PluginFolderPrefix = "plugin/";

        private static string Folder(NugetOptions options, string id, NuGetVersion version)
            => Path.Combine(Path.GetFullPath(options.CacheDirectory), "packages", id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant());

        /// <summary>The nuspec's <c>description</c> of a cached package (trimmed, at most 500 characters); null when absent or unreadable (it is only shown, never needed).</summary>
        public static string? DescriptionOf(NugetOptions options, string id, NuGetVersion version)
        {
            try
            {
                using PackageArchiveReader reader = new(Path.Combine(Folder(options, id, version), $"{id.ToLowerInvariant()}.{version.ToNormalizedString().ToLowerInvariant()}.nupkg"));
                string? text = reader.NuspecReader.GetDescription()?.Trim();
                return string.IsNullOrEmpty(text) ? null : text.Length <= 500 ? text : text[..499] + "…";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        public static string HashOf(string path)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return "sha512-" + Convert.ToBase64String(SHA512.HashData(stream));
        }

        /// <summary>Makes sure the <c>.nupkg</c> is in the cache and matches <paramref name="expectedHash"/> (when given); returns its hash.</summary>
        public static async Task<string> EnsureNupkgAsync(
            NugetOptions options, SourceSet sources, string id, NuGetVersion version, string? expectedHash, FindPackageByIdResource? source, CancellationToken cancellationToken)
        {
            string folder = Folder(options, id, version);
            string path = Path.Combine(folder, $"{id.ToLowerInvariant()}.{version.ToNormalizedString().ToLowerInvariant()}.nupkg");
            await CacheGate.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(path))
                {
                    string present = HashOf(path);
                    if (expectedHash == null || present == expectedHash)
                    {
                        return present;
                    }

                    // A different file under the same id and version: never trusted; it is replaced from a source if one has the locked one.
                    File.Delete(path);
                    DeleteQuietly(Path.Combine(folder, "content"));
                }

                if (!sources.HasUsable)
                {
                    throw new NugetException(
                        $"Package '{id}' {version.ToNormalizedString()} is not in the NuGet cache and no usable source is configured: restore the package with a NuGet source ({options.SourcesOptionName}).")
                    { Code = sources.Usable.Any() || options.Sources.Count > 0 ? "offline" : "not_restored" };
                }

                Directory.CreateDirectory(folder);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    if (!await sources.DownloadAsync(id, version, temporary, source, cancellationToken))
                    {
                        throw new NugetException($"Package '{id}' {version.ToNormalizedString()} was not found in the NuGet sources ({string.Join(", ", sources.Usable)}).") { Code = "not_found" };
                    }

                    string hash = HashOf(temporary);
                    if (expectedHash != null && hash != expectedHash)
                    {
                        throw new NugetException(
                            $"Package '{id}' {version.ToNormalizedString()} from the NuGet sources does not match the lock (expected {expectedHash[..Math.Min(19, expectedHash.Length)]}…, got {hash[..19]}…): it is not used.")
                        { Code = "hash_mismatch" };
                    }

                    File.Move(temporary, path, overwrite: true);
                    return hash;
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
            }
            finally
            {
                CacheGate.Release();
            }
        }

        /// <summary>The files of a cached package: libraries of the nearest target framework and native libraries of the runtime identifier.</summary>
        public static async Task<Extracted> ExtractAsync(NugetOptions options, string id, NuGetVersion version, string hash, NuGetFramework target, CancellationToken cancellationToken)
        {
            string folder = Folder(options, id, version);
            string content = Path.Combine(folder, "content");
            string marker = Path.Combine(folder, "ok.json");
            await CacheGate.WaitAsync(cancellationToken);
            try
            {
                if (!(File.Exists(marker) && Directory.Exists(content) && ReadMarker(marker) == hash))
                {
                    DeleteQuietly(content);
                    string temporary = Path.Combine(folder, "content." + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        ExtractTo(Path.Combine(folder, $"{id.ToLowerInvariant()}.{version.ToNormalizedString().ToLowerInvariant()}.nupkg"), temporary, target, options);
                        Directory.Move(temporary, content);
                    }
                    finally
                    {
                        DeleteQuietly(temporary);
                    }

                    await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { hash }), cancellationToken);
                }
            }
            finally
            {
                CacheGate.Release();
            }

            string lib = Path.Combine(content, "lib");
            string natives = Path.Combine(content, "native");
            string plugin = Path.Combine(content, "plugin");
            return new Extracted(
                Directory.Exists(lib) ? [.. Directory.EnumerateFiles(lib, "*.dll").Order(StringComparer.OrdinalIgnoreCase)] : [],
                Directory.Exists(natives) && Directory.EnumerateFiles(natives).Any() ? natives : null,
                File.Exists(Path.Combine(plugin, PluginFolder.ManifestName)) ? plugin : null);
        }

        private static string? ReadMarker(string marker)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(marker));
                return document.RootElement.TryGetProperty("hash", out JsonElement hash) ? hash.GetString() : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }
        }

        private static void ExtractTo(string nupkg, string destination, NuGetFramework target, NugetOptions options)
        {
            using PackageArchiveReader reader = new(nupkg);
            FrameworkReducer reducer = new();
            Directory.CreateDirectory(Path.Combine(destination, "lib"));
            string package = Path.GetFileNameWithoutExtension(nupkg);
            int entries = reader.GetFiles().Count();
            if (entries > options.MaxPackageEntries)
            {
                throw new NugetException($"Package '{package}' has {entries} entries, more than the {options.MaxPackageEntries} allowed: it is not extracted.") { Code = "too_large" };
            }

            Budget budget = new(package, options);

            // Managed libraries: runtime-specific ones (runtimes/<rid>/lib/<tfm>) beat the portable lib/<tfm> of the same name.
            List<string> rids = Rids(options.RuntimeIdentifier);
            Dictionary<string, string> managed = new(StringComparer.OrdinalIgnoreCase);
            FrameworkSpecificGroup[] groups = [.. reader.GetLibItems()];
            if (reducer.GetNearest(target, groups.Select(g => g.TargetFramework)) is { } nearest)
            {
                foreach (string item in groups.First(g => g.TargetFramework.Equals(nearest)).Items.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    managed[Path.GetFileName(item)] = item;
                }
            }

            foreach (string rid in rids)
            {
                string prefix = $"runtimes/{rid}/lib/";
                List<string> candidates = [.. reader.GetFiles().Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))];
                IEnumerable<NuGetFramework> frameworks = candidates.Select(f => f.Split('/')[3]).Distinct(StringComparer.OrdinalIgnoreCase).Select(NuGetFramework.ParseFolder);
                if (reducer.GetNearest(target, frameworks) is { } best)
                {
                    foreach (string item in candidates.Where(f => NuGetFramework.ParseFolder(f.Split('/')[3]).Equals(best)))
                    {
                        managed[Path.GetFileName(item)] = item;
                    }
                }
            }

            foreach ((string name, string item) in managed)
            {
                CopyEntry(reader, item, Path.Combine(destination, "lib", name), budget);
            }

            // Native libraries: the first runtime identifier that has any.
            foreach (string rid in rids)
            {
                string prefix = $"runtimes/{rid}/native/";
                List<string> natives = [.. reader.GetFiles().Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !f.EndsWith('/'))];
                if (natives.Count == 0)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.Combine(destination, "native"));
                foreach (string item in natives)
                {
                    CopyEntry(reader, item, Path.Combine(destination, "native", Path.GetFileName(item)), budget);
                }

                break;
            }

            // A plugin shaped as a folder (plugin/plugin.json): the whole plugin/ tree, with its relative paths (templates, executables…).
            List<string> pluginFiles = [.. reader.GetFiles().Where(f => f.StartsWith(PluginFolderPrefix, StringComparison.OrdinalIgnoreCase) && !f.EndsWith('/'))];
            if (pluginFiles.Any(f => string.Equals(f, PluginFolderPrefix + PluginFolder.ManifestName, StringComparison.OrdinalIgnoreCase)))
            {
                string root = Path.GetFullPath(Path.Combine(destination, "plugin"));
                foreach (string item in pluginFiles)
                {
                    string path = NugetResolver.PluginEntryPath(root, item[PluginFolderPrefix.Length..])
                        ?? throw new NugetException($"Package '{package}' has the entry '{item}', which is not a path inside plugin/: it is not extracted.") { Code = "invalid" };
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    CopyEntry(reader, item, path, budget);
                }
            }
        }


        /// <summary>Bytes still allowed for the files of one package (decompression bombs: the declared sizes are not trusted).</summary>
        private sealed class Budget(string package, NugetOptions options)
        {
            public string Package { get; } = package;

            public long PerEntry { get; } = options.MaxExtractedEntryBytes;

            public long Total { get; } = options.MaxExtractedPackageBytes;

            public long Left { get; set; } = options.MaxExtractedPackageBytes;
        }

        /// <summary>Copies one entry counting the bytes really written; past a limit it stops and the extraction fails (<c>too_large</c>).</summary>
        private static void CopyEntry(PackageArchiveReader reader, string item, string target, Budget budget)
        {
            using Stream input = reader.GetStream(item);
            using FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > budget.PerEntry)
                {
                    throw new NugetException($"'{item}' of package '{budget.Package}' is larger than {budget.PerEntry} bytes once extracted: the package is not extracted.") { Code = "too_large" };
                }

                if (read > budget.Left)
                {
                    throw new NugetException($"Package '{budget.Package}' is larger than {budget.Total} bytes once extracted: it is not extracted.") { Code = "too_large" };
                }

                budget.Left -= read;
                output.Write(buffer, 0, read);
            }
        }

        /// <summary>Runtime identifiers to look at, most specific first (<c>win-x64</c>, <c>win</c>, <c>unix</c>).</summary>
        private static List<string> Rids(string? runtimeIdentifier)
        {
            string rid = runtimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
            List<string> list = [rid];
            string os = rid.Split('-')[0];
            if (!list.Contains(os, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(os);
            }

            if (!os.StartsWith("win", StringComparison.OrdinalIgnoreCase))
            {
                list.Add("unix");
            }

            return list;
        }

        private static void DeleteQuietly(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a stale folder is replaced the next time.
            }
        }
    }
}
