using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.NugetLoader;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using WorkflowDesigner.Testing;

namespace AITaskAgent.NugetLoader.Tests;

/// <summary>The plugins of the loader tests load assemblies into the process: they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginCollection
{
    public const string Name = "Plugins";
}

/// <summary>A step of the host, for tag clashes.</summary>
[StepTag("ClashTag")]
public sealed class HostClashStep() : StepBase("ClashTag", typeof(IStepResult), typeof(JsonStepResult))
{
    protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
        => Task.FromResult<IStepResult>(new JsonStepResult(this, new JValue("host")));
}

/// <summary>
/// <see cref="PluginLoader"/> with NuGet plugins: step packages built by the tests (Roslyn, against the host's AITaskAgent.Abstractions/Core), served by a local
/// folder source (no network), installed, trusted or not, reloaded by a «restarted» loader and removed.
/// </summary>
[Collection(PluginCollection.Name)]
public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfd-step-packages", Guid.NewGuid().ToString("N"));
    private readonly NugetTestFeed _feed;

    public PluginLoaderTests() => _feed = new NugetTestFeed(Path.Combine(_root, "feed"));

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

    private string Install => Path.Combine(_root, "install");

    private string Cache => Path.Combine(_root, "cache");

    private PluginLoaderOptions Options(bool network = true, string? sourcesOptionName = null) => new()
    {
        Sources = network ? [_feed.Folder] : [],
        AllowNetwork = network,
        CacheDirectory = Cache,
        InstallDirectory = Install,
        SourcesOptionName = sourcesOptionName
    };

    private const string Usings = """
        using System.Threading;
        using System.Threading.Tasks;
        using AITaskAgent.Core.Abstractions;
        using AITaskAgent.Core.Attributes;
        using AITaskAgent.Core.Models;
        using AITaskAgent.Core.StepResults;
        using AITaskAgent.Core.Steps;
        using Newtonsoft.Json.Linq;
        """;

    /// <summary>A step class: <c>[StepTag(tag)]</c> when <paramref name="tag"/> is given.</summary>
    private static string Step(string className, string? tag = null, string modifiers = "public sealed", string baseClass = "StepBase") => $$"""
        {{(tag != null ? $"[StepTag(\"{tag}\")]" : "")}}
        {{modifiers}} class {{className}} : {{baseClass}}
        {
            public {{className.Split('<')[0]}}() : base("{{className.Split('<')[0]}}", typeof(IStepResult), typeof(JsonStepResult)) { }
            protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? last, CancellationToken ct)
                => Task.FromResult<IStepResult>(new JsonStepResult(this, new JValue("{{className}}")));
        }
        """;

    /// <summary>Compiles <paramref name="body"/> into a uniquely named assembly and packs it as <c>id.version</c>; returns the assembly name.</summary>
    private string PackSteps(string id, string version, string body, params string[] extra)
    {
        string assembly = "Steps" + Guid.NewGuid().ToString("N");
        string dll = Path.Combine(_root, "build", assembly + ".dll");
        PluginTestFixture.CompileTo(dll, assembly, Usings + "\nnamespace " + assembly + ";\n" + body, extra);
        _feed.Pack(id, version, new() { [$"lib/net10.0/{assembly}.dll"] = File.ReadAllBytes(dll) });
        return assembly;
    }

    /// <summary>As <see cref="PackSteps"/>, with more files in the package and NuGet dependencies (net10.0).</summary>
    private string PackStepsWith(string id, string version, string body, Dictionary<string, byte[]> files, params (string Dependency, string Range)[] dependencies)
    {
        string assembly = "Steps" + Guid.NewGuid().ToString("N");
        string dll = Path.Combine(_root, "build", assembly + ".dll");
        PluginTestFixture.CompileTo(dll, assembly, Usings + "\nnamespace " + assembly + ";\n" + body);
        files[$"lib/net10.0/{assembly}.dll"] = File.ReadAllBytes(dll);
        _feed.Pack(id, version, files, [.. dependencies.Select(d => ("net10.0", d.Dependency, d.Range))]);
        return assembly;
    }

    private string StoreFile => Path.Combine(Install, "plugins.json");

    private string LockOf(string id, string version) => Path.Combine(Install, "plugin-locks", id.ToLowerInvariant(), version, "nuget.lock.json");

    private static bool IsLoaded(string assembly)
        => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == assembly);

    /// <summary>Records every request; approves according to <see cref="Answer"/>; optionally waits, to make two installs overlap.</summary>
    private sealed class RecordingTrust(bool answer = true, int delayMs = 0) : ITrustPolicy
    {
        private int _inside;

        public ConcurrentQueue<TrustRequest> Requests { get; } = new();

        public bool Answer { get; set; } = answer;

        public int MaxConcurrent { get; private set; }

        public async ValueTask<bool> ApproveAsync(TrustRequest request, CancellationToken cancellationToken)
        {
            int inside = Interlocked.Increment(ref _inside);
            MaxConcurrent = Math.Max(MaxConcurrent, inside);
            Requests.Enqueue(request);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }

            Interlocked.Decrement(ref _inside);
            return Answer;
        }
    }

    // ── Install ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tagged_and_untagged_steps_are_registered_and_the_rest_is_skipped_with_a_reason()
    {
        string assembly = PackSteps("Acme.Steps", "1.0.0",
            Step("TaggedStep", "AcmeTagged") + Step("PlainStep")
            + Step("AbstractStep", modifiers: "public abstract") + Step("GenericStep<T>", modifiers: "public sealed")
            + Step("HiddenStep", modifiers: "internal sealed")
            + "public interface IAcmeStep : IStep { }\npublic sealed class NotAStep { }\n");
        StepRegistry registry = new();
        List<LoadReport> events = [];
        PluginLoader loader = new(Options(), registry, AlwaysTrust.Instance);
        loader.PluginsChanged += (_, report) => events.Add(report);

        LoadReport result = await loader.InstallAsync("Acme.Steps", "1.0.0");

        Assert.True(result.Approved);
        Assert.Empty(result.Problems);
        // The description of the nuspec (the test feed writes the id as description) reaches the loaded plugin.
        Assert.Equal("Acme.Steps", result.Plugin!.Description);
        // A step without [StepTag] is registered under its full name (namespace + class), never dropped.
        Assert.Equal([("AcmeTagged", true), (assembly + ".PlainStep", false)], result.Registered.Select(r => (r.Tag, r.Tagged)).OrderBy(r => r.Tag, StringComparer.Ordinal));
        Assert.Equal(
            [("HiddenStep", "not-public")],
            result.Skipped.Select(s => (s.Type.Split('.')[^1], s.Reason)).OrderBy(s => s.Item1, StringComparer.Ordinal));
        Assert.True(registry.TryGetType("AcmeTagged", out Type? tagged));
        Assert.Equal("plugin:nuget:Acme.Steps/1.0.0", AssemblyLoadContext.GetLoadContext(tagged.Assembly)?.Name);

        // The host's StepBase and IStep: the registry creates the step as any other.
        IStep step = registry.Create(assembly + ".PlainStep");
        Assert.Equal("PlainStep", step.Name);

        InstalledPlugin installed = Assert.Single(loader.Installed);
        Assert.Equal(("Acme.Steps", "1.0.0"), (installed.Id, installed.Version));
        Assert.Equal(installed.Sha512, installed.ApprovedSha512);
        Assert.Equal(["AcmeTagged", assembly + ".PlainStep"], installed.Steps.Order(StringComparer.Ordinal));
        Assert.Equal(PluginOrigin.Nuget, installed.Origin);
        Assert.Equal(new PluginIdentity(PluginOrigin.Nuget, "Acme.Steps", "1.0.0"), PluginIdentity.Of(tagged));
        LoadedPlugin plugin = Assert.Single(loader.Loaded);
        Assert.Same(plugin, result.Plugin);
        Assert.True(File.Exists(Path.Combine(Install, "plugin-locks", "acme.steps", "1.0.0", "nuget.lock.json")));
        Assert.Same(result, Assert.Single(events));
    }

    [Fact]
    public async Task Without_approval_nothing_is_loaded_registered_or_recorded()
    {
        string assembly = PackSteps("Denied.Steps", "1.0.0", Step("DeniedStep", "DeniedTag"));
        StepRegistry registry = new();
        RecordingTrust trust = new(answer: false);
        int events = 0;
        PluginLoader loader = new(Options(), registry, trust);
        loader.PluginsChanged += (_, _) => events++;
        string[] contexts = [.. AssemblyLoadContext.All.Select(c => c.Name ?? "")];

        LoadReport result = await loader.InstallAsync("Denied.Steps", "1.0.0");

        Assert.False(result.Approved);
        Assert.Equal("not-approved", Assert.Single(result.Problems).Code);
        Assert.Empty(result.Registered);
        Assert.Empty(registry.GetRegisteredTypes());
        Assert.False(IsLoaded(assembly));
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name?.StartsWith("plugin:nuget:Denied.Steps/", StringComparison.Ordinal) == true);
        Assert.Empty(loader.Installed);
        Assert.Equal(0, events);

        TrustRequest request = Assert.Single(trust.Requests);
        Assert.Equal(("Denied.Steps", "1.0.0", TrustRequestKind.Install), (request.Id, request.Version, request.Kind));
        Assert.StartsWith("sha512-", request.Sha512, StringComparison.Ordinal);
        Assert.Equal([assembly + ".dll"], request.Assemblies);
        Assert.Empty(request.Dependencies);

        // What it brings was read from metadata and shown before approval, and no load context was created for it (or for anything).
        Assert.Equal("DeniedTag", Assert.Single(request.Contributions!.Steps).Tag);
        Assert.Equal("DeniedTag", Assert.Single(result.Contributions!.Steps).Tag);
        Assert.Equal(contexts, AssemblyLoadContext.All.Select(c => c.Name ?? ""));
    }

    [Fact]
    public async Task A_tag_owned_by_another_type_stops_the_whole_plugin_before_trust_and_names_both_owners()
    {
        string assembly = PackSteps("Clash.Steps", "1.0.0", Step("ClashingStep", "ClashTag") + Step("FineStep", "FineTag"));
        StepRegistry registry = new();
        registry.Add(typeof(HostClashStep));
        RecordingTrust trust = new();
        PluginLoader loader = new(Options(), registry, trust);

        LoadReport result = await loader.InstallAsync("Clash.Steps", "1.0.0");

        // All or nothing: not even the step without a clash is registered, nobody is asked, nothing is loaded or recorded.
        LoadProblem problem = Assert.Single(result.Problems);
        Assert.Equal("collision", problem.Code);
        Assert.Contains("plugin 'Clash.Steps' 1.0.0 (nuget)", problem.Message, StringComparison.Ordinal);
        Assert.Contains("the host", problem.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(HostClashStep).FullName!, problem.Message, StringComparison.Ordinal);
        Assert.Empty(result.Registered);
        Assert.False(result.Recorded);
        Assert.False(registry.TryGetType("FineTag", out _));
        Assert.Empty(trust.Requests);
        Assert.False(IsLoaded(assembly));
        Assert.Empty(loader.Installed);
        Assert.True(registry.TryGetType("ClashTag", out Type? owner));
        Assert.Equal(typeof(HostClashStep), owner);
    }

    [Fact]
    public async Task A_tag_of_another_plugin_stops_the_second_plugin_and_the_message_names_both_plugins()
    {
        PackSteps("First.Owner", "1.0.0", Step("OwnerStep", "SharedTag"));
        PackSteps("Second.Owner", "1.0.0", Step("IntruderStep", "SharedTag") + Step("OtherStep", "OtherTag"));
        StepRegistry registry = new();
        PluginLoader loader = new(Options(), registry, AlwaysTrust.Instance);
        Assert.True((await loader.InstallAsync("First.Owner", "1.0.0")).Recorded);

        LoadReport second = await loader.InstallAsync("Second.Owner", "1.0.0");

        LoadProblem problem = Assert.Single(second.Problems);
        Assert.Equal("collision", problem.Code);
        Assert.Contains("plugin 'Second.Owner' 1.0.0 (nuget)", problem.Message, StringComparison.Ordinal);
        Assert.Contains("plugin 'First.Owner' 1.0.0 (nuget)", problem.Message, StringComparison.Ordinal);
        Assert.False(registry.TryGetType("OtherTag", out _));
        Assert.Equal(["First.Owner"], loader.Installed.Select(p => p.Id));
    }

    [Fact]
    public async Task Installing_the_same_package_again_is_idempotent()
    {
        PackSteps("Twice.Steps", "1.0.0", Step("TwiceStep", "TwiceTag"));
        RecordingTrust trust = new();
        int events = 0;
        PluginLoader loader = new(Options(), new StepRegistry(), trust);
        loader.PluginsChanged += (_, _) => events++;

        LoadReport first = await loader.InstallAsync("Twice.Steps", "1.0.0");
        LoadReport second = await loader.InstallAsync("Twice.Steps", "1.0.0");

        Assert.True(second.Approved);
        Assert.Empty(second.Problems);
        Assert.Equal(first.Registered, second.Registered);
        Assert.Contains(second.Notes, n => n.Contains("already installed", StringComparison.Ordinal));
        Assert.Single(trust.Requests);
        Assert.Equal(1, events);
        Assert.Single(loader.Installed);
    }

    [Fact]
    public async Task Another_version_of_a_loaded_package_is_recorded_for_the_next_start()
    {
        PackSteps("Versioned.Steps", "1.0.0", Step("VersionedStep", "VersionedTag"));
        PackSteps("Versioned.Steps", "2.0.0", Step("VersionedStep", "VersionedTag"));
        StepRegistry registry = new();
        RecordingTrust trust = new();
        PluginLoader loader = new(Options(), registry, trust);
        await loader.InstallAsync("Versioned.Steps", "[1.0.0]");
        registry.TryGetType("VersionedTag", out Type? v1);

        LoadReport update = await loader.InstallAsync("Versioned.Steps", "[2.0.0]");

        Assert.True(update.RestartRequired);
        Assert.Contains("restart", Assert.Single(update.Problems).Message, StringComparison.Ordinal);
        Assert.Empty(update.Registered);
        Assert.Equal(TrustRequestKind.Update, trust.Requests.Last().Kind);
        Assert.Equal("2.0.0", Assert.Single(loader.Installed).Version);
        Assert.True(registry.TryGetType("VersionedTag", out Type? still));
        Assert.Same(v1, still);

        // After the restart, 2.0.0 is what loads (already approved: nobody is asked).
        StepRegistry restarted = new();
        LoadReport reloaded = Assert.Single(await new PluginLoader(Options(network: false), restarted, NeverTrust.Instance).LoadInstalledAsync());
        Assert.Empty(reloaded.Problems);
        Assert.Equal("2.0.0", reloaded.Version);
        Assert.True(restarted.TryGetType("VersionedTag", out Type? v2));
        Assert.Equal("plugin:nuget:Versioned.Steps/2.0.0", AssemblyLoadContext.GetLoadContext(v2.Assembly)?.Name);
    }

    [Fact]
    public async Task An_update_whose_tag_belongs_to_another_loaded_plugin_is_stopped_before_trust_and_the_installed_version_stays()
    {
        PackSteps("Alpha.Steps", "1.0.0", Step("AlphaStep", "AlphaTag"));
        PackSteps("Alpha.Steps", "2.0.0", Step("AlphaStep", "AlphaTag") + Step("AlphaY", "YTag"));
        PackSteps("Beta.Steps", "1.0.0", Step("BetaY", "YTag"));
        StepRegistry registry = new();
        RecordingTrust trust = new();
        PluginLoader loader = new(Options(), registry, trust);
        Assert.True((await loader.InstallAsync("Alpha.Steps", "[1.0.0]")).Recorded);
        Assert.True((await loader.InstallAsync("Beta.Steps", "1.0.0")).Recorded);
        int asked = trust.Requests.Count;

        LoadReport update = await loader.InstallAsync("Alpha.Steps", "[2.0.0]");

        // Before: approved as restart-required, and at the next start Alpha (first by id) took YTag and Beta fell as a collision.
        LoadProblem problem = Assert.Single(update.Problems);
        Assert.Equal("collision", problem.Code);
        Assert.Contains("'YTag'", problem.Message, StringComparison.Ordinal);
        Assert.Contains("plugin 'Beta.Steps' 1.0.0 (nuget)", problem.Message, StringComparison.Ordinal);
        Assert.False(update.Recorded);
        Assert.False(update.RestartRequired);
        Assert.Equal(asked, trust.Requests.Count);
        Assert.Equal(["1.0.0", "1.0.0"], loader.Installed.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Version));

        // The next start loads both, as they were.
        StepRegistry restarted = new();
        IReadOnlyList<LoadReport> reloaded = await new PluginLoader(Options(network: false), restarted, NeverTrust.Instance).LoadInstalledAsync();
        Assert.All(reloaded, r => Assert.Empty(r.Problems));
        Assert.True(restarted.TryGetType("YTag", out Type? y));
        Assert.Equal("plugin:nuget:Beta.Steps/1.0.0", AssemblyLoadContext.GetLoadContext(y.Assembly)?.Name);
    }

    [Fact]
    public async Task An_update_refused_by_the_host_is_not_recorded_and_a_tag_recorded_for_the_next_start_is_taken()
    {
        PackSteps("Gamma.Steps", "1.0.0", Step("GammaStep", "GammaTag"));
        PackSteps("Gamma.Steps", "2.0.0", Step("GammaStep", "GammaTag") + Step("GammaZ", "ZTag"));
        PackSteps("Gamma.Steps", "3.0.0", Step("GammaStep", "GammaTag"));
        PackSteps("Delta.Steps", "1.0.0", Step("DeltaZ", "ZTag"));
        StepRegistry registry = new();
        List<string> offered = [];
        PluginLoader loader = new(Options() with
        {
            // The host's rule (tool names, in the designer) refuses version 3.0.0; it sees the update's types, loaded aside.
            Accept = plugin =>
            {
                offered.Add(plugin.Id + " " + plugin.Version);
                return plugin.Version == "3.0.0" ? [new LoadProblem("collision", "Tool 'x' of plugin 'Gamma.Steps' 3.0.0 is already offered by another plugin.")] : [];
            }
        }, registry, AlwaysTrust.Instance);
        Assert.True((await loader.InstallAsync("Gamma.Steps", "[1.0.0]")).Recorded);

        LoadReport refused = await loader.InstallAsync("Gamma.Steps", "[3.0.0]");
        Assert.True(refused.Approved);
        Assert.False(refused.Recorded);
        Assert.Contains(refused.Problems, p => p.Code == "collision" && p.Message.Contains("Tool 'x'", StringComparison.Ordinal));
        Assert.Contains("Gamma.Steps 3.0.0", offered);
        Assert.Equal("1.0.0", Assert.Single(loader.Installed).Version);
        Assert.True(registry.TryGetType("GammaTag", out Type? still));
        Assert.Equal("plugin:nuget:Gamma.Steps/1.0.0", AssemblyLoadContext.GetLoadContext(still.Assembly)?.Name);

        // 2.0.0 (with ZTag) is recorded for the next start: another plugin with ZTag is a collision now, not at that start.
        LoadReport update = await loader.InstallAsync("Gamma.Steps", "[2.0.0]");
        Assert.True(update.RestartRequired);
        Assert.True(update.Recorded);
        LoadReport delta = await loader.InstallAsync("Delta.Steps", "1.0.0");
        LoadProblem problem = Assert.Single(delta.Problems);
        Assert.Equal("collision", problem.Code);
        Assert.Contains("plugin 'Gamma.Steps' 2.0.0 (nuget), recorded to load at the next start", problem.Message, StringComparison.Ordinal);
        Assert.False(registry.TryGetType("ZTag", out _));
    }

    [Fact]
    public async Task A_content_conflict_names_the_installed_lock_or_cached_copy_of_the_same_id_and_version()
    {
        PackSteps("Same.Steps", "1.0.0", Step("SameStep", "SameNugetTag"));
        PluginLoader loader = new(Options(), new StepRegistry(), AlwaysTrust.Instance);
        Assert.True((await loader.InstallAsync("Same.Steps", "1.0.0")).Recorded);
        string installed = LocalNupkgRepository.Inspect(Path.Combine(_feed.Folder, "Same.Steps.1.0.0.nupkg")).Sha512;

        // Another build with the same id and version (what a user uploads): installing it would reuse the installed lock and cached copy.
        NugetTestFeed other = new(Path.Combine(_root, "other"));
        string assembly = "Steps" + Guid.NewGuid().ToString("N");
        string dll = Path.Combine(_root, "build", assembly + ".dll");
        PluginTestFixture.CompileTo(dll, assembly, Usings + "\nnamespace " + assembly + ";\n" + Step("SameStep", "SameNugetTag"));
        other.Pack("Same.Steps", "1.0.0", new() { [$"lib/net10.0/{assembly}.dll"] = File.ReadAllBytes(dll) });
        string uploaded = LocalNupkgRepository.Inspect(Path.Combine(other.Folder, "Same.Steps.1.0.0.nupkg")).Sha512;
        Assert.NotEqual(installed, uploaded);

        Assert.Null(loader.ContentConflict("Same.Steps", "1.0.0", installed));
        LoadProblem conflict = loader.ContentConflict("same.steps", "1.0", uploaded)!;
        Assert.Equal("package-conflict", conflict.Code);
        Assert.Contains("is installed with other content", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("another version", conflict.Message, StringComparison.Ordinal);

        // Removed: the cached copy is still in the way (an install would read it, not the source).
        Assert.True((await loader.RemoveAsync("Same.Steps")).Removed);
        LoadProblem cached = loader.ContentConflict("Same.Steps", "1.0.0", uploaded)!;
        Assert.Contains("The NuGet cache already has", cached.Message, StringComparison.Ordinal);
        Assert.Null(loader.ContentConflict("Other.Steps", "1.0.0", uploaded));
    }

    [Fact]
    public async Task A_dependency_or_native_library_replaced_after_the_approval_is_not_loaded_later()
    {
        string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        _feed.Pack("Late.Lib", "1.0.0", new() { ["lib/net10.0/LateLib.dll"] = NugetTestFeed.Library("LateLib", "public static class Late { public static int One() => 1; }") });
        PackStepsWith("Late.Steps", "1.0.0", Step("LateStep", "LateTag"),
            new() { [$"runtimes/{rid}/native/late_native.dll"] = [1, 2, 3, 4] }, ("Late.Lib", "[1.0.0]"));
        StepRegistry registry = new();
        LoadReport installed = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallAsync("Late.Steps", "1.0.0");
        Assert.True(installed.Recorded, string.Join("; ", installed.Problems.Select(p => p.Message)));
        Assert.True(registry.TryGetType("LateTag", out Type? step));
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(step.Assembly)!;

        // Checked and approved; replaced afterwards, before anything of the plugin needed them.
        File.WriteAllBytes(Path.Combine(Cache, "packages", "late.lib", "1.0.0", "content", "lib", "LateLib.dll"),
            NugetTestFeed.Library("LateLib", "public static class Late { public static int One() => 2; }"));
        File.WriteAllBytes(Path.Combine(Cache, "packages", "late.steps", "1.0.0", "content", "native", "late_native.dll"), [6, 6, 6]);

        FileLoadException dependency = Assert.Throws<FileLoadException>(() => context.LoadFromAssemblyName(new System.Reflection.AssemblyName("LateLib")));
        // The runtime may wrap what the load context threw: the innermost exception carries the code.
        Assert.Equal("content-changed", dependency.GetBaseException().Data[IsolatedLoadContext.ProblemKey]);
        Assert.Contains("changed after the plugin was approved", dependency.GetBaseException().Message, StringComparison.Ordinal);
        System.Reflection.MethodInfo loadNative = typeof(AssemblyLoadContext).GetMethod("LoadUnmanagedDll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        System.Reflection.TargetInvocationException native = Assert.Throws<System.Reflection.TargetInvocationException>(() => loadNative.Invoke(context, ["late_native"]));
        FileLoadException nativeChanged = Assert.IsType<FileLoadException>(native.InnerException);
        Assert.Equal("content-changed", nativeChanged.Data[IsolatedLoadContext.ProblemKey]);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "LateLib");
    }

    [Fact]
    public async Task A_decompression_bomb_in_a_dll_entry_is_capped_when_the_cache_is_checked_and_other_names_are_never_read()
    {
        byte[] zeros = new byte[3 * 1024 * 1024];
        string assembly = PackStepsWith("Bomb.Steps", "1.0.0", Step("BombStep", "BombTag"), new() { ["junk/unrelated.dll"] = zeros });
        PluginLoaderOptions capped = Options() with { MaxExtractedEntryBytes = 1024 * 1024 };

        // An entry whose name is not one of the extracted files is not decompressed at all: the plugin installs.
        Assert.True((await new PluginLoader(capped, new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Bomb.Steps", "1.0.0")).Recorded);

        // An entry with the name of the plugin's own assembly, inflating past the cap: stopped, nothing loaded.
        string dll = Path.Combine(_root, "build", assembly + ".dll");
        _feed.Pack("Bomb2.Steps", "1.0.0", new() { [$"lib/net10.0/{assembly}.dll"] = File.ReadAllBytes(dll), [$"junk/{assembly}.dll"] = zeros });
        StepRegistry registry = new();
        LoadReport bomb = await new PluginLoader(capped with { InstallDirectory = Path.Combine(_root, "install2") }, registry, AlwaysTrust.Instance).InstallAsync("Bomb2.Steps", "1.0.0");
        LoadProblem problem = Assert.Single(bomb.Problems);
        Assert.Equal("nuget-too-large", problem.Code);
        Assert.Contains($"junk/{assembly}.dll", problem.Message, StringComparison.Ordinal);
        Assert.False(bomb.Recorded);
        Assert.Empty(registry.GetRegisteredTypes());
    }

    // ── Restart ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task After_a_restart_installed_packages_load_from_the_cache_without_sources_or_asking_again()
    {
        string assembly = PackSteps("Kept.Steps", "1.0.0", Step("KeptStep", "KeptTag") + Step("KeptPlain"));
        await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Kept.Steps", "1.0.0");
        Directory.Delete(_feed.Folder, recursive: true);

        StepRegistry registry = new();
        int events = 0;
        PluginLoader restarted = new(Options(network: false), registry, NeverTrust.Instance);
        restarted.PluginsChanged += (_, _) => events++;
        LoadReport report = Assert.Single(await restarted.LoadInstalledAsync());

        Assert.True(report.Approved);
        Assert.Empty(report.Problems);
        Assert.Equal(["KeptTag", assembly + ".KeptPlain"], report.Registered.Select(r => r.Tag).Order(StringComparer.Ordinal));
        Assert.True(registry.TryGetType("KeptTag", out _));
        Assert.Equal(1, events);

        // Twice is harmless.
        LoadReport again = Assert.Single(await restarted.LoadInstalledAsync());
        Assert.Equal(report.Registered, again.Registered);
    }

    [Fact]
    public async Task Installing_again_offline_reuses_the_lock_written_for_the_same_request()
    {
        PackSteps("Offline.Steps", "1.0.0", Step("OfflineStep", "OfflineTag"));
        await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Offline.Steps", "1.0.0");
        Directory.Delete(_feed.Folder, recursive: true);

        StepRegistry registry = new();
        LoadReport report = await new PluginLoader(Options(network: false), registry, NeverTrust.Instance).InstallAsync("Offline.Steps", "1.0.0");

        Assert.Empty(report.Problems);
        Assert.Contains(report.Notes, n => n.Contains("reused", StringComparison.Ordinal));
        Assert.True(registry.TryGetType("OfflineTag", out _));
    }

    [Fact]
    public async Task A_changed_approved_hash_asks_the_trust_policy_again()
    {
        PackSteps("Asked.Steps", "1.0.0", Step("AskedStep", "AskedTag"));
        await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Asked.Steps", "1.0.0");
        string file = Path.Combine(Install, "plugins.json");
        JsonObject json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["plugins"]![0]!["approvedSha512"] = "sha512-somethingelse";
        File.WriteAllText(file, json.ToJsonString());

        RecordingTrust trust = new(answer: false);
        StepRegistry registry = new();
        LoadReport denied = Assert.Single(await new PluginLoader(Options(network: false), registry, trust).LoadInstalledAsync());
        Assert.False(denied.Approved);
        Assert.Empty(registry.GetRegisteredTypes());
        Assert.Equal(TrustRequestKind.Changed, Assert.Single(trust.Requests).Kind);

        trust.Answer = true;
        PluginLoader approving = new(Options(network: false), registry, trust);
        Assert.True(Assert.Single(await approving.LoadInstalledAsync()).Approved);
        Assert.Equal(approving.Installed[0].Sha512, approving.Installed[0].ApprovedSha512);
    }

    [Fact]
    public async Task On_install_a_cached_package_that_no_longer_matches_its_lock_is_reported_and_not_loaded()
    {
        string assembly = PackSteps("Tampered.Steps", "1.0.0", Step("TamperedStep", "TamperedTag"));
        await new PluginLoader(Options(), new StepRegistry(), NeverTrust.Instance).InstallAsync("Tampered.Steps", "1.0.0");
        string nupkg = Path.Combine(Cache, "packages", "tampered.steps", "1.0.0", "tampered.steps.1.0.0.nupkg");
        File.AppendAllText(nupkg, "tampered");

        // The denied install left its lock: installing again reuses it, and the cache no longer matches.
        StepRegistry registry = new();
        LoadReport report = await new PluginLoader(Options(network: false), registry, AlwaysTrust.Instance).InstallAsync("Tampered.Steps", "1.0.0");

        Assert.Equal("hash-mismatch", Assert.Single(report.Problems).Code);
        Assert.Empty(registry.GetRegisteredTypes());
        Assert.False(IsLoaded(assembly));
        Assert.True(File.Exists(nupkg));
    }

    [Fact]
    public async Task On_reload_a_tampered_cache_is_reported_and_not_loaded()
    {
        string assembly = PackSteps("Reload.Tampered", "1.0.0", Step("ReloadTamperedStep", "ReloadTamperedTag"));
        await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Reload.Tampered", "1.0.0");
        string folder = Path.Combine(Cache, "packages", "reload.tampered", "1.0.0");

        // A replaced DLL in the extracted folder: the .nupkg still matches, the bytes that would load do not.
        File.WriteAllBytes(Path.Combine(folder, "content", "lib", assembly + ".dll"), NugetTestFeed.GreeterDll);
        StepRegistry registry = new();
        LoadReport content = Assert.Single(await new PluginLoader(Options(network: false), registry, AlwaysTrust.Instance).LoadInstalledAsync());
        Assert.Equal("content-mismatch", Assert.Single(content.Problems).Code);

        // A different .nupkg under the same id and version.
        File.AppendAllText(Path.Combine(folder, "reload.tampered.1.0.0.nupkg"), "tampered");
        LoadReport hash = Assert.Single(await new PluginLoader(Options(network: false), registry, AlwaysTrust.Instance).LoadInstalledAsync());
        Assert.Equal("hash-mismatch", Assert.Single(hash.Problems).Code);
        Assert.Empty(registry.GetRegisteredTypes());
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_loaded_package_needs_a_restart_and_it_does_not_come_back()
    {
        PackSteps("Gone.Steps", "1.0.0", Step("GoneStep", "GoneTag"));
        StepRegistry registry = new();
        PluginLoader loader = new(Options(), registry, AlwaysTrust.Instance);
        await loader.InstallAsync("Gone.Steps", "1.0.0");

        RemoveResult removed = await loader.RemoveAsync("gone.steps");

        Assert.True(removed.Removed);
        Assert.True(removed.RestartRequired);
        Assert.Empty(loader.Installed);
        Assert.True(registry.TryGetType("GoneTag", out _));
        Assert.False(Directory.Exists(Path.Combine(Install, "plugin-locks", "gone.steps")));
        Assert.False((await loader.RemoveAsync("Gone.Steps")).Removed);

        PluginLoader restarted = new(Options(network: false), new StepRegistry(), AlwaysTrust.Instance);
        Assert.Empty(await restarted.LoadInstalledAsync());
        Assert.Empty(restarted.Installed);
    }

    // ── Validation and failures ──────────────────────────────────────────────

    [Theory]
    [InlineData("../evil", "1.0.0")]
    [InlineData("C:\\evil", "1.0.0")]
    [InlineData("", "1.0.0")]
    [InlineData("Fine", "1.*")]
    [InlineData("Fine", "")]
    public async Task An_invalid_request_is_a_validation_problem_and_writes_nothing(string id, string version)
    {
        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync(id, version);

        Assert.Equal("invalid-package", Assert.Single(report.Problems).Code);
        Assert.False(Directory.Exists(Install));
        Assert.False((await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).RemoveAsync(id)).Removed);
        Assert.False(Directory.Exists(Install));
    }

    [Fact]
    public async Task Without_sources_the_problem_names_the_application_setting()
    {
        LoadReport report = await new PluginLoader(Options(network: false, sourcesOptionName: "MyApp:StepSources"), new StepRegistry(), AlwaysTrust.Instance)
            .InstallAsync("Nowhere.Steps", "1.0.0");

        LoadProblem problem = Assert.Single(report.Problems);
        Assert.Equal("nuget-offline", problem.Code);
        Assert.Equal("Package 'Nowhere.Steps' needs a NuGet source and none is configured (MyApp:StepSources).", problem.Message);
    }

    [Fact]
    public async Task The_resolver_messages_of_the_designer_are_unchanged()
    {
        NugetException ex = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().RestoreAsync(
            [new NugetRequirement("Foo", "1.0.0")], new NugetOptions { CacheDirectory = Cache }));
        Assert.Equal("Package 'Foo' needs a NuGet source and none is configured (WorkflowDesigner:NuGetSources).", ex.Message);

        NugetLock lockFile = new() { Packages = [new NugetLockPackage("Foo", "1.0.0", "sha512-x", true)] };
        ex = await Assert.ThrowsAsync<NugetException>(() => new NugetResolver().MaterializeAsync(lockFile, new NugetOptions { CacheDirectory = Cache }));
        Assert.Equal(
            "Package 'Foo' 1.0.0 is not in the NuGet cache and no usable source is configured: restore the package with a NuGet source (WorkflowDesigner:NuGetSources).",
            ex.Message);
    }

    [Fact]
    public async Task Types_that_fail_to_load_are_reported_and_the_others_registered()
    {
        string missing = Path.Combine(_root, "build", "MissingLib.dll");
        PluginTestFixture.CompileTo(missing, "MissingLib", Usings + """

            namespace MissingLib;
            public abstract class ForeignBase(string name, System.Type input, System.Type output) : StepBase(name, input, output) { }
            """);
        PackSteps("Broken.Steps", "1.0.0",
            Step("BrokenStep", "BrokenTag", baseClass: "MissingLib.ForeignBase") + Step("GoodStep", "GoodTag"), missing);
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallAsync("Broken.Steps", "1.0.0");

        Assert.True(report.Approved);
        Assert.Equal("GoodTag", Assert.Single(report.Registered).Tag);
        LoadProblem problem = Assert.Single(report.Problems);
        Assert.Equal("type-load-failed", problem.Code);
        Assert.Contains("MissingLib", problem.Message, StringComparison.Ordinal);
        Assert.False(registry.TryGetType("BrokenTag", out _));
        Assert.Single(new PluginLoader(Options(network: false), new StepRegistry(), NeverTrust.Instance).Installed);
    }

    [Fact]
    public async Task Two_installs_at_once_are_serialized_and_the_install_file_stays_whole()
    {
        PackSteps("First.Steps", "1.0.0", Step("FirstStep", "FirstTag"));
        PackSteps("Second.Steps", "1.0.0", Step("SecondStep", "SecondTag"));
        StepRegistry registry = new();
        RecordingTrust trust = new(delayMs: 150);
        PluginLoader loader = new(Options(), registry, trust);

        LoadReport[] reports = await Task.WhenAll(
            Task.Run(() => loader.InstallAsync("First.Steps", "1.0.0")),
            Task.Run(() => loader.InstallAsync("Second.Steps", "1.0.0")));

        Assert.All(reports, r => Assert.Empty(r.Problems));
        Assert.Equal(1, trust.MaxConcurrent);
        Assert.True(registry.TryGetType("FirstTag", out _));
        Assert.True(registry.TryGetType("SecondTag", out _));
        JsonArray packages = JsonNode.Parse(File.ReadAllText(Path.Combine(Install, "plugins.json")))!["plugins"]!.AsArray();
        Assert.Equal(["First.Steps", "Second.Steps"], packages.Select(p => (string)p!["id"]!));
        Assert.Empty(Directory.EnumerateFiles(Install, "*.tmp"));
    }

    [Fact]
    public async Task A_corrupt_install_file_is_reported_and_kept_aside()
    {
        Directory.CreateDirectory(Install);
        string file = Path.Combine(Install, "plugins.json");
        File.WriteAllText(file, "{ not json");
        PackSteps("After.Steps", "1.0.0", Step("AfterStep", "AfterTag"));

        PluginLoader loader = new(Options(), new StepRegistry(), AlwaysTrust.Instance);
        LoadProblem problem = Assert.Single(loader.StoreProblems);
        Assert.Equal("store-corrupt", problem.Code);
        string copy = Assert.Single(Directory.EnumerateFiles(Install, "plugins.json.corrupt-*"));
        Assert.Equal("{ not json", File.ReadAllText(copy));
        Assert.Contains(copy, problem.Message, StringComparison.Ordinal);
        Assert.Empty(await loader.LoadInstalledAsync());

        Assert.Empty((await loader.InstallAsync("After.Steps", "1.0.0")).Problems);
        Assert.Equal("After.Steps", (string)JsonNode.Parse(File.ReadAllText(file))!["plugins"]![0]!["id"]!);
    }

    // ── Audit 2026-10-20: regressions ────────────────────────────────────────

    [Fact]
    public async Task Installing_an_installed_version_with_another_request_text_does_not_touch_its_lock_and_it_still_loads_after_a_restart()
    {
        PackSteps("Locked.Steps", "1.0.0", Step("LockedStep", "LockedTag"));
        await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Locked.Steps", "1.0.0");
        byte[] lockBytes = File.ReadAllBytes(LockOf("Locked.Steps", "1.0.0"));
        string store = File.ReadAllText(StoreFile);

        // Another process asks for "[1.0.0]": the installed version satisfies it, so its own lock is reused (nothing is resolved or written).
        RecordingTrust trust = new(answer: false);
        LoadReport same = await new PluginLoader(Options(), new StepRegistry(), trust).InstallAsync("Locked.Steps", "[1.0.0]");
        Assert.True(same.Approved);
        Assert.True(same.Recorded);
        Assert.Empty(trust.Requests);
        Assert.Contains(same.Notes, n => n.Contains("installed version 1.0.0", StringComparison.Ordinal));
        Assert.Equal(lockBytes, File.ReadAllBytes(LockOf("Locked.Steps", "1.0.0")));
        Assert.Equal("1.0.0", (string)JsonNode.Parse(File.ReadAllText(StoreFile))!["plugins"]![0]!["requested"]!);

        // Refused: the approved hash no longer matches, the request is denied, and neither the lock nor the entry change.
        File.WriteAllText(StoreFile, store.Replace("\"approvedSha512\": \"sha512-", "\"approvedSha512\": \"sha512-X", StringComparison.Ordinal));
        string tampered = File.ReadAllText(StoreFile);
        LoadReport denied = await new PluginLoader(Options(), new StepRegistry(), trust).InstallAsync("Locked.Steps", "[1.0.0]");
        Assert.False(denied.Approved);
        Assert.False(denied.Recorded);
        Assert.Equal(TrustRequestKind.Changed, Assert.Single(trust.Requests).Kind);
        Assert.Equal(lockBytes, File.ReadAllBytes(LockOf("Locked.Steps", "1.0.0")));
        Assert.Equal(tampered, File.ReadAllText(StoreFile));

        // Back to the approved entry: a restart, offline and approving nothing new, loads it.
        File.WriteAllText(StoreFile, store);
        StepRegistry restarted = new();
        LoadReport reloaded = Assert.Single(await new PluginLoader(Options(network: false), restarted, NeverTrust.Instance).LoadInstalledAsync());
        Assert.Empty(reloaded.Problems);
        Assert.True(restarted.TryGetType("LockedTag", out _));
    }

    [Fact]
    public async Task A_step_type_that_cannot_be_described_is_skipped_and_reported_and_the_rest_registered()
    {
        string missing = Path.Combine(_root, "build", "AbsentLib.dll");
        PluginTestFixture.CompileTo(missing, "AbsentLib", "namespace AbsentLib; public sealed class Thing { }");
        PackSteps("Undescribable.Steps", "1.0.0", """
            [StepTag("UndescribableTag")]
            public sealed class UndescribableStep : StepBase
            {
                public UndescribableStep() : base("UndescribableStep", typeof(IStepResult), typeof(JsonStepResult)) { }
                public AbsentLib.Thing? Extra { get; set; }
                protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? last, CancellationToken ct)
                    => Task.FromResult<IStepResult>(new JsonStepResult(this, new JValue("x")));
            }
            """ + Step("DescribableStep", "DescribableTag"), missing);
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallAsync("Undescribable.Steps", "1.0.0");

        Assert.True(report.Approved);
        Assert.True(report.Recorded);
        Assert.Equal("DescribableTag", Assert.Single(report.Registered).Tag);
        SkippedStep skipped = Assert.Single(report.Skipped);
        Assert.Equal(("UndescribableStep", "type-load-failed"), (skipped.Type.Split('.')[^1], skipped.Reason));
        Assert.Contains("AbsentLib", skipped.Message, StringComparison.Ordinal);
        Assert.Equal("type-load-failed", Assert.Single(report.Problems).Code);
        Assert.False(registry.TryGetType("UndescribableTag", out _));
        // Whatever is registered can be described: a catalog over this registry does not fail.
        Assert.All(registry.GetRegisteredTypes().Keys, tag => registry.Describe(tag));
    }

    [Fact]
    public async Task Two_loaders_over_the_same_folder_keep_each_others_entries()
    {
        PackSteps("Left.Steps", "1.0.0", Step("LeftStep", "LeftTag"));
        PackSteps("Right.Steps", "1.0.0", Step("RightStep", "RightTag"));
        PluginLoader left = new(Options(), new StepRegistry(), AlwaysTrust.Instance);
        PluginLoader right = new(Options(), new StepRegistry(), AlwaysTrust.Instance);
        Assert.Empty(left.Installed);
        Assert.Empty(right.Installed);

        await left.InstallAsync("Left.Steps", "1.0.0");
        await right.InstallAsync("Right.Steps", "1.0.0");

        string[] Ids() => [.. JsonNode.Parse(File.ReadAllText(StoreFile))!["plugins"]!.AsArray().Select(p => (string)p!["id"]!)];
        Assert.Equal(["Left.Steps", "Right.Steps"], Ids());
        Assert.Equal(2, right.Installed.Count);

        // The stale one removes its own package and keeps the other's.
        Assert.True((await left.RemoveAsync("Left.Steps")).Removed);
        Assert.Equal(["Right.Steps"], Ids());
        Assert.Empty(Directory.EnumerateFiles(Install, "*.lock"));
        Assert.Empty(Directory.EnumerateFiles(Install, "*.tmp"));
    }

    [Fact]
    public async Task Two_loaders_writing_at_the_same_time_lose_no_entry()
    {
        string[] ids = [.. Enumerable.Range(0, 4).Select(i => $"Race{i}.Steps")];
        foreach (string id in ids)
        {
            PackSteps(id, "1.0.0", Step(id.Replace(".", "", StringComparison.Ordinal) + "Step", id + "Tag"));
        }

        PluginLoader[] loaders = [.. ids.Select(_ => new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance))];
        Assert.All(loaders, l => Assert.Empty(l.Installed));

        LoadReport[] reports = await Task.WhenAll(ids.Select((id, i) => Task.Run(() => loaders[i].InstallAsync(id, "1.0.0"))));

        Assert.All(reports, r => Assert.True(r.Recorded, string.Join("; ", r.Problems.Select(p => p.Message))));
        Assert.Equal(ids, JsonNode.Parse(File.ReadAllText(StoreFile))!["plugins"]!.AsArray().Select(p => (string)p!["id"]!));
    }

    [Fact]
    public async Task A_reload_with_nothing_changed_does_not_rewrite_the_install_file()
    {
        PackSteps("Quiet.Steps", "1.0.0", Step("QuietStep", "QuietTag"));
        await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Quiet.Steps", "1.0.0");
        byte[] before = File.ReadAllBytes(StoreFile);
        DateTime written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(StoreFile, written);
        string[] files = [.. Directory.EnumerateFileSystemEntries(Install, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

        LoadReport report = Assert.Single(await new PluginLoader(Options(network: false), new StepRegistry(), NeverTrust.Instance).LoadInstalledAsync());

        Assert.Empty(report.Problems);
        Assert.Equal(before, File.ReadAllBytes(StoreFile));
        Assert.Equal(written, File.GetLastWriteTimeUtc(StoreFile));
        Assert.Equal(files, Directory.EnumerateFileSystemEntries(Install, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_replaced_native_library_or_dependency_dll_is_a_content_mismatch_before_anything_loads()
    {
        string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        byte[] native = [1, 2, 3, 4];
        _feed.Pack("Dep.Lib", "1.0.0", new() { ["lib/net10.0/TestLib.dll"] = NugetTestFeed.GreeterDll });
        string assembly = PackStepsWith("Native.Steps", "1.0.0", Step("NativeStep", "NativeTag"),
            new() { [$"runtimes/{rid}/native/acme_native.dll"] = native }, ("Dep.Lib", "[1.0.0]"));
        RecordingTrust trust = new();
        await new PluginLoader(Options(), new StepRegistry(), trust).InstallAsync("Native.Steps", "1.0.0");
        TrustRequest request = Assert.Single(trust.Requests);
        Assert.Equal(["acme_native.dll"], request.NativeLibraries);
        Assert.Contains("TestLib.dll", request.Assemblies);
        Assert.DoesNotContain("acme_native.dll", request.Assemblies);
        Assert.True(IsLoaded(assembly));
        int contexts = AssemblyLoadContext.All.Count(c => c.Name == "plugin:nuget:Native.Steps/1.0.0");

        // A different native library in the extracted folder: nothing is loaded.
        string nativeFile = Path.Combine(Cache, "packages", "native.steps", "1.0.0", "content", "native", "acme_native.dll");
        File.WriteAllBytes(nativeFile, [9, 9, 9]);
        StepRegistry registry = new();
        LoadReport tamperedNative = Assert.Single(await new PluginLoader(Options(network: false), registry, AlwaysTrust.Instance).LoadInstalledAsync());
        LoadProblem problem = Assert.Single(tamperedNative.Problems);
        Assert.Equal("content-mismatch", problem.Code);
        Assert.Contains("acme_native.dll", problem.Message, StringComparison.Ordinal);
        File.WriteAllBytes(nativeFile, native);

        // A different DLL of a dependency: the same.
        string dependency = Path.Combine(Cache, "packages", "dep.lib", "1.0.0", "content", "lib", "TestLib.dll");
        File.WriteAllBytes(dependency, NugetTestFeed.Library("TestLib", "public static class Other { }"));
        LoadReport tamperedDependency = Assert.Single(await new PluginLoader(Options(network: false), registry, AlwaysTrust.Instance).LoadInstalledAsync());
        Assert.Equal("content-mismatch", Assert.Single(tamperedDependency.Problems).Code);
        Assert.Empty(registry.GetRegisteredTypes());
        Assert.Equal(contexts, AssemblyLoadContext.All.Count(c => c.Name == "plugin:nuget:Native.Steps/1.0.0"));
    }

    [Fact]
    public async Task A_changed_package_installed_with_a_range_is_approved_offline_with_the_exact_version_it_shows()
    {
        PackSteps("Ranged.Steps", "1.0.0", Step("RangedStep", "RangedTag"));
        await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Ranged.Steps", "[1.0.0,2.0.0)");
        Directory.Delete(_feed.Folder, recursive: true);
        JsonObject json = JsonNode.Parse(File.ReadAllText(StoreFile))!.AsObject();
        json["plugins"]![0]!["approvedSha512"] = "sha512-somethingelse";
        File.WriteAllText(StoreFile, json.ToJsonString());

        RecordingTrust refusing = new(answer: false);
        LoadReport pending = Assert.Single(await new PluginLoader(Options(network: false), new StepRegistry(), refusing).LoadInstalledAsync());
        Assert.Equal("not-approved", Assert.Single(pending.Problems).Code);
        Assert.Equal("1.0.0", pending.Version);

        // What the CLI hint and the web's pending request carry: the exact version. No source, and the same closure.
        RecordingTrust approving = new();
        StepRegistry registry = new();
        LoadReport approved = await new PluginLoader(Options(network: false), registry, approving).InstallAsync("Ranged.Steps", pending.Version);

        Assert.Empty(approved.Problems);
        Assert.True(approved.Recorded);
        Assert.Equal(TrustRequestKind.Changed, Assert.Single(approving.Requests).Kind);
        Assert.Equal(refusing.Requests.Single().Sha512, approving.Requests.Single().Sha512);
        Assert.True(registry.TryGetType("RangedTag", out _));
        JsonNode entry = JsonNode.Parse(File.ReadAllText(StoreFile))!["plugins"]![0]!;
        Assert.Equal("[1.0.0,2.0.0)", (string)entry["requested"]!);
        Assert.Equal((string)entry["sha512"]!, (string)entry["approvedSha512"]!);
    }

    [Fact]
    public async Task An_approved_package_that_cannot_be_loaded_is_not_recorded_over_the_entry_already_there()
    {
        _feed.Pack("Old.Steps", "1.0.0", new() { ["lib/net48/Old.dll"] = NugetTestFeed.GreeterDll });
        Directory.CreateDirectory(Install);
        const string entry = """{ "version": 1, "plugins": [ { "id": "Old.Steps", "version": "1.0.0", "requested": "1.0.0", "sha512": "sha512-old", "approvedSha512": "sha512-old", "approvedClosure": "old", "installedAtUtc": "2026-01-01T00:00:00+00:00", "steps": [ "OldTag" ] } ] }""";
        File.WriteAllText(StoreFile, entry);

        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Old.Steps", "1.0.0");

        Assert.True(report.Approved);
        Assert.False(report.Recorded);
        Assert.Equal("no-assemblies", Assert.Single(report.Problems).Code);
        Assert.Equal(entry, File.ReadAllText(StoreFile));
        Assert.False(File.Exists(LockOf("Old.Steps", "1.0.0")));
    }

    [Fact]
    public async Task A_plugin_whose_own_assembly_cannot_be_read_stops_before_trust_and_a_reload_keeps_the_recorded_entry()
    {
        _feed.Pack("Flaky.Steps", "1.0.0", new() { ["lib/net10.0/Flaky.dll"] = [0x4D, 0x5A, 0, 0, 1, 2, 3] });
        RecordingTrust trust = new();
        LoadReport failed = await new PluginLoader(Options(), new StepRegistry(), trust).InstallAsync("Flaky.Steps", "1.0.0");
        Assert.Equal("load-failed", Assert.Single(failed.Problems).Code);
        Assert.False(failed.Recorded);
        Assert.False(failed.Approved);
        Assert.Empty(trust.Requests);

        // An entry as a load that worked would have left it (its closure is not the lock's, so reloading asks and approves again).
        string hash = (string)JsonNode.Parse(File.ReadAllText(LockOf("Flaky.Steps", "1.0.0")))!["packages"]![0]!["hash"]!;
        File.WriteAllText(StoreFile, new JsonObject
        {
            ["version"] = 1,
            ["plugins"] = new JsonArray(new JsonObject
            {
                ["id"] = "Flaky.Steps", ["version"] = "1.0.0", ["requested"] = "1.0.0", ["sha512"] = hash, ["approvedSha512"] = hash,
                ["approvedClosure"] = "stale", ["installedAtUtc"] = "2026-01-01T00:00:00+00:00", ["steps"] = new JsonArray("FlakyTag")
            })
        }.ToJsonString());

        string before = File.ReadAllText(StoreFile);
        LoadReport reloaded = Assert.Single(await new PluginLoader(Options(network: false), new StepRegistry(), AlwaysTrust.Instance).LoadInstalledAsync());

        Assert.Equal("load-failed", Assert.Single(reloaded.Problems).Code);
        JsonNode saved = JsonNode.Parse(File.ReadAllText(StoreFile))!["plugins"]![0]!;
        Assert.Equal(["FlakyTag"], saved["steps"]!.AsArray().Select(s => (string)s!));
        Assert.Equal(before, File.ReadAllText(StoreFile));
    }

    [Fact]
    public async Task Valid_json_with_invalid_values_is_reported_per_package_and_does_not_stop_the_start()
    {
        PackSteps("Fine.Steps", "1.0.0", Step("FineValuesStep", "FineValuesTag"));
        await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Fine.Steps", "1.0.0");
        JsonObject json = JsonNode.Parse(File.ReadAllText(StoreFile))!.AsObject();
        JsonArray packages = json["plugins"]!.AsArray();
        JsonObject Entry(string id, string version) => new()
        {
            ["id"] = id, ["version"] = version, ["requested"] = version, ["sha512"] = "sha512-x", ["approvedSha512"] = "sha512-x",
            ["installedAtUtc"] = "2026-01-01T00:00:00+00:00", ["steps"] = new JsonArray()
        };
        packages.Add(Entry("Range.Steps", "[1.0,2.0)"));
        packages.Add(Entry("BadLock.Steps", "1.0.0"));
        File.WriteAllText(StoreFile, json.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(LockOf("BadLock.Steps", "1.0.0"))!);
        File.WriteAllText(LockOf("BadLock.Steps", "1.0.0"),
            """{ "version": 1, "requiresHash": "", "packages": [ { "id": "BadLock.Steps", "version": "not-a-version", "hash": "sha512-x", "direct": true } ] }""");

        StepRegistry registry = new();
        PluginLoader loader = new(Options(network: false), registry, NeverTrust.Instance);
        IReadOnlyList<LoadReport> reports = await loader.LoadInstalledAsync();

        Assert.Empty(loader.StoreProblems);
        Assert.Equal("store-corrupt", Assert.Single(reports.Single(r => r.Id == "Range.Steps").Problems).Code);
        Assert.Equal("lock-missing", Assert.Single(reports.Single(r => r.Id == "BadLock.Steps").Problems).Code);
        Assert.Empty(reports.Single(r => r.Id == "Fine.Steps").Problems);
        Assert.True(registry.TryGetType("FineValuesTag", out _));
        // Installing it again does not reuse the unusable lock (offline here: reported, not thrown).
        Assert.StartsWith("nuget-", Assert.Single((await loader.InstallAsync("BadLock.Steps", "1.0.0")).Problems).Code, StringComparison.Ordinal);

        // An entry that is not an object with id, version and sha512 is the file being wrong: reported, kept aside, the start goes on.
        File.WriteAllText(StoreFile, """{ "version": 1, "plugins": [ null ] }""");
        PluginLoader nulls = new(Options(network: false), new StepRegistry(), NeverTrust.Instance);
        Assert.Equal("store-corrupt", Assert.Single(nulls.StoreProblems).Code);
        Assert.Empty(await nulls.LoadInstalledAsync());
    }
}
