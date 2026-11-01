using AITaskAgent.NugetLoader;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using System.Text.Json.Nodes;
using WorkflowDesigner.Testing;

namespace AITaskAgent.NugetLoader.Tests;

/// <summary>
/// The NuGet loader (architecture §16): declarations, the one resolver against a local folder source made of <c>.nupkg</c> files the tests
/// build themselves (no network), the private cache, the shared set and native libraries.
/// </summary>
public sealed class NugetLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfd-nuget-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless.
        }
    }

    private string Feed => Path.Combine(_root, "feed");

    private NugetOptions Options(params string[] sources) => new()
    {
        CacheDirectory = Path.Combine(_root, "cache"),
        Sources = sources.Length == 0 ? [Feed] : sources
    };

    private NugetTestFeed? _packs;

    private NugetTestFeed Packs => _packs ??= new NugetTestFeed(Feed);

    private static byte[] GreeterDll => NugetTestFeed.GreeterDll;

    private void Pack(string id, string version, Dictionary<string, byte[]>? files = null, params (string Framework, string Dependency, string Range)[] dependencies)
        => Packs.Pack(id, version, files, dependencies);

    private static NugetRequirement Need(string id, string version) => new(id, version);

    // ── Declarations ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Foo", "1.2.3", null)]
    [InlineData("Foo.Bar-baz", "[1.0,2.0)", null)]
    [InlineData("Foo", "[1.2.3]", null)]
    public void Valid_declarations_are_accepted(string id, string version, string? problem)
        => Assert.Equal(problem, new NugetRequirement(id, version).Problem());

    [Theory]
    [InlineData("Foo", "1.*", "floating")]
    [InlineData("Foo", "*", "floating")]
    [InlineData("Foo", "banana", "not a NuGet version")]
    [InlineData("Foo", "", "no version")]
    [InlineData("../x", "1.0.0", "not a valid NuGet package id")]
    [InlineData("", "1.0.0", "not a valid NuGet package id")]
    public void Invalid_declarations_say_what_is_wrong(string id, string version, string expected)
        => Assert.Contains(expected, new NugetRequirement(id, version).Problem(), StringComparison.Ordinal);

    [Fact]
    public void A_duplicated_id_is_reported_ignoring_case()
        => Assert.Contains("more than once", Assert.Single(NugetRequirement.Problems([Need("Foo", "1.0.0"), Need("foo", "2.0.0")])), StringComparison.Ordinal);

    [Fact]
    public void The_requirements_hash_ignores_order_and_case()
        => Assert.Equal(
            NugetRequirement.HashOf([Need("A", "1.0.0"), Need("B", "2.0.0")]),
            NugetRequirement.HashOf([Need("b", "2.0.0"), Need("a", "1.0.0")]));

    [Fact]
    public void An_extension_json_nuget_section_is_parsed_with_the_same_rules()
    {
        JsonObject json = JsonNode.Parse("""{ "name": "x", "nuget": [ { "id": "Foo", "version": "1.0.0" }, { "id": "Bar", "version": "[2.0,3.0)" } ] }""")!.AsObject();
        Assert.Equal([Need("Foo", "1.0.0"), Need("Bar", "[2.0,3.0)")], ExtensionNuget.Parse(json));
        Assert.Empty(ExtensionNuget.Parse(JsonNode.Parse("""{ "name": "x" }""")!.AsObject()));
        Assert.Throws<FormatException>(() => ExtensionNuget.Parse(JsonNode.Parse("""{ "nuget": [ { "id": "Foo", "version": "1.*" } ] }""")!.AsObject()));
        Assert.Throws<FormatException>(() => ExtensionNuget.Parse(JsonNode.Parse("""{ "nuget": "Foo" }""")!.AsObject()));
        Assert.Throws<FormatException>(() => ExtensionNuget.Parse(JsonNode.Parse("""{ "nuget": [ { "id": "Foo" } ] }""")!.AsObject()));
    }

    // ── Resolver ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_resolves_dependencies_locks_hashes_and_fills_the_private_cache()
    {
        Pack("Dep", "1.0.0", new() { ["lib/netstandard2.0/Dep.dll"] = GreeterDll });
        Pack("Main", "2.1.0", new() { ["lib/net10.0/Main.dll"] = GreeterDll }, ("net10.0", "Dep", "1.0.0"));

        NugetRestoreResult result = await new NugetResolver().RestoreAsync([Need("Main", "2.0.0")], Options());

        Assert.Equal(["Dep", "Main"], result.Lock.Packages.Select(p => p.Id));
        NugetLockPackage main = result.Lock.Packages.Single(p => p.Id == "Main");
        Assert.Equal("2.1.0", main.Version);
        Assert.True(main.Direct);
        Assert.False(result.Lock.Packages.Single(p => p.Id == "Dep").Direct);
        Assert.StartsWith("sha512-", main.Hash, StringComparison.Ordinal);
        Assert.Equal(["Dep/1.0.0"], main.Dependencies);
        Assert.True(result.Lock.Matches([Need("Main", "2.0.0")]));
        Assert.False(result.Lock.Matches([Need("Main", "2.1.0")]));
        Assert.Equal(["Dep.dll", "Main.dll"], result.Environment.References.Select(Path.GetFileName).Order());
        Assert.All(result.Environment.References, r => Assert.StartsWith(Path.Combine(_root, "cache"), r, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(_root, "cache", "packages", "main", "2.1.0", "main.2.1.0.nupkg")));
    }

    [Fact]
    public async Task The_lowest_version_that_satisfies_the_range_is_chosen()
    {
        foreach (string v in new[] { "1.0.0", "1.2.0", "2.0.0" })
        {
            Pack("Lib", v, new() { ["lib/net10.0/Lib.dll"] = GreeterDll });
        }

        Assert.Equal("1.2.0", (await new NugetResolver().RestoreAsync([Need("Lib", "1.1.0")], Options())).Lock.Packages.Single().Version);
        Assert.Equal("1.0.0", (await new NugetResolver().RestoreAsync([Need("Lib", "[1.0.0,2.0.0)")], Options())).Lock.Packages.Single().Version);
        Assert.Equal("2.0.0", (await new NugetResolver().RestoreAsync([Need("Lib", "[2.0.0]")], Options())).Lock.Packages.Single().Version);
    }

    [Fact]
    public async Task Only_the_dependency_group_of_the_nearest_target_framework_is_read()
    {
        // The net462 group names a package that exists nowhere: reading every group (as BRMS did) would fail.
        Pack("Dep", "1.0.0", new() { ["lib/net10.0/Dep.dll"] = GreeterDll });
        Pack("Main", "1.0.0", new() { ["lib/net10.0/Main.dll"] = GreeterDll }, ("net10.0", "Dep", "1.0.0"), ("net462", "GhostOnlyForNetFramework", "9.0.0"));

        NugetLock lockFile = (await new NugetResolver().RestoreAsync([Need("Main", "1.0.0")], Options())).Lock;

        Assert.Equal(["Dep", "Main"], lockFile.Packages.Select(p => p.Id));
    }

    [Fact]
    public async Task The_nearest_library_folder_is_the_one_materialized()
    {
        Pack("Multi", "1.0.0", new()
        {
            ["lib/net10.0/Multi.dll"] = GreeterDll,
            ["lib/net462/Multi.dll"] = [1, 2, 3],
            ["lib/netstandard2.0/Other.dll"] = [4, 5, 6]
        });

        NugetEnvironment environment = (await new NugetResolver().RestoreAsync([Need("Multi", "1.0.0")], Options())).Environment;

        string dll = Assert.Single(environment.References);
        Assert.Equal("Multi.dll", Path.GetFileName(dll));
        Assert.Equal(GreeterDll, File.ReadAllBytes(dll));
    }

    [Fact]
    public async Task On_a_diamond_the_highest_minimum_wins_and_incompatible_ranges_are_an_error()
    {
        foreach (string v in new[] { "1.0.0", "1.5.0", "2.0.0" })
        {
            Pack("Base", v, new() { ["lib/net10.0/Base.dll"] = GreeterDll });
        }

        Pack("Left", "1.0.0", new() { ["lib/net10.0/Left.dll"] = GreeterDll }, ("net10.0", "Base", "1.0.0"));
        Pack("Right", "1.0.0", new() { ["lib/net10.0/Right.dll"] = GreeterDll }, ("net10.0", "Base", "1.5.0"));
        Pack("Strict", "1.0.0", new() { ["lib/net10.0/Strict.dll"] = GreeterDll }, ("net10.0", "Base", "[2.0.0]"));

        NugetLock lockFile = (await new NugetResolver().RestoreAsync([Need("Left", "1.0.0"), Need("Right", "1.0.0")], Options())).Lock;
        Assert.Equal("1.5.0", lockFile.Packages.Single(p => p.Id == "Base").Version);

        NugetException conflict = await Assert.ThrowsAsync<NugetException>(
            () => new NugetResolver().RestoreAsync([Need("Right", "1.0.0"), Need("Strict", "1.0.0"), Need("Base", "[1.0.0,1.9.0]")], Options()));
        Assert.Equal("conflict", conflict.Code);
        Assert.Contains("Base", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_missing_from_the_sources_names_the_id_and_the_sources()
    {
        Directory.CreateDirectory(Feed);

        NugetException error = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().RestoreAsync([Need("Nope", "1.0.0")], Options()));

        Assert.Equal("not_found", error.Code);
        Assert.Contains("'Nope'", error.Message, StringComparison.Ordinal);
        Assert.Contains(Feed, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_sources_restoring_says_so()
    {
        NugetException error = await Assert.ThrowsAsync<NugetException>(
            () => new NugetResolver().RestoreAsync([Need("Foo", "1.0.0")], new NugetOptions { CacheDirectory = Path.Combine(_root, "cache"), Sources = [] }));

        Assert.Equal("offline", error.Code);
        Assert.Contains("NuGetSources", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remote_source_is_not_used_when_the_network_is_off()
    {
        NugetOptions options = new() { CacheDirectory = Path.Combine(_root, "cache"), Sources = ["https://nuget.invalid/v3/index.json"], AllowNetwork = false };

        NugetException error = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().RestoreAsync([Need("Foo", "1.0.0")], options));

        Assert.Equal("offline", error.Code);
    }

    // ── Materialization ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_lock_is_materialized_offline_from_the_cache_and_a_missing_cache_is_not_restored()
    {
        Pack("Lib", "1.0.0", new() { ["lib/net10.0/Lib.dll"] = GreeterDll });
        NugetLock lockFile = (await new NugetResolver().RestoreAsync([Need("Lib", "1.0.0")], Options())).Lock;
        NugetOptions offline = new() { CacheDirectory = Path.Combine(_root, "cache"), Sources = [], AllowNetwork = false };

        NugetEnvironment environment = await new NugetResolver().MaterializeAsync(lockFile, offline);
        Assert.Equal("Lib.dll", Path.GetFileName(Assert.Single(environment.References)));

        Directory.Delete(Path.Combine(_root, "cache"), recursive: true);
        NugetException error = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().MaterializeAsync(lockFile, offline));
        Assert.Equal("not_restored", error.Code);
    }

    [Fact]
    public async Task A_package_missing_from_the_cache_is_downloaded_by_hash_when_a_source_has_it_and_a_different_file_is_refused()
    {
        Pack("Lib", "1.0.0", new() { ["lib/net10.0/Lib.dll"] = GreeterDll });
        NugetLock lockFile = (await new NugetResolver().RestoreAsync([Need("Lib", "1.0.0")], Options())).Lock;
        Directory.Delete(Path.Combine(_root, "cache"), recursive: true);

        NugetEnvironment again = await new NugetResolver().MaterializeAsync(lockFile, Options());
        Assert.Single(again.References);

        // The feed now serves another file under the same id and version: the lock refuses it.
        Directory.Delete(Path.Combine(_root, "cache"), recursive: true);
        Pack("Lib", "1.0.0", new() { ["lib/net10.0/Lib.dll"] = [9, 9, 9] });
        NugetException error = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().MaterializeAsync(lockFile, Options()));
        Assert.Equal("hash_mismatch", error.Code);
        Assert.False(File.Exists(Path.Combine(_root, "cache", "packages", "lib", "1.0.0", "lib.1.0.0.nupkg")), "the refused file is not kept");
    }

    [Fact]
    public async Task A_cached_file_that_no_longer_matches_the_lock_is_replaced_not_trusted()
    {
        Pack("Lib", "1.0.0", new() { ["lib/net10.0/Lib.dll"] = GreeterDll });
        NugetLock lockFile = (await new NugetResolver().RestoreAsync([Need("Lib", "1.0.0")], Options())).Lock;
        string cached = Path.Combine(_root, "cache", "packages", "lib", "1.0.0", "lib.1.0.0.nupkg");
        File.WriteAllBytes(cached, [1, 2, 3]);

        NugetException offline = await Assert.ThrowsAsync<NugetException>(
            () => new NugetResolver().MaterializeAsync(lockFile, new NugetOptions { CacheDirectory = Path.Combine(_root, "cache"), Sources = [] }));
        Assert.Equal("not_restored", offline.Code);

        // With the source back, the right file comes again.
        NugetEnvironment repaired = await new NugetResolver().MaterializeAsync(lockFile, Options());
        Assert.Single(repaired.References);
    }

    // ── Shared set ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_host_supplies_the_shared_set_and_a_newer_major_is_reported()
    {
        Pack("Wants", "1.0.0", new()
        {
            ["lib/net10.0/Wants.dll"] = GreeterDll,
            ["lib/net10.0/Newtonsoft.Json.dll"] = [7, 7, 7]
        }, ("net10.0", "Newtonsoft.Json", "99.0.0"), ("net10.0", "Microsoft.Extensions.Logging.Abstractions", "6.0.0"), ("net10.0", "System.Memory", "4.5.5"));

        NugetRestoreResult result = await new NugetResolver().RestoreAsync([Need("Wants", "1.0.0")], Options());

        Assert.Equal(["Wants"], result.Lock.Packages.Select(p => p.Id));
        Assert.Contains(result.Lock.Provided, p => p.Id == "Newtonsoft.Json");
        Assert.Contains(result.Lock.Provided, p => p.Id == "Microsoft.Extensions.Logging.Abstractions");
        Assert.Contains(result.Notes, n => n.Level == NugetNoteLevel.Warning && n.Message.Contains("Newtonsoft.Json", StringComparison.Ordinal) && n.Message.Contains("newer major", StringComparison.Ordinal));
        Assert.Equal(["Wants.dll"], result.Environment.References.Select(Path.GetFileName));
        Assert.Contains(result.Environment.Notes, n => n.Message.Contains("assembly 'Newtonsoft.Json' is supplied by the host", StringComparison.Ordinal));
    }

    [Fact]
    public void The_shared_set_covers_the_contract_the_engine_and_the_unit_share()
    {
        foreach (string name in new[] { "AITaskAgent.Abstractions", "Newtonsoft.Json", "NJsonSchema", "NJsonSchema.CodeGeneration", "Microsoft.Extensions.Logging", "System.Text.Json", "NETStandard.Library" })
        {
            Assert.True(SharedAssemblySet.IsShared(name), name);
        }

        foreach (string name in new[] { "Humanizer.Core", "Serilog", "Microsoft.CodeAnalysis" })
        {
            Assert.False(SharedAssemblySet.IsShared(name), name);
        }

        Assert.True(SharedAssemblySet.HostProvides("Newtonsoft.Json"));
        Assert.False(SharedAssemblySet.HostProvides("Humanizer.Core"));
    }

    [Fact]
    public void The_host_can_add_to_the_shared_set_and_replace_its_additions()
    {
        try
        {
            SharedAssemblySet.Configure(["Zz.Acme.*", " Zz.Prefix. ", "Zz.Exact", "", "  ", "*"]);

            Assert.True(SharedAssemblySet.IsShared("Zz.Acme.Contracts"));
            Assert.True(SharedAssemblySet.IsShared("zz.prefix.Anything"));
            Assert.True(SharedAssemblySet.IsShared("zz.exact"));
            Assert.False(SharedAssemblySet.IsShared("Zz.ExactNot"));
            Assert.False(SharedAssemblySet.IsShared("Humanizer.Core"), "a lone '*' must not turn everything into shared");
            Assert.True(SharedAssemblySet.IsShared("AITaskAgent.Core"), "the built-in set stays");

            SharedAssemblySet.Configure(null);
            Assert.False(SharedAssemblySet.IsShared("Zz.Acme.Contracts"));
            Assert.True(SharedAssemblySet.IsShared("Newtonsoft.Json"));
        }
        finally
        {
            SharedAssemblySet.Configure(null);
        }
    }

    [Fact]
    public void Host_wins_only_for_shared_assemblies_the_host_really_has()
    {
        Assert.True(SharedAssemblySet.HostWins("Newtonsoft.Json"));
        Assert.False(SharedAssemblySet.HostWins("Humanizer.Core"));
        Assert.False(SharedAssemblySet.HostWins("System.Nothing.Like.This"), "shared by name, but the host has nothing to defer to");
    }

    // ── Native libraries ─────────────────────────────────────────────────────

    [Fact]
    public async Task Native_libraries_of_the_runtime_identifier_get_their_own_folder()
    {
        Pack("Native", "1.0.0", new()
        {
            ["lib/net10.0/Native.dll"] = GreeterDll,
            ["runtimes/win-x64/native/thing.dll"] = [1],
            ["runtimes/linux-x64/native/libthing.so"] = [2],
            ["runtimes/osx-arm64/native/libthing.dylib"] = [3]
        });

        NugetEnvironment windows = (await new NugetResolver().RestoreAsync([Need("Native", "1.0.0")], Options() with { RuntimeIdentifier = "win-x64" })).Environment;
        string folder = Assert.Single(windows.NativeDirectories);
        Assert.Equal(["thing.dll"], Directory.GetFiles(folder).Select(Path.GetFileName));

        Directory.Delete(Path.Combine(_root, "cache"), recursive: true);
        NugetEnvironment linux = (await new NugetResolver().RestoreAsync([Need("Native", "1.0.0")], Options() with { RuntimeIdentifier = "linux-x64" })).Environment;
        Assert.Equal(["libthing.so"], Directory.GetFiles(Assert.Single(linux.NativeDirectories)).Select(Path.GetFileName));
    }
}
