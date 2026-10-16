using System.Text.Json;
using AITaskAgent.Configuration;
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
/// <c>AITaskAgent:LlmProviders:Providers:&lt;p&gt;:Models</c>, que enlaza el framework, y <see cref="LlmUsageTracker"/> suma los
/// tokens de entrada y salida, las llamadas y el coste de los <see cref="LlmResponseEvent"/> finales, en total y por paso.
/// </summary>
public sealed class LlmUsageTests
{
    private static readonly string Smells = AnalysisPipelineFactory.AuditorStepName(ReportType.CodeSmells);

    private static LlmResponseEvent Final(string step, int prompt, int completion, decimal? cost, int choice = 0, int? cached = null) => new()
    {
        StepName = step,
        Content = "",
        FinishReason = FinishReason.Stop,
        ChoiceIndex = choice,
        TokensUsed = prompt + completion,
        PromptTokens = prompt,
        CompletionTokens = completion,
        CachedPromptTokens = cached,
        CostUsd = cost
    };

    [Fact]
    public void Cached_prompt_tokens_add_up_per_step_and_in_total_and_show_in_the_summary()
    {
        var tracker = new LlmUsageTracker();
        tracker.Observe(Final(Smells, 1000, 100, null, cached: 600));
        tracker.Observe(Final(Smells, 2000, 200, null));
        tracker.Observe(Final(AnalysisPipelineFactory.SynthesizerStepName, 500, 50, null, cached: 400));

        var usage = tracker.Snapshot();

        Assert.Equal(1000L, usage.Total.CachedPromptTokens);
        Assert.Equal([600L, 400L], usage.BySource.Select(s => s.CachedPromptTokens));
        Assert.Equal("3 llamadas · 3.500 tokens de entrada (1.000 en caché) · 350 de salida · coste sin precio configurado",
            usage.Total.Describe());
        var data = usage.ToData();
        Assert.Equal(1000L, data["cachedPromptTokens"]);
        Assert.Equal(600L, Assert.IsType<Dictionary<string, object?>>(Assert.IsType<List<object?>>(data["bySource"])[0])["cachedPromptTokens"]);
    }

    [Fact]
    public void Without_cached_tokens_the_count_is_zero_and_the_summary_does_not_mention_them()
    {
        var tracker = new LlmUsageTracker();
        tracker.Observe(Final(Smells, 1000, 100, null));

        var total = tracker.Snapshot().Total;

        Assert.Equal(0L, total.CachedPromptTokens);
        Assert.DoesNotContain("caché", total.Describe());
    }

    [Fact]
    public void The_usage_of_an_investigation_is_attributed_to_its_report_but_kept_as_its_own_source()
    {
        var investigation = AnalysisPipelineFactory.InvestigationStepName(ReportType.FlujosEndToEnd, "billing");
        var consolidation = AnalysisPipelineFactory.AuditorStepName(ReportType.FlujosEndToEnd);
        var tracker = new LlmUsageTracker();
        tracker.Observe(Final(investigation, 1000, 100, null));
        tracker.Observe(Final(investigation, 2000, 200, null));
        tracker.Observe(Final(consolidation, 500, 50, null));
        tracker.Observe(Final("Investigation_Nope_billing", 10, 1, null));

        var usage = tracker.Snapshot();

        Assert.Equal([investigation, consolidation, "Investigation_Nope_billing"], usage.BySource.Select(s => s.Source));
        Assert.Equal(["FlujosEndToEnd", "FlujosEndToEnd", null], usage.BySource.Select(s => s.Report));
        Assert.Equal((2, 3000L, 300L), (usage.BySource[0].Calls, usage.BySource[0].PromptTokens, usage.BySource[0].CompletionTokens));
        // Lo que se suma por informe es la investigación más la consolidación.
        Assert.Equal(3500L, usage.BySource.Where(s => s.Report == "FlujosEndToEnd").Sum(s => s.PromptTokens));
    }

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
        Assert.Null(dto.Data["cachedPromptTokens"]);
    }

    [Fact]
    public void The_final_llm_response_dto_carries_the_cached_prompt_tokens()
    {
        var dto = new AnalysisEventMapper("r1").Map(Final(Smells, 800, 100, 0.5m, cached: 600))!;

        Assert.Equal(600, dto.Data!["cachedPromptTokens"]);
    }

    private static Dictionary<string, AITaskAgent.LLM.Configuration.LlmRole> BuildRoles(IConfiguration configuration, List<string> log)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddCodebaseAnalysisLlmServices();
        services.AddAITaskAgent();
        using var provider = services.BuildServiceProvider();
        return LlmRoleFactory.BuildRoles(provider, _ => null, log.Add, [.. LlmRoleFactory.RequiredRoles, Chat.ChatAgent.RoleName]);
    }

    [Fact]
    public void Prices_are_read_from_the_provider_models_and_a_model_without_entry_has_no_cost()
    {
        const string llm = LlmRoleFactory.SectionName;
        var configuration = TestConfig.FromPairs(
            [.. FakeLlmConfig.Pairs(LlmServiceTypes.AzureOpenAI, [.. LlmRoleFactory.RequiredRoles, Chat.ChatAgent.RoleName])
                .Where(p => p.Item1 != $"{llm}:Roles:ChatAssistant:Model"),
             ($"{llm}:Providers:Fake:Models:0:Name", "fake-model"),
             ($"{llm}:Providers:Fake:Models:0:InputPricePerMillion", "2.5"),
             ($"{llm}:Providers:Fake:Models:0:OutputPricePerMillion", "10"),
             ($"{llm}:Roles:ChatAssistant:Model", "gpt-b")]);
        var log = new List<string>();

        var roles = BuildRoles(configuration, log);

        var provider = roles["Enricher"].Provider;
        Assert.Equal(12.5m, provider.CalculateCost("fake-model", 1_000_000, 1_000_000));
        Assert.Null(provider.CalculateCost("gpt-b", 1_000_000, 1_000_000));
        Assert.Contains(log, l => l.Contains("fake-model 2.5/10", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("Sin precio en 'Fake' para gpt-b:", StringComparison.Ordinal));
    }

    /// <summary>
    /// El JSON del sample trae la tarifa de Azure de sus dos modelos (USD por millón de tokens), así que el coste de un run
    /// sale calculado: un millón de entrada y otro de salida cuestan la suma de los dos precios.
    /// </summary>
    [Fact]
    public void The_sample_configuration_prices_its_models()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json"))
            .Build();
        var log = new List<string>();

        var roles = BuildRoles(configuration, log);

        var provider = roles["Enricher"].Provider;
        Assert.Equal(12m, provider.CalculateCost("gpt-6-sol", 1_000_000, 1_000_000));
        Assert.Equal(17.5m, provider.CalculateCost("gpt-5.4", 1_000_000, 1_000_000));
        // gpt-5.4 declara el precio de la entrada cacheada (0,25): 1 M de entrada todo caché + 1 M de salida.
        Assert.Equal(15.25m, provider.CalculateCost("gpt-5.4", 1_000_000, 1_000_000, cachedPromptTokens: 1_000_000));
        // gpt-6-sol también (0,20, dato del usuario del 2026-10-16): sin caché 12, con toda la entrada en caché 0,20 + 10.
        Assert.Equal(10.2m, provider.CalculateCost("gpt-6-sol", 1_000_000, 1_000_000, cachedPromptTokens: 1_000_000));
        // Los demás no: su entrada cacheada se cobra a precio normal.
        Assert.Equal(12m, provider.CalculateCost("gpt-6-sol-global", 1_000_000, 1_000_000));
        Assert.Equal(0.5m, provider.CalculateCost("gpt-4.1-nano", 1_000_000, 1_000_000));
        Assert.Equal(2.25m, provider.CalculateCost("gpt-5-mini", 1_000_000, 1_000_000));
        Assert.Equal(0.45m, provider.CalculateCost("gpt-5-nano", 1_000_000, 1_000_000));
        Assert.Equal(5.5m, provider.CalculateCost("o4-mini", 1_000_000, 1_000_000));
        // gpt-oss-120b no tiene tarifa: su coste no se calcula y el arranque lo avisa (solo a él).
        Assert.Null(provider.CalculateCost("gpt-oss-120b", 1_000_000, 1_000_000));
        Assert.Contains(log, l => l.StartsWith("💲 Sin precio en 'AzureOpenAI' para gpt-oss-120b:", StringComparison.Ordinal));
        Assert.Single(log, l => l.Contains("Sin precio", StringComparison.Ordinal));
    }

    [Fact]
    public void The_run_meta_file_holds_the_usage_of_the_run()
    {
        using var dir = new TempDirectory();
        var tracker = new LlmUsageTracker();
        tracker.Observe(Final(Smells, 1000, 100, null, cached: 250));
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
        Assert.Equal(1000, usage.GetProperty("promptTokens").GetInt64());
        Assert.Equal(250, usage.GetProperty("cachedPromptTokens").GetInt64());
        Assert.Equal(250, usage.GetProperty("bySource")[0].GetProperty("cachedPromptTokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("costUsd").ValueKind);
        Assert.Equal("CodeSmells", usage.GetProperty("bySource")[0].GetProperty("report").GetString());
        Assert.StartsWith("1.0.0", root.GetProperty("app").GetString());
    }
}
