using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Models;
using AITaskAgent.CSharp;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.CSharp.Tests;

/// <summary>
/// What a host does with a package's unit, in a test: compile, register the unit and the schemas of its response types in a
/// service provider, build pipelines with the YAML factory and run them. No LLM, no designer.
/// </summary>
internal sealed class UnitHarness : IDisposable
{
    private UnitHarness(ServiceProvider services, YamlPipelineFactory factory, SchemaRegistry schemas, UnitCompilation compilation)
    {
        Services = services;
        Factory = factory;
        Schemas = schemas;
        Compilation = compilation;
    }

    public ServiceProvider Services { get; }

    public YamlPipelineFactory Factory { get; }

    public SchemaRegistry Schemas { get; }

    public UnitCompilation Compilation { get; }

    public static async Task<UnitCompilation> CompileAsync(CSharpUnitSources sources, CSharpCompileOptions? options = null)
        => await new CSharpUnitCompiler(options).CompileAsync(sources);

    public static async Task<UnitHarness> CreateAsync(
        CSharpUnitSources sources, string package = "pkg", Action<IServiceCollection>? configure = null, CSharpCompileOptions? options = null,
        IReadOnlyDictionary<string, string>? schemaFiles = null)
    {
        UnitCompilation compilation = await CompileAsync(sources, options);
        if (!compilation.Success)
        {
            throw new InvalidOperationException("The unit does not compile:\n" + string.Join('\n', compilation.Diagnostics.Select(d => $"{d.Id} {d.File}:{d.Line}:{d.Column} {d.Message}")));
        }

        ServiceCollection services = new();
        services.AddLogging();
        services.AddCSharpExtension();
        services.AddSingleton<SchemaRegistry>();
        services.AddSingleton<HookRegistry>();
        services.AddSingleton<IHookRegistry>(sp => sp.GetRequiredService<HookRegistry>());
        configure?.Invoke(services);
        services.AddCSharpUnit(compilation.Unit!, package);

        ServiceProvider provider = services.BuildServiceProvider();
        SchemaRegistry schemas = provider.GetRequiredService<SchemaRegistry>();
        foreach ((string name, string json) in schemaFiles ?? new Dictionary<string, string>())
        {
            await schemas.AddAsync(name, json);
        }

        foreach (UnitResponseType type in compilation.Unit!.ResponseTypes)
        {
            schemas.Add(type.Name, type.Schema!);
        }

        YamlPipelineFactory factory = new(provider, provider.GetRequiredService<IStepRegistry>(), schemas, new Pipeline());
        return new UnitHarness(provider, factory, schemas, compilation);
    }

    public async Task<IStepResult> RunAsync(string yaml, JToken? input = null, PipelineContext? context = null)
    {
        YamlPipeline pipeline = Factory.CreateFromYaml(yaml);
        return await pipeline.ExecuteAsync(input ?? new JObject(), context ?? new PipelineContext(null, Services));
    }

    public void Dispose() => Services.Dispose();

    /// <summary>One <c>CSharpStep</c> as YAML plus the source the compiler needs, with the line of its first code line.</summary>
    public static (string Yaml, CSharpStepSource Source) Step(string id, string code, int yamlLineOffset = 0, string extra = "")
    {
        string indented = string.Join('\n', code.Split('\n').Select(l => "      " + l));
        string yaml = $"  - id: {id}\n    type: CSharpStep\n{extra}    code: |\n{indented}\n";
        // Line of the first code line inside a document that starts with "name: t\nsteps:\n" (2 lines) after `yamlLineOffset` earlier lines.
        int first = 2 + yamlLineOffset + 1 + 1 + CountLines(extra) + 1 + 1;
        return (yaml, new CSharpStepSource(id, code, first));
    }

    private static int CountLines(string text) => text.Length == 0 ? 0 : text.TrimEnd('\n').Split('\n').Length;

    public static string Document(params string[] steps) => "name: t\nsteps:\n" + string.Concat(steps);
}
