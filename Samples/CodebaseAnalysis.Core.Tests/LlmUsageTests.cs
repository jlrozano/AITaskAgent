using System.Text.Json;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Coste y consumo por ejecución (auditoría §4, «Coste por informe»): los precios se leen de
/// <c>LlmConfiguration:Providers:&lt;p&gt;:Models</c> con la forma del framework, y <see cref="LlmUsageTracker"/> suma los
/// tokens de entrada y salida, las llamadas y el coste de los <see cref="LlmResponseEvent"/> finales, en total y por paso.
/// </summary>
public sealed class LlmUsageTests
{
    private static readonly string Smells = AnalysisPipelineFactory.AuditorStepName(ReportType.CodeSmells);

    private static LlmResponseEvent Final(string step, int prompt, int completion, decimal? cost, int choice = 0) => new()
    {
        StepName = step,
        Content = "",
        FinishReason = FinishReason.Stop,
        ChoiceIndex = choice,
        TokensUsed = prompt + completion,
        PromptTokens = prompt,
        CompletionTokens = completion,
        CostUsd = cost
    };

    [Fact]
    public void The_tracker_adds_up_final_responses_per_step_and_in_total_and_ignores_chunks_and_reasoning()
    {
        var tracker = new LlmUsageTracker();
        tracker.Observe(Final(Smells, 1000, 100, null));
        tracker.Observe(new LlmResponseEvent { StepName = Smells, Content = "trozo", FinishReason = FinishReason.Streaming });
        tracker.Observe(new LlmResponseEvent { StepName = Smells, Content = "pienso", FinishReason = FinishReason.Stop, IsThinking = true });
        tracker.Observe(Final(Smells, 2000, 200, null));
        tracker.Observe(Final(AnalysisPipelineFactory.SynthesizerStepName, 500, 50, null));
        // Una opción alternativa sin reparto ni coste no es otra llamada.
        tracker.Observe(new LlmResponseEvent { StepName = Smells, Content = "alt", FinishReason = FinishReason.Stop, ChoiceIndex = 1, TokensUsed = 3300 });

        var usage = tracker.Snapshot();

        Assert.Equal((3, 3500L, 350L, 3850L), (usage.Total.Calls, usage.Total.PromptTokens, usage.Total.CompletionTokens, usage.Total.TotalTokens));
        Assert.Null(usage.Total.CostUsd);
        Assert.Equal(3, usage.Total.UnpricedCalls);
        Assert.Equal([Smells, AnalysisPipelineFactory.SynthesizerStepName], usage.BySource.Select(s => s.Source));
        var smells = usage.BySource[0];
        Assert.Equal(("CodeSmells", 2, 3000L, 300L), (smells.Report, smells.Calls, smells.PromptTokens, smells.CompletionTokens));
        Assert.Null(usage.BySource[1].Report);
        Assert.Equal("3 llamadas · 3.500 tokens de entrada · 350 de salida · coste sin precio configurado", usage.Total.Describe());
    }

    [Fact]
    public void A_cost_with_unpriced_calls_is_a_minimum_and_says_so()
    {
        var tracker = new LlmUsageTracker();
        tracker.Observe(Final(Smells, 1_000_000, 0, 1.25m));
        tracker.Observe(Final(Smells, 10, 0, null));

        var total = tracker.Snapshot().Total;

        Assert.Equal((1.25m, 1), (total.CostUsd, total.UnpricedCalls));
        Assert.Contains("coste 1,250 $ (1 llamadas sin precio: es un mínimo)", total.Describe());
        var data = tracker.Snapshot().ToData();
        Assert.Equal(1.25m, data["costUsd"]);
        var bySource = Assert.IsType<List<object?>>(data["bySource"]);
        Assert.Equal("CodeSmells", Assert.IsType<Dictionary<string, object?>>(Assert.Single(bySource))["report"]);
    }

    [Fact]
    public void A_service_that_reports_no_split_still_counts_its_total()
    {
        var tracker = new LlmUsageTracker();
        tracker.Observe(new LlmResponseEvent { StepName = Smells, Content = "", FinishReason = FinishReason.Stop, TokensUsed = 700 });

        var total = tracker.Snapshot().Total;

        Assert.Equal((1, 0L, 0L, 700L), (total.Calls, total.PromptTokens, total.CompletionTokens, total.TotalTokens));
    }

    [Fact]
    public void The_final_llm_response_dto_carries_the_split_and_the_cost()
    {
        var dto = new AnalysisEventMapper("r1").Map(Final(Smells, 800, 100, 0.5m))!;

        Assert.Equal(800, dto.Data!["promptTokens"]);
        Assert.Equal(100, dto.Data["completionTokens"]);
        Assert.Equal(0.5m, dto.Data["costUsd"]);
        Assert.Equal(900, dto.Data["tokens"]);
    }

    private static IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILlmService>(LlmServiceTypes.AzureOpenAI, new CannedLlmService("unused"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Prices_are_read_from_the_provider_models_and_an_entry_without_both_prices_does_not_count()
    {
        var configuration = TestConfig.FromPairs(
            ("LlmConfiguration:Providers:Main:ServiceType", "azure-openai"),
            ("LlmConfiguration:Providers:Main:BaseUrl", "https://x.example/"),
            ("LlmConfiguration:Providers:Main:Model", "gpt-a"),
            ("LlmConfiguration:Providers:Main:Models:0:Name", "gpt-a"),
            ("LlmConfiguration:Providers:Main:Models:0:InputPricePerMillion", "2.5"),
            ("LlmConfiguration:Providers:Main:Models:0:OutputPricePerMillion", "10"),
            ("LlmConfiguration:Providers:Main:Models:1:Name", "gpt-b"),
            ("LlmConfiguration:Providers:Main:Models:1:InputPricePerMillion", "1"));
        var log = new List<string>();

        var provider = LlmRoleFactory.BuildProviders(configuration, Services(), _ => null, log.Add)["Main"].Provider;

        Assert.Equal(12.5m, provider.CalculateCost("gpt-a", 1_000_000, 1_000_000));
        Assert.Null(provider.CalculateCost("gpt-b", 1_000_000, 1_000_000));
        Assert.Contains(log, l => l.Contains("gpt-a 2.5/10", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("Sin precio en 'Main' para gpt-b", StringComparison.Ordinal));
    }

    /// <summary>El JSON del sample trae la estructura con los precios a null: ningún número inventado y ningún coste.</summary>
    [Fact]
    public void The_sample_configuration_declares_the_model_without_prices()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json"))
            .Build();
        var models = configuration.GetSection("LlmConfiguration:Providers:AzureOpenAI:Models").GetChildren().ToList();
        var log = new List<string>();

        var prices = LlmRoleFactory.ReadModelPrices(configuration.GetSection("LlmConfiguration:Providers:AzureOpenAI"), log.Add);

        Assert.Equal("gpt-5.4", Assert.Single(models)["Name"]);
        Assert.Empty(prices);
        Assert.Contains(log, l => l.Contains("Sin precio en 'AzureOpenAI' para gpt-5.4", StringComparison.Ordinal));
    }

    [Fact]
    public void The_run_meta_file_holds_the_usage_of_the_run()
    {
        using var dir = new TempDirectory();
        var tracker = new LlmUsageTracker();
        tracker.Observe(Final(Smells, 1000, 100, null));
        var started = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

        var warning = RunMetaFile.TryWrite(dir.Path,
            RunMetaFile.Build(RunMetaFile.KindAnalysis, "r1", "succeeded", started, started.AddMinutes(2), tracker.Snapshot()));

        Assert.Null(warning);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.Path, RunMetaFile.FileName)));
        var root = doc.RootElement;
        Assert.Equal("analysis", root.GetProperty("kind").GetString());
        Assert.Equal(120_000, root.GetProperty("durationMs").GetInt64());
        Assert.Equal("2026-09-29T10:00:00.0000000+00:00", root.GetProperty("startedAt").GetString());
        var usage = root.GetProperty("usage");
        Assert.Equal(1, usage.GetProperty("calls").GetInt32());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("costUsd").ValueKind);
        Assert.Equal("CodeSmells", usage.GetProperty("bySource")[0].GetProperty("report").GetString());
        Assert.StartsWith("1.0.0", root.GetProperty("app").GetString());
    }
}
