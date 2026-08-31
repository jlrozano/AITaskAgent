using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using OpenAILLmService.Normalization;

namespace OpenAILLmService.Tests;

public class XmlToolCallNormalizerTests
{
    private static LlmResponse Response(string content, List<ToolCall>? toolCalls = null) => new()
    {
        Content = content,
        ToolCalls = toolCalls,
        FinishReason = FinishReason.Stop
    };

    [Fact]
    public void Extracts_an_inline_xml_tool_call_into_native_tool_calls()
    {
        var normalizer = new XmlToolCallNormalizer();
        var ctx = new NormalizationContext("Step", ["get_weather"]);
        var response = Response("<get_weather city=\"Madrid\"></get_weather>");

        var result = normalizer.Normalize(response, ctx);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("get_weather", call.Name);
        Assert.Contains("Madrid", call.Arguments);
        Assert.Equal(string.Empty, result.Content);
    }

    [Fact]
    public void Leaves_response_untouched_when_it_already_has_native_tool_calls()
    {
        var normalizer = new XmlToolCallNormalizer();
        var ctx = new NormalizationContext("Step", ["get_weather"]);
        var nativeCalls = new List<ToolCall> { new() { Id = "1", Name = "get_weather", Arguments = "{}" } };
        var response = Response("<get_weather city=\"Madrid\"></get_weather>", nativeCalls);

        var result = normalizer.Normalize(response, ctx);

        Assert.Same(nativeCalls, result.ToolCalls);
        Assert.Equal("<get_weather city=\"Madrid\"></get_weather>", result.Content);
    }

    [Fact]
    public void Leaves_response_untouched_when_no_tools_are_available()
    {
        var normalizer = new XmlToolCallNormalizer();
        var ctx = new NormalizationContext("Step", []);
        var response = Response("<get_weather city=\"Madrid\"></get_weather>");

        var result = normalizer.Normalize(response, ctx);

        Assert.Null(result.ToolCalls);
        Assert.Equal("<get_weather city=\"Madrid\"></get_weather>", result.Content);
    }

    [Fact]
    public void Plain_text_that_matches_no_known_tool_name_is_left_as_content()
    {
        var normalizer = new XmlToolCallNormalizer();
        var ctx = new NormalizationContext("Step", ["get_weather"]);
        var response = Response("just a normal answer with <em>markup</em> in it");

        var result = normalizer.Normalize(response, ctx);

        Assert.Null(result.ToolCalls);
        Assert.Equal("just a normal answer with <em>markup</em> in it", result.Content);
    }
}
