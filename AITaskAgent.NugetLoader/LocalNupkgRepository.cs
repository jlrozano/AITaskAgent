using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace AITaskAgent.NugetLoader;

/// <summary>Limits of <see cref="LocalNupkgRepository"/>.</summary>
public sealed record NupkgLimits
{
    /// <summary>Largest <c>.nupkg</c> accepted (bytes really read, not a declared length). Default 100 MiB.</summary>
    public long MaxBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Largest <c>.nuspec</c> (bytes really decompressed). Default 1 MiB.</summary>
    public long MaxNuspecBytes { get; init; } = 1024 * 1024;

    /// <summary>Most entries a <c>.nupkg</c> may have. Default 20 000.</summary>
    public int MaxEntries { get; init; } = 20_000;

    /// <summary>Most <c>.nupkg</c> files one <see cref="LocalNupkgRepository.AddFolderAsync"/> takes. Default 200.</summary>
    public int MaxFilesPerFolder { get; init; } = 200;
}

/// <summary>A NuGet dependency of an inspected package (the group of the nearest framework).</summary>
/// <param name="Id">Its id.</param>
/// <param name="Range">The range, in NuGet's normalized notation (<c>[1.0.0, )</c>).</param>
/// <param name="AvailableIn">
/// Where a satisfying version was found: <c>local-repository</c>; <c>host</c> when the host supplies that package (shared set, never
/// downloaded); null otherwise (another configured source may still have it: the install resolves it then).
/// </param>
public sealed record NupkgDependency(string Id, string Range, string? AvailableIn = null);

/// <summary>What a <c>.nupkg</c> is, read from its zip directory and its nuspec only.</summary>
/// <param name="Id">The id of the nuspec (never the file name).</param>
/// <param name="Version">The normalized version of the nuspec.</param>
/// <param name="Sha512"><c>sha512-&lt;base64&gt;</c> of the file (the lock's format).</param>
/// <param name="Size">Bytes.</param>
/// <param name="Dependencies">The dependencies of the nearest framework group to the target.</param>
/// <param name="Path">The file inspected.</param>
public sealed record NupkgInspection(string Id, string Version, string Sha512, long Size, IReadOnlyList<NupkgDependency> Dependencies, string Path);

/// <summary>What <see cref="LocalNupkgRepository.AddAsync"/> did.</summary>
/// <param name="Package">The package as it is in the repository.</param>
/// <param name="AlreadyPresent">True when the same id and version with the same bytes was already there (nothing written).</param>
public sealed record NupkgAddResult(NupkgInspection Package, bool AlreadyPresent);

/// <summary>One file of <see cref="LocalNupkgRepository.AddFolderAsync"/>: added, or why not.</summary>
public sealed record NupkgFolderItem(string File, NupkgAddResult? Result, NupkgException? Error);

/// <summary>The packages of the repository, and the files in it that are not valid packages.</summary>
public sealed record NupkgListing(IReadOnlyList<NupkgInspection> Packages, IReadOnlyList<LoadProblem> Problems);

/// <summary>
/// A <c>.nupkg</c> was refused. <see cref="Code"/>: <c>invalid_nupkg</c> (not a zip, no single root nuspec, invalid id or version, DTD,
/// too many entries), <c>package_conflict</c> (same id and version with other bytes), <c>too_large</c>, <c>invalid_path</c> (UNC or device path).
/// </summary>
public sealed class NupkgException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Stable code.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// A folder of <c>.nupkg</c> files a host keeps (a NuGet folder source): packages are added from files or streams after checking them
/// without extracting anything (the zip directory and the nuspec are the only things read), under the name <c>id.version.nupkg</c> in lower
/// case with the normalized version (the cache's convention), atomically. Adding loads and registers nothing: installing a plugin from it is
/// the loader's job, with its trust. Replacing the same version with other bytes is not supported (<c>package_conflict</c>).
/// </summary>
public sealed class LocalNupkgRepository
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly NupkgLimits _limits;

    /// <param name="folder">The repository folder (created on the first add). UNC and device paths are refused.</param>
    /// <param name="limits">Size limits; null = defaults.</param>
    /// <param name="targetFramework">Framework whose dependency group is reported.</param>
    public LocalNupkgRepository(string folder, NupkgLimits? limits = null, string targetFramework = "net10.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (PluginFolder.IsNetworkPath(folder))
        {
            throw new NupkgException("invalid_path", $"'{folder}' is a network or device path: the repository must be a local folder.");
        }

        Folder = Path.GetFullPath(folder);
        _limits = limits ?? new NupkgLimits();
        TargetFramework = targetFramework;
    }

    /// <summary>The repository folder, absolute (what a NuGet source list names).</summary>
    public string Folder { get; }

    /// <summary>The framework whose dependency group is reported.</summary>
    public string TargetFramework { get; }

    /// <summary>The file name of a package in the repository: <c>id.version.nupkg</c>, lower case, normalized version.</summary>
    public static string FileNameOf(string id, string version) => $"{id.ToLowerInvariant()}.{NuGetVersion.Parse(version).ToNormalizedString().ToLowerInvariant()}.nupkg";

    /// <summary>
    /// Checks <paramref name="path"/> without extracting it: size, zip signature, entry count, exactly one <c>.nuspec</c> at the root (read with a
    /// byte limit and no DTD), id and version read from the nuspec (<c>PackageArchiveReader.GetIdentity()</c>) and validated.
    /// </summary>
    /// <exception cref="NupkgException">It is not a valid package.</exception>
    public static NupkgInspection Inspect(string path, NupkgLimits? limits = null, string targetFramework = "net10.0", string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        limits ??= new NupkgLimits();
        FileInfo info = new(path);
        string name = displayName ?? $"'{info.Name}'";
        string head = char.ToUpperInvariant(name[0]) + name[1..];
        if (!info.Exists)
        {
            throw new NupkgException("invalid_nupkg", $"{head} does not exist.");
        }

        if (info.Length > limits.MaxBytes)
        {
            throw new NupkgException("too_large", $"{head} has {info.Length} bytes, more than the {limits.MaxBytes} allowed.");
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] header = new byte[4];
        if (stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) < 4 || header[0] != 'P' || header[1] != 'K' || header[2] != 3 || header[3] != 4)
        {
            throw new NupkgException("invalid_nupkg", $"{head} is not a .nupkg (not a zip file).");
        }

        stream.Position = 0;
        string sha = "sha512-" + Convert.ToBase64String(SHA512.HashData(stream));
        stream.Position = 0;

        try
        {
            using (ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count > limits.MaxEntries)
                {
                    throw new NupkgException("invalid_nupkg", $"{head} has {zip.Entries.Count} entries, more than the {limits.MaxEntries} allowed.");
                }

                List<ZipArchiveEntry> nuspecs = [.. zip.Entries.Where(e => !e.FullName.Contains('/') && !e.FullName.Contains('\\') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))];
                if (nuspecs.Count != 1)
                {
                    throw new NupkgException("invalid_nupkg", $"{head} must have exactly one .nuspec at its root; it has {nuspecs.Count}.");
                }

                CheckNuspec(nuspecs[0], limits, name);
            }

            stream.Position = 0;
            using PackageArchiveReader reader = new(stream, leaveStreamOpen: true);
            PackageIdentity identity = reader.GetIdentity();
            if (new NugetRequirement(identity.Id ?? string.Empty, "1.0.0").Problem() is { } problem)
            {
                throw new NupkgException("invalid_nupkg", $"{head}: {problem}");
            }

            if (identity.Version is null)
            {
                throw new NupkgException("invalid_nupkg", $"{head} has no version in its nuspec.");
            }

            List<PackageDependencyGroup> groups = [.. reader.GetPackageDependencies()];
            NuGetFramework? nearest = new FrameworkReducer().GetNearest(NuGetFramework.Parse(targetFramework), groups.Select(g => g.TargetFramework));
            IReadOnlyList<NupkgDependency> dependencies = nearest == null
                ? []
                : [.. groups.First(g => g.TargetFramework.Equals(nearest)).Packages.Select(d => new NupkgDependency(d.Id, (d.VersionRange ?? VersionRange.All).ToNormalizedString()))];
            return new NupkgInspection(identity.Id!, identity.Version.ToNormalizedString(), sha, info.Length, dependencies, Path.GetFullPath(path));
        }
        catch (NupkgException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or PackagingException or InvalidOperationException or ArgumentException or FormatException or IOException)
        {
            // An IOException names the file it failed on: with an upload that is the server's temporary file, never shown.
            throw new NupkgException("invalid_nupkg", ex is IOException ? $"{head} could not be read as a .nupkg." : $"{head} is not a valid .nupkg: {ex.Message}", ex);
        }
    }

    /// <summary>What an upload is called in its errors: the server's temporary file name is never shown.</summary>
    private const string UploadedFile = "the uploaded file";

    /// <summary>
    /// Adds the package in <paramref name="content"/>: written to a temporary file of the repository (stopping past
    /// <see cref="NupkgLimits.MaxBytes"/>), inspected, then moved to its name. Same id, version and bytes = nothing written
    /// (<see cref="NupkgAddResult.AlreadyPresent"/>).
    /// </summary>
    /// <exception cref="NupkgException"><c>invalid_nupkg</c>, <c>too_large</c> or <c>package_conflict</c>; nothing is left in the repository.</exception>
    public Task<NupkgAddResult> AddAsync(Stream content, CancellationToken cancellationToken = default) => AddNamedAsync(content, UploadedFile, cancellationToken);

    private async Task<NupkgAddResult> AddNamedAsync(Stream content, string displayName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(Folder);
        string temporary = Path.Combine(Folder, ".upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > _limits.MaxBytes)
                    {
                        throw new NupkgException("too_large", $"The package is larger than the {_limits.MaxBytes} bytes allowed.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            NupkgInspection inspected = Inspect(temporary, _limits, TargetFramework, displayName);
            string target = Inside(FileNameOf(inspected.Id, inspected.Version));
            await Gate.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(target))
                {
                    NupkgInspection present = Inspect(target, _limits, TargetFramework);
                    if (present.Sha512 != inspected.Sha512)
                    {
                        throw new NupkgException("package_conflict",
                            $"'{inspected.Id}' {inspected.Version} is already in the repository with other content: replacing a version is not supported (publish another version, or remove it first).");
                    }

                    return new NupkgAddResult(WithAvailability(present), AlreadyPresent: true);
                }

                File.Move(temporary, target, overwrite: false);
            }
            finally
            {
                Gate.Release();
            }

            return new NupkgAddResult(WithAvailability(inspected with { Path = target }), AlreadyPresent: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Adds the <c>.nupkg</c> at <paramref name="path"/> (a local file: UNC and device paths are refused).</summary>
    /// <exception cref="NupkgException">As <see cref="AddAsync"/>, or <c>invalid_path</c>.</exception>
    public async Task<NupkgAddResult> AddFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (PluginFolder.IsNetworkPath(path))
        {
            throw new NupkgException("invalid_path", $"'{path}' is a network or device path: add a local file.");
        }

        if (!File.Exists(path))
        {
            throw new NupkgException("invalid_nupkg", $"'{path}' does not exist.");
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await AddNamedAsync(stream, $"'{Path.GetFileName(path)}'", cancellationToken);
    }

    /// <summary>Adds every <c>*.nupkg</c> at the top of <paramref name="folder"/> (at most <see cref="NupkgLimits.MaxFilesPerFolder"/>), each on its own.</summary>
    /// <exception cref="NupkgException"><c>invalid_path</c>, or more files than allowed (<c>too_large</c>); nothing is added then.</exception>
    public async Task<IReadOnlyList<NupkgFolderItem>> AddFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (PluginFolder.IsNetworkPath(folder) || !Directory.Exists(folder))
        {
            throw new NupkgException("invalid_path", $"'{folder}' is not a local folder.");
        }

        string[] files = [.. Directory.GetFiles(folder, "*.nupkg", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase)];
        if (files.Length > _limits.MaxFilesPerFolder)
        {
            throw new NupkgException("too_large", $"'{folder}' has {files.Length} .nupkg files, more than the {_limits.MaxFilesPerFolder} one import takes.");
        }

        List<NupkgFolderItem> items = [];
        foreach (string file in files)
        {
            try
            {
                items.Add(new NupkgFolderItem(file, await AddFileAsync(file, cancellationToken), null));
            }
            catch (NupkgException ex)
            {
                items.Add(new NupkgFolderItem(file, null, ex));
            }
        }

        // Dependencies are reported against the repository once everything of the folder is in.
        return [.. items.Select(i => i.Result is { } result ? i with { Result = result with { Package = WithAvailability(result.Package) } } : i)];
    }

    /// <summary>The packages of the repository (inspected again) and the files that are not valid packages.</summary>
    public NupkgListing List()
    {
        if (!Directory.Exists(Folder))
        {
            return new NupkgListing([], []);
        }

        List<NupkgInspection> packages = [];
        List<LoadProblem> problems = [];
        foreach (string file in Directory.GetFiles(Folder, "*.nupkg", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                packages.Add(Inspect(file, _limits, TargetFramework));
            }
            catch (NupkgException ex)
            {
                problems.Add(new LoadProblem(ex.Code, ex.Message));
            }
        }

        return new NupkgListing([.. packages.Select(p => p with { Dependencies = Availability(p.Dependencies, packages) })], problems);
    }

    /// <summary>Removes <paramref name="id"/> <paramref name="version"/> from the repository (what is installed is not touched). False when it is not there.</summary>
    /// <exception cref="ArgumentException">The id or version is not valid.</exception>
    public bool Remove(string id, string version)
    {
        if (new NugetRequirement(id ?? string.Empty, "1.0.0").Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(id));
        }

        if (!NuGetVersion.TryParse(version, out _))
        {
            throw new ArgumentException($"'{version}' is not a version.", nameof(version));
        }

        string path = Inside(FileNameOf(id!, version));
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// The id and version of each file already looked at, by path, size and last write time: the availability of the dependencies of every
    /// added package reads the whole folder, and an import of N files would otherwise inspect N² of them (only id and version are used from
    /// here; a listing always inspects again).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Written, NupkgInspection? Package)> _identities = new(StringComparer.OrdinalIgnoreCase);

    private NupkgInspection WithAvailability(NupkgInspection package)
    {
        List<NupkgInspection> present = [];
        if (Directory.Exists(Folder))
        {
            foreach (string file in Directory.GetFiles(Folder, "*.nupkg", SearchOption.TopDirectoryOnly))
            {
                if (IdentityOf(file) is { } known)
                {
                    present.Add(known);
                }
            }
        }

        return package with { Dependencies = Availability(package.Dependencies, present) };
    }

    /// <summary>The inspection of <paramref name="file"/>, from <see cref="_identities"/> while its size and write time are the same; null when it is not a package.</summary>
    private NupkgInspection? IdentityOf(string file)
    {
        FileInfo info = new(file);
        if (!info.Exists)
        {
            return null;
        }

        if (_identities.TryGetValue(file, out (long Length, DateTime Written, NupkgInspection? Package) seen) && seen.Length == info.Length && seen.Written == info.LastWriteTimeUtc)
        {
            return seen.Package;
        }

        NupkgInspection? package;
        try
        {
            package = Inspect(file, _limits, TargetFramework);
        }
        catch (NupkgException)
        {
            package = null; // Not a package: it satisfies nothing.
        }

        _identities[file] = (info.Length, info.LastWriteTimeUtc, package);
        return package;
    }

    private static IReadOnlyList<NupkgDependency> Availability(IReadOnlyList<NupkgDependency> dependencies, IReadOnlyList<NupkgInspection> present)
        => [.. dependencies.Select(d => d with
        {
            AvailableIn = VersionRange.TryParse(d.Range, out VersionRange? range) && present.Any(p => string.Equals(p.Id, d.Id, StringComparison.OrdinalIgnoreCase) && range.Satisfies(NuGetVersion.Parse(p.Version)))
                ? "local-repository"
                : SharedAssemblySet.HostProvides(d.Id) ? "host" : null
        })];

    /// <summary>The nuspec is decompressed with a byte limit and parsed with DTDs prohibited (no entity expansion, no external references).</summary>
    private static void CheckNuspec(ZipArchiveEntry nuspec, NupkgLimits limits, string name)
    {
        using MemoryStream bytes = new();
        using (Stream input = nuspec.Open())
        {
            byte[] buffer = new byte[16384];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes.Length + read > limits.MaxNuspecBytes)
                {
                    throw new NupkgException("invalid_nupkg", $"The nuspec of {name} is larger than the {limits.MaxNuspecBytes} bytes allowed.");
                }

                bytes.Write(buffer, 0, read);
            }
        }

        bytes.Position = 0;
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 };
        try
        {
            using XmlReader reader = XmlReader.Create(bytes, settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            throw new NupkgException("invalid_nupkg", $"The nuspec of {name} is not acceptable XML (DTDs are not allowed): {ex.Message}", ex);
        }
    }

    /// <summary>A file name below <see cref="Folder"/>, never outside it.</summary>
    private string Inside(string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(Folder, fileName));
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(path), Folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new NupkgException("invalid_nupkg", $"'{fileName}' is not a file name inside the repository.");
        }

        return path;
    }
}
