using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Packaging;
using NuGet.Versioning;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AITaskAgent.NugetLoader;

/// <summary>What <see cref="PluginLoader.InspectFolder"/> read of a folder plugin, without loading or recording anything.</summary>
/// <param name="Folder">The folder, absolute.</param>
/// <param name="Manifest">Its manifest (null when a problem stops it).</param>
/// <param name="Tree">Its files and tree hash (null when it could not be hashed).</param>
/// <param name="Contributions">What it contributes, from metadata (null when it was not scanned).</param>
/// <param name="Problems">Everything wrong, warnings included (<c>legacy-manifest</c>, <c>info-mismatch</c>…).</param>
public sealed record FolderInspection(string Folder, PluginManifest? Manifest, PluginFolderTree? Tree, PluginContributions? Contributions, IReadOnlyList<LoadProblem> Problems);

/// <summary>
/// Installs plugins into a running process, for any application: a plugin is a NuGet package (resolved or taken from its lock, materialized
/// from the private cache with its SHA-512 checked) or a folder (a <c>dotnet publish</c> output with an optional <c>plugin.json</c>, hashed
/// as a whole tree). What it contributes is read from the metadata of its own assemblies (<see cref="PluginScanner"/>) and shown to
/// <see cref="ITrustPolicy"/>; only after approval is it loaded into a load context of its own (<c>plugin:&lt;origin&gt;:&lt;id&gt;/&lt;version&gt;</c>),
/// its steps registered in <see cref="IStepRegistry"/> and the rest handed to the host as a <see cref="LoadedPlugin"/>. What is installed is
/// recorded in <c>plugins.json</c> so <see cref="LoadInstalledAsync"/> loads it again, offline, when the process starts.
/// </summary>
/// <remarks>
/// <para>Nothing of a plugin is loaded before the trust policy approves it (or its exact content was approved before): resolving,
/// materializing, hashing and scanning only read files (no <see cref="Assembly"/> of the plugin is loaded before approval). No type of the
/// plugin is instantiated by the loader; after approval each step type is described once (<see cref="IStepRegistry.Describe(Type)"/>, as any
/// catalog does), which constructs the custom attributes declared on it.</para>
/// <para>All or nothing: a plugin whose step tag, hook name or response type name belongs to another owner (another plugin or the host) is
/// not registered at all, and the problem <c>collision</c> names both owners; <see cref="PluginLoaderOptions.Accept"/> can refuse it too
/// (tool names). Adding is live; removing or updating a plugin needs a restart (<see cref="LoadReport.RestartRequired"/>,
/// <see cref="RemoveResult.RestartRequired"/>): load contexts are not collectible and the registry has no removal.</para>
/// <para>Operations of one instance are serialized. Several instances (or processes) over the same folder do not lose each other's entries of
/// <c>plugins.json</c>: every write re-reads the file under a lock file of the install folder and applies only its own change.</para>
/// </remarks>
public sealed class PluginLoader
{
    private readonly PluginLoaderOptions _options;
    private readonly IStepRegistry _registry;
    private readonly ITrustPolicy _trust;
    private readonly ILogger _logger;
    private readonly PluginStore _store;
    private readonly NugetResolver _resolver = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, LoadedState> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hookOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _responseOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoadProblem> _storeProblems = [];
    private readonly List<string> _storeNotes = [];
    private PluginFile? _state;

    /// <param name="options">Sources, cache and install folders.</param>
    /// <param name="registry">Where the steps are registered (the application's registry; adding after the service provider is built is supported).</param>
    /// <param name="trust">Asked before a plugin's code is loaded.</param>
    /// <param name="logger">Optional.</param>
    public PluginLoader(PluginLoaderOptions options, IStepRegistry registry, ITrustPolicy trust, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InstallDirectory);
        _options = options;
        _registry = registry;
        _trust = trust;
        _logger = logger ?? NullLogger.Instance;
        _store = new PluginStore(options.InstallDirectory, options.LegacyStepPackagesDirectory);
    }

    /// <summary>Raised after a plugin changed what is registered or installed (a new plugin loaded, an update recorded for the next start).</summary>
    /// <remarks>Raised on the thread of the operation; an exception of a handler is logged and does not fail the operation.</remarks>
    public event EventHandler<LoadReport>? PluginsChanged;

    /// <summary>The options it was created with.</summary>
    public PluginLoaderOptions Options => _options;

    /// <summary>The path of <c>plugins.json</c>.</summary>
    public string RegistryPath => _store.FilePath;

    /// <summary>The plugins recorded in <c>plugins.json</c>.</summary>
    public IReadOnlyList<InstalledPlugin> Installed
    {
        get
        {
            lock (_stateLock)
            {
                return [.. State().Plugins.Select(p => p.ToPublic())];
            }
        }
    }

    /// <summary>The plugins registered in this process by this loader.</summary>
    public IReadOnlyList<LoadedPlugin> Loaded
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _loaded.Values.Select(l => l.Plugin).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>Problems of the registry file itself (<c>store-corrupt</c>: unreadable, copied aside, list started empty).</summary>
    public IReadOnlyList<LoadProblem> StoreProblems
    {
        get
        {
            lock (_stateLock)
            {
                State();
                return [.. _storeProblems];
            }
        }
    }

    /// <summary>For the record: the migration of a <c>steps-packages.json</c>, when it happened in this process.</summary>
    public IReadOnlyList<string> StoreNotes
    {
        get
        {
            lock (_stateLock)
            {
                State();
                return [.. _storeNotes];
            }
        }
    }

    /// <summary>
    /// Reads a folder plugin without loading or recording anything: manifest, tree hash and contributions (from metadata). For a host that
    /// lists folders waiting for trust.
    /// </summary>
    public static FolderInspection InspectFolder(string folder, string? id = null, int maxFiles = 20_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string root = Path.GetFullPath(folder);
        (PluginManifest? manifest, IReadOnlyList<LoadProblem> manifestProblems) = PluginFolder.ReadManifest(root, id);
        List<LoadProblem> problems = [.. manifestProblems];
        if (manifest == null)
        {
            return new FolderInspection(root, null, null, null, problems);
        }

        PluginFolderTree tree;
        try
        {
            tree = PluginFolder.Hash(root, maxFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new LoadProblem("invalid-plugin", $"The plugin folder '{root}' cannot be hashed: {ex.Message}"));
            return new FolderInspection(root, manifest, null, null, problems);
        }

        PluginScanResult scan = PluginScanner.Scan(manifest.Assemblies, FolderDlls(tree), manifest.Extension);
        problems.AddRange(scan.Problems);
        return new FolderInspection(root, manifest, tree, scan.Contributions, problems);
    }

    /// <summary>
    /// Installs the NuGet plugin <paramref name="id"/> at <paramref name="version"/> (a NuGet version or range, never floating: architecture
    /// §16.1). A lock already written for exactly this request is reused (nothing is resolved); otherwise the package is resolved against the
    /// sources, which needs <see cref="PluginLoaderOptions.AllowNetwork"/>.
    /// </summary>
    /// <returns>What happened; problems are reported, not thrown (only cancellation is).</returns>
    public async Task<LoadReport> InstallAsync(string id, string version, CancellationToken cancellationToken = default)
    {
        NugetRequirement requirement = new(id?.Trim() ?? string.Empty, version?.Trim() ?? string.Empty);
        if (requirement.Problem() is { } invalid)
        {
            return Failed(id ?? string.Empty, version ?? string.Empty, "invalid-package", invalid);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await InstallNugetCoreAsync(requirement, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Installs the folder plugin of <paramref name="folder"/> (its manifest's id, else <paramref name="id"/>, else the folder name). The whole
    /// tree is hashed and approved; the folder is used in place (nothing is copied) and recorded in <c>plugins.json</c>.
    /// </summary>
    /// <returns>What happened; problems are reported, not thrown (only cancellation is).</returns>
    public async Task<LoadReport> InstallFolderAsync(string folder, string? id = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await InstallFolderCoreAsync(folder, id, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads every plugin of <c>plugins.json</c>, without reading any NuGet source. A plugin whose files no longer match what was approved asks
    /// the trust policy again; one that cannot be loaded is reported and stays recorded.
    /// </summary>
    public async Task<IReadOnlyList<LoadReport>> LoadInstalledAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<LoadReport> reports = [];
            List<PluginEntry> entries;
            lock (_stateLock)
            {
                entries = [.. State().Plugins];
            }

            foreach (PluginEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reports.Add(entry.Origin == PluginOrigin.Folder ? await ReloadFolderAsync(entry, cancellationToken) : await ReloadNugetAsync(entry, cancellationToken));
            }

            return reports;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes <paramref name="id"/> from <c>plugins.json</c> (and its locks) so the next start does not load it. Its types stay loaded and
    /// registered in this process until it restarts. A folder plugin's folder is not touched.
    /// </summary>
    public async Task<RemoveResult> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        string trimmed = id?.Trim() ?? string.Empty;
        if (new NugetRequirement(trimmed, "1.0.0").Problem() is { } invalid)
        {
            return new RemoveResult(id ?? string.Empty, false, false, invalid);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            bool inMemory;
            PluginEntry? entry;
            lock (_stateLock)
            {
                inMemory = _loaded.ContainsKey(trimmed);
                entry = State().Plugins.FirstOrDefault(p => Same(p.Id, trimmed));
            }

            if (entry == null)
            {
                return new RemoveResult(trimmed, false, false, $"Plugin '{trimmed}' is not installed.");
            }

            Save(file => file.Plugins.RemoveAll(p => Same(p.Id, trimmed)));
            _store.DeleteLocks(trimmed);
            _logger.LogInformation("Plugin {Id} {Version} removed (restart required: {Restart}).", entry.Id, entry.Version, inMemory);
            return new RemoveResult(entry.Id, true, inMemory, inMemory
                ? $"Plugin '{entry.Id}' {entry.Version} is removed; its steps stay registered until the application restarts."
                : $"Plugin '{entry.Id}' {entry.Version} is removed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Why installing the NuGet plugin <paramref name="id"/> <paramref name="version"/> would not load the package whose SHA-512 is
    /// <paramref name="sha512"/> (<c>sha512-&lt;base64&gt;</c>, e.g. a file just added to a local repository): the recorded plugin, the lock written
    /// for that version or the NuGet cache already have that id and version with other content, and an install reuses them without reading
    /// any source. Problem <c>package-conflict</c> (what is in the way and what to do); null when nothing is.
    /// </summary>
    public LoadProblem? ContentConflict(string id, string version, string sha512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha512);
        if (!NuGetVersion.TryParse(version, out NuGetVersion? exact))
        {
            throw new ArgumentException($"'{version}' is not a version.", nameof(version));
        }

        string normalized = exact.ToNormalizedString();
        static string Short(string hash) => hash[..Math.Min(19, hash.Length)] + "…";
        string what = $"'{id}' {normalized}";
        string fix = "Give the package another version, or remove the plugin and delete that copy first.";
        PluginEntry? entry;
        lock (_stateLock)
        {
            entry = State().Plugins.FirstOrDefault(p => Same(p.Id, id) && p.Origin == PluginOrigin.Nuget
                && NuGetVersion.TryParse(p.Version, out NuGetVersion? v) && v == exact);
        }

        if (entry != null && entry.Sha512 != sha512)
        {
            return new LoadProblem("package-conflict",
                $"Plugin {what} is installed with other content ({Short(entry.Sha512)}; this package is {Short(sha512)}): installing it would load the installed copy. {fix}");
        }

        string lockPath = _store.LockPath(id, normalized);
        if (File.Exists(lockPath))
        {
            try
            {
                if (NugetLock.Parse(File.ReadAllText(lockPath)).Packages.FirstOrDefault(p => p.Direct && Same(p.Id, id)) is { } locked && locked.Hash != sha512)
                {
                    return new LoadProblem("package-conflict",
                        $"The lock of plugin {what} ({lockPath}) pins other content ({Short(locked.Hash)}; this package is {Short(sha512)}): installing it would load that copy. {fix}");
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // An unreadable lock is not reused by an install.
            }
        }

        string lowerId = id.ToLowerInvariant();
        string lowerVersion = normalized.ToLowerInvariant();
        string cached = Path.Combine(Path.GetFullPath(_options.CacheDirectory), "packages", lowerId, lowerVersion, $"{lowerId}.{lowerVersion}.nupkg");
        if (File.Exists(cached))
        {
            string present;
            using (FileStream stream = new(cached, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                present = "sha512-" + Convert.ToBase64String(SHA512.HashData(stream));
            }

            if (present != sha512)
            {
                return new LoadProblem("package-conflict",
                    $"The NuGet cache already has {what} with other content ({cached}, {Short(present)}; this package is {Short(sha512)}): installing it would load the cached copy. {fix}");
            }
        }

        return null;
    }

    // ── NuGet: install ───────────────────────────────────────────────────────

    private async Task<LoadReport> InstallNugetCoreAsync(NugetRequirement requirement, CancellationToken cancellationToken)
    {
        string id = requirement.Id;
        List<string> notes = [];
        NugetLock lockFile;
        NugetEnvironment environment;
        bool reused = false;
        string requested = requirement.Version;
        PluginEntry? entry;
        LoadedState? loaded;
        lock (_stateLock)
        {
            entry = State().Plugins.FirstOrDefault(p => Same(p.Id, id));
            loaded = _loaded.GetValueOrDefault(id);
        }

        if (entry != null && entry.Origin != PluginOrigin.Nuget)
        {
            return Failed(id, requirement.Version, "duplicate-id",
                $"A folder plugin with id '{entry.Id}' is installed ({entry.Folder}): remove it before installing the NuGet package '{id}'.");
        }

        try
        {
            NugetLock? existing = _store.FindLock(id, requirement);
            string reuseNote = "The lock written for this request was reused (nothing was resolved).";
            // The installed version satisfies the request (the exact version a pending request or a reload problem shows, for a package that
            // was installed with a range): its own lock is reused, so approving it needs no source and cannot resolve another closure.
            if (existing == null && InstalledLock(entry, requirement) is { } installed)
            {
                existing = installed;
                requested = entry!.Requested ?? entry.Version;
                reuseNote = $"The lock of the installed version {entry.Version} was reused: it satisfies '{requirement.Version}' (nothing was resolved).";
            }

            if (existing != null)
            {
                if (CacheMismatch(existing) is { } mismatch)
                {
                    return Failed(id, requirement.Version, "hash-mismatch", mismatch);
                }

                lockFile = existing;
                reused = true;
                environment = await _resolver.MaterializeAsync(lockFile, ResolverOptions(_options.AllowNetwork), cancellationToken);
                notes.Add(reuseNote);
            }
            else
            {
                NugetRestoreResult restored = await _resolver.RestoreAsync([requirement], ResolverOptions(_options.AllowNetwork), cancellationToken);
                lockFile = restored.Lock;
                environment = restored.Environment;
                notes.AddRange(restored.Notes.Where(n => !restored.Environment.Notes.Contains(n)).Select(n => n.Message));
            }
        }
        catch (NugetException ex)
        {
            // A package (the plugin or one of its dependencies) no usable source has: the message names it; say what fixes it.
            string message = ex.Code == "not_found" ? ex.Message + " " + NotFoundHint : ex.Message;
            return Failed(id, requirement.Version, "nuget-" + ex.Code.Replace('_', '-'), message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or FormatException)
        {
            return Failed(id, requirement.Version, "nuget-failed", ex.Message);
        }

        notes.AddRange(environment.Notes.Select(n => n.Message));
        if (lockFile.Packages.FirstOrDefault(p => p.Direct && Same(p.Id, id)) is not { } direct)
        {
            return Failed(id, requirement.Version, "invalid-package", $"Package '{id}' is supplied by the host; it brings nothing to load.");
        }

        // A lock for a version that is not installed is written now, so the approval of a second call reuses it offline. The lock of an
        // installed version is the one reloading checks against its approved closure: a different closure waits for approval (Record),
        // otherwise a refused or failed install would leave the installed package unloadable at the next start.
        string closure = ClosureHash(lockFile);
        bool deferLock = entry != null && entry.Version == direct.Version && entry.ApprovedClosure != closure;
        if (!reused && !deferLock)
        {
            _store.WriteLock(direct.Id, direct.Version, lockFile);
        }

        bool writeLock = !reused && deferLock;
        if (NugetTarget(direct, environment, out LoadProblem? manifestProblem) is not { } target)
        {
            return Failed(direct.Id, direct.Version, manifestProblem!.Code, manifestProblem.Message, notes);
        }

        PluginScanResult scan = PluginScanner.Scan(target.OwnFiles, target.References, target.ExtensionType);

        // Another version is already loaded: its types cannot leave the process, so the new one is recorded for the next start.
        if (loaded != null && loaded.Version != direct.Version)
        {
            // The same rules as a first install, against the other owners (the loaded version's own tags are not a collision): a tag, hook or
            // response type of another plugin stops it now, before trust, instead of making one of the two a collision at the next start.
            List<LoadProblem> others = [.. OtherOwners(Collisions(target.Id, target.Version, target.Origin, scan.Contributions))];
            if (Stopping([.. scan.Problems, .. others]) is { Count: > 0 } stops)
            {
                return Report(target, notes, scan, problems: [.. scan.Problems.Except(stops), .. stops]);
            }

            Dictionary<string, byte[]> updateBytes = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> updateFiles = new(StringComparer.OrdinalIgnoreCase);
            if (ContentProblem(environment, direct, updateBytes, updateFiles) is { } updateContent)
            {
                return Failed(direct.Id, direct.Version, updateContent.Code, updateContent.Message, notes);
            }

            (bool approved, string updateClosure) = await ApproveNugetAsync(entry, lockFile, direct, environment, TrustRequestKind.Update, scan.Contributions, cancellationToken);
            if (!approved)
            {
                return NotApproved(target, notes, scan);
            }

            // Tool names (the host's rule) need the types: the approved version is loaded aside, offered to Accept, and nothing is registered.
            if (Probe(target, updateBytes, scan, updateClosure, updateFiles) is { Count: > 0 } refusedUpdate)
            {
                return Report(target, notes, scan, problems: [.. scan.Problems, .. refusedUpdate]) with { Approved = true };
            }

            Record(direct, lockFile, writeLock, NewNugetEntry(direct, requested, closure, [.. scan.Contributions.Steps.Select(s => s.Tag)]));
            LoadReport update = Report(target, notes, scan, problems: [new LoadProblem("restart-required",
                $"Plugin '{direct.Id}' {loaded.Version} is loaded in this process; {direct.Version} is installed and loads when the application restarts.")]) with
            {
                Approved = true,
                Recorded = true
            };
            Raise(update);
            return update;
        }

        if (loaded != null && entry != null && entry.Version == direct.Version && entry.ApprovedClosure == closure)
        {
            return Report(target, notes, scan) with
            {
                Approved = true,
                Recorded = true,
                Registered = loaded.Plugin.Steps,
                Skipped = loaded.Skipped,
                Plugin = loaded.Plugin,
                Notes = [.. notes, $"Plugin '{direct.Id}' {direct.Version} is already installed and loaded."]
            };
        }

        Dictionary<string, byte[]> verified = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> approvedFiles = new(StringComparer.OrdinalIgnoreCase);
        if (ContentProblem(environment, direct, verified, approvedFiles) is { } content)
        {
            return Failed(direct.Id, direct.Version, content.Code, content.Message, notes);
        }

        // A loaded plugin with other content of the same version is an update too: only the other owners count.
        if (Stopping([.. scan.Problems, .. OtherOwners(Collisions(target.Id, target.Version, target.Origin, scan.Contributions), keepOwn: loaded == null)]) is { Count: > 0 } blocked)
        {
            return Report(target, notes, scan, problems: [.. scan.Problems.Except(blocked), .. blocked]);
        }

        TrustRequestKind kind = entry == null ? TrustRequestKind.Install : entry.Version != direct.Version ? TrustRequestKind.Update : TrustRequestKind.Changed;
        (bool ok, string approvedClosure) = await ApproveNugetAsync(entry, lockFile, direct, environment, kind, scan.Contributions, cancellationToken);
        if (!ok)
        {
            return NotApproved(target, notes, scan);
        }

        if (loaded != null && loaded.Key != approvedClosure && Probe(target, verified, scan, approvedClosure, approvedFiles) is { Count: > 0 } refusedChange)
        {
            return Report(target, notes, scan, problems: [.. scan.Problems, .. refusedChange]) with { Approved = true };
        }

        LoadReport report = loaded == null
            ? Load(target, notes, verified, scan, approvedClosure, approvedFiles: approvedFiles)
            : loaded.Key == approvedClosure
                ? Report(target, notes, scan) with
                {
                    Approved = true,
                    Registered = loaded.Plugin.Steps,
                    Skipped = loaded.Skipped,
                    Plugin = loaded.Plugin,
                    Notes = [.. notes, $"Plugin '{direct.Id}' {direct.Version} was already loaded in this process; it is installed again."]
                }
                : Report(target, notes, scan, problems: [new LoadProblem("restart-required",
                    $"Plugin '{direct.Id}' {direct.Version} is loaded in this process with other content; the approved content loads when the application restarts.")]) with
                {
                    Approved = true,
                    Registered = loaded.Plugin.Steps,
                    Skipped = loaded.Skipped,
                    Plugin = loaded.Plugin
                };

        if (report.Plugin == null && !_loaded.ContainsKey(direct.Id))
        {
            // Approved, but nothing of it was registered (no assembly, a collision, refused by the host): not recorded (an entry already there
            // is left as it was, with its lock).
            return report;
        }

        Record(direct, lockFile, writeLock, NewNugetEntry(direct, requested, approvedClosure, [.. report.Registered.Select(r => r.Tag)]));
        report = report with { Recorded = true };
        Raise(report);
        return report;
    }

    /// <summary>Writes the lock when it was held back until approval, then records <paramref name="newEntry"/>.</summary>
    private void Record(NugetLockPackage direct, NugetLock lockFile, bool writeLock, PluginEntry newEntry)
    {
        if (writeLock)
        {
            _store.WriteLock(direct.Id, direct.Version, lockFile);
        }

        Save(file => Upsert(file, newEntry));
    }

    /// <summary>The lock of the installed version of <paramref name="entry"/> when that version satisfies <paramref name="requirement"/>; null otherwise.</summary>
    private NugetLock? InstalledLock(PluginEntry? entry, NugetRequirement requirement)
    {
        if (entry == null || entry.Origin != PluginOrigin.Nuget || !NuGetVersion.TryParse(entry.Version, out NuGetVersion? version) || !requirement.Range.Satisfies(version))
        {
            return null;
        }

        string path = _store.LockPath(entry.Id, entry.Version);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            NugetLock lockFile = NugetLock.Parse(File.ReadAllText(path));
            return PluginStore.IsUsable(lockFile) && lockFile.Packages.Any(p => p.Direct && Same(p.Id, entry.Id) && p.Version == entry.Version) ? lockFile : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private async Task<LoadReport> ReloadNugetAsync(PluginEntry entry, CancellationToken cancellationToken)
    {
        if (new NugetRequirement(entry.Id, entry.Version).Problem() is { } invalid)
        {
            return Failed(entry.Id, entry.Version, "invalid-package", invalid);
        }

        if (!NuGetVersion.TryParse(entry.Version, out _))
        {
            return Failed(entry.Id, entry.Version, "store-corrupt",
                $"{PluginStore.FileName} records '{entry.Version}' for plugin '{entry.Id}', which is not an exact version: install it again.");
        }

        LoadedState? loaded;
        lock (_stateLock)
        {
            loaded = _loaded.GetValueOrDefault(entry.Id);
        }

        if (loaded != null)
        {
            return loaded.Version == entry.Version
                ? new LoadReport { Id = entry.Id, Version = entry.Version, Approved = true, Registered = loaded.Plugin.Steps, Skipped = loaded.Skipped, Plugin = loaded.Plugin, Notes = ["Already loaded."] }
                : Failed(entry.Id, entry.Version, "restart-required",
                    $"Plugin '{entry.Id}' {loaded.Version} is loaded in this process; {entry.Version} loads when the application restarts.");
        }

        string lockPath = _store.LockPath(entry.Id, entry.Version);
        NugetLock lockFile;
        NugetEnvironment environment;
        try
        {
            if (!File.Exists(lockPath))
            {
                return Failed(entry.Id, entry.Version, "lock-missing", $"Plugin '{entry.Id}' {entry.Version} has no lock ({lockPath}): install it again.");
            }

            lockFile = NugetLock.Parse(await File.ReadAllTextAsync(lockPath, cancellationToken));
            if (!PluginStore.IsUsable(lockFile))
            {
                return Failed(entry.Id, entry.Version, "lock-missing", $"The lock of plugin '{entry.Id}' ({lockPath}) has an invalid package entry: install it again.");
            }

            if (CacheMismatch(lockFile) is { } mismatch)
            {
                return Failed(entry.Id, entry.Version, "hash-mismatch", mismatch);
            }

            environment = await _resolver.MaterializeAsync(lockFile, ResolverOptions(allowNetwork: false), cancellationToken);
        }
        catch (NugetException ex)
        {
            return Failed(entry.Id, entry.Version, "nuget-" + ex.Code.Replace('_', '-'), ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or FormatException)
        {
            return Failed(entry.Id, entry.Version, "nuget-failed", ex.Message);
        }

        if (lockFile.Packages.FirstOrDefault(p => p.Direct && Same(p.Id, entry.Id) && p.Version == entry.Version) is not { } direct)
        {
            return Failed(entry.Id, entry.Version, "lock-missing", $"The lock of plugin '{entry.Id}' does not pin version {entry.Version}: install it again.");
        }

        List<string> notes = [.. environment.Notes.Select(n => n.Message)];
        Dictionary<string, byte[]> verified = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> approvedFiles = new(StringComparer.OrdinalIgnoreCase);
        if (ContentProblem(environment, direct, verified, approvedFiles) is { } content)
        {
            return Failed(direct.Id, direct.Version, content.Code, content.Message, notes);
        }

        if (NugetTarget(direct, environment, out LoadProblem? manifestProblem) is not { } target)
        {
            return Failed(direct.Id, direct.Version, manifestProblem!.Code, manifestProblem.Message, notes);
        }

        PluginScanResult scan = PluginScanner.Scan(target.OwnFiles, target.References, target.ExtensionType);
        if (Stopping([.. scan.Problems, .. Collisions(target.Id, target.Version, target.Origin, scan.Contributions)]) is { Count: > 0 } blocked)
        {
            return Report(target, notes, scan, problems: [.. scan.Problems.Except(blocked), .. blocked]);
        }

        (bool approved, string closure) = await ApproveNugetAsync(entry, lockFile, direct, environment, TrustRequestKind.Changed, scan.Contributions, cancellationToken);
        if (!approved)
        {
            return NotApproved(target, notes, scan);
        }

        LoadReport report = Load(target, notes, verified, scan, closure, approvedFiles: approvedFiles);
        UpdateEntryAfterReload(entry, report, direct.Hash, closure, entry.Version);
        Raise(report);
        return report;
    }

    // ── Folder: install and reload ───────────────────────────────────────────

    private async Task<LoadReport> InstallFolderCoreAsync(string folder, string? id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(folder);
        if (FolderTarget(root, id) is not { } prepared)
        {
            return FolderFailure(root, id);
        }

        (Target target, PluginFolderTree tree, PluginScanResult scan, List<LoadProblem> warnings) = prepared;
        List<string> notes = [];
        PluginEntry? entry;
        LoadedState? loaded;
        lock (_stateLock)
        {
            entry = State().Plugins.FirstOrDefault(p => Same(p.Id, target.Id));
            loaded = _loaded.GetValueOrDefault(target.Id);
        }

        if (entry != null && entry.Origin != PluginOrigin.Folder)
        {
            return Report(target, notes, scan, problems: [new LoadProblem("duplicate-id",
                $"A NuGet plugin with id '{entry.Id}' is installed ({entry.Version}): remove it before installing the folder '{root}' as '{target.Id}'.")]);
        }

        if (Stopping(scan.Problems) is { Count: > 0 } stops)
        {
            return Report(target, notes, scan, problems: [.. warnings, .. scan.Problems]);
        }

        bool sameFolder = entry?.Folder != null && string.Equals(Path.GetFullPath(entry.Folder), root, StringComparison.OrdinalIgnoreCase);
        if (loaded != null)
        {
            if (loaded.Key == tree.Sha512 && sameFolder && entry!.Version == target.Version)
            {
                return Report(target, notes, scan, problems: warnings) with
                {
                    Approved = true,
                    Recorded = true,
                    Registered = loaded.Plugin.Steps,
                    Skipped = loaded.Skipped,
                    Plugin = loaded.Plugin,
                    Notes = [$"Plugin '{target.Id}' {target.Version} is already installed and loaded."]
                };
            }

            // The very folder and content that are loaded, without an entry (removed while loaded) or with another one: installing it again is
            // recording it again, nothing to restart for.
            if (loaded.Key == tree.Sha512 && loaded.Version == target.Version
                && string.Equals(Path.GetFullPath(loaded.Plugin.Folder), root, StringComparison.OrdinalIgnoreCase))
            {
                TrustRequestKind againKind = entry == null ? TrustRequestKind.Install : TrustRequestKind.Changed;
                if (!await ApproveFolderAsync(sameFolder ? entry : null, target, tree, scan.Contributions, againKind, cancellationToken))
                {
                    return NotApproved(target, notes, scan);
                }

                Save(file => Upsert(file, NewFolderEntry(target, tree, [.. loaded.Plugin.Steps.Select(s => s.Tag)])));
                LoadReport again = Report(target, notes, scan, problems: warnings) with
                {
                    Approved = true,
                    Recorded = true,
                    Registered = loaded.Plugin.Steps,
                    Skipped = loaded.Skipped,
                    Plugin = loaded.Plugin,
                    Notes = [$"Plugin '{target.Id}' {target.Version} was already loaded in this process; it is installed again."]
                };
                Raise(again);
                return again;
            }

            // The same rules as a first install, against the other owners (the loaded version's own tags are not a collision).
            if (Stopping(OtherOwners(Collisions(target.Id, target.Version, target.Origin, scan.Contributions))) is { Count: > 0 } updateBlocked)
            {
                return Report(target, notes, scan, problems: [.. warnings, .. scan.Problems, .. updateBlocked]);
            }

            if (VerifiedFolderBytes(target, tree) is not { } updateBytes)
            {
                return Report(target, notes, scan, problems: [new LoadProblem("content-mismatch",
                    $"A file of '{root}' changed while it was being read: nothing is recorded. Install it again.")]);
            }

            // Its types cannot leave the process: the approved content is recorded for the next start.
            TrustRequestKind updateKind = entry != null && entry.Version == target.Version ? TrustRequestKind.Changed : TrustRequestKind.Update;
            if (!await ApproveFolderAsync(entry, target, tree, scan.Contributions, updateKind, cancellationToken))
            {
                return NotApproved(target, notes, scan);
            }

            // Tool names (the host's rule) need the types: the approved folder is loaded aside, offered to Accept, and nothing is registered.
            if (Probe(target, updateBytes, scan, tree.Sha512) is { Count: > 0 } refusedUpdate)
            {
                return Report(target, notes, scan, problems: [.. warnings, .. scan.Problems, .. refusedUpdate]) with { Approved = true };
            }

            Save(file => Upsert(file, NewFolderEntry(target, tree, [.. scan.Contributions.Steps.Select(s => s.Tag)])));
            LoadReport update = Report(target, notes, scan, problems: [.. warnings, new LoadProblem("restart-required",
                $"Plugin '{target.Id}' {loaded.Version} is loaded in this process with other content; the approved folder loads when the application restarts.")]) with
            {
                Approved = true,
                Recorded = true
            };
            Raise(update);
            return update;
        }

        if (Stopping(Collisions(target.Id, target.Version, target.Origin, scan.Contributions)) is { Count: > 0 } blocked)
        {
            return Report(target, notes, scan, problems: [.. warnings, .. scan.Problems, .. blocked]);
        }

        if (VerifiedFolderBytes(target, tree) is not { } verified)
        {
            return Report(target, notes, scan, problems: [new LoadProblem("content-mismatch",
                $"A file of '{root}' changed while it was being read: nothing is loaded. Install it again.")]);
        }

        TrustRequestKind kind = entry == null ? TrustRequestKind.Install : entry.Version != target.Version ? TrustRequestKind.Update : TrustRequestKind.Changed;
        if (!await ApproveFolderAsync(sameFolder ? entry : null, target, tree, scan.Contributions, kind, cancellationToken))
        {
            return NotApproved(target, notes, scan);
        }

        LoadReport report = Load(target, notes, verified, scan, tree.Sha512, warnings);
        if (report.Plugin == null)
        {
            return report;
        }

        Save(file => Upsert(file, NewFolderEntry(target, tree, [.. report.Registered.Select(r => r.Tag)])));
        report = report with { Recorded = true };
        Raise(report);
        return report;
    }

    private async Task<LoadReport> ReloadFolderAsync(PluginEntry entry, CancellationToken cancellationToken)
    {
        LoadedState? loaded;
        lock (_stateLock)
        {
            loaded = _loaded.GetValueOrDefault(entry.Id);
        }

        string root = Path.GetFullPath(entry.Folder!);
        if (loaded != null)
        {
            return loaded.Key == entry.ApprovedSha512
                ? new LoadReport { Id = entry.Id, Version = loaded.Version, Origin = PluginOrigin.Folder, Folder = root, Approved = true, Registered = loaded.Plugin.Steps, Skipped = loaded.Skipped, Plugin = loaded.Plugin, Notes = ["Already loaded."] }
                : Failed(entry.Id, entry.Version, "restart-required",
                    $"Plugin '{entry.Id}' is loaded in this process with other content; the recorded folder loads when the application restarts.", origin: PluginOrigin.Folder, folder: root);
        }

        if (FolderTarget(root, entry.Id) is not { } prepared)
        {
            return FolderFailure(root, entry.Id);
        }

        (Target target, PluginFolderTree tree, PluginScanResult scan, List<LoadProblem> warnings) = prepared;
        List<string> notes = [];
        if (Stopping([.. scan.Problems, .. Collisions(target.Id, target.Version, target.Origin, scan.Contributions)]) is { Count: > 0 } blocked)
        {
            return Report(target, notes, scan, problems: [.. warnings, .. scan.Problems.Except(blocked), .. blocked]);
        }

        if (VerifiedFolderBytes(target, tree) is not { } verified)
        {
            return Report(target, notes, scan, problems: [new LoadProblem("content-mismatch", $"A file of '{root}' changed while it was being read: nothing is loaded.")]);
        }

        if (!await ApproveFolderAsync(entry, target, tree, scan.Contributions, TrustRequestKind.Changed, cancellationToken))
        {
            return NotApproved(target, notes, scan);
        }

        LoadReport report = Load(target, notes, verified, scan, tree.Sha512, warnings);
        UpdateEntryAfterReload(entry, report, tree.Sha512, null, target.Version);
        Raise(report);
        return report;
    }

    /// <summary>Manifest, tree and scan of a folder; null when the manifest or the hash stopped it (<see cref="FolderFailure"/> reports why).</summary>
    private (Target Target, PluginFolderTree Tree, PluginScanResult Scan, List<LoadProblem> Warnings)? FolderTarget(string root, string? id)
    {
        (PluginManifest? manifest, IReadOnlyList<LoadProblem> problems) = PluginFolder.ReadManifest(root, id);
        if (manifest == null)
        {
            return null;
        }

        PluginFolderTree tree;
        try
        {
            tree = PluginFolder.Hash(root, _options.MaxFolderFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        PluginScanResult scan = PluginScanner.Scan(manifest.Assemblies, FolderDlls(tree), manifest.Extension);
        Target target = new(PluginOrigin.Folder, manifest.Id, manifest.Version, tree.Folder, manifest.Assemblies, [], [], tree, manifest.Description, manifest.Extension);
        return (target, tree, scan, [.. problems]);
    }

    private LoadReport FolderFailure(string root, string? id)
    {
        FolderInspection inspection = InspectFolder(root, id, _options.MaxFolderFiles);
        LoadProblem problem = inspection.Problems.FirstOrDefault(p => p.Code != "legacy-manifest")
            ?? new LoadProblem("invalid-plugin", $"The plugin folder '{root}' cannot be read.");
        return Failed(inspection.Manifest?.Id ?? id ?? Path.GetFileName(root), inspection.Manifest?.Version ?? string.Empty, problem.Code, problem.Message,
            origin: PluginOrigin.Folder, folder: root);
    }

    /// <summary>The own assemblies' bytes, checked against the tree hash (what is loaded); null when one changed since it was hashed.</summary>
    private static Dictionary<string, byte[]>? VerifiedFolderBytes(Target target, PluginFolderTree tree)
    {
        Dictionary<string, byte[]> verified = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in target.OwnFiles)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (tree.HashOf(file) is not { } expected || Convert.ToHexStringLower(SHA512.HashData(bytes)) != expected)
            {
                return null;
            }

            verified[file] = bytes;
        }

        return verified;
    }

    private async Task<bool> ApproveFolderAsync(
        PluginEntry? entry, Target target, PluginFolderTree tree, PluginContributions contributions, TrustRequestKind kind, CancellationToken cancellationToken)
    {
        if (entry != null && entry.Origin == PluginOrigin.Folder && entry.ApprovedSha512 == tree.Sha512 && entry.Version == target.Version
            && entry.Folder != null && string.Equals(Path.GetFullPath(entry.Folder), tree.Folder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        TrustRequest request = new(target.Id, target.Version, tree.Sha512, [],
            [.. tree.Files.Keys.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)], kind)
        {
            Origin = PluginOrigin.Folder,
            Folder = tree.Folder,
            Contributions = contributions
        };
        bool approved = await _trust.ApproveAsync(request, cancellationToken);
        _logger.LogInformation("Plugin {Id} {Version} (folder {Folder}, {Kind}): trust {Decision}.", target.Id, target.Version, tree.Folder, kind, approved ? "approved" : "denied");
        return approved;
    }

    private static IEnumerable<string> FolderDlls(PluginFolderTree tree)
        => tree.Files.Keys.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(f => Path.Combine(tree.Folder, f.Replace('/', Path.DirectorySeparatorChar)));

    private static PluginEntry NewFolderEntry(Target target, PluginFolderTree tree, IReadOnlyList<string> steps) => new()
    {
        Id = target.Id,
        Origin = PluginOrigin.Folder,
        Version = target.Version,
        Folder = tree.Folder,
        Sha512 = tree.Sha512,
        ApprovedSha512 = tree.Sha512,
        Description = target.Description,
        InstalledAtUtc = DateTimeOffset.UtcNow,
        Steps = steps
    };

    /// <summary>
    /// After a reload: the entry is written only when something changed (hashes, version, steps), so a reload at start leaves <c>plugins.json</c>
    /// untouched; a load that registered nothing keeps the steps of the last load that worked.
    /// </summary>
    private void UpdateEntryAfterReload(PluginEntry entry, LoadReport report, string sha512, string? closure, string version)
    {
        bool failed = report.Plugin == null;
        IReadOnlyList<string> steps = failed ? entry.Steps : [.. report.Registered.Select(r => r.Tag)];
        if (failed && (entry.ApprovedSha512 == sha512 || !report.Approved))
        {
            return;
        }

        if (entry.Sha512 != sha512 || entry.ApprovedSha512 != sha512 || entry.ApprovedClosure != closure || entry.Version != version || !entry.Steps.SequenceEqual(steps, StringComparer.Ordinal))
        {
            // Applied to the entry as it is on disk now, and only if it is still the same plugin (another loader may have changed it).
            Save(file =>
            {
                if (file.Plugins.FirstOrDefault(p => Same(p.Id, entry.Id)) is { } current && current.Origin == entry.Origin
                    && (entry.Origin == PluginOrigin.Folder || current.Version == entry.Version))
                {
                    Upsert(file, current with { Sha512 = sha512, ApprovedSha512 = sha512, ApprovedClosure = closure, Version = version, Steps = steps });
                }
            });
        }
    }

    // ── Trust (NuGet) ────────────────────────────────────────────────────────

    /// <summary>Approved when the entry already approved this exact closure; otherwise the policy decides.</summary>
    private async Task<(bool Approved, string Closure)> ApproveNugetAsync(
        PluginEntry? entry, NugetLock lockFile, NugetLockPackage direct, NugetEnvironment environment, TrustRequestKind kind, PluginContributions contributions,
        CancellationToken cancellationToken)
    {
        string closure = ClosureHash(lockFile);
        if (entry != null && entry.Version == direct.Version && entry.ApprovedSha512 == direct.Hash && entry.ApprovedClosure == closure)
        {
            return (true, closure);
        }

        TrustRequest request = new(
            direct.Id, direct.Version, direct.Hash,
            [.. lockFile.Packages.Where(p => p != direct).Select(p => new TrustDependency(p.Id, p.Version, p.Hash))],
            [.. environment.Packages.SelectMany(p => p.Assemblies).Select(Path.GetFileName).OfType<string>()
                .Concat(environment.Packages.Where(p => p.PluginDirectory != null).SelectMany(p => PluginDirectoryFiles(p.PluginDirectory!)
                    .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .Select(f => "plugin/" + Path.GetRelativePath(p.PluginDirectory!, f).Replace('\\', '/'))))
                .Distinct(StringComparer.OrdinalIgnoreCase)],
            kind)
        {
            NativeLibraries = [.. environment.Packages.SelectMany(NativeFiles).Select(Path.GetFileName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
            Origin = PluginOrigin.Nuget,
            Contributions = contributions
        };
        bool approved = await _trust.ApproveAsync(request, cancellationToken);
        _logger.LogInformation("Plugin {Id} {Version} ({Kind}): trust {Decision}.", direct.Id, direct.Version, kind, approved ? "approved" : "denied");
        return (approved, closure);
    }

    private static string ClosureHash(NugetLock lockFile)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lockFile.Packages
            .Select(p => $"{p.Id.ToLowerInvariant()}/{p.Version}/{p.Hash}")
            .Order(StringComparer.Ordinal)))));

    /// <summary>
    /// What a NuGet plugin loads. A package with <c>plugin/plugin.json</c> is a plugin folder inside the package: its own assemblies are the
    /// manifest's (<c>assemblies</c>, or the folder rules of §25.2), its folder (<see cref="PluginContext"/>.Folder) the extracted <c>plugin/</c>
    /// with its content files, and the other DLLs at the top of that folder are private dependencies beside the closure's (the nuspec's
    /// <c>dependencies</c>). The manifest's id must be the package's; the nuspec's version wins. Otherwise the own assemblies are the package's
    /// <c>lib/&lt;tfm&gt;</c> DLLs. Null target = the manifest stops it (<paramref name="problem"/>).
    /// </summary>
    private static Target? NugetTarget(NugetLockPackage direct, NugetEnvironment environment, out LoadProblem? problem)
    {
        problem = null;
        NugetMaterializedPackage own = environment.Packages.First(p => p.Direct && Same(p.Id, direct.Id));
        if (own.PluginDirectory is { } directory)
        {
            (PluginManifest? manifest, IReadOnlyList<LoadProblem> problems) = PluginFolder.ReadManifest(directory, direct.Id);
            if (manifest == null)
            {
                problem = problems.FirstOrDefault() ?? new LoadProblem("bad-manifest", $"plugin/{PluginFolder.ManifestName} of package '{direct.Id}' cannot be read.");
                return null;
            }

            List<string> privateDlls = [.. PluginDirectoryDlls(directory)];
            return new Target(PluginOrigin.Nuget, direct.Id, direct.Version, Path.GetFullPath(directory), manifest.Assemblies,
                [.. privateDlls, .. environment.References], environment.NativeDirectories, null, own.Description ?? manifest.Description, manifest.Extension);
        }

        string folder = own.Assemblies.Count > 0 ? Path.GetDirectoryName(own.Assemblies[0])! : string.Empty;
        return new Target(PluginOrigin.Nuget, direct.Id, direct.Version, folder, own.Assemblies, environment.References, environment.NativeDirectories, null, own.Description);
    }

    /// <summary>The DLLs at the top of the extracted <c>plugin/</c> folder (subfolders, such as a private executable's, are not the plugin's dependencies).</summary>
    private static IEnumerable<string> PluginDirectoryDlls(string directory)
        => Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase);

    // ── Collisions ───────────────────────────────────────────────────────────

    /// <summary>Codes that stop a plugin (the rest of the scan's problems are warnings).</summary>
    private static List<LoadProblem> Stopping(IEnumerable<LoadProblem> problems)
        => [.. problems.Where(p => p.Code is "collision" or "multiple-configurations" or "multiple-extensions" or "extension-not-found" or "invalid-extension"
            or "extension-failed" or "invalid-plugin" or "load-failed" or "restart-required")];

    /// <summary>
    /// The step tags, hook names and response type names of <paramref name="contributions"/> that another owner already has (another plugin
    /// of this loader or the host); the message names both. Owners with the same id (another version of this plugin) are left to the
    /// restart rules.
    /// </summary>
    private List<LoadProblem> Collisions(string id, string version, PluginOrigin origin, PluginContributions contributions)
    {
        List<LoadProblem> problems = [];
        string self = new PluginIdentity(origin, id, version).ToString();
        foreach (StepContribution step in contributions.Steps)
        {
            if (_registry.TryGetType(step.Tag, out Type? existing) && existing != null)
            {
                PluginIdentity? owner = PluginIdentity.Of(existing);
                if (owner != null && Same(owner.Id, id))
                {
                    if (owner.Version != version || owner.Origin != origin)
                    {
                        problems.Add(new LoadProblem("restart-required",
                            $"Step tag '{step.Tag}' is registered by {owner}, another version of this plugin loaded in this process: restart the application to use {version}."));
                    }

                    continue;
                }

                problems.Add(new LoadProblem("collision",
                    $"Step tag '{step.Tag}' of {self} (type '{step.TypeName}') is already registered by {Describe(owner, existing)} (type '{existing.FullName}'): {self} is not loaded."));
            }
        }

        lock (_stateLock)
        {
            // Another plugin's update recorded for the next start (another version or other content than the one loaded): its step tags are
            // taken too, or the plugin that loads first at the next start (id order) would make the other one a collision then.
            foreach (PluginEntry pending in State().Plugins.Where(p => !Same(p.Id, id)))
            {
                if (!_loaded.TryGetValue(pending.Id, out LoadedState? current)
                    || (current.Version == pending.Version && (pending.Origin == PluginOrigin.Nuget ? current.Key == pending.ApprovedClosure : current.Key == pending.ApprovedSha512)))
                {
                    continue;
                }

                foreach (StepContribution step in contributions.Steps.Where(s => pending.Steps.Contains(s.Tag, StringComparer.Ordinal)))
                {
                    if (!problems.Any(p => p.Code == "collision" && p.Message.Contains($"'{step.Tag}'", StringComparison.Ordinal)))
                    {
                        problems.Add(new LoadProblem("collision",
                            $"Step tag '{step.Tag}' of {self} (type '{step.TypeName}') is taken by plugin '{pending.Id}' {pending.Version} ({PluginIdentity.OriginText(pending.Origin)}), recorded to load at the next start: {self} is not loaded."));
                    }
                }
            }

            foreach (HookContribution hook in contributions.Hooks)
            {
                if (_hookOwners.TryGetValue(hook.Name, out string? owner) && !Same(owner, id))
                {
                    problems.Add(new LoadProblem("collision", $"Hook name '{hook.Name}' of {self} is already registered by plugin '{owner}': {self} is not loaded."));
                }
            }

            foreach (ResponseTypeContribution response in contributions.ResponseTypes)
            {
                if (_responseOwners.TryGetValue(response.Name, out string? owner) && !Same(owner, id))
                {
                    problems.Add(new LoadProblem("collision", $"Response type '{response.Name}' of {self} is already declared by plugin '{owner}': {self} is not loaded."));
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// <paramref name="problems"/> without the <c>restart-required</c> ones (a step tag of another version of the same plugin), unless
    /// <paramref name="keepOwn"/>: what stops an update of a loaded plugin is only what belongs to another owner.
    /// </summary>
    private static IEnumerable<LoadProblem> OtherOwners(IEnumerable<LoadProblem> problems, bool keepOwn = false)
        => keepOwn ? problems : problems.Where(p => p.Code != "restart-required");

    private static string Describe(PluginIdentity? owner, Type existing)
        => owner?.ToString() ?? $"the host ({existing.Assembly.GetName().Name})";

    // ── Load and register ────────────────────────────────────────────────────

    /// <param name="target">The plugin.</param>
    /// <param name="notes">Notes so far.</param>
    /// <param name="verified">The checked bytes of the plugin's own assemblies: what is loaded.</param>
    /// <param name="scan">Its contributions, from metadata.</param>
    /// <param name="key">The approved content (NuGet closure hash, folder tree hash), kept with the loaded plugin.</param>
    /// <param name="warnings">Problems found before (manifest warnings).</param>
    /// <param name="approvedFiles">NuGet: the checked hashes of the closure's files (<see cref="ContentProblem"/>), checked again by the load context.</param>
    private LoadReport Load(
        Target target, List<string> notes, IReadOnlyDictionary<string, byte[]> verified, PluginScanResult scan, string key, IReadOnlyList<LoadProblem>? warnings = null,
        IReadOnlyDictionary<string, string>? approvedFiles = null)
        => LoadCore(target, notes, verified, scan, key, warnings, approvedFiles, register: true, out _);

    /// <summary>
    /// An approved update of a loaded plugin (another version, or other content) is loaded into a load context of its own and offered to
    /// <see cref="PluginLoaderOptions.Accept"/> (tool names) without registering anything, so a refusal is known now and not at the next start,
    /// when the plugins load in id order and another plugin would lose. Returns the problems that refuse it (empty = it may be recorded).
    /// The probe's load context stays in the process (contexts are not collectible).
    /// </summary>
    private List<LoadProblem> Probe(
        Target target, IReadOnlyDictionary<string, byte[]> verified, PluginScanResult scan, string key, IReadOnlyDictionary<string, string>? approvedFiles = null)
    {
        LoadReport report = LoadCore(target, [], verified, scan, key, null, approvedFiles, register: false, out List<LoadProblem> refused);
        return refused.Count > 0 ? refused : [.. report.Problems.Where(p => p.Code is "no-assemblies" or "load-failed")];
    }

    private LoadReport LoadCore(
        Target target, List<string> notes, IReadOnlyDictionary<string, byte[]> verified, PluginScanResult scan, string key, IReadOnlyList<LoadProblem>? warnings,
        IReadOnlyDictionary<string, string>? approvedFiles, bool register, out List<LoadProblem> refused)
    {
        refused = [];
        List<LoadProblem> problems = [.. warnings ?? [], .. scan.Problems];
        List<SkippedStep> skipped = [.. scan.Contributions.Skipped];
        if (target.OwnFiles.Count == 0)
        {
            return Report(target, notes, scan, problems: [.. problems, new LoadProblem("no-assemblies",
                $"Plugin '{target.Id}' {target.Version} has no assembly{(target.Origin == PluginOrigin.Nuget ? " for " + _options.TargetFramework : string.Empty)}.")]) with { Approved = true };
        }

        string contextName = PluginIdentity.ContextName(target.Origin, target.Id, target.Version);
        IsolatedLoadContext context = target.Tree == null && approvedFiles != null
            ? IsolatedLoadContext.Named(contextName, target.OwnFiles[0], target.References, target.NativeDirectories, approvedFiles)
            : IsolatedLoadContext.Named(contextName, target.OwnFiles[0], target.References, target.NativeDirectories, target.Tree);
        bool contentChanged = false;
        void NoteChanged(Exception ex)
        {
            if (ex.GetBaseException().Data[IsolatedLoadContext.ProblemKey] is "content-changed" || ex.Data[IsolatedLoadContext.ProblemKey] is "content-changed")
            {
                contentChanged = true;
            }
        }
        Dictionary<string, Assembly> assemblies = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in target.OwnFiles)
        {
            try
            {
                assemblies[file] = context.LoadMain(verified.TryGetValue(file, out byte[]? bytes) ? bytes : File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException)
            {
                NoteChanged(ex);
                problems.Add(new LoadProblem("load-failed", $"'{Path.GetFileName(file)}' could not be loaded: {ex.Message}"));
            }
        }

        if (assemblies.Count == 0)
        {
            return Report(target, notes, scan, problems: problems) with { Approved = true };
        }

        notes.Add($"Scanned {string.Join(", ", assemblies.Values.Select(a => a.GetName().Name + ".dll"))}.");

        // Contributions, resolved by their metadata tokens in the loaded assemblies (the same bytes that were scanned).
        T? Resolve<T>(MetadataRef reference, string what, Func<Module, int, T?> resolve) where T : class
        {
            if (!assemblies.TryGetValue(reference.File, out Assembly? assembly))
            {
                return null;
            }

            try
            {
                return resolve(assembly.ManifestModule, reference.Token);
            }
            catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException or ArgumentException or MissingMethodException)
            {
                NoteChanged(ex);
                problems.Add(new LoadProblem("type-load-failed", $"{what} could not be loaded and is left out (a dependency is missing?): {ex.GetBaseException().Message}"));
                return null;
            }
        }

        // A plugin driven by an IExtension (after approval, inside its load context): run Register against a capturing builder; what it really
        // registered replaces the metadata scan's lists (the scan was only the preview the user approved). No IExtension = type discovery.
        PluginExtensionRun? run = null;
        if (scan.Contributions.Extension is { } extensionRef)
        {
            run = RunExtension(target, extensionRef, (c, what) => Resolve(c.Ref, what, (m, t) => m.ResolveType(t)), problems, NoteChanged);
            if (run == null)
            {
                return Report(target, notes, scan, problems: problems) with { Approved = true };
            }
        }

        PluginContributions effective = run?.Contributions ?? scan.Contributions;
        List<(string Tag, string TypeName, bool Tagged, Type? Type)> stepSource = run != null
            ? [.. run.Capture.CapturedSteps.Select(s => (s.Tag, s.Type.FullName ?? s.Type.Name, s.Type.GetCustomAttribute<StepTagAttribute>(inherit: false)?.TagName == s.Tag, (Type?)s.Type))]
            : [.. scan.Contributions.Steps.Select(s => (s.Tag, s.TypeName, s.Tagged, Resolve(s.Ref, $"Step type '{s.TypeName}'", (m, t) => m.ResolveType(t))))];

        List<(string Tag, Type Type, bool Tagged)> steps = [];
        foreach ((string stepTag, string stepTypeName, bool stepTagged, Type? stepType) in stepSource)
        {
            if (stepType is not { } type)
            {
                skipped.Add(new SkippedStep(stepTypeName, "type-load-failed", $"'{stepTypeName}' could not be loaded."));
                continue;
            }

            // Described once, as every catalog will: a property of a type from a missing assembly or an attribute whose constructor throws would
            // otherwise be registered and break whoever lists the steps.
            try
            {
                _registry.Describe(type);
            }
            catch (Exception ex)
            {
                NoteChanged(ex);
                string message = $"'{stepTypeName}' could not be described and is not registered: {ex.GetBaseException().Message}";
                skipped.Add(new SkippedStep(stepTypeName, "type-load-failed", message));
                problems.Add(new LoadProblem("type-load-failed", message));
                continue;
            }

            steps.Add((stepTag, type, stepTagged));
        }

        List<Type> Types(IEnumerable<TypeContribution> items, string what)
            => [.. items.Select(i => Resolve(i.Ref, $"{what} '{i.TypeName}'", (m, t) => m.ResolveType(t))).OfType<Type>()];

        List<Type> tools;
        List<Type> toolSources;
        List<Type> templates;
        Type? configuration;
        List<PluginHook> hooks;
        List<PluginResponseType> responses;
        if (run != null)
        {
            ExtensionCapture capture = run.Capture;
            tools = [.. capture.ToolTypes];
            toolSources = [.. capture.ToolSources.Select(s => s.GetType())];
            templates = [.. capture.TemplateSources.Select(s => s.GetType())];
            configuration = null;
            hooks = [.. capture.Hooks.Select(h => new PluginHook(h.Name, h.Kind, h.Method, h.Description))];
            responses = [.. capture.ResponseTypes.Select(r => new PluginResponseType(r.Name, r.Type))];
        }
        else
        {
            tools = Types(scan.Contributions.Tools, "Tool");
            toolSources = Types(scan.Contributions.ToolSources, "Tool source");
            templates = Types(scan.Contributions.TemplateSources, "Template source");
            configuration = scan.Contributions.Configuration is { } config ? Types([config], "Configuration").FirstOrDefault() : null;
            hooks = [.. scan.Contributions.Hooks
                .Select(h => (Hook: h, Method: Resolve(h.Ref, $"Hook '{h.Name}'", (m, t) => m.ResolveMethod(t) as MethodInfo)))
                .Where(h => h.Method != null)
                .Select(h => new PluginHook(h.Hook.Name, h.Hook.Kind, h.Method!, h.Hook.Description))];
            responses = [.. scan.Contributions.ResponseTypes
                .Select(r => (r.Name, Type: Resolve(r.Ref, $"Response type '{r.Name}'", (m, t) => m.ResolveType(t))))
                .Where(r => r.Type != null)
                .Select(r => new PluginResponseType(r.Name, r.Type!))];
        }

        LoadedPlugin plugin = new(
            target.Id, target.Version, target.Origin, target.Folder, [.. assemblies.Values],
            [.. steps.Select(s => new RegisteredStep(s.Tag, s.Type, s.Tagged))], tools, toolSources, hooks, responses, templates, configuration)
        {
            Description = target.Description,
            Extension = run?.Extension
        };

        // All or nothing: a file that changed after the approval refuses it; then the collisions, checked again now that the plugin's types
        // exist (nothing of it is registered yet; for a probe of an update, the tags of the loaded version are its own), then the host's word.
        if (contentChanged)
        {
            refused = [new LoadProblem("content-changed",
                $"A file of plugin '{target.Id}' {target.Version} changed after it was approved: nothing of it is registered. Approve it again (install it again).")];
        }
        else
        {
            refused = Stopping(OtherOwners(Collisions(target.Id, target.Version, target.Origin, effective), register));
            if (refused.Count == 0 && _options.Accept is { } accept)
            {
                refused = [.. accept(plugin) ?? []];
            }
        }

        if (refused.Count > 0)
        {
            _logger.LogWarning("Plugin {Id} {Version} is not registered: {Problems}", target.Id, target.Version, string.Join(" ", refused.Select(r => r.Message)));
            return Report(target, notes, scan, problems: [.. problems, .. refused]) with { Approved = true, Skipped = skipped };
        }

        if (!register)
        {
            return Report(target, notes, scan, problems: problems) with { Approved = true, Skipped = skipped };
        }

        List<RegisteredStep> registered = [];
        foreach ((string tag, Type type, bool tagged) in steps)
        {
            try
            {
                _registry.Add(type, tag);
                registered.Add(new RegisteredStep(tag, type, tagged));
            }
            catch (InvalidOperationException) when (_registry.TryGetType(tag, out Type? raced) && raced != type)
            {
                problems.Add(new LoadProblem("collision", $"Step tag '{tag}' was registered by '{raced!.FullName}' while plugin '{target.Id}' was loading; '{type.FullName}' is not registered."));
            }
            catch (ArgumentException ex)
            {
                skipped.Add(new SkippedStep(type.FullName ?? type.Name, "rejected", ex.Message));
            }
        }

        plugin = plugin with { Steps = registered };
        lock (_stateLock)
        {
            _loaded[target.Id] = new LoadedState(target.Version, key, context, plugin, skipped);
            foreach (PluginHook hook in hooks)
            {
                _hookOwners[hook.Name] = target.Id;
            }

            foreach (PluginResponseType response in responses)
            {
                _responseOwners[response.Name] = target.Id;
            }
        }

        foreach (RegisteredStep step in registered.Where(s => !s.Tagged))
        {
            _logger.LogInformation("Step {Type} of plugin {Id} has no [StepTag]; it is registered as '{Tag}'.", step.Type.FullName, target.Id, step.Tag);
        }

        _logger.LogInformation("Plugin {Id} {Version} ({Origin}): {Steps} step(s), {Tools} tool(s), {Sources} tool source(s), {Hooks} hook(s), {Responses} response type(s), {Templates} template source(s), configuration {Configuration}; {Problems} problem(s).",
            target.Id, target.Version, PluginIdentity.OriginText(target.Origin), registered.Count, tools.Count, toolSources.Count, hooks.Count, responses.Count, templates.Count,
            configuration?.FullName ?? "none", problems.Count);
        return Report(target, notes, scan, problems: problems) with
        {
            Approved = true,
            Registered = registered,
            Skipped = skipped,
            Plugin = plugin
        };
    }

    /// <summary>
    /// Creates the plugin's <see cref="IExtension"/> (inside its load context, after approval) and runs it
    /// (<see cref="PluginExtensionRunner.Run"/>). Null = it could not run (<c>extension-failed</c> is in <paramref name="problems"/>): the plugin
    /// registers nothing.
    /// </summary>
    private PluginExtensionRun? RunExtension(
        Target target, TypeContribution reference, Func<TypeContribution, string, Type?> resolve, List<LoadProblem> problems, Action<Exception> noteChanged)
    {
        Type? type = resolve(reference, $"Extension '{reference.TypeName}'");
        if (type == null)
        {
            problems.Add(new LoadProblem("extension-failed", $"The extension '{reference.TypeName}' of plugin '{target.Id}' could not be loaded."));
            return null;
        }

        IExtension instance;
        try
        {
            instance = Activator.CreateInstance(type) as IExtension
                ?? throw new InvalidOperationException($"'{reference.TypeName}' does not implement IExtension.");
        }
        catch (Exception ex)
        {
            noteChanged(ex);
            problems.Add(new LoadProblem("extension-failed", $"The extension '{reference.TypeName}' of plugin '{target.Id}' could not be created: {ex.GetBaseException().Message}"));
            return null;
        }

        return PluginExtensionRunner.Run(
            instance, new PluginContext(target.Id, target.Version, target.Folder, EmptyConfiguration.Instance), _registry, problems, noteChanged, _logger);
    }

    // ── Files ────────────────────────────────────────────────────────────────

    private NugetOptions ResolverOptions(bool allowNetwork) => new()
    {
        CacheDirectory = _options.CacheDirectory,
        Sources = _options.Sources,
        AllowNetwork = allowNetwork,
        AllowRemote = _options.AllowRemote,
        TargetFramework = _options.TargetFramework,
        MaxExtractedEntryBytes = _options.MaxExtractedEntryBytes,
        MaxExtractedPackageBytes = _options.MaxExtractedPackageBytes,
        SourcesOptionName = _options.SourcesOptionName ?? NugetOptions.DefaultSourcesOptionName
    };

    /// <summary>
    /// A cached <c>.nupkg</c> of the lock whose SHA-512 is not the locked one (checked before the resolver, which would delete it and, offline,
    /// report only that it is missing); null when every present file matches. Path layout of architecture §16.4.
    /// </summary>
    private string? CacheMismatch(NugetLock lockFile)
    {
        foreach (NugetLockPackage package in lockFile.Packages)
        {
            string version = NuGetVersion.Parse(package.Version).ToNormalizedString().ToLowerInvariant();
            string id = package.Id.ToLowerInvariant();
            string path = Path.Combine(Path.GetFullPath(_options.CacheDirectory), "packages", id, version, $"{id}.{version}.nupkg");
            if (!File.Exists(path))
            {
                continue;
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string present = "sha512-" + Convert.ToBase64String(SHA512.HashData(stream));
            if (present != package.Hash)
            {
                return $"The cached package '{package.Id}' {package.Version} does not match its lock (expected {package.Hash[..Math.Min(19, package.Hash.Length)]}…, " +
                       $"got {present[..19]}…): it is not loaded. Delete it from the cache and install again.";
            }
        }

        return null;
    }

    /// <summary>
    /// Every extracted assembly and native library of the closure must be, byte for byte, a file of its verified <c>.nupkg</c>: the extracted
    /// folder of the cache is trusted by the resolver once its marker matches, so a file replaced there would otherwise load under the package's
    /// approved hash. The checked bytes of <paramref name="direct"/>'s own assemblies go to <paramref name="verified"/>, so those are what is
    /// loaded; the SHA-512 of every checked file (dependencies and native libraries too) goes to <paramref name="approvedFiles"/>, which the
    /// load context checks again when it reads a dependency or a native library later (a file replaced after this check is not loaded).
    /// Only the entries whose file name is one of the extracted files are decompressed, each and all together within
    /// <see cref="PluginLoaderOptions.MaxExtractedEntryBytes"/> / <see cref="PluginLoaderOptions.MaxExtractedPackageBytes"/> (bytes really read).
    /// </summary>
    private LoadProblem? ContentProblem(NugetEnvironment environment, NugetLockPackage direct, Dictionary<string, byte[]> verified, Dictionary<string, string>? approvedFiles = null)
    {
        foreach (NugetMaterializedPackage package in environment.Packages.Where(p => p.Assemblies.Count > 0 || p.NativeDirectory != null || p.PluginDirectory != null))
        {
            string version = NuGetVersion.Parse(package.Version).ToNormalizedString().ToLowerInvariant();
            string id = package.Id.ToLowerInvariant();
            string nupkg = Path.Combine(Path.GetFullPath(_options.CacheDirectory), "packages", id, version, $"{id}.{version}.nupkg");
            bool own = package.Direct && Same(package.Id, direct.Id);
            List<string> natives = [.. NativeFiles(package)];
            HashSet<string> extracted = new(
                [.. package.Assemblies.Select(f => Path.GetFileName(f).ToLowerInvariant()), .. natives.Select(f => Path.GetFileName(f).ToLowerInvariant())],
                StringComparer.Ordinal);
            try
            {
                using PackageArchiveReader reader = new(nupkg);
                HashSet<string> shipped = new(StringComparer.Ordinal);
                Dictionary<string, string> pluginShipped = new(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (string item in reader.GetFiles())
                {
                    // plugin/<path>: the package is a plugin folder; every file of it is checked (content files and private executables too).
                    if (package.PluginDirectory != null && item.StartsWith("plugin/", StringComparison.OrdinalIgnoreCase) && !item.EndsWith('/'))
                    {
                        using Stream pluginInput = reader.GetStream(item);
                        if (CappedSha256(pluginInput, ref total) is not { } pluginHash)
                        {
                            return new LoadProblem("nuget-too-large",
                                $"Entry '{item}' of package '{package.Id}' {package.Version} decompresses past the {_options.MaxExtractedEntryBytes} bytes allowed per entry " +
                                $"or the {_options.MaxExtractedPackageBytes} allowed per package: nothing is loaded.");
                        }

                        pluginShipped[item["plugin/".Length..].Replace('\\', '/')] = pluginHash;
                        continue;
                    }

                    // runtimes/<rid>/native/<file>: what the resolver extracts to content/native.
                    string[] parts = item.Split('/');
                    bool native = parts is ["runtimes", _, "native", { Length: > 0 }];
                    if (!native && !item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // An entry that cannot be one of the extracted files is never decompressed (a bomb under another name costs nothing).
                    if (!extracted.Contains(Path.GetFileName(item).ToLowerInvariant()))
                    {
                        continue;
                    }

                    using Stream input = reader.GetStream(item);
                    if (CappedSha256(input, ref total) is not { } hash)
                    {
                        return new LoadProblem("nuget-too-large",
                            $"Entry '{item}' of package '{package.Id}' {package.Version} decompresses past the {_options.MaxExtractedEntryBytes} bytes allowed per entry " +
                            $"or the {_options.MaxExtractedPackageBytes} allowed per package: nothing is loaded.");
                    }

                    shipped.Add(Path.GetFileName(item).ToLowerInvariant() + ":" + hash);
                    if (native)
                    {
                        shipped.Add("native/" + Path.GetFileName(item).ToLowerInvariant() + ":" + hash);
                    }
                }

                foreach (string file in package.Assemblies)
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    string key = Path.GetFileName(file).ToLowerInvariant() + ":" + Convert.ToHexString(SHA256.HashData(bytes));
                    if (!shipped.Contains(key))
                    {
                        return new LoadProblem("content-mismatch",
                            $"'{Path.GetFileName(file)}' in the cache is not the file of package '{package.Id}' {package.Version}: nothing is loaded. Delete the package from the cache and install again.");
                    }

                    if (own)
                    {
                        verified[file] = bytes;
                    }

                    if (approvedFiles != null)
                    {
                        approvedFiles[Path.GetFullPath(file)] = Convert.ToHexStringLower(SHA512.HashData(bytes));
                    }
                }

                foreach (string file in natives)
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    string key = "native/" + Path.GetFileName(file).ToLowerInvariant() + ":" + Convert.ToHexString(SHA256.HashData(bytes));
                    if (!shipped.Contains(key))
                    {
                        return new LoadProblem("content-mismatch",
                            $"Native library '{Path.GetFileName(file)}' in the cache is not the file of package '{package.Id}' {package.Version}: nothing is loaded. Delete the package from the cache and install again.");
                    }

                    if (approvedFiles != null)
                    {
                        approvedFiles[Path.GetFullPath(file)] = Convert.ToHexStringLower(SHA512.HashData(bytes));
                    }
                }

                if (package.PluginDirectory is { } directory && PluginDirectoryProblem(package, directory, pluginShipped, own, verified, approvedFiles) is { } pluginProblem)
                {
                    return pluginProblem;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new LoadProblem("content-mismatch", $"Package '{package.Id}' {package.Version} could not be checked in the cache: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// The upper-case hex SHA-256 of <paramref name="input"/>, counting the bytes really read against the per-entry cap and, through
    /// <paramref name="total"/>, the per-package cap; null past either.
    /// </summary>
    private string? CappedSha256(Stream input, ref long total)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long entry = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            entry += read;
            total += read;
            if (entry > _options.MaxExtractedEntryBytes || total > _options.MaxExtractedPackageBytes)
            {
                return null;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// The extracted <c>plugin/</c> folder must be, file for file, the <c>plugin/</c> entries of the verified <c>.nupkg</c> (<paramref name="shipped"/>:
    /// relative path → SHA-256): a file added, missing or changed in the cache is <c>content-mismatch</c>. Every file's SHA-512 goes to
    /// <paramref name="approvedFiles"/> (the load context loads a private DLL only with that hash); the top-level DLLs of the direct package go
    /// to <paramref name="verified"/> (the own assemblies are loaded from those bytes).
    /// </summary>
    private static LoadProblem? PluginDirectoryProblem(
        NugetMaterializedPackage package, string directory, IReadOnlyDictionary<string, string> shipped, bool own, Dictionary<string, byte[]> verified,
        Dictionary<string, string>? approvedFiles)
    {
        List<string> files = [.. PluginDirectoryFiles(directory)];
        if (files.Count != shipped.Count)
        {
            return new LoadProblem("content-mismatch",
                $"The plugin folder of package '{package.Id}' {package.Version} in the cache has {files.Count} files and the package {shipped.Count}: nothing is loaded. Delete the package from the cache and install again.");
        }

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            byte[] bytes = File.ReadAllBytes(file);
            if (!shipped.TryGetValue(relative, out string? expected) || expected != Convert.ToHexString(SHA256.HashData(bytes)))
            {
                return new LoadProblem("content-mismatch",
                    $"'plugin/{relative}' in the cache is not the file of package '{package.Id}' {package.Version}: nothing is loaded. Delete the package from the cache and install again.");
            }

            if (own && relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !relative.Contains('/'))
            {
                verified[Path.GetFullPath(file)] = bytes;
            }

            if (approvedFiles != null)
            {
                approvedFiles[Path.GetFullPath(file)] = Convert.ToHexStringLower(SHA512.HashData(bytes));
            }
        }

        return null;
    }

    /// <summary>Every file below an extracted <c>plugin/</c> folder (links are not followed).</summary>
    private static IEnumerable<string> PluginDirectoryFiles(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Order(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>The extracted native libraries of <paramref name="package"/> (none when it has no native folder).</summary>
    private static IEnumerable<string> NativeFiles(NugetMaterializedPackage package)
        => package.NativeDirectory != null && Directory.Exists(package.NativeDirectory)
            ? Directory.EnumerateFiles(package.NativeDirectory).Order(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>The registry file, read once (callers hold <see cref="_stateLock"/>).</summary>
    private PluginFile State()
    {
        if (_state == null)
        {
            (PluginFile file, LoadProblem? problem) = _store.Read(out string? note);
            if (problem != null)
            {
                _storeProblems.Add(problem);
                _logger.LogWarning("{Message}", problem.Message);
            }

            if (note != null)
            {
                _storeNotes.Add(note);
                _logger.LogInformation("{Message}", note);
            }

            _state = file;
        }

        return _state;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the registry file as it is on disk now (read again under the folder's lock file, so the entries
    /// another loader wrote meanwhile are kept), writes it and only then makes it the current one.
    /// </summary>
    private void Save(Action<PluginFile> change)
    {
        lock (_stateLock)
        {
            State();
            using IDisposable folderLock = _store.Lock();
            (PluginFile current, LoadProblem? problem) = _store.ReadCurrent();
            if (problem != null && _storeProblems.All(p => p.Code != problem.Code))
            {
                _storeProblems.Add(problem);
                _logger.LogWarning("{Message}", problem.Message);
            }

            change(current);
            current.Plugins.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));
            _store.Write(current);
            _state = current;
        }
    }

    private static void Upsert(PluginFile file, PluginEntry entry)
    {
        file.Plugins.RemoveAll(p => Same(p.Id, entry.Id));
        file.Plugins.Add(entry);
    }

    private static PluginEntry NewNugetEntry(NugetLockPackage direct, string requested, string closure, IReadOnlyList<string> steps) => new()
    {
        Id = direct.Id,
        Origin = PluginOrigin.Nuget,
        Version = direct.Version,
        Requested = requested,
        Sha512 = direct.Hash,
        ApprovedSha512 = direct.Hash,
        ApprovedClosure = closure,
        InstalledAtUtc = DateTimeOffset.UtcNow,
        Steps = steps
    };

    // ── Reports ──────────────────────────────────────────────────────────────

    private void Raise(LoadReport report)
    {
        try
        {
            PluginsChanged?.Invoke(this, report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A PluginsChanged handler failed for plugin {Id}.", report.Id);
        }
    }

    private static LoadReport Report(Target target, IReadOnlyList<string> notes, PluginScanResult scan, IReadOnlyList<LoadProblem>? problems = null) => new()
    {
        Id = target.Id,
        Version = target.Version,
        Origin = target.Origin,
        Folder = target.Origin == PluginOrigin.Folder ? target.Folder : null,
        Problems = problems ?? scan.Problems,
        Skipped = scan.Contributions.Skipped,
        Notes = [.. notes],
        Contributions = scan.Contributions
    };

    private LoadReport Failed(string id, string version, string code, string message, IReadOnlyList<string>? notes = null, PluginOrigin origin = PluginOrigin.Nuget, string? folder = null)
    {
        _logger.LogWarning("Plugin {Id} {Version}: {Code}: {Message}", id, version, code, message);
        return new LoadReport { Id = id, Version = version, Origin = origin, Folder = folder, Problems = [new LoadProblem(code, message)], Notes = notes ?? [] };
    }

    private static LoadReport NotApproved(Target target, IReadOnlyList<string> notes, PluginScanResult scan)
        => Report(target, notes, scan, problems: [.. scan.Problems, new LoadProblem("not-approved", $"Plugin '{target.Id}' {target.Version} was not approved: nothing of it was loaded.")]);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Appended to <c>nuget-not-found</c> while installing: the package named in the message is in none of the usable sources.</summary>
    public const string NotFoundHint = "Configure a NuGet source that has it, or add that .nupkg to the local repository too.";

    /// <summary>What is about to be loaded.</summary>
    private sealed record Target(
        PluginOrigin Origin, string Id, string Version, string Folder, IReadOnlyList<string> OwnFiles, IReadOnlyList<string> References,
        IReadOnlyList<string> NativeDirectories, PluginFolderTree? Tree, string? Description, string? ExtensionType = null);

    private sealed record LoadedState(string Version, string Key, IsolatedLoadContext Context, LoadedPlugin Plugin, IReadOnlyList<SkippedStep> Skipped);
}
