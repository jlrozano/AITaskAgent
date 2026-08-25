using AITaskAgent.LLM.Support;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Tests;

/// <summary>
/// Open-weight models routinely ignore the OpenAI tool_calls protocol and emit calls inside the
/// message text, each family in its own shape. This parser is what lets those models work at all,
/// so each supported shape gets a test.
/// </summary>
public class ToolCallXmlParserTests
{
    private static readonly HashSet<string> Tools = new(StringComparer.OrdinalIgnoreCase)
    {
        "view_file", "write_to_file", "get_weather"
    };

    private static string Arg(string argumentsJson, string key) =>
        JObject.Parse(argumentsJson)[key]?.ToString() ?? "";

    [Fact]
    public void Parses_glm_pipe_delimited_calls()
    {
        var content = "<|get_weather><arg_key>city<arg_value>Madrid<|/get_weather|>";

        var calls = ToolCallXmlParser.ParseToolCalls(content, Tools);

        var call = Assert.Single(calls);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Madrid", Arg(call.Arguments, "city"));
    }

    [Fact]
    public void Parses_named_tool_call_wrapper()
    {
        var content = "<tool_call>get_weather\n<city>Madrid</city></tool_call>";

        var calls = ToolCallXmlParser.ParseToolCalls(content, Tools);

        var call = Assert.Single(calls);
        Assert.Equal("get_weather", call.Name);
    }

    [Fact]
    public void Parses_plain_xml_with_child_elements()
    {
        var content = "<get_weather><city>Madrid</city></get_weather>";

        var calls = ToolCallXmlParser.ParseToolCalls(content, Tools);

        var call = Assert.Single(calls);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Madrid", Arg(call.Arguments, "city"));
    }

    [Fact]
    public void Parses_xml_attributes_as_arguments()
    {
        var content = "<view_file path=\"src/main.cs\"></view_file>";

        var calls = ToolCallXmlParser.ParseToolCalls(content, Tools);

        var call = Assert.Single(calls);
        Assert.Equal("view_file", call.Name);
        Assert.Equal("src/main.cs", Arg(call.Arguments, "path"));
    }

    [Fact]
    public void Parses_json_inside_a_tool_call_wrapper()
    {
        var content = """<tool_call>{"name":"get_weather","arguments":{"city":"Madrid"}}</tool_call>""";

        var calls = ToolCallXmlParser.ParseToolCalls(content, Tools);

        var call = Assert.Single(calls);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Madrid", Arg(call.Arguments, "city"));
    }

    [Fact]
    public void Parses_bare_json_in_content()
    {
        var content = """{"name":"get_weather","arguments":{"city":"Madrid"}}""";

        var calls = ToolCallXmlParser.ParseJsonInContentToolCalls(content, Tools);

        var call = Assert.Single(calls);
        Assert.Equal("get_weather", call.Name);
    }

    [Fact]
    public void Matches_names_across_dash_and_underscore()
    {
        var content = "<get-weather><city>Madrid</city></get-weather>";

        var calls = ToolCallXmlParser.ParseToolCalls(content, Tools);

        Assert.Equal("get_weather", Assert.Single(calls).Name);
    }

    [Fact]
    public void Ignores_tags_that_are_not_known_tools()
    {
        // Deliberately no substring matching: <file> must not resolve to view_file, or ordinary
        // prose containing angle brackets would turn into phantom tool calls.
        var content = "<file>README.md</file>";

        Assert.Empty(ToolCallXmlParser.ParseToolCalls(content, Tools));
    }

    [Fact]
    public void Assigns_a_distinct_id_to_each_call()
    {
        var content = "<get_weather><city>Madrid</city></get_weather><view_file path=\"a.cs\"></view_file>";

        var calls = ToolCallXmlParser.ParseToolCalls(content, Tools);

        Assert.Equal(2, calls.Count);
        Assert.Equal(2, calls.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void RemoveToolCallTags_strips_the_call_from_the_prose()
    {
        var content = "Checking. <get_weather><city>Madrid</city></get_weather> Done.";

        var cleaned = ToolCallXmlParser.RemoveToolCallTags(content, Tools);

        Assert.DoesNotContain("get_weather", cleaned);
        Assert.Contains("Checking.", cleaned);
        Assert.Contains("Done.", cleaned);
    }
}
