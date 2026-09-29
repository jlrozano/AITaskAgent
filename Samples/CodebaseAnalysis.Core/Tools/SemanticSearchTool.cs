using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Reports;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// <c>semantic_search { query, top_k?, kinds? }</c> (plan v2 §8.2.4): busca por significado en los documentos e
/// informes añadidos a los embeddings. Solo en el chat; los auditores no la reciben (V2-D17).
/// </summary>
public sealed class SemanticSearchTool : LlmTool
{
    public const string ToolName = "semantic_search";

    public override string Name => ToolName;
    public override string Description =>
        "Busca por significado en la documentación aportada y en los informes añadidos a los embeddings. Úsala para localizar qué documentación habla de algo; confirma con read_document o read_report antes de citar. Cada resultado indica si su fuente está desactualizada (stale).";
    public override bool IsReadOnly => true;

    protected override BinaryData ParametersSchema => BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "query": { "type": "string", "description": "Qué buscar, en lenguaje natural." },
        "top_k": { "type": "integer", "description": "Resultados (8 por defecto, 20 como máximo)." },
        "kinds": { "type": "array", "items": { "type": "string", "enum": ["document", "report", "custom"] }, "description": "Opcional: limitar a documentos, informes del análisis o informes propios." }
      },
      "required": ["query"]
    }
    """);

    protected override async Task<string> InternalExecuteAsync(string argumentsJson, PipelineContext context, ILogger logger, CancellationToken cancellationToken)
    {
        string? query = null;
        int? topK = null;
        var kinds = new List<SourceKind>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                query = q.GetString();
            if (doc.RootElement.TryGetProperty("top_k", out var k) && k.TryGetInt32(out var value))
                topK = value;
            if (doc.RootElement.TryGetProperty("kinds", out var ks) && ks.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ks.EnumerateArray())
                {
                    switch (item.GetString())
                    {
                        case SourceRef.DocumentKindName: kinds.Add(SourceKind.Document); break;
                        case SourceRef.ReportKindName: kinds.Add(SourceKind.Report); break;
                        case SourceRef.CustomKindName: kinds.Add(SourceKind.Custom); break;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Argumentos JSON no válidos: {ex.Message}");
        }

        var runtime = ChatKnowledgeServicesAccessor.Get(context)?.Embeddings;
        if (runtime is not { Enabled: true })
            return $"La búsqueda semántica no está disponible: {runtime?.DisabledReason ?? "no hay embeddings configurados"}.";

        var paths = ReportToolSupport.GetKnowledgePaths(context)
            ?? throw new InvalidOperationException("No hay repositorio activo.");
        IReadOnlyList<Knowledge.Embeddings.SemanticSearchResult> results;
        try
        {
            results = await runtime.For(paths).SearchAsync(query ?? string.Empty, topK, kinds, cancellationToken);
        }
        catch (KnowledgeOperationException ex)
        {
            throw new InvalidOperationException($"[{ex.Code}] {ex.Message}", ex);
        }

        if (results.Count == 0)
            return "Sin resultados: no hay documentación añadida a los embeddings o nada se parece a la consulta.";

        return ReadDocumentTool.DataNotice + "\n" + JsonSerializer.Serialize(results.Select(r => new
        {
            score = Math.Round(r.Score, 4),
            source = new { kind = r.Source.KindName, id = r.Source.Id, title = r.Title },
            section = r.Section,
            lines = r.StartLine is int s ? $"{s}-{r.EndLine}" : null,
            text = r.Text,
            stale = r.Stale
        }), ReportToolSupport.JsonOptions);
    }
}
