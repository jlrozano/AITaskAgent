using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.Loader;
using WorkflowDesigner.Testing;
using static AITaskAgent.NugetLoader.Tests.PluginTestKit;

namespace AITaskAgent.NugetLoader.Tests;

/// <summary>
/// A NuGet plugin shaped as a folder (architecture §25.2): the <c>.nupkg</c> carries <c>plugin/plugin.json</c>, its own assemblies, private
/// DLLs and content files under <c>plugin/</c>, and declares its third-party libraries as nuspec dependencies that the resolver downloads from
/// the configured sources. This is how <c>Samples/CodebaseAnalysis.Designer</c> ships as one file.
/// </summary>
[Collection(PluginCollection.Name)]
public sealed class PluginNupkgFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfd-plugin-nupkg-folders", Guid.NewGuid().ToString("N"));
    private readonly NugetTestFeed _repository;
    private readonly NugetTestFeed _feed;

    public PluginNupkgFolderTests()
    {
        // Two sources, as in the designer: the local repository (the uploaded plugin) and a NuGet feed (its dependencies).
        _repository = new NugetTestFeed(Path.Combine(_root, "nuget-repo"));
        _feed = new NugetTestFeed(Path.Combine(_root, "feed"));
    }

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

    private PluginLoaderOptions Options(bool withFeed = true) => new()
    {
        Sources = withFeed ? [_repository.Folder, _feed.Folder] : [_repository.Folder],
        AllowNetwork = true,
        CacheDirectory = Path.Combine(_root, "cache"),
        InstallDirectory = Path.Combine(_root, "install")
    };

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

    /// <summary>The names of what <see cref="PackFolderPlugin"/> built.</summary>
    private sealed record Built(string Main, string Helper, string Dependency);

    /// <summary>
    /// Packs <c>id</c> 1.0.0 into the repository: <c>plugin/plugin.json</c> naming only the main assembly, the main assembly (a tagged step, a
    /// tool source that reports its folder, a probe that calls the helper and the dependency), a helper DLL beside it with a tagged step of its
    /// own (not an own assembly: never scanned), a template, and a private executable's DLL in a subfolder; plus a nuspec dependency on
    /// <c>Acme.Dep</c> [1.0.0, ) packed into the feed unless <paramref name="packDependency"/> is false.
    /// </summary>
    private Built PackFolderPlugin(string id, string? manifestId = null, bool packDependency = true)
    {
        string main = NewAssembly("Main");
        string helper = NewAssembly("Helper");
        string dependency = NewAssembly("Dep");
        string build = Path.Combine(_root, "build", main);
        byte[] depBytes = NugetTestFeed.Library(dependency, "namespace " + dependency + " { public static class DepValue { public static string Get() => \"from-dependency\"; } }");
        string depDll = Path.Combine(build, dependency + ".dll");
        Directory.CreateDirectory(build);
        File.WriteAllBytes(depDll, depBytes);
        Compile(Path.Combine(build, helper + ".dll"), helper,
            Step("HelperStep", "HelperTag") + "\npublic static class HelperValue { public static string Get() => \"from-helper\"; }\n");
        Compile(Path.Combine(build, main + ".dll"), main, $$"""
            {{Step("MainStep", "MainTag")}}
            public sealed class FolderTool : ITool
            {
                private readonly string _folder;
                public FolderTool(string folder) => _folder = folder;
                public string Name => "folder.tool";
                public string Description => _folder;
                public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
                public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken = default)
                    => Task.FromResult(_folder);
            }
            public sealed class FolderTools : IToolSource
            {
                public IEnumerable<ITool> Create(PluginContext context) => [new FolderTool(context.Folder)];
            }
            public static class Probe
            {
                public static string Run() => {{helper}}.HelperValue.Get() + "|" + {{dependency}}.DepValue.Get();
            }
            """, Path.Combine(build, helper + ".dll"), depDll);

        if (packDependency)
        {
            _feed.Pack("Acme.Dep", "1.0.0", new() { [$"lib/net10.0/{dependency}.dll"] = depBytes });
        }

        _repository.Pack(id, "1.0.0", new()
        {
            ["plugin/plugin.json"] = System.Text.Encoding.UTF8.GetBytes(
                $$"""{ "id": "{{manifestId ?? id}}", "version": "9.9.9", "description": "Folder in a package.", "contract": 1, "assemblies": [ "{{main}}.dll" ] }"""),
            [$"plugin/{main}.dll"] = File.ReadAllBytes(Path.Combine(build, main + ".dll")),
            [$"plugin/{helper}.dll"] = File.ReadAllBytes(Path.Combine(build, helper + ".dll")),
            ["plugin/templates/report.md"] = System.Text.Encoding.UTF8.GetBytes("# {{ title }}"),
            ["plugin/js/orders.js"] = System.Text.Encoding.UTF8.GetBytes("exports.ok = function (args) { return true; };"),
            ["plugin/tool/Private.dll"] = NugetTestFeed.Library(NewAssembly("Private"), "public static class P { }")
        }, ("net10.0", "Acme.Dep", "1.0.0"));
        return new Built(main, helper, dependency);
    }

    [Fact]
    public async Task A_package_with_plugin_json_loads_as_a_folder_with_its_content_and_downloads_its_dependencies_from_a_source()
    {
        Built built = PackFolderPlugin("Acme.FolderPkg");
        StepRegistry registry = new();
        RecordingTrust trust = new();
        PluginLoader loader = new(Options(), registry, trust);

        LoadReport report = await loader.InstallAsync("Acme.FolderPkg", "1.0.0");

        Assert.True(report.Recorded, string.Join("; ", report.Problems.Select(p => p.Code + ": " + p.Message)));
        Assert.Empty(report.Problems);
        LoadedPlugin plugin = report.Plugin!;

        // Only the manifest's assembly is scanned: the helper's tagged step is not the plugin's; the nuspec's version wins over the manifest's.
        Assert.Equal(["MainTag"], report.Registered.Select(r => r.Tag));
        Assert.False(registry.TryGetType("HelperTag", out _));
        Assert.Equal(("1.0.0", PluginOrigin.Nuget), (plugin.Version, plugin.Origin));
        Assert.Equal([built.Main + ".dll"], report.Contributions?.Assemblies);
        Assert.True(registry.TryGetType("MainTag", out Type? step));
        Assert.Equal("plugin:nuget:Acme.FolderPkg/1.0.0", AssemblyLoadContext.GetLoadContext(step.Assembly)?.Name);

        // The folder is the extracted plugin/ with its content files and subfolders, inside the cache.
        Assert.EndsWith(Path.Combine("content", "plugin"), plugin.Folder, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_root, "cache")), plugin.Folder, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# {{ title }}", File.ReadAllText(Path.Combine(plugin.Folder, "templates", "report.md")));
        Assert.True(File.Exists(Path.Combine(plugin.Folder, "tool", "Private.dll")));
        IToolSource source = (IToolSource)Activator.CreateInstance(Assert.Single(plugin.ToolSources))!;
        Assert.Equal(plugin.Folder, Assert.Single(source.Create(new PluginContext(plugin.Id, plugin.Version, plugin.Folder, null!))).Description);

        // The helper beside the own assembly and the dependency downloaded from the feed load in the plugin's context.
        Type probe = step.Assembly.GetType(built.Main + ".Probe")!;
        Assert.Equal("from-helper|from-dependency", probe.GetMethod("Run")!.Invoke(null, null));

        // The trust request shows the closure (the dependency with its hash) and every DLL of plugin/.
        TrustRequest request = Assert.Single(trust.Requests);
        Assert.Equal("Acme.Dep", Assert.Single(request.Dependencies).Id);
        Assert.Contains($"plugin/{built.Main}.dll", request.Assemblies);
        Assert.Contains($"plugin/{built.Helper}.dll", request.Assemblies);
        Assert.Contains("plugin/tool/Private.dll", request.Assemblies);
        Assert.Contains($"{built.Dependency}.dll", request.Assemblies);

        // A restarted loader reloads it from the cache and the lock, offline.
        PluginLoader restarted = new(Options() with { AllowNetwork = false }, new StepRegistry(), NeverTrust.Instance);
        LoadReport reloaded = Assert.Single(await restarted.LoadInstalledAsync(CancellationToken.None));
        Assert.Empty(reloaded.Problems);
        Assert.Equal(["MainTag"], reloaded.Registered.Select(r => r.Tag));
    }

    [Fact]
    public async Task A_file_added_to_the_extracted_plugin_folder_stops_the_reload()
    {
        PackFolderPlugin("Acme.Tampered");
        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Acme.Tampered", "1.0.0");
        Assert.True(report.Recorded);

        File.WriteAllText(Path.Combine(report.Plugin!.Folder, "templates", "planted.md"), "not in the package");
        PluginLoader restarted = new(Options() with { AllowNetwork = false }, new StepRegistry(), AlwaysTrust.Instance);
        LoadReport reloaded = Assert.Single(await restarted.LoadInstalledAsync(CancellationToken.None));

        Assert.Null(reloaded.Plugin);
        Assert.Equal("content-mismatch", Assert.Single(reloaded.Problems).Code);
    }

    [Fact]
    public async Task The_js_libraries_of_a_package_land_in_the_plugin_folder_and_a_changed_one_stops_the_reload()
    {
        // architecture §18.7: a .nupkg plugin carries its JavaScript libraries as plugin/js/<name>.js; the host reads them from
        // PluginContext.Folder/js. They are entries of the verified package, so a file changed in the cache is content-mismatch.
        PackFolderPlugin("Acme.Js");
        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallAsync("Acme.Js", "1.0.0");
        Assert.True(report.Recorded, string.Join("; ", report.Problems.Select(p => p.Code + ": " + p.Message)));
        string library = Path.Combine(report.Plugin!.Folder, "js", "orders.js");
        Assert.Equal("exports.ok = function (args) { return true; };", File.ReadAllText(library));

        File.WriteAllText(library, "exports.ok = function (args) { return false; };");
        PluginLoader restarted = new(Options() with { AllowNetwork = false }, new StepRegistry(), AlwaysTrust.Instance);
        LoadReport reloaded = Assert.Single(await restarted.LoadInstalledAsync(CancellationToken.None));

        Assert.Null(reloaded.Plugin);
        Assert.Equal("content-mismatch", Assert.Single(reloaded.Problems).Code);
    }

    [Fact]
    public async Task A_manifest_with_another_id_stops_the_install()
    {
        PackFolderPlugin("Acme.Mismatch", manifestId: "Other.Id");
        StepRegistry registry = new();
        PluginLoader loader = new(Options(), registry, AlwaysTrust.Instance);

        LoadReport report = await loader.InstallAsync("Acme.Mismatch", "1.0.0");

        Assert.False(report.Recorded);
        Assert.Equal("id-mismatch", Assert.Single(report.Problems).Code);
        Assert.Empty(loader.Installed);
        Assert.Empty(registry.GetRegisteredTypes());
    }

    [Fact]
    public async Task A_dependency_no_source_has_is_nuget_not_found_naming_it_with_what_to_do()
    {
        PackFolderPlugin("Acme.Lonely", packDependency: false);
        PluginLoader loader = new(Options(withFeed: false), new StepRegistry(), AlwaysTrust.Instance);

        LoadReport report = await loader.InstallAsync("Acme.Lonely", "1.0.0");

        LoadProblem problem = Assert.Single(report.Problems);
        Assert.Equal("nuget-not-found", problem.Code);
        Assert.Contains("'Acme.Dep'", problem.Message, StringComparison.Ordinal);
        Assert.EndsWith(PluginLoader.NotFoundHint, problem.Message, StringComparison.Ordinal);
        Assert.Empty(loader.Installed);
    }

    [Fact]
    public void An_entry_that_escapes_plugin_is_refused_when_extracting()
    {
        string root = Path.GetFullPath(Path.Combine(_root, "x", "plugin"));
        Assert.Null(NugetResolver.PluginEntryPath(root, "../evil.dll"));
        Assert.Null(NugetResolver.PluginEntryPath(root, "a/../../evil.dll"));
        Assert.Null(NugetResolver.PluginEntryPath(root, "C:/evil.dll"));
        Assert.Null(NugetResolver.PluginEntryPath(root, "/evil.dll"));
        Assert.Equal(Path.Combine(root, "templates", "a.md"), NugetResolver.PluginEntryPath(root, "templates/a.md"));
    }
}
