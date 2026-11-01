using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Implementation;
using AITaskAgent.YAML.Execution;
using AITaskAgent.YAML.Schemas;
using AITaskAgent.YAML.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using System.Collections.Concurrent;

namespace AITaskAgent.Tests;

/// <summary>
/// <c>tools: [names]</c> on a YAML <see cref="YamlLlmStep"/>: the names are resolved against the tools of the
/// provider the pipeline is built with (never a static registry), the model is offered only those, an
/// unknown name fails the build with the available names, and without <c>tools</c> the role's tools
/// still apply. No real LLM: <see cref="ToolRecordingLlmService"/>.
/// </summary>
public sealed class YamlLlmStepToolsTests
{
    private sealed class FakeTool(string name) : ITool
    {
        public string Name { get; } = name;

        public string Description => $"Fake tool {Name}";

        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = """{ "type": "object" }""" };

        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");
    }

    /// <summary>LLM double that records the tool names of every request.</summary>
    private sealed class ToolRecordingLlmService : ILlmService
    {
        public ConcurrentQueue<string[]> ToolsPerCall { get; } = new();

        public string Name => "tool-recording";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            ToolsPerCall.Enqueue([.. (request.Tools ?? []).Select(t => t.Name)]);
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = "done", FinishReason = FinishReason.Stop }]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static (YamlPipelineFactory Factory, ToolRecordingLlmService Llm) Build(
        Action<IServiceCollection> tools, params ITool[] roleTools)
    {
        ToolRecordingLlmService llm = new();
        LlmEnvironment environment = new() { DefaultRole = "default" };
        environment.Roles["default"] = new LlmRole
        {
            Provider = new LlmProvider { Service = llm },
            Model = "test-model",
            Tools = roleTools.Length > 0 ? [.. roleTools] : null
        };

        StepRegistry registry = new();
        registry.AddFromAssembly(typeof(StepRegistry).Assembly);
        registry.Add(typeof(YamlLlmStep));

        ServiceCollection services = new();
        services.AddSingleton<IOptions<LlmEnvironment>>(Options.Create(environment));
        tools(services);

        YamlPipelineFactory factory = new(services.BuildServiceProvider(), registry, new SchemaRegistry(), new Pipeline(loggerFactory: NullLoggerFactory.Instance));
        return (factory, llm);
    }

    private static async Task<string[]> RunAndGetOfferedTools(YamlPipelineFactory factory, ToolRecordingLlmService llm, string yaml)
    {
        YamlPipeline pipeline = factory.CreateFromYaml(yaml);
        var result = await pipeline.ExecuteAsync(new JObject(), new LlmPipelineContext());
        Assert.False(result.HasError, result.Error?.Message);
        return Assert.Single(llm.ToolsPerCall);
    }

    [Fact]
    public async Task The_model_is_offered_only_the_tools_the_step_names()
    {
        var (factory, llm) = Build(
            s => s.AddSingleton<ITool>(new FakeTool("alpha")).AddSingleton<ITool>(new FakeTool("beta")),
            new FakeTool("role_tool"));

        string[] offered = await RunAndGetOfferedTools(factory, llm, """
            name: tools
            steps:
              - { id: ask, type: LlmStep, userPrompt: hi, tools: [ALPHA] }
            """);

        Assert.Equal(["alpha"], offered);
    }

    [Fact]
    public async Task Without_tools_the_role_tools_still_apply()
    {
        var (factory, llm) = Build(s => s.AddSingleton<ITool>(new FakeTool("alpha")), new FakeTool("role_tool"));

        string[] offered = await RunAndGetOfferedTools(factory, llm, """
            name: role
            steps:
              - { id: ask, type: LlmStep, userPrompt: hi }
            """);

        Assert.Equal(["role_tool"], offered);
    }

    [Fact]
    public async Task Names_are_also_found_in_the_providers_own_tool_registry()
    {
        // A per-provider ITools (not AvailableTools.Instance): what a host registers for one run.
        var (factory, llm) = Build(s => s.AddSingleton<ITools>(new AvailableTools([new FakeTool("from_registry")])));

        string[] offered = await RunAndGetOfferedTools(factory, llm, """
            name: registry
            steps:
              - { id: ask, type: LlmStep, userPrompt: hi, tools: [from_registry] }
            """);

        Assert.Equal(["from_registry"], offered);
    }

    [Fact]
    public async Task An_alias_resolves_ignoring_case_and_the_model_is_offered_the_tools_own_name()
    {
        var (factory, llm) = Build(s => s
            .AddSingleton<ITool>(new FakeTool("view"))
            .AddSingleton(new ToolAlias("FileTools:view_file", new FakeTool("view_file"))));

        string[] offered = await RunAndGetOfferedTools(factory, llm, """
            name: alias
            steps:
              - { id: ask, type: LlmStep, userPrompt: hi, tools: [" filetools:VIEW_FILE "] }
            """);

        Assert.Equal(["view_file"], offered);
    }

    [Fact]
    public async Task A_bare_name_wins_over_an_alias_with_the_same_text()
    {
        var (factory, llm) = Build(s => s
            .AddSingleton<ITool>(new FakeTool("x"))
            .AddSingleton(new ToolAlias("x", new FakeTool("other"))));

        string[] offered = await RunAndGetOfferedTools(factory, llm, """
            name: priority
            steps:
              - { id: ask, type: LlmStep, userPrompt: hi, tools: [x] }
            """);

        Assert.Equal(["x"], offered);
    }

    [Fact]
    public void An_unknown_name_lists_the_aliases_among_the_available_tools()
    {
        var (factory, _) = Build(s => s.AddSingleton(new ToolAlias("Host:alpha", new FakeTool("alpha"))));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateFromYaml("""
            name: bad
            steps:
              - { id: ask, type: LlmStep, userPrompt: hi, tools: [nope] }
            """));

        Assert.Contains("Host:alpha", ex.Message);
    }

    [Fact]
    public void An_unknown_name_fails_the_build_naming_it_and_the_available_tools()
    {
        var (factory, _) = Build(s => s.AddSingleton<ITool>(new FakeTool("beta")).AddSingleton<ITool>(new FakeTool("alpha")));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateFromYaml("""
            name: bad
            steps:
              - { id: ask, type: LlmStep, userPrompt: hi, tools: [alpha, nope] }
            """));

        Assert.Contains("Step 'ask'", ex.Message);
        Assert.Contains("'nope'", ex.Message);
        Assert.Contains("Available tools: alpha, beta.", ex.Message);
    }

    [Fact]
    public void Two_providers_do_not_see_each_others_tools()
    {
        var (withTool, _) = Build(s => s.AddSingleton<ITool>(new FakeTool("only_here")));
        var (without, _) = Build(_ => { });
        const string yaml = """
            name: isolated
            steps:
              - { id: ask, type: LlmStep, userPrompt: hi, tools: [only_here] }
            """;

        withTool.CreateFromYaml(yaml);
        var ex = Assert.Throws<InvalidOperationException>(() => without.CreateFromYaml(yaml));
        Assert.Contains("(none registered)", ex.Message);
    }

    [Fact]
    public void Describe_exposes_tools_as_a_list_of_names_with_a_description()
    {
        StepRegistry registry = new();
        registry.Add(typeof(YamlLlmStep));

        JsonSchemaProperty tools = registry.Describe("LlmStep").Parameters!.Properties["tools"];

        Assert.Equal(JsonObjectType.Array, tools.Type);
        Assert.Equal(JsonObjectType.String, Assert.Single(tools.Items).Type);
        Assert.False(string.IsNullOrWhiteSpace(tools.Description));
        Assert.False(registry.Describe("LlmStep").Parameters!.RequiredProperties.Contains("tools"));
    }
}
