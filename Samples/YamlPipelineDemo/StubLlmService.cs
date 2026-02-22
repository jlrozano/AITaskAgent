using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Models;

namespace YamlPipelineDemo;

/// <summary>
/// Stub LLM service that returns a hardcoded JSON response matching ResumenOutput.
/// Used to demonstrate the YAML Pipeline Engine machinery without a real API call.
/// </summary>
public sealed class StubLlmService : ILlmService
{
    private const string HardcodedResponse = """
        {
          "resumen": "Factura de Acme S.A. por servicios de consultoría en marzo 2026 por un monto de $1,500.00.",
          "aprobado": true,
          "motivo": "El monto es razonable para servicios de consultoría mensuales."
        }
        """;

    public Task<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"  [StubLlm] SystemPrompt: {Truncate(request.SystemPrompt)}");
        Console.WriteLine($"  [StubLm]  UserMessage:  {Truncate(GetLastUserMessage(request))}");

        return Task.FromResult(new LlmResponse
        {
            Content = HardcodedResponse
        });
    }

    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 8192;

    private static string? GetLastUserMessage(LlmRequest request)
    {
        var messages = request.Conversation?.History?.Messages;
        if (messages == null || messages.Count == 0) return null;
        return messages[^1].Content;
    }

    private static string Truncate(string? s, int max = 80)
    {
        if (s == null) return "(null)";
        return s.Length <= max ? s : s[..max] + "...";
    }
}
