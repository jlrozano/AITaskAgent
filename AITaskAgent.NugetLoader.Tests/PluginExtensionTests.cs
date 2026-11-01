using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;
using System.Runtime.Loader;
using WorkflowDesigner.Testing;
using static AITaskAgent.NugetLoader.Tests.PluginTestKit;

namespace AITaskAgent.NugetLoader.Tests;

/// <summary>
/// A plugin driven by its <see cref="IExtension"/> (architecture §25.2, §25.3a): one extension = entry point, two = <c>multiple-extensions</c>
/// before trust, none = type discovery; nothing of the plugin runs before approval; <c>Info()</c> is compared with what <c>Register</c> did.
/// </summary>
[Collection(PluginCollection.Name)]
public sealed class PluginExtensionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wfd-plugin-extensions", Guid.NewGuid().ToString("N"));

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

    private PluginLoaderOptions Options() => new() { CacheDirectory = Path.Combine(_root, "cache"), InstallDirectory = Path.Combine(_root, "install") };

    private string Folder(string name, string assembly, string body, string? manifestExtension = null)
    {
        string folder = Path.Combine(_root, "plugins", name);
        Compile(Path.Combine(folder, assembly + ".dll"), assembly, body);
        string extension = manifestExtension == null ? string.Empty : $$""", "extension": "{{assembly}}.{{manifestExtension}}" """;
        WriteText(Path.Combine(folder, PluginFolder.ManifestName),
            $$"""{ "id": "{{name}}", "version": "1.0.0", "contract": 1, "assemblies": [ "{{assembly}}.dll" ]{{extension}} }""");
        return folder;
    }

    private static string Extension(string className, string register, string info, string constructor = "")
        => $$"""
            public sealed class {{className}} : IExtension
            {
                public {{className}}() { {{constructor}} }
                public string Name => "{{className}}";
                public void Register(IExtensionBuilder builder) { {{register}} }
                public ExtensionInfo Info() { {{info}} }
            }
            """;

    [Fact]
    public async Task One_extension_drives_the_plugin_and_only_what_it_registers_is_registered()
    {
        string assembly = NewAssembly("Ext");
        string folder = Folder("OneExt", assembly,
            Step("TaggedStep", "ExtTag") + Step("PlainStep") + Tool("EchoTool", "ext_echo") + Extension("Entry",
                register: "builder.AddStepsFromAssembly(typeof(Entry).Assembly); builder.AddToolType(typeof(EchoTool));",
                info: """
                    return new ExtensionInfo { Entries = [ ExtensionEntry.For(ExtensionKinds.Step, typeof(TaggedStep), "ExtTag"), ExtensionEntry.For(ExtensionKinds.Tool, typeof(EchoTool)) ] };
                    """));
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(report.Recorded);
        Assert.Equal(assembly + ".Entry", report.Contributions!.Extension?.TypeName);
        Assert.DoesNotContain(report.Problems, p => p.Code == "info-mismatch");
        Assert.True(registry.TryGetType("ExtTag", out _));
        Assert.False(registry.TryGetType(assembly + ".PlainStep", out _));
        PluginExtension extension = report.Plugin!.Extension!;
        Assert.True(extension.Matches);
        Assert.Equal(assembly + ".Entry", extension.TypeName);
        Assert.Equal([assembly + ".EchoTool"], report.Plugin.Tools.Select(t => t.FullName));
        Assert.Equal(["ExtTag"], report.Registered.Select(s => s.Tag));
        Assert.Equal(["ExtTag"], report.Plugin.Steps.Select(s => s.Tag));
    }

    [Fact]
    public async Task Info_that_does_not_match_the_registration_is_a_warning_naming_the_items_and_the_real_registration_wins()
    {
        string assembly = NewAssembly("Mis");
        string folder = Folder("MisExt", assembly,
            Step("TaggedStep", "MisTag") + Tool("EchoTool", "mis_echo") + Extension("Entry",
                register: "builder.AddStepsFromAssembly(typeof(Entry).Assembly); builder.AddToolType(typeof(EchoTool));",
                info: """
                    return new ExtensionInfo { Entries = [ new ExtensionEntry(ExtensionKinds.Step, "MisTag"), new ExtensionEntry(ExtensionKinds.Step, "GhostStep") ] };
                    """));
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(report.Recorded);
        LoadProblem mismatch = Assert.Single(report.Problems, p => p.Code == "info-mismatch");
        Assert.Contains("GhostStep", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(assembly + ".EchoTool", mismatch.Message, StringComparison.Ordinal);
        PluginExtension extension = report.Plugin!.Extension!;
        Assert.False(extension.Matches);
        Assert.Equal(["GhostStep"], extension.Missing.Select(e => e.Name));
        Assert.Equal([assembly + ".EchoTool"], extension.Extra.Select(e => e.Type));
        Assert.Equal([assembly + ".EchoTool"], report.Plugin.Tools.Select(t => t.FullName));
        Assert.True(registry.TryGetType("MisTag", out _));
        Assert.False(registry.TryGetType("GhostStep", out _));
    }

    [Fact]
    public async Task Two_extensions_stop_the_plugin_from_metadata_before_trust_and_nothing_is_loaded()
    {
        string assembly = NewAssembly("Two");
        string folder = Folder("TwoExt", assembly,
            Step("TaggedStep", "TwoTag") + Extension("First", "", "return ExtensionInfo.Empty;") + Extension("Second", "", "return ExtensionInfo.Empty;"));
        string[] contexts = [.. AssemblyLoadContext.All.Select(c => c.Name ?? "")];
        List<TrustRequest> asked = [];

        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), new CountingTrust(asked)).InstallFolderAsync(folder);

        LoadProblem problem = Assert.Single(report.Problems, p => p.Code == "multiple-extensions");
        Assert.Contains("First", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Second", problem.Message, StringComparison.Ordinal);
        Assert.False(report.Recorded);
        Assert.Empty(asked);
        Assert.Null(report.Plugin);
        Assert.False(IsLoaded(assembly));
        Assert.Equal(contexts, AssemblyLoadContext.All.Select(c => c.Name ?? ""));
        Assert.Equal([assembly + ".First", assembly + ".Second"], report.Contributions!.Extensions);
        Assert.Null(report.Contributions.Extension);
    }

    [Fact]
    public async Task The_manifest_names_the_entry_point_among_several_extensions()
    {
        string assembly = NewAssembly("Named");
        string folder = Folder("NamedExt", assembly,
            Step("TaggedStep", "NamedTag") + Step("OtherStep", "OtherTag")
            + Extension("First", "builder.AddStep<TaggedStep>();", "return ExtensionInfo.Empty;")
            + Extension("Second", "builder.AddStep<OtherStep>();", "return ExtensionInfo.Empty;"),
            manifestExtension: "Second");
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(report.Recorded);
        Assert.Equal(assembly + ".Second", report.Plugin!.Extension!.TypeName);
        Assert.True(registry.TryGetType("OtherTag", out _));
        Assert.False(registry.TryGetType("NamedTag", out _));
    }

    [Fact]
    public async Task A_manifest_that_names_a_missing_extension_stops_the_plugin()
    {
        string assembly = NewAssembly("Gone");
        string folder = Folder("GoneExt", assembly, Step("TaggedStep", "GoneTag") + Extension("Real", "", "return ExtensionInfo.Empty;"), manifestExtension: "Imaginary");

        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.Contains(report.Problems, p => p.Code == "extension-not-found" && p.Message.Contains("Imaginary", StringComparison.Ordinal));
        Assert.False(report.Recorded);
        Assert.False(IsLoaded(assembly));
    }

    [Fact]
    public async Task A_plugin_without_an_extension_keeps_loading_by_type_discovery()
    {
        string assembly = NewAssembly("Plain");
        string folder = Folder("PlainPlugin", assembly, Step("TaggedStep", "PlainTag") + Step("UntaggedStep") + Tool("EchoTool", "plain_echo"));
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(report.Recorded);
        Assert.Null(report.Plugin!.Extension);
        Assert.Null(report.Contributions!.Extension);
        Assert.True(registry.TryGetType("PlainTag", out _));
        Assert.True(registry.TryGetType(assembly + ".UntaggedStep", out _));
        Assert.Equal([assembly + ".EchoTool"], report.Plugin.Tools.Select(t => t.FullName));
    }

    [Fact]
    public async Task Nothing_of_the_plugin_runs_before_approval_and_the_extension_runs_only_after()
    {
        string assembly = NewAssembly("Gate");
        string marks = Path.Combine(_root, "marks");
        Directory.CreateDirectory(marks);
        string Mark(string name) => Path.Combine(marks, name).Replace("\\", "\\\\", StringComparison.Ordinal);
        string folder = Folder("GateExt", assembly,
            Step("TaggedStep", "GateTag") + Extension("Entry",
                register: $"System.IO.File.WriteAllText(\"{Mark("register")}\", \"x\"); builder.AddStep<TaggedStep>();",
                info: $"System.IO.File.WriteAllText(\"{Mark("info")}\", \"x\"); return ExtensionInfo.Empty;",
                constructor: $"System.IO.File.WriteAllText(\"{Mark("ctor")}\", \"x\");"));
        string[] contexts = [.. AssemblyLoadContext.All.Select(c => c.Name ?? "")];
        List<TrustRequest> asked = [];

        LoadReport refused = await new PluginLoader(Options(), new StepRegistry(), new CountingTrust(asked, answer: false)).InstallFolderAsync(folder);

        TrustRequest request = Assert.Single(asked);
        Assert.Equal(assembly + ".Entry", request.Contributions!.Extension?.TypeName);
        Assert.Contains(refused.Problems, p => p.Code == "not-approved");
        Assert.Empty(Directory.GetFiles(marks));
        Assert.False(IsLoaded(assembly));
        Assert.Equal(contexts, AssemblyLoadContext.All.Select(c => c.Name ?? ""));

        LoadReport approved = await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(approved.Recorded);
        Assert.Equal(["ctor", "info", "register"], Directory.GetFiles(marks).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(IsLoaded(assembly));
    }

    [Fact]
    public async Task An_extension_whose_Register_throws_registers_nothing_and_the_plugin_is_not_loaded()
    {
        string assembly = NewAssembly("Boom");
        string folder = Folder("BoomExt", assembly,
            Step("TaggedStep", "BoomTag") + Extension("Entry", "builder.AddStep<TaggedStep>(); throw new InvalidOperationException(\"no way\");", "return ExtensionInfo.Empty;"));
        StepRegistry registry = new();

        LoadReport report = await new PluginLoader(Options(), registry, AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.Contains(report.Problems, p => p.Code == "extension-failed" && p.Message.Contains("no way", StringComparison.Ordinal));
        Assert.Null(report.Plugin);
        Assert.False(report.Recorded);
        Assert.False(registry.TryGetType("BoomTag", out _));
    }

    [Fact]
    public async Task An_Info_that_throws_is_taken_as_empty_with_a_warning_and_the_registration_is_all_extra()
    {
        string assembly = NewAssembly("Lie");
        string folder = Folder("LieExt", assembly,
            Step("TaggedStep", "LieTag") + Extension("Entry", "builder.AddStep<TaggedStep>();", "throw new InvalidOperationException(\"info broke\");"));

        LoadReport report = await new PluginLoader(Options(), new StepRegistry(), AlwaysTrust.Instance).InstallFolderAsync(folder);

        Assert.True(report.Recorded);
        Assert.Contains(report.Problems, p => p.Code == "info-failed" && p.Message.Contains("info broke", StringComparison.Ordinal));
        Assert.Contains(report.Problems, p => p.Code == "info-mismatch" && p.Message.Contains("LieTag", StringComparison.Ordinal));
        Assert.Equal(["LieTag"], report.Plugin!.Extension!.Extra.Select(e => e.Name));
    }

    private sealed class CountingTrust(List<TrustRequest> asked, bool answer = true) : ITrustPolicy
    {
        public ValueTask<bool> ApproveAsync(TrustRequest request, CancellationToken cancellationToken)
        {
            asked.Add(request);
            return ValueTask.FromResult(answer);
        }
    }
}
