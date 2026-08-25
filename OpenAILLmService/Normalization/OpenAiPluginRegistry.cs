using AITaskAgent.LLM.Models;
using System.Collections.Concurrent;

namespace OpenAILLmService.Normalization;

/// <summary>
/// Registry para extender el comportamiento del servicio OpenAI
/// (Reasoning Extractors y Response Normalizers).
/// </summary>
public static class OpenAiPluginRegistry
{
    private static readonly ConcurrentBag<IReasoningExtractor> _extractors =
    [
        new ThinkTagReasoningExtractor()
    ];

    private static readonly ConcurrentBag<IResponseNormalizer> _normalizers =
    [
        new XmlToolCallNormalizer()
    ];

    /// <summary>
    /// Aplica todos los extractores registrados a una respuesta ensamblada (non-streaming).
    /// </summary>
    public static LlmResponse ApplyExtractors(LlmResponse response)
    {
        var current = response;
        foreach (var extractor in _extractors)
            current = extractor.NormalizeAssembled(current);
        return current;
    }

    /// <summary>
    /// Aplica todos los normalizadores registrados a una respuesta ensamblada (non-streaming).
    /// </summary>
    public static LlmResponse ApplyNormalizers(LlmResponse response, NormalizationContext context)
    {
        var current = response;
        foreach (var normalizer in _normalizers)
            current = normalizer.Normalize(current, context);
        return current;
    }

    /// <summary>
    /// Procesa un chunk de streaming a través de todos los extractores.
    /// Devuelve contenido limpio y reasoning del chunk actual, sin acumulación global.
    /// El Content de cada extractor es el delta de entrada para el siguiente.
    /// </summary>
    public static ChunkResult ProcessStreamChunk(string delta, TagExtractionContext ctx)
    {
        var currentDelta      = delta;
        var totalReasoning    = new System.Text.StringBuilder();

        foreach (var extractor in _extractors)
        {
            var result = extractor.ProcessChunk(currentDelta, ctx);
            totalReasoning.Append(result.Reasoning);
            currentDelta = result.Content;
        }

        return new ChunkResult
        {
            Content   = currentDelta,
            Reasoning = totalReasoning.ToString()
        };
    }

    /// <summary>
    /// Flush al final del stream SSE ([DONE]).
    /// Libera cualquier PendingBuffer retenido en todos los extractores.
    /// </summary>
    public static ChunkResult FlushStream(TagExtractionContext ctx)
    {
        var totalContent   = new System.Text.StringBuilder();
        var totalReasoning = new System.Text.StringBuilder();

        foreach (var extractor in _extractors)
        {
            var result = extractor.Flush(ctx);
            totalContent.Append(result.Content);
            totalReasoning.Append(result.Reasoning);
        }

        return new ChunkResult
        {
            Content   = totalContent.ToString(),
            Reasoning = totalReasoning.ToString()
        };
    }

    public static void RegisterExtractor(IReasoningExtractor extractor)  => _extractors.Add(extractor);
    public static void RegisterNormalizer(IResponseNormalizer normalizer) => _normalizers.Add(normalizer);
}