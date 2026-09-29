using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Support.Template;

namespace AITaskAgent.Tests;

/// <summary>
/// End-to-end canary for the AdvancedLlmPipeline regression: StoryPipelineFactory built its steps
/// with CustomStatelessTemplateLlmStep(name, role, templateProvider, promptTemplateName:
/// "outliner_prompt", ...) - a bare name, no ".md" - and FileTemplateProvider briefly failed to
/// find the file, returned the raw name as if it were the rendered content, and every step sent
/// the LLM its own template name ("outliner_prompt") instead of a real prompt. Unit tests on
/// FileTemplateProvider/PromptTemplateResolver in isolation would not have caught this: the actual
/// break was in how StatelessTemplateLlmStep wires a real ITemplateProvider through
/// BuildSystemMessageAsync/BuildUserMessageAsync into the LlmRequest a step actually sends. This
/// test reproduces that exact wiring - a real FileTemplateProvider over real files on disk, driven
/// through IStep.ExecuteAsync exactly as StoryPipelineFactory's CustomStatelessTemplateLlmStep does
/// it - and fails if that path ever again produces a bare template name instead of its content.
/// </summary>
public sealed class TemplateLlmStepCanaryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("TemplateLlmStepCanaryTests").FullName;

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

    [Fact]
    public async Task Step_sends_the_rendered_template_content_not_the_bare_template_name()
    {
        File.WriteAllText(Path.Combine(_dir, "outliner_system.md"), "You are a careful, thorough story outliner.");
        File.WriteAllText(Path.Combine(_dir, "outliner_prompt.md"), "Write a detailed outline for: {{Input}}");

        var templateProvider = new FileTemplateProvider(_dir, validateTemplates: true);
        var capturingService = new CapturingService();
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = capturingService },
            Model = "test-model"
        };

        // Same shape as StoryPipelineFactory's CustomStatelessTemplateLlmStep: bare names, no
        // extension, forwarded straight into StatelessTemplateLlmStep's base constructor.
        IStep step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Outliner", role, templateProvider,
            promtTemplateString: "outliner_prompt",
            systemTemplateString: "outliner_system");

        var input = new LlmStringStepResult(new EmptyStep("Seed")) { Content = "vampiros extraterrestres" };

        await step.ExecuteAsync(input, new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        Assert.Single(capturingService.Requests);
        var request = capturingService.Requests[0];

        Assert.Equal("You are a careful, thorough story outliner.", request.SystemPrompt);
        Assert.NotEqual("outliner_system", request.SystemPrompt);

        // The captured request shares the live ConversationContext, which gains the assistant's
        // reply once InvokeAsync returns - filter by role rather than taking the last message.
        var userMessage = request.Conversation.History.Messages.Last(m => m.Role == "user").Content;
        Assert.Equal("Write a detailed outline for: vampiros extraterrestres", userMessage);
        Assert.NotEqual("outliner_prompt", userMessage);
    }
}
