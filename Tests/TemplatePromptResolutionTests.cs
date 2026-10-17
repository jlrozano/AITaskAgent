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
/// TemplateLlmStep defaults to "{name}_prompt.md" / "{name}_system.md". FileTemplateProvider then
/// appended ".md" again, missed the file, and PromptTemplateResolver sent the literal
/// "Writer_prompt.md" to the model. Inline text passed alongside a provider was sent with its
/// placeholders unrendered. Driven end to end through the request the step actually sends.
/// </summary>
public sealed class TemplatePromptResolutionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("TemplatePromptResolutionTests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class CapturingService : ILlmService
    {
        public List<LlmRequest> Requests { get; } = [];

        public string Name => "capturing-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(LlmResult.Success(
                [new LlmResponse { Content = "reply", FinishReason = FinishReason.Stop }]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("not exercised by this test");

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static LlmRole RoleFor(ILlmService service) => new()
    {
        Provider = new LlmProvider { Service = service },
        Model = "test-model"
    };

    private static LlmStringStepResult Input(string content) =>
        new(new EmptyStep("Seed")) { Content = content };

    private static string? UserMessage(LlmRequest request) =>
        request.Conversation.History.Messages.Last(m => m.Role == "user").Content;

    [Fact]
    public async Task Default_template_names_load_their_files_through_FileTemplateProvider()
    {
        File.WriteAllText(Path.Combine(_dir, "Writer_prompt.md"), "Write about: {{Input}}");
        File.WriteAllText(Path.Combine(_dir, "Writer_system.md"), "You are a writer.");
        var service = new CapturingService();

        // No prompt strings: the step falls back to "Writer_prompt.md" / "Writer_system.md".
        IStep step = new TemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Writer", RoleFor(service), new FileTemplateProvider(_dir));

        await step.ExecuteAsync(Input("dragons"), new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        var request = Assert.Single(service.Requests);
        Assert.Equal("You are a writer.", request.SystemPrompt);
        Assert.Equal("Write about: dragons", UserMessage(request));
    }

    [Fact]
    public async Task Inline_prompt_text_is_rendered_even_when_a_provider_is_supplied()
    {
        var service = new CapturingService();

        IStep step = new TemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Writer", RoleFor(service), new FileTemplateProvider(_dir),
            promtTemplateString: "Hola {{Input}}",
            systemTemplateString: "Sistema para {{Input}}");

        await step.ExecuteAsync(Input("mundo"), new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        var request = Assert.Single(service.Requests);
        Assert.Equal("Sistema para mundo", request.SystemPrompt);
        Assert.Equal("Hola mundo", UserMessage(request));
    }

    [Fact]
    public async Task A_missing_default_template_fails_instead_of_sending_its_name()
    {
        // Empty folder: "Writer_prompt.md" does not exist.
        var service = new CapturingService();

        IStep step = new TemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Writer", RoleFor(service), new FileTemplateProvider(_dir));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            step.ExecuteAsync(Input("dragons"), new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None));

        Assert.Contains("Writer_prompt.md", ex.Message);
        Assert.Empty(service.Requests);
    }
}
