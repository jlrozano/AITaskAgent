using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;

namespace OpenAILLmService.Normalization;

public sealed class XmlToolCallNormalizer : IResponseNormalizer
{

    public LlmResponse Normalize(LlmResponse response, NormalizationContext ctx)
    {
        // 1. Si ya tiene tool calls nativos, no hacemos nada
        if (response.ToolCalls != null && response.ToolCalls.Count > 0)
            return response;

        // 2. Si no hay herramientas disponibles, no podemos parsear XML a tool calls
        if (ctx.AvailableToolNames == null || ctx.AvailableToolNames.Count == 0)
            return response;

        var toolNamesHashSet = ctx.AvailableToolNames.ToHashSet();
        var toolCalls = ToolCallXmlParser.ParseToolCalls(response.Content, toolNamesHashSet);

        if (toolCalls.Count == 0)
            return response;

        var cleanContent = ToolCallXmlParser.RemoveToolCallTags(response.Content, toolNamesHashSet).Trim();

        return response with
        {
            Content = cleanContent,
            ToolCalls = toolCalls
        };
    }
}
