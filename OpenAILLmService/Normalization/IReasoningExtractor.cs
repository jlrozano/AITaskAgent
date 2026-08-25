using AITaskAgent.LLM.Models;

namespace OpenAILLmService.Normalization;

public interface IReasoningExtractor
{
    /// <summary>
    /// Streaming: procesa un chunk y devuelve contenido y reasoning separados.
    /// El estado entre chunks se mantiene en TagExtractionContext.
    /// </summary>
    ChunkResult ProcessChunk(string delta, TagExtractionContext ctx);

    /// <summary>
    /// Flush al final del stream: libera cualquier PendingBuffer retenido.
    /// </summary>
    ChunkResult Flush(TagExtractionContext ctx);

    /// <summary>
    /// Post-ensamblado (non-streaming): normaliza LlmResponse moviendo
    /// reasoning de Content a Reasoning.
    /// </summary>
    LlmResponse NormalizeAssembled(LlmResponse response);
}