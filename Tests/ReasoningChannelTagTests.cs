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
/// A real capture (FileToolsTestApp against a reasoning model, non-streaming) showed the loop that
/// <see cref="UndefinedToolCallTests"/> was meant to fix surviving the fix, for reasons that had
/// nothing to do with the model ignoring instructions - it obeyed them exactly:
///
/// 1. It wrote a perfectly formed "&lt;write_file&gt;" tag, with the full file body inside, but in the
///    REASONING channel: a reasoning model routes its whole answer there whenever it also emits
///    tool calls, leaving Content empty. Tag processing only ever looked at Content, so the write
///    was silently dropped. The design decision (2026-09-04) is that a tag is a tag wherever the
///    model wrote it: one scanner runs over reasoning and content alike, in both transports, and
///    processes each tag when it closes. (Recorded trade-off, D2 in docs/project_notes: a draft
///    skeleton in the reasoning executes too.)
/// 2. The assistant message persisted alongside the tool calls hardcoded Content = string.Empty,
///    so even the record of the attempt vanished from the history. The model could not tell it had
///    already done the work, and asked again.
/// 3. Being handed GetInstructions() again ("write the tag in your response text NOW") right after
///    it had just written the tag is a contradiction it cannot satisfy - so it retried until the
///    repeat detector killed the step.
/// </summary>
public sealed class ReasoningChannelTagTests
{
    private const string ReasoningWithTag = """
        Let's craft the analysis content: list bugs, improvements, observations.
        Now we need to output the analysis using the write_file tag.
        <write_file path="analisis.md" summary="Analysis of the project">
        # Analisis
        contenido real del archivo
        </write_file>
        Then we need to explain that we created the file.
        """;

    private sealed class FakeReadOnlyTool : ITool
    {
        public string Name => "list_dir";
        public string Description => "test tool";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult("ok");
    }

    /// <summary>Records what it was asked to write and returns a reference, like the real handler.</summary>
    private sealed class RecordingTagHandler : ITagHandler
    {
        public List<string> WrittenBodies { get; } = [];

        public string TagName => "write_file";

        public string GetInstructions() => "### Tag: write_file\nUnique marker: USE-THE-TAG-9f31.";

        public Task<string?> OnCompleteTagAsync(
            Dictionary<string, string> attributes, string content, PipelineContext context, CancellationToken cancellationToken)
        {
            WrittenBodies.Add(content);
            attributes.TryGetValue("path", out var path);
            return Task.FromResult<string?>($"[write_file: {path} ({content.Length} bytes)]");
        }
    }

    /// <summary>
    /// Reproduces the captured response shape: everything in the reasoning channel, Content empty,
    /// and (optionally) the same tag ALSO requested as a native tool call - which is what the model
    /// actually did. Answers normally from the second call on, so the turn can end on its own
    /// instead of via the stuck-loop guard.
    /// </summary>
    private sealed class ReasoningChannelService(
        bool alsoCallTagAsTool,
        bool streaming,
        string? reasoning = null) : ILlmService
    {
        private readonly string _reasoning = reasoning ?? ReasoningWithTag;

        public List<List<(string Role, string Content)>> RequestSnapshots { get; } = [];

        public string Name => "canned-stub";

        private int _calls;

        private void Snapshot(LlmRequest request) =>
            RequestSnapshots.Add([.. request.Conversation.History.Messages
                .Select(m => (m.Role ?? string.Empty, m.Content ?? string.Empty))]);

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Snapshot(request);

            if (++_calls > 1)
            {
                return Task.FromResult(LlmResult.Success(
                    [new LlmResponse { Content = "He creado el archivo.", FinishReason = FinishReason.Stop }]));
            }

            return Task.FromResult(LlmResult.Success(
                [new LlmResponse
                {
                    Content = "",
                    Reasoning = _reasoning,
                    FinishReason = alsoCallTagAsTool ? FinishReason.ToolCalls : FinishReason.Stop,
                    ToolCalls = alsoCallTagAsTool
                        ? [new ToolCall { Id = "call_1", Name = "write_file", Arguments = "{\"path\":\"analisis.md\"}" }]
                        : null
                }]));
        }

        public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!streaming) throw new NotImplementedException("not exercised by this test");

            Snapshot(request);
            await Task.Yield();

            // Chunked mid-tag on purpose: the reasoning channel has to survive a tag split across
            // deltas exactly like the content channel does.
            foreach (var slice in Slice(_reasoning, 17))
            {
                yield return new LlmStreamChunk { Delta = slice, IsThinking = true };
            }

            yield return new LlmStreamChunk { Delta = "", IsComplete = true, FinishReason = FinishReason.Stop };
        }

        private static IEnumerable<string> Slice(string text, int size)
        {
            for (var i = 0; i < text.Length; i += size)
            {
                yield return text.Substring(i, Math.Min(size, text.Length - i));
            }
        }

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static (IStep Step, ReasoningChannelService Service, RecordingTagHandler Handler) BuildStep(
        bool alsoCallTagAsTool = false, bool streaming = false)
    {
        var service = new ReasoningChannelService(alsoCallTagAsTool, streaming);
        var handler = new RecordingTagHandler();

        IStep step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "FileAgent",
            new LlmRole
            {
                Provider = new LlmProvider { Service = service },
                Model = "test-model",
                Inference = new LlmInferenceSettings { UseStreaming = streaming }
            },
            templateProvider: null,
            promtTemplateString: "{{Input}}")
        {
            Tools = [new FakeReadOnlyTool()],
            TagHandlers = [handler]
        };

        return (step, service, handler);
    }

    private static Task<IStepResult> RunAsync(IStep step) =>
        step.ExecuteAsync(
            new LlmStringStepResult(new EmptyStep("Seed")) { Content = "analiza el proyecto" },
            new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

    [Fact]
    public async Task Tag_written_in_the_reasoning_channel_is_executed()
    {
        var (step, _, handler) = BuildStep();

        await RunAsync(step);

        Assert.Single(handler.WrittenBodies);
        Assert.Contains("contenido real del archivo", handler.WrittenBodies[0]);
    }

    [Fact]
    public async Task Tag_written_in_the_reasoning_channel_is_executed_while_streaming()
    {
        var (step, _, handler) = BuildStep(streaming: true);

        await RunAsync(step);

        Assert.Single(handler.WrittenBodies);
        Assert.Contains("contenido real del archivo", handler.WrittenBodies[0]);
    }

    [Fact]
    public async Task Only_the_tag_reference_survives_from_the_reasoning_channel()
    {
        var (step, _, _) = BuildStep();

        var result = await RunAsync(step);
        var answer = (result as LlmStringStepResult)?.Content ?? string.Empty;

        Assert.Contains("[write_file: analisis.md", answer);

        // The tag body must not come back as text: it went to the file, and echoing it would both
        // dump the whole file into the answer and re-send it on every later request.
        Assert.DoesNotContain("contenido real del archivo", answer);
        Assert.DoesNotContain("<write_file", answer);

        // The reasoning prose is shown to the user as a thinking event but never persisted.
        Assert.DoesNotContain("Let's craft the analysis content", answer);
        Assert.DoesNotContain("Then we need to explain", answer);
    }

    [Fact]
    public async Task Assistant_message_sent_with_tool_calls_carries_the_tag_reference()
    {
        var (step, service, _) = BuildStep(alsoCallTagAsTool: true);

        await RunAsync(step);

        Assert.True(service.RequestSnapshots.Count >= 2, "expected a second request after the tool call");

        // The history the model gets back must show the write happened. Persisting an empty
        // assistant message here is what left it blind to its own completed action.
        var assistantMessages = service.RequestSnapshots[1]
            .Where(m => m.Role == LlmConstants.MessageRoles.Assistant)
            .Select(m => m.Content)
            .ToList();

        Assert.Contains(assistantMessages, m => m.Contains("[write_file: analisis.md"));
        Assert.DoesNotContain(assistantMessages, m => m.Contains("contenido real del archivo"));
    }

    [Fact]
    public async Task Tag_called_as_a_tool_after_it_already_ran_is_told_the_action_is_done()
    {
        var (step, service, handler) = BuildStep(alsoCallTagAsTool: true);

        await RunAsync(step);

        Assert.True(service.RequestSnapshots.Count >= 2, "expected a second request after the tool call");

        var toolMessages = service.RequestSnapshots[1]
            .Where(m => m.Role == LlmConstants.MessageRoles.Tool)
            .Select(m => m.Content)
            .ToList();

        Assert.Contains(toolMessages, m => m.Contains("already ran"));

        // Re-sending the usage instructions here is the contradiction that kept the loop alive:
        // it tells the model to write a tag it has just written.
        Assert.DoesNotContain(toolMessages, m => m.Contains("USE-THE-TAG-9f31"));

        // And the write itself happened exactly once, from the tag - not once per channel.
        Assert.Single(handler.WrittenBodies);
    }

    [Fact]
    public async Task Tag_never_written_still_gets_the_usage_instructions_back()
    {
        // Guards the opposite case: the "already ran" branch must not swallow the original fix
        // when the model only called the tool and never wrote the tag anywhere.
        var service = new ReasoningChannelService(
            alsoCallTagAsTool: true,
            streaming: false,
            reasoning: "I will just call the write_file function and be done with it.");

        var handler = new RecordingTagHandler();
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
            TagHandlers = [handler]
        };

        await RunAsync(step);

        var toolMessages = service.RequestSnapshots[1]
            .Where(m => m.Role == LlmConstants.MessageRoles.Tool)
            .Select(m => m.Content)
            .ToList();

        Assert.Empty(handler.WrittenBodies);
        Assert.Contains(toolMessages, m => m.Contains("is not a callable tool"));
        Assert.Contains(toolMessages, m => m.Contains("USE-THE-TAG-9f31"));
    }
}
