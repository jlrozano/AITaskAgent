using AITaskAgent.Configuration;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AITaskAgent.Tests;

/// <summary>
/// Three ways the configuration binder lost or mangled values of <c>AITaskAgent:LlmProviders</c>, found while moving the
/// codebase-analysis sample onto it: <c>Inference:Options</c> wiped the facade keys bound before it, a <c>null</c> price
/// in <c>Models</c> refused to start, and an <c>ApiVersion</c> shaped like a date came back as a reformatted date.
/// </summary>
public sealed class LlmProvidersConfigurationBindingTests
{
    private sealed class StubService : ILlmService
    {
        public string Name => "stub";
        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(LlmResult.Success([]));
        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<LlmStreamChunk>();
        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private const string Llm = "AITaskAgent:LlmProviders";

    private static ServiceProvider Build(Dictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?>
        {
            [$"{Llm}:Providers:P1:BaseUrl"] = "https://example.test",
            [$"{Llm}:Providers:P1:ServiceType"] = "stub",
            [$"{Llm}:Roles:default:Provider"] = "P1",
            [$"{Llm}:Roles:default:Model"] = "test-model"
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton<ILlmService, StubService>();
        services.AddAITaskAgent();
        return services.BuildServiceProvider();
    }

    private static LlmEnvironment Environment(Dictionary<string, string?> extra)
    {
        using var provider = Build(extra);
        return provider.GetRequiredService<IOptions<LlmEnvironment>>().Value;
    }

    [Fact]
    public void Inference_Options_keeps_the_facade_keys_whatever_their_order()
    {
        // Configuration hands keys over sorted: JsonSupport and MaxTokens come before Options, Temperature after it.
        var inference = Environment(new()
        {
            [$"{Llm}:Roles:default:Inference:MaxTokens"] = "4096",
            [$"{Llm}:Roles:default:Inference:JsonSupport"] = "true",
            [$"{Llm}:Roles:default:Inference:Temperature"] = "0.2",
            [$"{Llm}:Roles:default:Inference:Options:reasoning_effort"] = "none"
        }).Roles["default"].Inference!;

        Assert.Equal(4096, inference.MaxTokens);
        Assert.True(inference.JsonSupport);
        Assert.Equal(0.2f, inference.Temperature);
        Assert.Equal("none", inference.GetOption<string>("reasoning_effort"));
    }

    [Fact]
    public void Assigning_Options_in_an_initializer_keeps_the_facades_set_before_it()
    {
        var inference = new LlmInferenceSettings
        {
            MaxTokens = 100,
            Options = new() { ["reasoning_effort"] = "none" }
        };

        Assert.Equal(100, inference.MaxTokens);
        Assert.Equal("none", inference.GetOption<string>("reasoning_effort"));
    }

    [Fact]
    public void A_null_model_price_binds_as_unknown_and_its_cost_is_not_computed()
    {
        var provider = Environment(new()
        {
            [$"{Llm}:Providers:P1:Models:0:Name"] = "test-model",
            // What the JSON provider stores for "InputPricePerMillion": null.
            [$"{Llm}:Providers:P1:Models:0:InputPricePerMillion"] = "",
            [$"{Llm}:Providers:P1:Models:0:OutputPricePerMillion"] = null
        }).Providers["P1"];

        var model = Assert.Single(provider.Models);
        Assert.Null(model.InputPricePerMillion);
        Assert.Null(model.OutputPricePerMillion);
        Assert.Null(provider.CalculateCost("test-model", 1000, 1000));
    }

    [Fact]
    public void The_cached_input_price_binds_when_present_and_is_null_when_absent()
    {
        var provider = Environment(new()
        {
            [$"{Llm}:Providers:P1:Models:0:Name"] = "cached",
            [$"{Llm}:Providers:P1:Models:0:InputPricePerMillion"] = "2.5",
            [$"{Llm}:Providers:P1:Models:0:OutputPricePerMillion"] = "15",
            [$"{Llm}:Providers:P1:Models:0:CachedInputPricePerMillion"] = "0.25",
            [$"{Llm}:Providers:P1:Models:1:Name"] = "plain",
            [$"{Llm}:Providers:P1:Models:1:InputPricePerMillion"] = "2.5",
            [$"{Llm}:Providers:P1:Models:1:OutputPricePerMillion"] = "15"
        }).Providers["P1"];

        Assert.Equal(0.25m, provider.Models[0].CachedInputPricePerMillion);
        Assert.Null(provider.Models[1].CachedInputPricePerMillion);
        Assert.Equal(0.25m, provider.CalculateCost("cached", 1_000_000, 0, cachedPromptTokens: 1_000_000));
        Assert.Equal(2.5m, provider.CalculateCost("plain", 1_000_000, 0, cachedPromptTokens: 1_000_000));
    }

    [Fact]
    public void An_ApiVersion_shaped_like_a_date_arrives_untouched()
    {
        var provider = Environment(new()
        {
            [$"{Llm}:Providers:P1:ApiVersion"] = "2024-10-21"
        }).Providers["P1"];

        Assert.Equal("2024-10-21", provider.ApiVersion);
    }

    [Fact]
    public void TimeSpan_options_still_bind_from_their_string_form()
    {
        using var provider = Build(new()
        {
            ["AITaskAgent:Timeouts:DefaultStepTimeout"] = "00:02:30",
            ["AITaskAgent:Timeouts:DefaultPipelineTimeout"] = "1.02:00:00"
        });

        var timeouts = provider.GetRequiredService<IOptions<AITaskAgentOptions>>().Value.Timeouts;

        Assert.Equal(TimeSpan.FromSeconds(150), timeouts.DefaultStepTimeout);
        Assert.Equal(TimeSpan.FromHours(26), timeouts.DefaultPipelineTimeout);
    }

    private sealed class Dated
    {
        public DateTime When { get; set; }
        public DateTimeOffset? At { get; set; }
        public string? Label { get; set; }
    }

    [Fact]
    public void DateTime_properties_still_bind_and_a_date_shaped_string_stays_a_string()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["S:When"] = "2024-10-21T08:30:00Z",
            ["S:At"] = "2024-10-21T08:30:00+02:00",
            ["S:Label"] = "2024-10-21"
        }).Build();

        var dated = configuration.GetEx<Dated>("S")!;

        Assert.Equal(new DateTime(2024, 10, 21, 8, 30, 0, DateTimeKind.Utc), dated.When.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2024, 10, 21, 8, 30, 0, TimeSpan.FromHours(2)), dated.At);
        Assert.Equal("2024-10-21", dated.Label);
    }
}
