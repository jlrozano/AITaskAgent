using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Support.Template;
using Samples.AdvancedLlmPipeline;

namespace AITaskAgent.Tests;

/// <summary>
/// AdvancedLlmPipeline's *_prompt.md templates were written against an older render model where
/// the whole step-result was the template model (its own .Value, plus any extra properties). The
/// current PromptTemplateResolver instead exposes only { Input = input.Value, Context }, so every
/// placeholder using the old "Value." prefix - or a bare name for a plain-string Input - silently
/// resolved to its own "?? default" fallback instead of real content (Intention.OptimizedPrompt
/// never even existed as a property; the real name is DetailedPrompt). These pin the corrected
/// placeholders against the real production types (Intention, ParallelResult, CriticData) so a
/// future rename or resolver change breaks loudly instead of silently falling back to a default.
/// </summary>
public sealed class TemplatePlaceholderRegressionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("TemplatePlaceholderRegressionTests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class CapturingService : ILlmService
    {
        public List<LlmRequest> Requests { get; } = [];
        public string Name => "capturing-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(LlmResult.Success(
                [new LlmResponse { Content = "irrelevant assistant reply", FinishReason = FinishReason.Stop }]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("not exercised by this test");

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static string CapturedUserMessage(CapturingService service) =>
        service.Requests[0].Conversation.History.Messages.Last(m => m.Role == "user").Content!;

    private static LlmRole StubRole(CapturingService service) =>
        new() { Provider = new LlmProvider { Service = service }, Model = "test-model" };

    [Fact]
    public async Task Outliner_prompt_resolves_Intention_DetailedPrompt_not_the_stale_OptimizedPrompt_fallback()
    {
        File.WriteAllText(Path.Combine(_dir, "outliner_prompt.md"), "{{Input.DetailedPrompt ?? Create a story outline}}");
        File.WriteAllText(Path.Combine(_dir, "outliner_system.md"), "You are a careful, thorough story outliner.");
        var templateProvider = new FileTemplateProvider(_dir, validateTemplates: true);
        var capturingService = new CapturingService();

        IStep step = new StatelessTemplateLlmStep<LlmStepResult<Intention>, LlmStringStepResult>(
            "Outliner", StubRole(capturingService), templateProvider,
            promtTemplateString: "outliner_prompt", systemTemplateString: "outliner_system");

        var input = new LlmStepResult<Intention>(new EmptyStep("Seed"))
        {
            Value = new Intention { DetailedPrompt = "Escribe un cuento sobre vacas voladoras asesinas" }
        };

        await step.ExecuteAsync(input, new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        var userMessage = CapturedUserMessage(capturingService);
        Assert.Equal("Escribe un cuento sobre vacas voladoras asesinas", userMessage);
        Assert.NotEqual("Create a story outline", userMessage);
    }

    [Fact]
    public async Task Critic_prompt_resolves_both_parallel_drafts_via_dictionary_navigation()
    {
        File.WriteAllText(
            Path.Combine(_dir, "critic_prompt.md"),
            "## Draft 1\n{{Input.Writer1.Value ?? No draft 1}}\n\n## Draft 2\n{{Input.Writer2.Value ?? No draft 2}}");
        File.WriteAllText(Path.Combine(_dir, "critic_system.md"), "You are a literary critic.");
        var templateProvider = new FileTemplateProvider(_dir, validateTemplates: true);
        var capturingService = new CapturingService();

        IStep step = new StatelessTemplateLlmStep<ParallelResult, LlmStringStepResult>(
            "Critic", StubRole(capturingService), templateProvider,
            promtTemplateString: "critic_prompt", systemTemplateString: "critic_system");

        var parallelResult = new ParallelResult(new EmptyStep("ParallelWriters"));
        parallelResult.AddResult("Writer1", new LlmStringStepResult(new EmptyStep("Writer1")) { Content = "Draft about killer flying cows." });
        parallelResult.AddResult("Writer2", new LlmStringStepResult(new EmptyStep("Writer2")) { Content = "Otro borrador sobre vacas asesinas." });

        await step.ExecuteAsync(parallelResult, new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        var userMessage = CapturedUserMessage(capturingService);
        Assert.Contains("Draft about killer flying cows.", userMessage);
        Assert.Contains("Otro borrador sobre vacas asesinas.", userMessage);
        Assert.DoesNotContain("No draft 1", userMessage);
        Assert.DoesNotContain("No draft 2", userMessage);
    }

    [Fact]
    public async Task Rewriter_prompt_resolves_drafts_and_critique_after_CriticResult_merges_them_into_CriticData()
    {
        File.WriteAllText(
            Path.Combine(_dir, "rewriter_prompt.md"),
            "## Draft 1\n{{Input.Writer1 ?? No draft 1}}\n\n## Draft 2\n{{Input.Writer2 ?? No draft 2}}\n\n"
            + "## Recommendations\n{{Input.recommendations ?? No recommendations}}\n\n"
            + "## Detailed Critique\n{{Input.detailedCritique ?? No detailed critique}}");
        File.WriteAllText(Path.Combine(_dir, "rewriter_system.md"), "You are a rewriter.");
        var templateProvider = new FileTemplateProvider(_dir, validateTemplates: true);
        var capturingService = new CapturingService();

        IStep step = new StatelessTemplateLlmStep<CriticResult, LlmStringStepResult>(
            "Rewriter", StubRole(capturingService), templateProvider,
            promtTemplateString: "rewriter_prompt", systemTemplateString: "rewriter_system");

        // Same merge StatelessRewriterStep performs before calling base.ExecuteAsync: Writer1/
        // Writer2 write through CriticResult's properties into the immutable CriticData Value.
        var criticResult = new CriticResult(new EmptyStep("Critic"))
        {
            Value = new CriticData { Recommendations = "Add more tension.", DetailedCritique = "Pacing is uneven." }
        };
        criticResult.Writer1 = "Primer borrador.";
        criticResult.Writer2 = "Segundo borrador.";

        await step.ExecuteAsync(criticResult, new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        var userMessage = CapturedUserMessage(capturingService);
        Assert.Contains("Primer borrador.", userMessage);
        Assert.Contains("Segundo borrador.", userMessage);
        Assert.Contains("Add more tension.", userMessage);
        Assert.Contains("Pacing is uneven.", userMessage);
        Assert.DoesNotContain("No draft 1", userMessage);
        Assert.DoesNotContain("No draft 2", userMessage);
        Assert.DoesNotContain("No recommendations", userMessage);
        Assert.DoesNotContain("No detailed critique", userMessage);
    }

    [Fact]
    public void CriticResult_Writer1_and_Writer2_are_excluded_from_the_LLM_structured_output_schema()
    {
        // Writer1/Writer2 are merged in by code after the Critic's structured-output call returns -
        // if they leaked into the schema, the LLM would be asked to produce them itself. The schema
        // actually sent to providers is built from the step-result type via GetStepActivatorInfo,
        // the same path BaseLlmStep uses.
        var schema = StepResultFactory.GetStepActivatorInfo(typeof(CriticResult)).JsonSchema;

        Assert.NotNull(schema);
        Assert.DoesNotContain(schema!.Properties.Keys, k => k.Equals("writer1", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(schema.Properties.Keys, k => k.Equals("writer2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(schema.Properties.Keys, k => k.Equals("recommendations", StringComparison.OrdinalIgnoreCase));
    }
}
