using AITaskAgent.Core.Steps;
using System.IO.Compression;
using System.Text;
using WorkflowDesigner.Testing;
using static AITaskAgent.NugetLoader.Tests.PluginTestKit;

namespace AITaskAgent.NugetLoader.Tests;

/// <summary>
/// <see cref="LocalNupkgRepository"/> (adding <c>.nupkg</c> files from the computer, with the security checks of the plugin spec §5),
/// installing plugins offline from it, and the resolver's limits: <see cref="NugetOptions.AllowRemote"/> and the decompression caps.
/// </summary>
[Collection(PluginCollection.Name)]
public sealed class LocalNupkgRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfd-nupkg-repo", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder left behind is harmless.
        }
    }

    private string Repo => Path.Combine(_root, "repo");

    private NugetTestFeed Outside => new(Path.Combine(_root, "outside"));

    /// <summary>A package built with NuGet's own builder in a folder outside the repository; returns its path.</summary>
    private string Pack(string id, string version, Dictionary<string, byte[]>? files = null, params (string Dependency, string Range)[] dependencies)
    {
        NugetTestFeed feed = Outside;
        feed.Pack(id, version, files ?? new() { [$"lib/net10.0/{id}.dll"] = NugetTestFeed.GreeterDll }, [.. dependencies.Select(d => ("net10.0", d.Dependency, d.Range))]);
        return Path.Combine(feed.Folder, $"{id}.{version}.nupkg");
    }

    /// <summary>A zip written by hand (what NuGet's builder would refuse to write): entries by name.</summary>
    private string Raw(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        string path = Path.Combine(_root, "raw", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        using ZipArchive zip = new(stream, ZipArchiveMode.Create);
        foreach ((string entry, byte[] bytes) in entries)
        {
            using Stream output = zip.CreateEntry(entry, CompressionLevel.SmallestSize).Open();
            output.Write(bytes);
        }

        return path;
    }

    private static byte[] Nuspec(string id, string version = "1.0.0", string prolog = "", string description = "d")
        => Encoding.UTF8.GetBytes($"""<?xml version="1.0" encoding="utf-8"?>{prolog}<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>{id}</id><version>{version}</version><authors>t</authors><description>{description}</description></metadata></package>""");

    private static async Task<NupkgException> Refused(Func<Task> action) => await Assert.ThrowsAsync<NupkgException>(action);

    // ── Adding ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_package_is_added_under_its_lower_case_name_and_reports_its_identity_hash_and_dependencies()
    {
        LocalNupkgRepository repository = new(Repo);
        string file = Pack("Acme.Tools", "1.02.0", dependencies: ("Acme.Base", "[1.0.0]"));

        NupkgAddResult added = await repository.AddFileAsync(file);

        Assert.False(added.AlreadyPresent);
        Assert.Equal(("Acme.Tools", "1.2.0"), (added.Package.Id, added.Package.Version));
        Assert.StartsWith("sha512-", added.Package.Sha512, StringComparison.Ordinal);
        Assert.Equal(new FileInfo(file).Length, added.Package.Size);
        Assert.Equal(Path.Combine(Repo, "acme.tools.1.2.0.nupkg"), added.Package.Path);
        NupkgDependency dependency = Assert.Single(added.Package.Dependencies);
        Assert.Equal(("Acme.Base", (string?)null), (dependency.Id, dependency.AvailableIn));

        // Once the dependency is there, it is reported as available in the repository.
        await repository.AddFileAsync(Pack("Acme.Base", "1.0.0"));
        NupkgListing listing = repository.List();
        Assert.Equal(["Acme.Base", "Acme.Tools"], listing.Packages.Select(p => p.Id));
        Assert.Equal("local-repository", listing.Packages.Single(p => p.Id == "Acme.Tools").Dependencies[0].AvailableIn);
        Assert.Empty(listing.Problems);
        Assert.Empty(Directory.EnumerateFiles(Repo, "*.tmp"));
    }

    [Fact]
    public async Task A_dependency_the_host_supplies_is_reported_as_available_in_the_host()
    {
        LocalNupkgRepository repository = new(Repo);

        // Newtonsoft.Json is of the shared set and this process has it: it is never downloaded, so nothing has to be uploaded for it.
        NupkgAddResult added = await repository.AddFileAsync(Pack("Acme.Json", "1.0.0", dependencies: [("Newtonsoft.Json", "[13.0.1, )"), ("Acme.Missing", "[1.0.0, )")]));

        Assert.Equal([("Acme.Missing", (string?)null), ("Newtonsoft.Json", "host")],
            added.Package.Dependencies.Select(d => (d.Id, d.AvailableIn)).OrderBy(d => d.Id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_same_bytes_again_are_idempotent_and_other_bytes_for_the_same_version_are_a_conflict()
    {
        LocalNupkgRepository repository = new(Repo);
        string first = Pack("Twice.Pkg", "1.0.0");
        byte[] original = File.ReadAllBytes(first);
        await repository.AddFileAsync(first);

        Assert.True((await repository.AddFileAsync(first)).AlreadyPresent);

        string other = Pack("Twice.Pkg", "1.0.0", new() { ["lib/net10.0/Other.dll"] = NugetTestFeed.Library("Other", "public static class X { }") });
        NupkgException conflict = await Refused(() => repository.AddFileAsync(other));
        Assert.Equal("package_conflict", conflict.Code);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Repo, "twice.pkg.1.0.0.nupkg")));
        Assert.Single(Directory.EnumerateFiles(Repo));
    }

    [Fact]
    public async Task A_file_that_is_not_a_zip_is_refused_and_leaves_nothing()
    {
        string path = Path.Combine(_root, "fake.nupkg");
        WriteText(path, "MZ this is not a zip");

        NupkgException ex = await Refused(() => new LocalNupkgRepository(Repo).AddFileAsync(path));

        Assert.Equal("invalid_nupkg", ex.Code);
        Assert.Contains("not a zip", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(Repo));
    }

    [Fact]
    public async Task An_upload_that_is_not_valid_never_shows_the_servers_temporary_file_or_folders()
    {
        NupkgException notZip = await Refused(() => new LocalNupkgRepository(Repo).AddAsync(new MemoryStream("MZ this is not a zip"u8.ToArray())));
        NupkgException empty = await Refused(() => new LocalNupkgRepository(Repo).AddAsync(new MemoryStream()));

        foreach (NupkgException ex in new[] { notZip, empty })
        {
            Assert.Equal("invalid_nupkg", ex.Code);
            Assert.StartsWith("The uploaded file", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(".upload-", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(".tmp", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // A file added by name keeps naming that file: it is the caller's own.
        string path = Path.Combine(_root, "fake.nupkg");
        WriteText(path, "MZ this is not a zip");
        Assert.Contains("'fake.nupkg'", (await Refused(() => new LocalNupkgRepository(Repo).AddFileAsync(path))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("evil/../../x")]
    [InlineData("C:\\evil")]
    public async Task An_id_that_is_not_a_package_id_is_refused(string id)
    {
        string path = Raw("evil.nupkg", ("evil.nuspec", Nuspec(id)), ("lib/net10.0/Evil.dll", NugetTestFeed.GreeterDll));

        NupkgException ex = await Refused(() => new LocalNupkgRepository(Repo).AddFileAsync(path));

        Assert.Equal("invalid_nupkg", ex.Code);
        Assert.Empty(Directory.EnumerateFiles(Repo));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.nupkg", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task A_nuspec_with_a_dtd_is_refused()
    {
        string path = Raw("dtd.nupkg",
            ("dtd.nuspec", Nuspec("Dtd.Pkg", prolog: """<!DOCTYPE package [ <!ENTITY boom "boom"> ]>""", description: "&boom;")));

        NupkgException ex = await Refused(() => new LocalNupkgRepository(Repo).AddFileAsync(path));

        Assert.Equal("invalid_nupkg", ex.Code);
        Assert.Contains("DTD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_nuspec_or_two_nuspecs_at_the_root_are_refused()
    {
        string none = Raw("none.nupkg", ("lib/net10.0/A.dll", NugetTestFeed.GreeterDll), ("sub/a.nuspec", Nuspec("A")));
        string two = Raw("two.nupkg", ("a.nuspec", Nuspec("A")), ("b.nuspec", Nuspec("B")));
        LocalNupkgRepository repository = new(Repo);

        Assert.Equal("invalid_nupkg", (await Refused(() => repository.AddFileAsync(none))).Code);
        Assert.Equal("invalid_nupkg", (await Refused(() => repository.AddFileAsync(two))).Code);
    }

    [Fact]
    public async Task A_nuspec_that_decompresses_past_its_limit_is_refused_without_reading_it_all()
    {
        // 8 MiB of spaces compress to a few KiB: the declared and compressed sizes say nothing, the bytes read do.
        byte[] huge = [.. Nuspec("Bomb.Pkg").Take(40), .. Enumerable.Repeat((byte)' ', 8 * 1024 * 1024), .. Nuspec("Bomb.Pkg").Skip(40)];
        string path = Raw("bomb.nupkg", ("bomb.nuspec", huge));
        Assert.True(new FileInfo(path).Length < 200 * 1024);

        NupkgException ex = await Refused(() => new LocalNupkgRepository(Repo).AddFileAsync(path));

        Assert.Equal("invalid_nupkg", ex.Code);
        Assert.Contains("larger than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_past_the_size_limit_is_cut_and_nothing_is_left()
    {
        LocalNupkgRepository repository = new(Repo, new NupkgLimits { MaxBytes = 1024 });
        using MemoryStream content = new([(byte)'P', (byte)'K', 3, 4, .. new byte[5000]]);

        NupkgException ex = await Refused(() => repository.AddAsync(content));

        Assert.Equal("too_large", ex.Code);
        Assert.Empty(Directory.EnumerateFiles(Repo));
    }

    [Fact]
    public async Task Network_and_device_paths_are_refused_and_a_folder_import_has_a_file_limit()
    {
        Assert.Equal("invalid_path", Assert.Throws<NupkgException>(() => new LocalNupkgRepository(@"\\server\share\repo")).Code);
        LocalNupkgRepository repository = new(Repo, new NupkgLimits { MaxFilesPerFolder = 1 });
        Assert.Equal("invalid_path", (await Refused(() => repository.AddFileAsync(@"\\?\C:\x.nupkg"))).Code);
        Assert.Equal("invalid_path", (await Refused(() => repository.AddFolderAsync(@"\\server\share"))).Code);

        Pack("One.Pkg", "1.0.0");
        Pack("Two.Pkg", "1.0.0");
        Assert.Equal("too_large", (await Refused(() => repository.AddFolderAsync(Outside.Folder))).Code);
        Assert.False(Directory.Exists(Repo) && Directory.EnumerateFiles(Repo).Any());
    }

    [Fact]
    public async Task A_folder_import_adds_each_file_on_its_own_and_reports_the_bad_ones()
    {
        Pack("Good.Pkg", "1.0.0", dependencies: ("Base.Pkg", "1.0.0"));
        Pack("Base.Pkg", "1.0.0");
        WriteText(Path.Combine(Outside.Folder, "broken.nupkg"), "not a zip");

        IReadOnlyList<NupkgFolderItem> items = await new LocalNupkgRepository(Repo).AddFolderAsync(Outside.Folder);

        Assert.Equal(3, items.Count);
        Assert.Equal("invalid_nupkg", items.Single(i => i.File.EndsWith("broken.nupkg", StringComparison.Ordinal)).Error!.Code);
        NupkgAddResult good = items.Single(i => i.Result?.Package.Id == "Good.Pkg").Result!;
        Assert.Equal("local-repository", Assert.Single(good.Package.Dependencies).AvailableIn);
    }

    [Fact]
    public async Task Removing_takes_the_file_out_of_the_repository_only()
    {
        LocalNupkgRepository repository = new(Repo);
        await repository.AddFileAsync(Pack("Gone.Pkg", "1.0.0"));

        Assert.True(repository.Remove("Gone.Pkg", "1.0.0"));
        Assert.False(repository.Remove("Gone.Pkg", "1.0.0"));
        Assert.Throws<ArgumentException>(() => repository.Remove("../x", "1.0.0"));
        Assert.Empty(repository.List().Packages);
    }

    // ── Installing from the repository ───────────────────────────────────────

    private PluginLoaderOptions LoaderOptions(params string[] sources) => new()
    {
        CacheDirectory = Path.Combine(_root, "cache"),
        InstallDirectory = Path.Combine(_root, "install"),
        Sources = sources,
        AllowNetwork = true,
        AllowRemote = false
    };

    [Fact]
    public async Task A_plugin_installs_offline_from_the_repository_and_the_remote_sources_are_not_read()
    {
        string assembly = NewAssembly("Repo");
        string dll = Path.Combine(_root, "build", assembly + ".dll");
        Compile(dll, assembly, Step("RepoStep", "RepoTag"));
        LocalNupkgRepository repository = new(Repo);
        await repository.AddFileAsync(Pack("Repo.Steps", "1.0.0", new() { [$"lib/net10.0/{assembly}.dll"] = File.ReadAllBytes(dll) }));
        StepRegistry registry = new();

        // An unreachable https source next to the repository: with AllowRemote off it is never opened.
        LoadReport report = await new PluginLoader(LoaderOptions(Repo, "https://127.0.0.1:9/v3/index.json"), registry, AlwaysTrust.Instance).InstallAsync("Repo.Steps", "1.0.0");

        Assert.True(report.Recorded, string.Join("; ", report.Problems.Select(p => p.Message)));
        Assert.True(registry.TryGetType("RepoTag", out _));
    }

    [Fact]
    public async Task An_unreachable_remote_source_does_not_hide_the_repository()
    {
        string assembly = NewAssembly("Reach");
        string dll = Path.Combine(_root, "build", assembly + ".dll");
        Compile(dll, assembly, Step("ReachStep", "ReachTag"));
        LocalNupkgRepository repository = new(Repo);
        await repository.AddFileAsync(Pack("Reach.Steps", "1.0.0", new() { [$"lib/net10.0/{assembly}.dll"] = File.ReadAllBytes(dll) }));
        StepRegistry registry = new();

        // The https source is configured and allowed (AllowRemote) but cannot be opened: the repository, listed first, still answers.
        PluginLoaderOptions options = new()
        {
            CacheDirectory = Path.Combine(_root, "cache"),
            InstallDirectory = Path.Combine(_root, "install"),
            Sources = [Repo, "https://127.0.0.1:9/v3/index.json"],
            AllowNetwork = true,
            AllowRemote = true
        };
        LoadReport report = await new PluginLoader(options, registry, AlwaysTrust.Instance).InstallAsync("Reach.Steps", "1.0.0");

        Assert.True(report.Recorded, string.Join("; ", report.Problems.Select(p => p.Message)));
        Assert.True(registry.TryGetType("ReachTag", out _));
    }

    [Fact]
    public async Task A_dependency_that_no_source_has_fails_naming_it()
    {
        LocalNupkgRepository repository = new(Repo);
        await repository.AddFileAsync(Pack("Needy.Steps", "1.0.0", dependencies: ("Missing.Dep", "[1.0.0]")));

        LoadReport report = await new PluginLoader(LoaderOptions(Repo), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Needy.Steps", "1.0.0");

        LoadProblem problem = Assert.Single(report.Problems);
        Assert.Equal("nuget-not-found", problem.Code);
        Assert.Contains("Missing.Dep", problem.Message, StringComparison.Ordinal);
        Assert.False(report.Recorded);
    }

    [Fact]
    public async Task Only_remote_sources_with_remote_off_is_offline()
    {
        NugetException ex = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().RestoreAsync(
            [new NugetRequirement("Foo", "1.0.0")],
            new NugetOptions { CacheDirectory = Path.Combine(_root, "cache"), Sources = ["https://127.0.0.1:9/v3/index.json"], AllowRemote = false }));

        Assert.Equal("offline", ex.Code);
        Assert.Contains("remote sources are off", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_source_is_not_a_local_folder_when_remote_sources_are_off()
    {
        NugetException ex = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().RestoreAsync(
            [new NugetRequirement("Foo", "1.0.0")],
            new NugetOptions { CacheDirectory = Path.Combine(_root, "cache"), Sources = [@"\\127.0.0.1\no-share", @"\\?\C:\nope"], AllowNetwork = true, AllowRemote = false }));

        Assert.Equal("offline", ex.Code);
        Assert.Contains("remote sources are off", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_availability_of_dependencies_follows_a_file_of_the_repository_that_changed()
    {
        LocalNupkgRepository repository = new(Repo);
        Directory.CreateDirectory(Repo);
        // A file that is not a package, with the name the dependency will have: it satisfies nothing (and is remembered as such).
        string slot = Path.Combine(Repo, LocalNupkgRepository.FileNameOf("Late.Base", "1.0.0"));
        WriteText(slot, "not a zip");
        NupkgAddResult first = await repository.AddFileAsync(Pack("Late.Tools", "1.0.0", dependencies: ("Late.Base", "[1.0.0]")));
        Assert.Null(Assert.Single(first.Package.Dependencies).AvailableIn);

        // Replaced by the real package (another size and write time): seen again, not taken from what was remembered.
        File.Copy(Pack("Late.Base", "1.0.0"), slot, overwrite: true);
        NupkgAddResult again = await repository.AddFileAsync(Path.Combine(Repo, "late.tools.1.0.0.nupkg"));
        Assert.True(again.AlreadyPresent);
        Assert.Equal("local-repository", Assert.Single(again.Package.Dependencies).AvailableIn);
    }

    // ── Decompression limits of the resolver ─────────────────────────────────

    [Fact]
    public async Task An_entry_that_extracts_past_its_limit_stops_the_extraction_and_leaves_no_content()
    {
        NugetTestFeed feed = new(Path.Combine(_root, "bombs"));
        feed.Pack("Bomb.Lib", "1.0.0", new() { ["lib/net10.0/Bomb.dll"] = new byte[4 * 1024 * 1024] });
        string cache = Path.Combine(_root, "cache");
        NugetOptions options = new() { CacheDirectory = cache, Sources = [feed.Folder], MaxExtractedEntryBytes = 1024 * 1024 };

        NugetException entry = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().RestoreAsync([new NugetRequirement("Bomb.Lib", "1.0.0")], options));
        Assert.Equal("too_large", entry.Code);
        Assert.False(Directory.Exists(Path.Combine(cache, "packages", "bomb.lib", "1.0.0", "content")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(cache, "packages", "bomb.lib", "1.0.0")));

        NugetException total = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().RestoreAsync(
            [new NugetRequirement("Bomb.Lib", "1.0.0")], options with { MaxExtractedEntryBytes = 8 * 1024 * 1024, MaxExtractedPackageBytes = 2 * 1024 * 1024 }));
        Assert.Equal("too_large", total.Code);

        NugetException entries = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().RestoreAsync(
            [new NugetRequirement("Bomb.Lib", "1.0.0")], options with { MaxExtractedEntryBytes = 8 * 1024 * 1024, MaxPackageEntries = 2 }));
        Assert.Equal("too_large", entries.Code);
    }
}
