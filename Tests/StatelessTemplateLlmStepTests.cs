using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// TemplateLlmStep's constructor derives a default template name from the step name when the
/// caller does not pass one - "{name}_prompt.md" for the prompt and role.SystemPrompt, or
/// "{name}_system.md", for the system prompt. StoryPipelineFactory's steps always pass explicit
/// names, so these conventions only ever fire for callers that don't - pin them so a future
/// refactor of the defaulting logic doesn't silently change what an unconfigured step sends.
/// </summary>
public sealed class StatelessTemplateLlmStepTests
{
    private sealed class StubService : ILlmService
    {
        public string Name => "stub";
        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(LlmResult.Success([]));
        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<LlmStreamChunk>();
        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static LlmRole RoleWith(string? systemPrompt = null) => new()
    {
        Provider = new LlmProvider { Service = new StubService() },
        Model = "test-model",
        SystemPrompt = systemPrompt
    };

    [Fact]
    public void Defaults_prompt_template_name_to_name_prompt_md_when_not_specified()
    {
        var step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Outliner", RoleWith());

        Assert.Equal("Outliner_prompt.md", step.Prompt);
    }

    [Fact]
    public void Defaults_system_template_name_to_name_system_md_when_role_has_no_system_prompt()
    {
        var step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Outliner", RoleWith());

        Assert.Equal("Outliner_system.md", step.SystemPrompt);
    }

    [Fact]
    public void Defaults_system_prompt_to_the_role_SystemPrompt_when_present()
    {
        var step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Outliner", RoleWith(systemPrompt: "You are an outliner."));

        Assert.Equal("You are an outliner.", step.SystemPrompt);
    }

    [Fact]
    public void Explicit_template_names_win_over_the_defaults()
    {
        var step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Outliner", RoleWith(),
            promtTemplateString: "outliner_prompt.md",
            systemTemplateString: "outliner_system.md");

        Assert.Equal("outliner_prompt.md", step.Prompt);
        Assert.Equal("outliner_system.md", step.SystemPrompt);
    }
}
