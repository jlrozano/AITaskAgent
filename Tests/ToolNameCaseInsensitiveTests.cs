using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Tools.Abstractions;
using Microsoft.Extensions.Logging;

namespace AITaskAgent.Tests;

/// <summary>
/// Tool lookup at execution time was ordinal while the registry, the tag lookup and the stuck-loop guard all ignore
/// case: a model calling <c>Read_File</c> for a tool named <c>read_file</c> got "not found".
/// </summary>
public sealed class ToolNameCaseInsensitiveTests
{
    private sealed class CountingTool : ITool
    {
        public int Calls;
        public string Name => "read_file";
        public string Description => "test tool";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult("file content");
        }
    }

    /// <summary>First turn asks for the tool with the wrong casing; the next turn ends the conversation.</summary>
    private sealed class MiscasedCallService : ILlmService
    {
        private int _calls;
        public string Name => "miscased-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var response = ++_calls == 1
                ? new LlmResponse
                {
                    Content = "",
                    FinishReason = FinishReason.ToolCalls,
                    ToolCalls = [new ToolCall { Id = "call_1", Name = "Read_File", Arguments = "{}" }]
                }
                : new LlmResponse { Content = "done", FinishReason = FinishReason.Stop };
            return Task.FromResult(LlmResult.Success([response]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("not exercised by this test");

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    [Fact]
    public async Task A_tool_call_with_different_casing_still_runs_the_tool()
    {
        var tool = new CountingTool();
        IStep step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "FileAgent",
            new LlmRole
            {
                Provider = new LlmProvider { Service = new MiscasedCallService() },
                Model = "test-model",
                Inference = new LlmInferenceSettings { UseStreaming = false }
            },
            templateProvider: null,
            promtTemplateString: "{{Input}}")
        {
            Tools = [tool]
        };

        await step.ExecuteAsync(
            new LlmStringStepResult(new EmptyStep("Seed")) { Content = "hazlo" },
            new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        Assert.Equal(1, tool.Calls);
    }
}
