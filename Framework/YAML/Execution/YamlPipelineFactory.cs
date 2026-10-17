using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.YAML.Abstractions;
using AITaskAgent.YAML.Attributes;
using AITaskAgent.YAML.Compilation;
using AITaskAgent.YAML.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AITaskAgent.YAML.Execution;

/// <summary>
/// YAML pipeline document model. A pipeline file contains a name and a list of steps.
/// </summary>
internal sealed class YamlPipelineDocument
{
    public string Name { get; init; } = string.Empty;
    public List<IYamlStep> Steps { get; init; } = [];
}

/// <summary>
/// A built pipeline ready for execution.
/// </summary>
public sealed class YamlPipeline(string name, IReadOnlyList<IStep> steps, IPipeline pipeline)
{
    public string Name { get; } = name;
    public IReadOnlyList<IStep> Steps { get; } = steps;

    public Task<IStepResult> ExecuteAsync<T>(
        T input,
        PipelineContext? context = null,
        CancellationToken cancellationToken = default)
        => pipeline.ExecuteAsync(Name, Steps, input, context, cancellationToken: cancellationToken);
}

/// <summary>
/// Builds a YAML-defined pipeline from one or more YAML files.
///
/// Responsibilities:
///   1. Merge all *.yaml files from a folder (multi-file pipelines).
///   2. Discover IYamlStep implementations via [YamlStepTag] reflection.
///   3. Deserialize using YamlDotNet with DI-powered object factory.
///   4. Inject compiled InputType/OutputType from SchemaCompiler.
///   5. Fail-fast semantic validation: cycle detection + schema mismatch.
///   6. Build topologically ordered step list.
/// </summary>
public sealed class YamlPipelineFactory(
    IServiceProvider serviceProvider,
    SchemaCompiler schemaCompiler,
    IPipeline pipeline,
    IOptions<LlmEnvironment>? llmEnvironment = null)
{
    /// <summary>Creates a pipeline from all *.yaml files in a folder tree.</summary>
    public async Task<YamlPipeline> CreateFromFolderAsync(string pipelineFolder)
    {
        var files = Directory.GetFiles(pipelineFolder, "*.yaml", SearchOption.AllDirectories);
        var parts = new List<string>();
        foreach (var file in files)
            parts.Add(await File.ReadAllTextAsync(file));

        var merged = string.Join($"{Environment.NewLine}---{Environment.NewLine}", parts);
        return CreateFromYaml(merged);
    }

    /// <summary>Creates a pipeline from a merged YAML string.</summary>
    public YamlPipeline CreateFromYaml(string mergedYaml)
    {
        var deserializer = BuildDeserializer();
        var doc = DeserializeMerged(deserializer, mergedYaml);

        // Resolve each YamlLlmStep's role. Steps are deserialized against a placeholder role
        // (NullLlmService) because the profile name is only known after parsing.
        foreach (var step in doc.Steps.OfType<YamlLlmStep>())
        {
            step.ApplyRole(ResolveRole(step.ProfileName, step.StepId));
        }

        // Inject compiled InputType / OutputType
        foreach (var step in doc.Steps)
        {
            if (step is AITaskAgent.Core.Steps.StepBase stepBase)
            {
                stepBase.InputType = schemaCompiler.GetCompiledType(RequireSchema(step.InputSchema, step.StepId, "inputSchema"));
                stepBase.OutputType = schemaCompiler.GetCompiledType(RequireSchema(step.OutputSchema, step.StepId, "outputSchema"));

            }
        }

        // Fail-fast DAG validation
        var orderedSteps = ValidateAndOrder(doc.Steps);

        return new YamlPipeline(doc.Name, orderedSteps, pipeline);
    }

    // ── Private ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every document of a multi-document stream (files joined with <c>---</c>) and combines
    /// them: steps are concatenated, and the pipeline name is the first non-empty one.
    /// <c>Deserialize&lt;T&gt;(string)</c> accepts a single document only and throws on the second.
    /// </summary>
    private static YamlPipelineDocument DeserializeMerged(IDeserializer deserializer, string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();

        var name = string.Empty;
        var steps = new List<IYamlStep>();
        var count = 0;

        while (parser.Accept<DocumentStart>(out _))
        {
            var part = deserializer.Deserialize<YamlPipelineDocument>(parser);
            if (part == null) continue; // empty document
            count++;
            if (string.IsNullOrEmpty(name)) name = part.Name;
            steps.AddRange(part.Steps);
        }

        if (count == 0)
            throw new InvalidOperationException("The pipeline YAML contains no documents.");

        return new YamlPipelineDocument { Name = name, Steps = steps };
    }

    private IDeserializer BuildDeserializer()
    {
        var builder = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithObjectFactory(type =>
            {
                try { return ActivatorUtilities.CreateInstance(serviceProvider, type); }
                catch { return Activator.CreateInstance(type)!; }
            });

        // Discover all IYamlStep types decorated with [YamlStepTag]
        var stepTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => typeof(IYamlStep).IsAssignableFrom(t)
                     && !t.IsInterface
                     && !t.IsAbstract
                     && t.GetCustomAttribute<YamlStepTagAttribute>() != null);

        foreach (var type in stepTypes)
        {
            var tag = type.GetCustomAttribute<YamlStepTagAttribute>()!.TagName;
            builder.WithTagMapping($"!{tag}", type);
        }

        return builder.Build();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch { return []; }
    }

    /// <summary>
    /// Topological sort (Kahn's algorithm) + schema-mismatch check.
    /// Throws ConfigurationException on cycle or schema mismatch.
    /// </summary>
    /// <summary>
    /// Every YAML step must declare both schemas: they are what lets the factory compile real
    /// types and check that adjacent steps agree before anything runs.
    /// </summary>
    private static string RequireSchema(string? schema, string stepId, string yamlKey)
        => string.IsNullOrWhiteSpace(schema)
            ? throw new InvalidOperationException(
                $"Step '{stepId}' is missing '{yamlKey}'. Every YAML step must declare both " +
                "inputSchema and outputSchema so their types can be compiled and validated.")
            : schema;

    /// <summary>
    /// Resolves the role a step should run with.
    ///
    /// A step names its role via <c>profileName</c>; when it does not, the environment's
    /// <see cref="LlmEnvironment.DefaultRole"/> applies. Both misses are hard errors: running a
    /// step against the wrong model silently is worse than refusing to start.
    /// </summary>
    private LlmRole ResolveRole(string? profileName, string stepId)
    {
        var env = llmEnvironment?.Value
            ?? throw new InvalidOperationException(
                $"Step '{stepId}' declares an LLM step but no LlmEnvironment is configured. " +
                "Register the framework services and provide the AITaskAgent:LlmProviders section.");

        var roleName = profileName ?? env.DefaultRole;

        if (!env.Roles.TryGetValue(roleName, out var role))
        {
            var available = env.Roles.Count > 0
                ? string.Join(", ", env.Roles.Keys)
                : "(none configured)";
            throw new InvalidOperationException(
                $"Step '{stepId}' requests LLM role '{roleName}', which is not configured. " +
                $"Available roles: {available}.");
        }

        return role;
    }

    private static List<IStep> ValidateAndOrder(List<IYamlStep> steps)
    {
        var byId = steps.ToDictionary(s => s.StepId);

        // Build adjacency: dependsOn edges (B depends on A  →  A must come before B)
        var inDegree = steps.ToDictionary(s => s.StepId, _ => 0);
        var dependents = steps.ToDictionary(s => s.StepId, _ => new List<string>());

        foreach (var step in steps)
        {
            if (step.DependsOn == null) continue;
            foreach (var dep in step.DependsOn)
            {
                if (!byId.ContainsKey(dep))
                    throw new InvalidOperationException(
                        $"Step '{step.StepId}' depends on '{dep}' which does not exist in the pipeline.");

                inDegree[step.StepId]++;
                dependents[dep].Add(step.StepId);

                // Schema mismatch check: dep.OutputSchema must equal step.InputSchema
                if (byId[dep].OutputSchema != step.InputSchema)
                    throw new InvalidOperationException(
                        $"Schema mismatch: step '{dep}' outputs schema '{byId[dep].OutputSchema}' " +
                        $"but step '{step.StepId}' expects input schema '{step.InputSchema}'.");
            }
        }

        // Kahn's algorithm
        var queue = new Queue<string>(
            inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var ordered = new List<IStep>();

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            ordered.Add((IStep)byId[id]);

            foreach (var dependent in dependents[id])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (ordered.Count != steps.Count)
            throw new InvalidOperationException(
                "Cycle detected in pipeline DAG. Check 'dependsOn' references for circular dependencies.");

        return ordered;
    }

}
