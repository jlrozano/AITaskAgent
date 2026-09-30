using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;

namespace TextToTemplateDemo;

/// <summary>
/// <see cref="ILlmService"/> sin red: guarda la petición y responde con el mensaje de usuario tal
/// como lo recibió, es decir, con el prompt ya renderizado por el paso de plantilla.
/// </summary>
internal sealed class EchoLlmService : ILlmService
{
    public LlmRequest? LastRequest { get; private set; }

    /// <summary>Último mensaje de usuario de la conversación que envió el paso.</summary>
    public string? LastUserMessage =>
        LastRequest?.Conversation.History.Messages.LastOrDefault(m => m.Role == "user")?.Content;

    public string Name => "echo";

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(LlmResult.Success(
            [new LlmResponse { Content = LastUserMessage ?? "", FinishReason = FinishReason.Stop }],
            tokensUsed: 0, promptTokens: 0, completionTokens: 0, model: "echo-model"));
    }

    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("La demo no usa streaming.");

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 1_000_000;
}
