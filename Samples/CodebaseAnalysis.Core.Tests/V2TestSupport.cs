using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using System.Runtime.CompilerServices;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="ILlmService"/> en streaming y sin red que, en cada iteración, pide la siguiente tool del guion con sus
/// argumentos; agotado el guion, responde <see cref="FinalAnswer"/>. Guarda el resultado de cada tool.
/// </summary>
internal sealed class ToolScriptLlmService(params (string Tool, string Arguments)[] script) : ILlmService
{
    public const string FinalAnswer = "Hecho.";

    private readonly Lock _lock = new();
    private readonly List<string?> _toolResults = [];
    private int _next;

    public string Name => "tool-script";

    /// <summary>Contenido de los mensajes <c>tool</c> que ha ido recibiendo, en orden.</summary>
    public IReadOnlyList<string?> ToolResults
    {
        get
        {
            lock (_lock)
            {
                return [.. _toolResults];
            }
        }
    }

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Este servicio solo responde en streaming.");

    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = request.Conversation.History.Messages;
        int index;
        lock (_lock)
        {
            if (messages.Count > 0 && messages[^1].Role == LlmConstants.MessageRoles.Tool)
                _toolResults.Add(messages[^1].Content);
            index = _next++;
        }

        await Task.Yield();
        if (index < script.Length)
        {
            var (tool, arguments) = script[index];
            yield return new LlmStreamChunk
            {
                Delta = string.Empty,
                ToolCallUpdates = [new ToolCallUpdate { Index = 0, ToolCallId = $"call_{index + 1}", FunctionName = tool, FunctionArgumentsUpdate = arguments }]
            };
            yield return new LlmStreamChunk { Delta = string.Empty, IsComplete = true, FinishReason = FinishReason.ToolCalls, TokensUsed = 10 };
            yield break;
        }

        yield return new LlmStreamChunk { Delta = FinalAnswer };
        yield return new LlmStreamChunk { Delta = string.Empty, IsComplete = true, FinishReason = FinishReason.Stop, TokensUsed = 10 };
    }

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 1_000_000;
}
