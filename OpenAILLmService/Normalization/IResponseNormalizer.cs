using AITaskAgent.LLM.Models;

namespace OpenAILLmService.Normalization;

public interface IResponseNormalizer
{
    /// <summary>
    /// Normaliza una LlmResponse completamente ensamblada.
    /// Se aplica DESPUÉS del ensamblado de streaming o de la llamada non-streaming,
    /// y DESPUÉS de que los IReasoningExtractor hayan procesado su parte.
    /// </summary>
    LlmResponse Normalize(LlmResponse response, NormalizationContext ctx);
}

public sealed record NormalizationContext(
    string StepName,
    IReadOnlyList<string>? AvailableToolNames
);
