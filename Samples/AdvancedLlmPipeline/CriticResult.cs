using System.ComponentModel;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.LLM.Results;
using Newtonsoft.Json;

namespace Samples.AdvancedLlmPipeline;

/// <summary>
/// DTO con los datos de la crítica devueltos por el LLM.
/// </summary>
[Description("Literary critique response containing recommendations and detailed analysis as plain text strings.")]
public sealed record CriticData
{
    /// <summary>
    /// Recomendaciones específicas para mejorar la historia.
    /// Este campo se pasa al siguiente paso (Rewriter).
    /// </summary>
    [JsonProperty("recommendations")]
    [Description("Specific and actionable recommendations to improve the story. Write as a single paragraph or bullet-point list in plain text format. Do NOT use nested objects.")]
    public string Recommendations { get; init; } = string.Empty;

    /// <summary>
    /// Crítica detallada de ambos borradores.
    /// Este campo se muestra en consola para observabilidad.
    /// </summary>
    [JsonProperty("detailedCritique")]
    [Description("Comprehensive critique of both drafts covering narrative structure, character development, language, and emotional impact. Write as plain text paragraphs. Do NOT use nested JSON objects - this must be a single string value.")]
    public string DetailedCritique { get; init; } = string.Empty;
}

/// <summary>
/// Resultado estructurado del paso Crítico.
/// Value contiene CriticData con las recomendaciones y crítica.
/// </summary>
public sealed class CriticResult(IStep step) : LlmStepResult<CriticData>(step)
{
    /// <summary>
    /// Gets the name of the first writer associated with the item.
    /// </summary>
    public string Writer1 { get; set; } = string.Empty;
    /// <summary>
    /// Gets the name of the second writer associated with the item.
    /// </summary>
    public string Writer2 { get; set; } = string.Empty;

    /// <summary>
    /// Validación estructural del resultado.
    /// </summary>
    public override Task<(bool IsValid, string? Error)> ValidateAsync()
    {
        if (Value == null)
        {
            return Task.FromResult((false, (string?)"Critic response is null"));
        }

        if (string.IsNullOrWhiteSpace(Value.Recommendations))
        {
            return Task.FromResult((false, (string?)"Recommendations field is empty"));
        }

        if (string.IsNullOrWhiteSpace(Value.DetailedCritique))
        {
            return Task.FromResult((false, (string?)"DetailedCritique field is empty"));
        }

        return Task.FromResult((true, (string?)null));
    }
}
