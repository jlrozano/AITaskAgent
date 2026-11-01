using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Steps;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WorkflowDesigner.Testing;
using static AITaskAgent.NugetLoader.Tests.PluginTestKit;

namespace AITaskAgent.NugetLoader.Tests;

/// <summary>
/// <see cref="PluginLoader"/> with folder plugins (a folder of DLLs with an optional <c>plugin.json</c>), the metadata scanner, the tree hash,
/// collisions (all or nothing) and the migration of <c>steps-packages.json</c>.
/// </summary>
[Collection(PluginCollection.Name)]
public sealed class PluginFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfd-plugin-folders", Guid.NewGuid().ToString("N"));

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

    private PluginLoaderOptions Options(Func<LoadedPlugin, IReadOnlyList<LoadProblem>>? accept = null) => new()
    {
        CacheDirectory = Path.Combine(_root, "cache"),
        InstallDirectory = Install,
        Accept = accept
    };

    private static string Manifest(string id, string assembly, string version = "1.2.0", int contract = 1)
        => $$"""{ "id": "{{id}}", "version": "{{version}}", "description": "A test plugin.", "contract": {{contract}}, "assemblies": [ "{{assembly}}.dll" ] }""";

    /// <summary>A plugin folder <c>&lt;root&gt;/&lt;name&gt;</c> with <paramref name="body"/> compiled as <c>&lt;assembly&gt;.dll</c> and <paramref name="manifest"/> (null = none).</summary>
    private string Folder(string name, string assembly, string body, string? manifest, params string[] extra)
    {
        string folder = Path.Combine(_root, "plugins", name);
        Compile(Path.Combine(folder, assembly + ".dll"), assembly, body, extra);
        if (manifest != null)
        {
            WriteText(Path.Combine(folder, PluginFolder.ManifestName), manifest);
        }

        return folder;
    }

    private sealed class RecordingTrust(bool answer = true) : ITrustPolicy
    {
        public ConcurrentQueue<TrustRequest> Requests { get; } = new();

        public bool Answer { get; set; } = answer;

        public ValueTask<bool> ApproveAsync(TrustRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return ValueTask.FromResult(Answer);
        }
    }

    // ── Discovery and trust ──────────────────────────────────────────────────

    [Fact]
    public async Task Everything_a_plugin_brings_is_read_from_metadata_shown_before_approval_and_nothing_loads_until_it_is_approved()
    {
        string assembly = NewAssembly();
        string folder = Folder("Acme", assembly, FullPlugin("AcmeTag", "acme.echo", "Acme.Done"), Manifest("Acme.Folder", assembly));
        StepRegistry registry = new();
        RecordingTrust trust = new(answer: false);
        PluginLoader loader = new(Options(), registry, trust);
        string[] contexts = [.. AssemblyLoadContext.All.Select(c => c.Name ?? "")];

        LoadReport refused = await loader.InstallFolderAsync(folder);

        Assert.False(refused.Approved);
        Assert.Contains(refused.Problems, p => p.Code == "not-approved");
        Assert.False(IsLoaded(assembly));
        Assert.Equal(contexts, AssemblyLoadContext.All.Select(c => c.Name ?? ""));
        Assert.Empty(registry.GetRegisteredTypes());
        Assert.Empty(loader.Installed);

        TrustRequest request = Assert.Single(trust.Requests);
        Assert.Equal(("Acme.Folder", "1.2.0", PluginOrigin.Folder, TrustRequestKind.Install), (request.Id, request.Version, request.Origin, request.Kind));
        Assert.Equal(Path.GetFullPath(folder), request.Folder);
        Assert.Equal(PluginFolder.Hash(folder).Sha512, request.Sha512);
        Assert.Equal([assembly + ".dll"], request.Assemblies);
        PluginContributions shown = request.Contributions!;
        Assert.Equal(["AcmeTag", assembly + ".PlainStep"], shown.Steps.Select(s => s.Tag).Order(StringComparer.Ordinal));
        Assert.Equal([assembly + ".EchoTool"], shown.Tools.Select(t => t.TypeName));
        Assert.Equal([assembly + ".ArgsTools"], shown.ToolSources.Select(t => t.TypeName));
        Assert.Equal([assembly + ".Templates"], shown.TemplateSources.Select(t => t.TypeName));
        Assert.Equal(assembly + ".Configuration", shown.Configuration?.TypeName);
        Assert.Equal([("Acme.Done", HookKind.Hook), ("Hooks.Check", HookKind.StepValidator)], shown.Hooks.Select(h => (h.Name, h.Kind)));
        Assert.Equal(("Finding", assembly + ".FindingDto"), (Assert.Single(shown.ResponseTypes).Name, shown.ResponseTypes[0].TypeName));
        Assert.Contains(refused.Problems, p => p.Code == "invalid-hook" && p.Message.Contains("NotStatic", StringComparison.Ordinal));

        trust.Answer = true;
        LoadReport report = await loader.InstallFolderAsync(folder);

        Assert.True(report.Approved);
        Assert.True(report.Recorded);
        Assert.DoesNotContain(report.Problems, p => p.Code is "collision" or "not-approved");
        LoadedPlugin plugin = report.Plugin!;
        Assert.Equal(("Acme.Folder", "1.2.0", PluginOrigin.Folder, "A test plugin."), (plugin.Id, plugin.Version, plugin.Origin, plugin.Description));
        Assert.True(registry.TryGetType("AcmeTag", out Type? tagged));
        Assert.True(registry.TryGetType(assembly + ".PlainStep", out _));
        Assert.Equal("plugin:folder:Acme.Folder/1.2.0", AssemblyLoadContext.GetLoadContext(tagged.Assembly)?.Name);
        Assert.Equal(new PluginIdentity(PluginOrigin.Folder, "Acme.Folder", "1.2.0"), PluginIdentity.Of(tagged));
        Assert.Equal([assembly + ".EchoTool"], plugin.Tools.Select(t => t.FullName));
        Assert.Equal([assembly + ".ArgsTools"], plugin.ToolSources.Select(t => t.FullName));
        Assert.Equal([assembly + ".Templates"], plugin.TemplateSources.Select(t => t.FullName));
        Assert.Equal(("Finding", assembly + ".FindingDto"), (Assert.Single(plugin.ResponseTypes).Name, plugin.ResponseTypes[0].Type.FullName));

        // The host wires the rest: hooks into its registry with the plugin as source, the configuration from its class.
        HookRegistry hooks = new();
        PluginRegistration.AddHooks(hooks, plugin);
        Assert.All(hooks.List(), h => Assert.Equal("plugin:Acme.Folder", h.Source));
        Assert.Equal(["Acme.Done", "Hooks.Check"], hooks.List().Select(h => h.Name).Order(StringComparer.Ordinal));
        IPluginConfiguration configuration = (IPluginConfiguration)Activator.CreateInstance(plugin.Configuration!)!;
        Assert.Equal(["AcmeRoot"], configuration.RootSections);
        Assert.NotNull(configuration.Schema());

        InstalledPlugin installed = Assert.Single(loader.Installed);
        Assert.Equal((PluginOrigin.Folder, Path.GetFullPath(folder), "A test plugin."), (installed.Origin, installed.Folder, installed.Description));
        Assert.Equal(installed.Sha512, installed.ApprovedSha512);
        JsonNode entry = JsonNode.Parse(File.ReadAllText(Path.Combine(Install, "plugins.json")))!["plugins"]![0]!;
        Assert.Equal("folder", (string)entry["origin"]!);
    }

    [Fact]
    public async Task After_a_restart_the_folder_loads_without_asking_and_any_changed_file_asks_again()
    {
        string assembly = NewAssembly();
        string folder = Folder("Kept", assembly, Step("KeptStep", "KeptFolderTag"), Manifest("Kept.Folder", assembly));
        Assert.True((await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallFolderAsync(folder)).Recorded);
        string store = Path.Combine(Install, "plugins.json");
        byte[] before = File.ReadAllBytes(store);

        StepRegistry registry = new();
        LoadReport reloaded = Assert.Single(await new PluginLoader(Options(), registry, NeverTrust.Instance).LoadInstalledAsync());
        Assert.True(reloaded.Approved);
        Assert.True(registry.TryGetType("KeptFolderTag", out _));
        Assert.Equal(before, File.ReadAllBytes(store));

        // Any file of the tree, not only DLLs: the approval is for the whole folder.
        WriteText(Path.Combine(folder, "data", "notes.txt"), "new");
        RecordingTrust trust = new(answer: false);
        LoadReport changed = Assert.Single(await new PluginLoader(Options(), new StepRegistry(), trust).LoadInstalledAsync());
        Assert.False(changed.Approved);
        Assert.Equal(TrustRequestKind.Changed, Assert.Single(trust.Requests).Kind);
        Assert.NotEqual(Assert.Single(new PluginLoader(Options(), new StepRegistry(), NeverTrust.Instance).Installed).ApprovedSha512, trust.Requests.Single().Sha512);
    }

    [Fact]
    public void The_tree_hash_is_the_sha512_of_the_sorted_lines_of_path_and_file_hash()
    {
        string folder = Path.Combine(_root, "tree");
        WriteText(Path.Combine(folder, "b.txt"), "bee");
        WriteText(Path.Combine(folder, "a", "c.txt"), "sea");
        static string Hex(string text) => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(text)));
        string lines = $"a/c.txt\t{Hex("sea")}\nb.txt\t{Hex("bee")}\n";

        PluginFolderTree tree = PluginFolder.Hash(folder);

        Assert.Equal("sha512-" + Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(lines))), tree.Sha512);
        Assert.Equal(["a/c.txt", "b.txt"], tree.Files.Keys);
        Assert.Equal(Hex("sea"), tree.HashOf(Path.Combine(folder, "a", "c.txt")));
        Assert.Null(tree.HashOf(Path.Combine(_root, "elsewhere.txt")));
        File.Move(Path.Combine(folder, "b.txt"), Path.Combine(folder, "d.txt"));
        Assert.NotEqual(tree.Sha512, PluginFolder.Hash(folder).Sha512);
    }

    // ── Manifest rules ───────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_manifest_the_dll_named_like_the_folder_is_the_plugin_and_the_folder_name_its_id()
    {
        string assembly = NewAssembly("Bare");
        string folder = Folder(assembly, assembly, Step("BareStep", "BareTag"), manifest: null);
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(report.Recorded);
        Assert.Equal((assembly, "0.0.0"), (report.Id, report.Version));
        Assert.True(registry.TryGetType("BareTag", out _));

        // Several DLLs and none named like the folder: the plugin must say which are its own.
        string other = Path.Combine(_root, "plugins", "Many");
        Compile(Path.Combine(other, "One.dll"), NewAssembly(), Step("OneStep"));
        Compile(Path.Combine(other, "Two.dll"), NewAssembly(), Step("TwoStep"));
        Assert.Equal("no-assemblies", Assert.Single((await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallFolderAsync(other)).Problems).Code);
    }

    [Fact]
    public async Task An_extension_json_is_read_as_a_plugin_json_with_a_warning_and_its_IExtension_drives_the_plugin()
    {
        string assembly = NewAssembly("Legacy");
        string folder = Folder("Legacy", assembly, Step("LegacyStep", "LegacyTag") + """
            public sealed class OldExtension : IExtension
            {
                public string Name => "Legacy";
                public void Register(IExtensionBuilder builder) => builder.AddStep<LegacyStep>("LegacyTag");
                public ExtensionInfo Info() => new() { Entries = [ new ExtensionEntry(ExtensionKinds.Step, "LegacyTag") ] };
            }
            """, manifest: null);
        WriteText(Path.Combine(folder, PluginFolder.LegacyManifestName),
            $$"""{ "name": "Legacy.Ext", "version": "2.0.0", "assembly": "{{assembly}}.dll", "contract": 1, "nuget": [] }""");
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(report.Recorded);
        Assert.Equal(("Legacy.Ext", "2.0.0"), (report.Id, report.Version));
        Assert.Contains(report.Problems, p => p.Code == "legacy-manifest" && p.Message.Contains("'nuget' section is ignored", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Problems, p => p.Code is "info-mismatch" or "extension-failed");
        Assert.True(registry.TryGetType("LegacyTag", out _));
        Assert.Equal([assembly + ".OldExtension"], report.Contributions!.Extensions);
        Assert.Equal(assembly + ".OldExtension", report.Plugin!.Extension!.TypeName);
    }

    [Theory]
    [InlineData("{ not json", "bad-manifest")]
    [InlineData("""{ "id": "X", "contract": 2 }""", "contract-too-new")]
    [InlineData("""{ "id": "X" }""", "bad-manifest")]
    [InlineData("""{ "id": "../x", "contract": 1 }""", "invalid-plugin")]
    [InlineData("""{ "id": "X", "contract": 1, "assemblies": [ "../outside.dll" ] }""", "bad-manifest")]
    [InlineData("""{ "id": "X", "contract": 1, "version": "banana" }""", "bad-manifest")]
    [InlineData("""{ "id": "Other", "contract": 1 }""", "id-mismatch")]
    public async Task A_bad_manifest_is_reported_and_nothing_is_asked_or_loaded(string manifest, string code)
    {
        string assembly = NewAssembly();
        string folder = Folder("Bad", assembly, Step("BadStep", "BadTag"), manifest);
        RecordingTrust trust = new();

        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), trust).InstallFolderAsync(folder, id: code == "id-mismatch" ? "Expected" : null);

        Assert.Equal(code, Assert.Single(report.Problems).Code);
        Assert.Empty(trust.Requests);
        Assert.False(IsLoaded(assembly));
        Assert.False(File.Exists(Path.Combine(Install, "plugins.json")));
    }

    [Fact]
    public async Task Only_the_plugins_own_assemblies_are_scanned_and_base_types_are_followed_into_its_dependencies()
    {
        string dependency = NewAssembly("Dep");
        string own = NewAssembly("Own");
        string folder = Path.Combine(_root, "plugins", "Layered");
        string depDll = Path.Combine(folder, dependency + ".dll");
        Compile(depDll, dependency, "public abstract class LayerBase(string name) : StepBase(name, typeof(IStepResult), typeof(JsonStepResult)) { }\n" + Step("DependencyStep", "DependencyTag"));
        Compile(Path.Combine(folder, own + ".dll"), own, $$"""
            [StepTag("LayeredTag")]
            public sealed class LayeredStep : {{dependency}}.LayerBase
            {
                public LayeredStep() : base("LayeredStep") { }
                protected override Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? last, CancellationToken ct)
                    => Task.FromResult<IStepResult>(new JsonStepResult(this, new JValue("layered")));
            }
            """, depDll);
        WriteText(Path.Combine(folder, PluginFolder.ManifestName), Manifest("Layered", own));
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(report.Recorded, string.Join("; ", report.Problems.Select(p => p.Message)));
        Assert.Equal(["LayeredTag"], report.Registered.Select(r => r.Tag));
        Assert.False(registry.TryGetType("DependencyTag", out _));
        Assert.True(registry.TryGetType("LayeredTag", out Type? layered));
        Assert.Equal("LayeredStep", registry.Create("LayeredTag").Name);
        Assert.Equal("plugin:folder:Layered/1.2.0", AssemblyLoadContext.GetLoadContext(layered.BaseType!.Assembly)?.Name);
    }

    // ── Collisions, all or nothing ───────────────────────────────────────────

    [Fact]
    public async Task A_hook_name_of_another_plugin_stops_the_second_plugin_whole_and_names_both()
    {
        string first = NewAssembly();
        string second = NewAssembly();
        string hooks(string name) => $$"""public static class Hooks { [Hook(Name = "{{name}}")] public static Task Done(FinalizeArgs args) => Task.CompletedTask; }""";
        string a = Folder("HookA", first, Step("HookAStep", "HookATag") + hooks("Shared.Hook"), Manifest("Hook.A", first));
        string b = Folder("HookB", second, Step("HookBStep", "HookBTag") + hooks("Shared.Hook"), Manifest("Hook.B", second));
        StepRegistry registry = new();
        RecordingTrust trust = new();
        PluginLoader loader = new(Options(), registry, trust);
        Assert.True((await loader.InstallFolderAsync(a)).Recorded);

        LoadReport report = await loader.InstallFolderAsync(b);

        LoadProblem problem = Assert.Single(report.Problems, p => p.Code == "collision");
        Assert.Contains("'Shared.Hook'", problem.Message, StringComparison.Ordinal);
        Assert.Contains("plugin 'Hook.B' 1.2.0 (folder)", problem.Message, StringComparison.Ordinal);
        Assert.Contains("plugin 'Hook.A'", problem.Message, StringComparison.Ordinal);
        Assert.False(report.Recorded);
        Assert.False(registry.TryGetType("HookBTag", out _));
        Assert.False(IsLoaded(second));
        Assert.Single(trust.Requests);
        Assert.Equal(["Hook.A"], loader.Installed.Select(p => p.Id));
    }

    [Fact]
    public async Task A_step_tag_declared_twice_or_two_configurations_stop_the_plugin_before_trust()
    {
        string assembly = NewAssembly();
        string folder = Folder("Twice", assembly, Step("OneStep", "SameTag") + Step("OtherStep", "SameTag") + """
            public sealed class ConfigA : IPluginConfiguration { public Type? OptionsType => null; }
            public sealed class ConfigB : IPluginConfiguration { public Type? OptionsType => null; }
            """, Manifest("Twice", assembly));
        RecordingTrust trust = new();

        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), trust).InstallFolderAsync(folder);

        Assert.Contains(report.Problems, p => p.Code == "collision" && p.Message.Contains("'SameTag'", StringComparison.Ordinal));
        Assert.Contains(report.Problems, p => p.Code == "multiple-configurations");
        Assert.Empty(trust.Requests);
        Assert.False(IsLoaded(assembly));
    }

    [Fact]
    public async Task The_host_can_refuse_a_loaded_plugin_and_then_nothing_of_it_is_registered_or_recorded()
    {
        string assembly = NewAssembly();
        string folder = Folder("Refused", assembly, Step("RefusedStep", "RefusedTag") + Tool("RefusedTool", "read_file"), Manifest("Refused", assembly));
        StepRegistry registry = new();
        LoadedPlugin? seen = null;
        PluginLoader loader = new(Options(accept: plugin =>
        {
            seen = plugin;
            return [new LoadProblem("collision", "Tool 'read_file' of plugin 'Refused' is already registered by FileTools.")];
        }), registry, AlwaysTrust.Instance);

        LoadReport report = await loader.InstallFolderAsync(folder);

        Assert.True(report.Approved);
        Assert.False(report.Recorded);
        Assert.Null(report.Plugin);
        Assert.Contains(report.Problems, p => p.Message.Contains("FileTools", StringComparison.Ordinal));
        Assert.Equal([assembly + ".RefusedTool"], seen!.Tools.Select(t => t.FullName));
        Assert.False(registry.TryGetType("RefusedTag", out _));
        Assert.Empty(loader.Installed);
        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public async Task A_changed_folder_whose_new_hook_belongs_to_another_loaded_plugin_is_stopped_and_one_refused_by_the_host_is_not_recorded()
    {
        string hooks(string name) => $$"""public static class Hooks { [Hook(Name = "{{name}}")] public static Task Done(FinalizeArgs args) => Task.CompletedTask; }""";
        string alphaAssembly = NewAssembly();
        string alpha = Folder("AlphaF", alphaAssembly, Step("AlphaFStep", "AlphaFTag"), Manifest("Alpha.F", alphaAssembly, "1.0.0"));
        string betaAssembly = NewAssembly();
        string beta = Folder("BetaF", betaAssembly, Step("BetaFStep", "BetaFTag") + hooks("Shared.Y"), Manifest("Beta.F", betaAssembly));
        bool refuseTools = false;
        RecordingTrust trust = new();
        PluginLoader loader = new(Options(accept: plugin => refuseTools && plugin.Id == "Alpha.F"
            ? [new LoadProblem("collision", "Tool 'y' of plugin 'Alpha.F' is already offered by plugin 'Beta.F'.")]
            : []), new StepRegistry(), trust);
        Assert.True((await loader.InstallFolderAsync(alpha)).Recorded);
        Assert.True((await loader.InstallFolderAsync(beta)).Recorded);
        int asked = trust.Requests.Count;

        // Alpha 2.0.0 now declares Beta's hook name: stopped before trust, the entry keeps 1.0.0.
        Compile(Path.Combine(alpha, alphaAssembly + ".dll"), alphaAssembly, Step("AlphaFStep", "AlphaFTag") + hooks("Shared.Y"));
        WriteText(Path.Combine(alpha, PluginFolder.ManifestName), Manifest("Alpha.F", alphaAssembly, "2.0.0"));
        LoadReport update = await loader.InstallFolderAsync(alpha);
        Assert.Contains(update.Problems, p => p.Code == "collision" && p.Message.Contains("'Shared.Y'", StringComparison.Ordinal) && p.Message.Contains("'Beta.F'", StringComparison.Ordinal));
        Assert.False(update.Recorded);
        Assert.False(update.RestartRequired);
        Assert.Equal(asked, trust.Requests.Count);
        Assert.Equal("1.0.0", loader.Installed.Single(p => p.Id == "Alpha.F").Version);

        // Without the hook, but the host refuses its tools: approved, loaded aside for Accept, not recorded.
        Compile(Path.Combine(alpha, alphaAssembly + ".dll"), alphaAssembly, Step("AlphaFStep", "AlphaFTag") + Tool("AlphaTool", "y"));
        refuseTools = true;
        LoadReport refused = await loader.InstallFolderAsync(alpha);
        Assert.True(refused.Approved);
        Assert.False(refused.Recorded);
        Assert.Contains(refused.Problems, p => p.Message.Contains("Tool 'y'", StringComparison.Ordinal));
        Assert.Equal("1.0.0", loader.Installed.Single(p => p.Id == "Alpha.F").Version);

        // Accepted: recorded for the next start, as before.
        refuseTools = false;
        LoadReport accepted = await loader.InstallFolderAsync(alpha);
        Assert.True(accepted.RestartRequired);
        Assert.Equal("2.0.0", loader.Installed.Single(p => p.Id == "Alpha.F").Version);
    }

    [Fact]
    public async Task Installing_again_a_folder_removed_while_loaded_with_the_same_content_records_it_without_a_restart()
    {
        string assembly = NewAssembly();
        string folder = Folder("Again", assembly, Step("AgainStep", "AgainTag"), Manifest("Again.F", assembly));
        RecordingTrust trust = new();
        PluginLoader loader = new(Options(), new StepRegistry(), trust);
        Assert.True((await loader.InstallFolderAsync(folder)).Recorded);
        Assert.True((await loader.RemoveAsync("Again.F")).RestartRequired);

        LoadReport again = await loader.InstallFolderAsync(folder);

        Assert.True(again.Recorded);
        Assert.False(again.RestartRequired);
        Assert.Empty(again.Problems);
        Assert.NotNull(again.Plugin);
        Assert.Equal(TrustRequestKind.Install, trust.Requests.Last().Kind);
        Assert.Equal("Again.F", Assert.Single(loader.Installed).Id);
    }

    [Fact]
    public void A_folder_load_context_does_not_load_a_native_library_that_changed_after_the_approval()
    {
        string own = NewAssembly("Native");
        string folder = Path.Combine(_root, "plugins", "NativeF");
        Compile(Path.Combine(folder, own + ".dll"), own, "public static class Nothing { }");
        string native = Path.Combine(folder, "folder_native.dll");
        File.WriteAllBytes(native, [1, 2, 3]);
        PluginFolderTree tree = PluginFolder.Hash(folder);
        File.WriteAllBytes(native, [4, 5, 6]);
        IsolatedLoadContext context = IsolatedLoadContext.Named("test:native", Path.Combine(folder, own + ".dll"), [], [folder], tree);
        MethodInfo load = typeof(AssemblyLoadContext).GetMethod("LoadUnmanagedDll", BindingFlags.Instance | BindingFlags.NonPublic)!;

        TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() => load.Invoke(context, ["folder_native"]));

        Assert.Equal("content-changed", Assert.IsType<FileLoadException>(thrown.InnerException).Data[IsolatedLoadContext.ProblemKey]);
    }

    [Fact]
    public async Task A_folder_with_the_id_of_an_installed_nuget_plugin_is_refused_and_removing_a_folder_plugin_leaves_its_folder()
    {
        string assembly = NewAssembly();
        string folder = Folder("Same", assembly, Step("SameStep", "SameFolderTag"), Manifest("Same.Id", assembly));
        Directory.CreateDirectory(Install);
        File.WriteAllText(Path.Combine(Install, "plugins.json"),
            """{ "version": 1, "plugins": [ { "id": "Same.Id", "origin": "nuget", "version": "1.0.0", "requested": "1.0.0", "sha512": "sha512-x", "installedAtUtc": "2026-01-01T00:00:00+00:00", "steps": [] } ] }""");
        PluginLoader loader = new(Options(), new StepRegistry(), AlwaysTrust.Instance);

        Assert.Equal("duplicate-id", Assert.Single((await loader.InstallFolderAsync(folder)).Problems).Code);
        Assert.True((await loader.RemoveAsync("Same.Id")).Removed);

        Assert.True((await loader.InstallFolderAsync(folder)).Recorded);
        RemoveResult removed = await loader.RemoveAsync("Same.Id");
        Assert.True(removed.RestartRequired);
        Assert.True(File.Exists(Path.Combine(folder, assembly + ".dll")));
        Assert.Empty(await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).LoadInstalledAsync());
    }

    [Fact]
    public void A_scan_loads_nothing_and_a_folder_load_context_loads_only_the_approved_bytes()
    {
        string dependency = NewAssembly("Lib");
        string own = NewAssembly("Main");
        string folder = Path.Combine(_root, "plugins", "Checked");
        string depDll = Path.Combine(folder, dependency + ".dll");
        Compile(depDll, dependency, "public static class Helper { public static string Hello() => \"v1\"; }");
        Compile(Path.Combine(folder, own + ".dll"), own, "public static class Uses { public static string Call() => " + dependency + ".Helper.Hello(); }", depDll);
        string[] contexts = [.. AssemblyLoadContext.All.Select(c => c.Name ?? "")];

        PluginScanResult scan = PluginScanner.Scan([Path.Combine(folder, own + ".dll")], [depDll]);
        Assert.Empty(scan.Problems);
        Assert.Equal(contexts, AssemblyLoadContext.All.Select(c => c.Name ?? ""));
        Assert.False(IsLoaded(own));
        Assert.False(IsLoaded(dependency));

        PluginFolderTree tree = PluginFolder.Hash(folder);
        Compile(depDll, dependency, "public static class Helper { public static string Hello() => \"v2\"; }");
        IsolatedLoadContext context = IsolatedLoadContext.Named("test:checked", Path.Combine(folder, own + ".dll"), [], [], tree);
        Assert.Throws<FileLoadException>(() => context.LoadFromAssemblyName(new AssemblyName(dependency)));

        // A file added after the approval is not loaded from the folder at all.
        string late = NewAssembly("Late");
        Compile(Path.Combine(folder, late + ".dll"), late, "public static class Late { }");
        Assert.Throws<FileNotFoundException>(() => context.LoadFromAssemblyName(new AssemblyName(late)));
    }

    // ── Migration ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_steps_packages_json_is_migrated_with_its_locks_and_the_packages_load_from_the_new_registry()
    {
        string legacy = Path.Combine(_root, "step-packages");
        NugetTestFeed feed = new(Path.Combine(_root, "feed"));
        string assembly = NewAssembly("Migrated");
        string dll = Path.Combine(_root, "build", assembly + ".dll");
        Compile(dll, assembly, Step("MigratedStep", "MigratedTag"));
        feed.Pack("Migrated.Steps", "1.0.0", new() { [$"lib/net10.0/{assembly}.dll"] = File.ReadAllBytes(dll) });

        // Installed as the former loader would have left it: <legacy>/steps-packages.json and <legacy>/<id>/<version>/nuget.lock.json.
        string first = Path.Combine(_root, "first");
        PluginLoaderOptions firstOptions = Options() with { InstallDirectory = first, Sources = [feed.Folder], AllowNetwork = true };
        Assert.True((await new PluginLoader(firstOptions, new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Migrated.Steps", "1.0.0")).Recorded);
        JsonObject registry = JsonNode.Parse(File.ReadAllText(Path.Combine(first, "plugins.json")))!.AsObject();
        JsonArray entries = registry["plugins"]!.AsArray();
        registry.Remove("plugins");
        foreach (JsonNode? item in entries)
        {
            item!.AsObject().Remove("origin");
        }

        registry["packages"] = entries;
        WriteText(Path.Combine(legacy, "steps-packages.json"), registry.ToJsonString());
        WriteText(Path.Combine(legacy, "migrated.steps", "1.0.0", "nuget.lock.json"),
            File.ReadAllText(Path.Combine(first, "plugin-locks", "migrated.steps", "1.0.0", "nuget.lock.json")));

        StepRegistry steps = new();
        PluginLoader loader = new(Options() with { LegacyStepPackagesDirectory = legacy }, steps, NeverTrust.Instance);
        LoadReport report = Assert.Single(await loader.LoadInstalledAsync());

        Assert.True(report.Approved, string.Join("; ", report.Problems.Select(p => p.Message)));
        Assert.True(steps.TryGetType("MigratedTag", out _));
        Assert.Contains("migrated", Assert.Single(loader.StoreNotes), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(legacy, "steps-packages.json")));
        Assert.True(File.Exists(Path.Combine(legacy, "steps-packages.json.migrated")));
        Assert.True(File.Exists(Path.Combine(Install, "plugin-locks", "migrated.steps", "1.0.0", "nuget.lock.json")));
        InstalledPlugin installed = Assert.Single(loader.Installed);
        Assert.Equal((PluginOrigin.Nuget, "1.0.0"), (installed.Origin, installed.Requested));

        // Once: a second loader reads plugins.json and finds nothing to migrate.
        Assert.Empty(new PluginLoader(Options() with { LegacyStepPackagesDirectory = legacy }, new StepRegistry(), NeverTrust.Instance).StoreNotes);
    }
}
