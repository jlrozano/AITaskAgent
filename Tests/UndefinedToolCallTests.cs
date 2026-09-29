using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Streaming;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Tests;

/// <summary>
/// A real session had a weak free model call "write_file" as a native tool_call even though it
/// was never offered as one (only registered as a tag) and its own system prompt told it not to.
/// The fix is neither a hard failure (no fallback provider may be configured) nor silently
/// guessing at file content from the tool_call's arguments (a real capture showed the model
/// omits the content entirely when it does this - only Path/Summary, no body). Instead: log a
/// warning and hand the model back the exact GetInstructions() text for the tag it should have
/// used, verbatim, so the correction is unambiguous on the very next turn instead of trusting it
/// to re-read a system prompt several tool calls back.
/// </summary>
public sealed class UndefinedToolCallTests
{
    private sealed class FakeReadOnlyTool : ITool
    {
        public string Name => "list_dir";
        public string Description => "test tool";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }

    private sealed class FakeTagHandler : ITagHandler
    {
        public string TagName => "write_file";
        public string GetInstructions() => "### Tag: write_file\nUnique marker: USE-THE-TAG-9f31.";
        public Task<string?> OnCompleteTagAsync(Dictionary<string, string> attributes, string content, PipelineContext context, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>Always answers with the same undefined tool_call, so the stuck-loop guard is what
    /// eventually ends the turn - proving the path no longer throws.</summary>
    private sealed class UndefinedToolCallService(string undefinedToolName) : ILlmService
    {
        public List<LlmRequest> Requests { get; } = [];

        public string Name => "canned-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(LlmResult.Success(
                [new LlmResponse
                {
                    Content = "",
                    FinishReason = FinishReason.ToolCalls,
                    ToolCalls = [new ToolCall { Id = $"call_{Requests.Count}", Name = undefinedToolName, Arguments = "{}" }]
                }]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("not exercised by this test");

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static (IStep Step, UndefinedToolCallService Service) BuildStep(
        string undefinedToolName, List<ITagHandler>? streamingHandlers = null)
    {
        var service = new UndefinedToolCallService(undefinedToolName);
        IStep step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "FileAgent",
            new LlmRole
            {
                Provider = new LlmProvider { Service = service },
                Model = "test-model",
                Inference = new LlmInferenceSettings { UseStreaming = false }
            },
            templateProvider: null,
            promtTemplateString: "{{Input}}")
        {
            Tools = [new FakeReadOnlyTool()],
            TagHandlers = streamingHandlers ?? []
        };
        return (step, service);
    }

    private static Task<IStepResult> RunAsync(IStep step) =>
        step.ExecuteAsync(
            new LlmStringStepResult(new EmptyStep("Seed")) { Content = "hazlo" },
            new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

    [Fact]
    public async Task Calling_an_undefined_tool_that_matches_a_tag_does_not_throw()
    {
        var (step, _) = BuildStep("write_file", [new FakeTagHandler()]);

        var result = await RunAsync(step);

        // No exception (no fallback dependency); the stuck-loop guard is what ends the turn.
        var content = (result as LlmStringStepResult)?.Content ?? string.Empty;
        Assert.Contains("Execution stopped", content);
    }

    [Fact]
    public async Task Tag_mismatch_resends_the_tag_handler_own_instructions_verbatim()
    {
        var (step, service) = BuildStep("write_file", [new FakeTagHandler()]);

        await RunAsync(step);

        // The 2nd request's conversation must carry the tool-result message with the tag's own
        // GetInstructions() text, not a generic "not found".
        Assert.True(service.Requests.Count >= 2, "expected at least a retry after the first undefined call");
        var secondRequestMessages = service.Requests[1].Conversation.History.Messages
            .Select(m => m.Content ?? string.Empty);
        Assert.Contains(secondRequestMessages, m => m.Contains("USE-THE-TAG-9f31"));
        Assert.Contains(secondRequestMessages, m => m.Contains("is not a callable tool"));
    }

    [Fact]
    public async Task Genuinely_unknown_tool_name_gets_the_plain_not_found_message()
    {
        var (step, service) = BuildStep("some_made_up_tool");

        await RunAsync(step);

        Assert.True(service.Requests.Count >= 2, "expected at least a retry after the first undefined call");
        var secondRequestMessages = service.Requests[1].Conversation.History.Messages
            .Select(m => m.Content ?? string.Empty);
        Assert.Contains(secondRequestMessages, m => m.Contains("Tool 'some_made_up_tool' not found"));
    }
}
