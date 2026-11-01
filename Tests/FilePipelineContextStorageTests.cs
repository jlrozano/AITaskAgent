using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Results;
using AITaskAgent.YAML.Execution;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace AITaskAgent.Tests;

/// <summary>
/// <see cref="FilePipelineContextStorage"/> and <see cref="PipelineContextSerializer"/>: what survives a
/// save/load, what is refused, file names, and the point of it all: a YAML run suspended in one
/// service provider resumes in another (a restart) with fast-forward.
/// </summary>
public sealed class FilePipelineContextStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aitask-tests", Guid.NewGuid().ToString("N"));
    private readonly string _id = Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Key(string name) => $"{_id}-{name}";

    private Dictionary<string, string?> FileStorage() => new()
    {
        ["AITaskAgent:Storage:Context:Type"] = "file",
        ["AITaskAgent:Storage:Context:Directory"] = _directory
    };

    // ── Restart ──────────────────────────────────────────────────────────────

    private string RestartYaml => """
        name: restart
        steps:
          - { id: before, type: Counter, key: KEY-before, output: '{ "order": 7 }' }
          - { id: draft, type: LlmStep, dependsOn: [before], userPrompt: "Draft for order {{input.order}}" }
          - id: grp
            type: GroupStep
            dependsOn: [draft]
            steps:
              - { id: ask, type: SuspendStep, message: "Approve?" }
          - { id: final, type: LlmStep, dependsOn: [grp], userPrompt: "Approved: {{input.approved}}" }
          - { id: after, type: Counter, key: KEY-after, dependsOn: [final] }
        """.Replace("KEY-", _id + "-", StringComparison.Ordinal);

    [Fact]
    public async Task A_run_suspended_in_one_provider_resumes_in_a_new_one_without_repeating_finished_steps()
    {
        string correlationId;
        RecordingLlmService firstLlm = new(call => "draft text");

        // Process 1: run until the suspension and persist the context.
        await using (var first = HostKit.Build(firstLlm, FileStorage()))
        {
            YamlPipeline pipeline = first.GetRequiredService<YamlPipelineFactory>().CreateFromYaml(RestartYaml);
            PipelineContext context = first.GetRequiredService<PipelineContextFactory>().NewContext();
            correlationId = context.CorrelationId;

            IStepResult suspended = await pipeline.ExecuteAsync(new JObject(), context);
            Assert.NotNull(suspended.SuspendInfo);

            var storage = Assert.IsType<FilePipelineContextStorage>(first.GetRequiredService<IPipelineContextStorage>());
            await storage.SaveAsync(correlationId, context, CancellationToken.None);
            Assert.True(File.Exists(storage.GetFilePath(correlationId)));
        }

        Assert.Equal(1, CountingStep.Count(Key("before")));
        Assert.Equal(1, firstLlm.Calls);

        // Process 2: nothing of the first provider survives but the folder.
        RecordingLlmService secondLlm = new(call => "final text");
        await using var second = HostKit.Build(secondLlm, FileStorage());
        YamlPipeline resumedPipeline = second.GetRequiredService<YamlPipelineFactory>().CreateFromYaml(RestartYaml);
        PipelineContext? loaded = await second.GetRequiredService<IPipelineContextStorage>().LoadAsync(correlationId, CancellationToken.None);

        var llmContext = Assert.IsType<LlmPipelineContext>(loaded);
        Assert.Equal(correlationId, llmContext.CorrelationId);
        Assert.True(PipelineResume.IsSuspended(llmContext));
        Assert.Equal("grp/ask", PipelineResume.GetSuspendedStepPath(llmContext));
        Assert.Same(secondLlm, llmContext.ServiceProvider.GetService<AITaskAgent.LLM.Abstractions.ILlmService>());
        Assert.Equal(["user:Draft for order 7", "assistant:draft text"],
            llmContext.Conversation.History.Messages.Select(m => $"{m.Role}:{m.Content}"));

        IStepResult result = await resumedPipeline.ExecuteAsync(JObject.Parse("""{ "approved": "yes" }"""), llmContext);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Null(result.SuspendInfo);
        Assert.Equal("final text", result.Value);
        Assert.Equal(1, CountingStep.Count(Key("before")));
        Assert.Equal(1, CountingStep.Count(Key("after")));
        Assert.Equal(1, secondLlm.Calls);
        Assert.Equal(
            ["user:Draft for order 7", "assistant:draft text", "user:Approved: yes"],
            Assert.Single(secondLlm.Conversations));
    }

    // ── Serializer ───────────────────────────────────────────────────────────

    [Fact]
    public void The_framework_results_and_plain_metadata_survive_a_round_trip()
    {
        EmptyStep step = new("s");
        ParallelResult parallel = new(new EmptyStep("fan"));
        parallel.AddResult("b1", new StepResult<string>(new EmptyStep("b1"), "one"));
        parallel.AddResult("b2", new JsonStepResult(new EmptyStep("b2"), JObject.Parse("""{ "n": 2 }""")));

        PipelineContext context = new() { CorrelationId = "round-trip" };
        context.Metadata["text"] = "2024-10-21";
        context.Metadata["number"] = 42;
        context.Metadata["json"] = JObject.Parse("""{ "when": "2024-10-21T10:00:00Z" }""");
        context.Metadata["list"] = new List<string> { "a", "b" };
        context.Metadata["nothing"] = null;
        context.StepResults["text"] = new StepResult<string>(step, "hello");
        context.StepResults["json"] = new JsonStepResult(step, JObject.Parse("""{ "a": [1, 2] }"""), JsonSchema.CreateAnySchema());
        context.StepResults["break"] = new BreakStepResult(step, "out");
        context.StepResults["fan"] = parallel;
        context.StepResults["llm"] = new LlmStringStepResult(new YamlLlmStepStub("llm")) { Content = "answer" };
        context.StepResults["ask"] = new AwaitingInputResult(new SuspendStep("ask"), "Why?")
        {
            SuspendInfo = "awaiting-input",
            DataToReview = JObject.Parse("""{ "id": 1 }"""),
            RejectionReason = "bad"
        };

        PipelineContext restored = PipelineContextSerializer.Deserialize(
            PipelineContextSerializer.Serialize(context), new PipelineContext());

        Assert.Equal("round-trip", restored.CorrelationId);
        Assert.Equal("2024-10-21", restored.Metadata["text"]);
        Assert.Equal(42, restored.Metadata["number"]);
        Assert.Equal("2024-10-21T10:00:00Z", ((JToken)restored.Metadata["json"]!)["when"]!.Value<string>());
        Assert.Equal(["a", "b"], Assert.IsType<List<string>>(restored.Metadata["list"]));
        Assert.Null(restored.Metadata["nothing"]);

        Assert.Equal("hello", Assert.IsType<StepResult<string>>(restored.StepResults["text"]).Value);
        Assert.Equal("s", restored.StepResults["text"].StepName);
        Assert.Equal(nameof(EmptyStep), restored.StepResults["text"].StepType);
        Assert.Equal(2, Assert.IsType<JsonStepResult>(restored.StepResults["json"]).Value!["a"]!.Count());
        Assert.Equal("out", Assert.IsType<BreakStepResult>(restored.StepResults["break"]).Value);

        var fan = Assert.IsType<ParallelResult>(restored.StepResults["fan"]);
        Assert.Equal("one", fan.GetResult("b1")!.Value);
        Assert.Equal(2, ((JToken)fan.GetResult("b2")!.Value!)["n"]!.Value<int>());

        var llm = Assert.IsType<LlmStringStepResult>(restored.StepResults["llm"]);
        Assert.Equal("answer", llm.Content);
        Assert.Equal(nameof(YamlLlmStepStub), llm.StepType);

        var ask = Assert.IsType<AwaitingInputResult>(restored.StepResults["ask"]);
        Assert.Equal("awaiting-input", ask.SuspendInfo);
        Assert.Equal("Why?", ask.Message);
        Assert.Equal("Why?", ask.Value);
        Assert.Equal("bad", ask.RejectionReason);
        Assert.Equal(1, ((JToken)ask.DataToReview!)["id"]!.Value<int>());
    }

    [Fact]
    public async Task A_value_that_cannot_be_stored_fails_the_save_and_keeps_the_previous_file()
    {
        FilePipelineContextStorage storage = new(_directory, () => new PipelineContext());
        PipelineContext context = new() { CorrelationId = "keep" };
        context.Metadata["ok"] = "first";
        await storage.SaveAsync("keep", context, CancellationToken.None);

        context.Metadata["ok"] = "second";
        context.Metadata["callback"] = new Func<int>(() => 1);
        var live = await Assert.ThrowsAsync<NotSupportedException>(() => storage.SaveAsync("keep", context, CancellationToken.None));
        Assert.Contains("Metadata['callback']", live.Message);

        context.Metadata.TryRemove("callback", out _);
        context.Metadata["lossy"] = Lossy.Of("x");
        var lossy = await Assert.ThrowsAsync<NotSupportedException>(() => storage.SaveAsync("keep", context, CancellationToken.None));
        Assert.Contains("Metadata['lossy']", lossy.Message);

        PipelineContext? loaded = await storage.LoadAsync("keep", CancellationToken.None);
        Assert.Equal("first", loaded!.Metadata["ok"]);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task A_context_saved_as_an_llm_context_cannot_be_restored_into_a_base_one()
    {
        FilePipelineContextStorage writer = new(_directory, () => new LlmPipelineContext());
        await writer.SaveAsync("llm", new LlmPipelineContext { CorrelationId = "llm" }, CancellationToken.None);

        FilePipelineContextStorage reader = new(_directory, () => new PipelineContext());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.LoadAsync("llm", CancellationToken.None));
        Assert.Contains(nameof(LlmPipelineContext), ex.Message);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData(@"..\..\escape")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("ünïcode-✓")]
    public async Task Any_id_maps_to_one_file_inside_the_folder(string id)
    {
        FilePipelineContextStorage storage = new(_directory, () => new PipelineContext());
        string path = storage.GetFilePath(id);

        Assert.Equal(storage.Directory, Path.GetDirectoryName(path));
        Assert.StartsWith("ctx-", Path.GetFileName(path));

        await storage.SaveAsync(id, new PipelineContext { CorrelationId = id }, CancellationToken.None);
        PipelineContext? loaded = await storage.LoadAsync(id, CancellationToken.None);
        Assert.Equal(id, loaded!.CorrelationId);
        Assert.Single(Directory.GetFiles(_directory));

        await storage.DeleteAsync(id, CancellationToken.None);
        Assert.Null(await storage.LoadAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Ids_that_differ_only_in_case_or_length_get_different_files()
    {
        FilePipelineContextStorage storage = new(_directory, () => new PipelineContext());
        string longId = new('x', 400);
        string[] ids = ["Order", "order", "ORDER", longId, longId + "y"];

        foreach (string id in ids)
        {
            await storage.SaveAsync(id, new PipelineContext { CorrelationId = id }, CancellationToken.None);
        }

        Assert.Equal(ids.Length, ids.Select(storage.GetFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string id in ids)
        {
            Assert.Equal(id, (await storage.LoadAsync(id, CancellationToken.None))!.CorrelationId);
        }

        await storage.ClearAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    /// <summary>Its value has a private setter, which Newtonsoft does not use: it comes back as "default".</summary>
    private sealed class Lossy
    {
        public string Value { get; private set; } = "default";

        public static Lossy Of(string value) => new() { Value = value };
    }

    /// <summary>Stand-in step type, so the stored StepType differs from the EmptyStep used to rebuild it.</summary>
    private sealed class YamlLlmStepStub(string name) : IStep
    {
        public string Name { get; } = name;
        public IResume? Resume => null;
        public Type InputType => typeof(IStepResult);
        public Type OutputType => typeof(IStepResult);
        public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
