using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Support;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace OpenAILLmService.Tests;

/// <summary>
/// LlmInferenceSettings (the base of LlmRequest) stores framework-facade properties
/// (Temperature, MaxTokens, JsonSupport, ...) and vendor-specific passthrough options in the
/// *same* Options JObject. BuildRequestBody used to dump that whole bag into the HTTP body
/// verbatim, so a role with JsonSupport:true sent a literal "JsonSupport" field that OpenRouter
/// and Gemini's OpenAI-compat endpoint rejected with HTTP 400 (unrecognized property).
/// </summary>
public class BuildRequestBodyTests
{
    private static LlmRequest MinimalRequest() => new()
    {
        Model = "test-model",
        Conversation = new ConversationContext()
    };

    [Theory]
    [InlineData("Temperature")]
    [InlineData("TopP")]
    [InlineData("MaxTokens")]
    [InlineData("UseStreaming")]
    [InlineData("JsonSupport")]
    [InlineData("ToolSupport")]
    [InlineData("Stop")]
    public void Framework_facade_keys_are_not_forwarded_as_raw_body_fields(string facadeKey)
    {
        var request = MinimalRequest() with { JsonSupport = true, Temperature = 0.3f, MaxTokens = 2000 };

        var body = OpenAILlmService.BuildRequestBody(request);

        Assert.False(body.ContainsKey(facadeKey),
            $"'{facadeKey}' leaked into the request body as a raw field.");
    }

    [Fact]
    public void Vendor_specific_options_are_still_forwarded_verbatim()
    {
        var request = MinimalRequest();
        request.Options["provider_specific_thing"] = 123;

        var body = OpenAILlmService.BuildRequestBody(request);

        Assert.Equal(123, body["provider_specific_thing"]?.Value<int>());
    }

    [Fact]
    public void Typed_temperature_maps_to_lowercase_openai_field()
    {
        var request = MinimalRequest() with { Temperature = 0.42f };

        var body = OpenAILlmService.BuildRequestBody(request);

        Assert.Equal(0.42f, body["temperature"]?.Value<float>());
        Assert.False(body.ContainsKey("Temperature"));
    }

    [Fact]
    public void Typed_max_tokens_and_top_p_and_stop_map_to_openai_fields()
    {
        var request = MinimalRequest() with { MaxTokens = 512, TopP = 0.9f, Stop = ["END"] };

        var body = OpenAILlmService.BuildRequestBody(request);

        Assert.Equal(512, body["max_tokens"]?.Value<int>());
        Assert.Equal(0.9f, body["top_p"]?.Value<float>());
        Assert.Equal(["END"], body["stop"]!.Select(t => t.Value<string>()));
    }

    [Fact]
    public void Json_object_response_format_without_schema_uses_json_object_mode()
    {
        var request = MinimalRequest() with
        {
            ResponseFormat = new ResponseFormatOptions { Type = ResponseFormatType.JsonObject }
        };

        var body = OpenAILlmService.BuildRequestBody(request);

        Assert.Equal("json_object", body["response_format"]?["type"]?.Value<string>());
    }

    [Fact]
    public void Json_object_response_format_with_schema_embeds_the_schema()
    {
        var schema = new JsonSchema { Type = JsonObjectType.Object };
        var request = MinimalRequest() with
        {
            ResponseFormat = new ResponseFormatOptions
            {
                Type = ResponseFormatType.JsonObject,
                JsonSchema = schema
            }
        };

        var body = OpenAILlmService.BuildRequestBody(request);

        Assert.Equal("json_schema", body["response_format"]?["type"]?.Value<string>());
        Assert.NotNull(body["response_format"]?["json_schema"]?["schema"]);
    }

    [Fact]
    public void Streaming_request_adds_stream_flag_and_usage_option()
    {
        var body = OpenAILlmService.BuildRequestBody(MinimalRequest(), stream: true);

        Assert.True(body["stream"]?.Value<bool>());
        Assert.True(body["stream_options"]?["include_usage"]?.Value<bool>());
    }

    [Fact]
    public void Non_streaming_request_has_no_stream_fields()
    {
        var body = OpenAILlmService.BuildRequestBody(MinimalRequest());

        Assert.False(body.ContainsKey("stream"));
        Assert.False(body.ContainsKey("stream_options"));
    }
}
