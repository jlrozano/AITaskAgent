using AITaskAgent.LLM.Models;

namespace AITaskAgent.LLM.Abstractions;

/// <summary>
/// Placeholder ILlmService used as a sentinel for YAML steps before their preset is
/// resolved by the pipeline factory. If this service is ever actually invoked, it throws
/// to make the configuration error visible immediately.
/// </summary>
internal sealed class NullLlmService : ILlmService
{
    public string Name => "Null";

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "NullLlmService was invoked — the step's LLM preset was not resolved before execution. " +
            "Ensure YamlPipelineFactory.InjectDependencies has run and a valid ILlmResolver is registered.");

    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "NullLlmService was invoked — the step's LLM preset was not resolved before execution.");

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 0;
}
