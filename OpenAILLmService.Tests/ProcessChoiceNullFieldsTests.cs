using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using OpenAILLmService.Normalization;

namespace OpenAILLmService.Tests;

/// <summary>
/// A real Azure OpenAI response returned a choice shaped like
/// <c>{"index":0,"message":null,"finish_reason":"content_filter"}</c>, and crashed with
/// "Cannot access child value on Newtonsoft.Json.Linq.JValue" instead of parsing to an empty
/// response. Json.NET's indexer on a JObject returns a real C# null when a key is absent, but a
/// JValue wrapping an explicit JSON `null` when the key IS present with that value - so
/// `message?["content"]` does NOT short-circuit in the second case and instead indexes into the
/// JValue, which only JObject/JArray know how to do. ProcessChoice now casts with `as JObject`
/// rather than indexing directly, which turns both cases into a real C# null.
/// </summary>
public class ProcessChoiceNullFieldsTests
{
    private static OpenAILlmService Service() => new(NullLogger<OpenAILlmService>.Instance);

    private static NormalizationContext Ctx() => new("openai", null);

    [Fact]
    public void A_choice_with_an_explicit_null_message_parses_to_an_empty_response()
    {
        var choice = JObject.Parse("""{"index":0,"message":null,"finish_reason":"content_filter"}""");

        var response = Service().ProcessChoice(choice, Ctx());

        Assert.Equal(string.Empty, response.Content);
        Assert.Null(response.Reasoning);
        Assert.Null(response.ToolCalls);
    }

    [Fact]
    public void A_message_with_explicit_null_content_and_reasoning_parses_to_an_empty_response()
    {
        // The ordinary shape for a tool-calls-only turn: message is a real object, but its
        // scalar fields are explicit JSON nulls rather than absent.
        var choice = JObject.Parse("""
            {"index":0,"message":{"role":"assistant","content":null,"reasoning_content":null,"tool_calls":null},"finish_reason":"stop"}
            """);

        var response = Service().ProcessChoice(choice, Ctx());

        Assert.Equal(string.Empty, response.Content);
        Assert.Null(response.Reasoning);
        Assert.Null(response.ToolCalls);
    }

    [Fact]
    public void A_normal_message_still_parses_correctly()
    {
        var choice = JObject.Parse("""
            {"index":0,"message":{"role":"assistant","content":"hola"},"finish_reason":"stop"}
            """);

        var response = Service().ProcessChoice(choice, Ctx());

        Assert.Equal("hola", response.Content);
    }
}
