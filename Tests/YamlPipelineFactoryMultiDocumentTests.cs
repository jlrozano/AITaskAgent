using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.YAML.Abstractions;
using AITaskAgent.YAML.Attributes;
using AITaskAgent.YAML.Compilation;
using AITaskAgent.YAML.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.Tests;

/// <summary>
/// <c>CreateFromFolderAsync</c> joins every *.yaml file with <c>---</c>, but the merged text was deserialized as a
/// single document, so two or more files threw "Expected 'StreamEnd', got 'DocumentStart'". The factory now reads every
/// document: steps are concatenated and the pipeline name is the first non-empty one.
/// </summary>
public sealed class YamlPipelineFactoryMultiDocumentTests
{
    [YamlStepTag("MultiDocProbe")]
    public sealed class MultiDocProbeStep : StepBase, IYamlStep
    {
        public MultiDocProbeStep() : base("probe", typeof(object), typeof(object)) { }

        public string StepId { get => Name; init { Name = value; } }
        public string[]? DependsOn { get; init; }

        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException("not executed by these tests");
    }

    private const string Schema = """{ "type": "object", "properties": { "text": { "type": "string" } } }""";

    private static async Task<(YamlPipelineFactory Factory, string Schema)> BuildFactoryAsync()
    {
        // Each compiled schema becomes an assembly of that name in the default load context, so it must be unique per test.
        var schemaName = "MultiDoc" + Guid.NewGuid().ToString("N");
        var compiler = new SchemaCompiler();
        await compiler.CompileAsync(schemaName, Schema);
        var factory = new YamlPipelineFactory(
            new ServiceCollection().BuildServiceProvider(),
            compiler,
            new Pipeline(loggerFactory: NullLoggerFactory.Instance));
        return (factory, schemaName);
    }

    private static string Step(string schema, string id, string? dependsOn = null) =>
        $"""
          - !MultiDocProbe
            stepId: {id}
            inputSchema: {schema}
            outputSchema: {schema}
        """ + (dependsOn == null ? "" : $"\n    dependsOn: [{dependsOn}]");

    [Fact]
    public async Task Two_documents_are_merged_into_one_pipeline()
    {
        var (factory, schema) = await BuildFactoryAsync();
        var yaml = $"name: first\nsteps:\n{Step(schema, "a")}\n---\nsteps:\n{Step(schema, "b", dependsOn: "a")}\n";

        var pipeline = factory.CreateFromYaml(yaml);

        Assert.Equal("first", pipeline.Name);
        Assert.Equal(["a", "b"], pipeline.Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task A_single_document_still_works()
    {
        var (factory, schema) = await BuildFactoryAsync();

        var pipeline = factory.CreateFromYaml($"name: solo\nsteps:\n{Step(schema, "a")}\n");

        Assert.Equal("solo", pipeline.Name);
        Assert.Equal("a", Assert.Single(pipeline.Steps).Name);
    }

    [Fact]
    public async Task A_folder_with_two_yaml_files_builds_one_pipeline()
    {
        var (factory, schema) = await BuildFactoryAsync();
        var folder = Path.Combine(Path.GetTempPath(), "yaml-multidoc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "one.yaml"), $"name: folder\nsteps:\n{Step(schema, "a")}\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "two.yaml"), $"steps:\n{Step(schema, "b", dependsOn: "a")}\n");

            var pipeline = await factory.CreateFromFolderAsync(folder);

            Assert.Equal("folder", pipeline.Name);
            Assert.Equal(["a", "b"], pipeline.Steps.Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Empty_yaml_fails_with_a_clear_message()
    {
        var (factory, schema) = await BuildFactoryAsync();

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateFromYaml(""));

        Assert.Contains("no documents", ex.Message);
    }
}
