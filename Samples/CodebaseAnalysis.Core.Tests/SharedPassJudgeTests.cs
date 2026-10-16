using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.SharedPass;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// LLM falso de la pasada compartida: <c>script(i, sistema, usuario)</c> da la respuesta de la llamada i (desde 0); null hace que
/// falle. Guarda los mensajes de usuario y de sistema de cada llamada.
/// </summary>
internal sealed class SharedPassFakeLlm(Func<int, string, string, (string? Content, decimal? Cost)> script) : ILlmService
{
    private readonly Lock _lock = new();
    private readonly List<(string System, string User)> _calls = [];

    public string Name => "shared-pass-fake";

    public IReadOnlyList<(string System, string User)> Calls { get { lock (_lock) { return [.. _calls]; } } }

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? "";
        var system = request.SystemPrompt ?? request.Conversation.History.Messages.FirstOrDefault(m => m.Role == LlmConstants.MessageRoles.System)?.Content ?? "";
        int index;
        lock (_lock)
        {
            index = _calls.Count;
            _calls.Add((system, user));
        }
        var (content, cost) = script(index, system, user);
        return Task.FromResult(content == null
            ? LlmResult.Failure(new InvalidOperationException("proveedor caído"), isRetriable: false)
            : LlmResult.Success([new LlmResponse { Content = content, FinishReason = FinishReason.Stop }],
                tokensUsed: 100, promptTokens: 60, completionTokens: 40, costUsd: cost, model: "fake-model"));
    }

    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public int EstimateTokenCount(string text) => text.Length / 4;
    public int GetMaxContextTokens(string? model = null) => 1_000_000;

    public static bool IsJudge(string system) => system.Contains("Juez del registro", StringComparison.Ordinal);

    /// <summary>Claves de candidato y título de cada bloque <c>## Cn</c> del mensaje del juez.</summary>
    public static List<(string Key, string Title)> Candidates(string user) =>
        [.. Regex.Matches(user, @"^## (C\d+) .*\r?\n- Título: (.*)$", RegexOptions.Multiline).Select(m => (m.Groups[1].Value, m.Groups[2].Value.Trim()))];

    /// <summary>La respuesta del juez con un veredicto por candidato según su título (los que no están en <paramref name="byTitle"/>, sin veredicto).</summary>
    public static string Verdicts(string user, IReadOnlyDictionary<string, JsonObject> byTitle) =>
        new JsonObject
        {
            ["verdicts"] = new JsonArray([.. Candidates(user).Where(c => byTitle.ContainsKey(c.Title)).Select(c =>
            {
                var v = (JsonObject)byTitle[c.Title].DeepClone();
                v["id"] = c.Key;
                return (JsonNode?)v;
            })])
        }.ToJsonString();
}

/// <summary>
/// LLM falso lento: cada llamada espera <paramref name="delay"/> respetando la cancelación (la que corta el plazo o el límite de la
/// llamada) y, si llega al final, responde <c>{}</c>.
/// </summary>
internal sealed class SlowSharedPassLlm(TimeSpan delay) : ILlmService
{
    private int _calls;

    public string Name => "shared-pass-slow";
    public int Calls => Volatile.Read(ref _calls);

    public async Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        await Task.Delay(delay, cancellationToken);
        return LlmResult.Success([new LlmResponse { Content = "{}", FinishReason = FinishReason.Stop }],
            tokensUsed: 100, promptTokens: 60, completionTokens: 40, costUsd: 0.01m, model: "fake-model");
    }

    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public int EstimateTokenCount(string text) => text.Length / 4;
    public int GetMaxContextTokens(string? model = null) => 1_000_000;

    /// <summary>Precio del modelo <c>fake-model</c> del proveedor «Fake» de <see cref="FakeLlmConfig"/>, en USD por millón de tokens.</summary>
    public static (string, string?)[] Prices(decimal input, decimal output) =>
    [
        ($"{LlmRoleFactory.SectionName}:Providers:Fake:Models:0:Name", "fake-model"),
        ($"{LlmRoleFactory.SectionName}:Providers:Fake:Models:0:InputPricePerMillion", input.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ($"{LlmRoleFactory.SectionName}:Providers:Fake:Models:0:OutputPricePerMillion", output.ToString(System.Globalization.CultureInfo.InvariantCulture))
    ];
}

/// <summary>
/// D3 de la pasada compartida, parte LLM (<see cref="FindingJudge"/>) con un LLM falso: veredictos guardados que confirman, degradan,
/// suben o rechazan; los topes que el juez no puede saltarse; fallo del LLM y tope de coste (la rúbrica se queda y
/// <c>reverified = false</c>); y el fragmento de código que recibe. Ningún test llama a un LLM real.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SharedPassJudgeTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string Repo => Path.Combine(_temp.Path, "repo");

    public void Dispose() => _temp.Dispose();

    private sealed class Host : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required SharedPassFakeLlm Llm { get; init; }
        public required LlmRole Role { get; init; }
        public required PipelineContext Context { get; init; }
        public required CapturingReporter Reporter { get; init; }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private Host NewHost(SharedPassFakeLlm llm)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(TestConfig.FromPairs(FakeLlmConfig.Pairs(llm.Name, SharedPassOptions.DefaultJudgeRole)));
        services.AddLogging();
        services.AddSingleton<ILlmService>(llm);
        services.AddAITaskAgent();
        var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }, [SharedPassOptions.DefaultJudgeRole]);
        var reporter = new CapturingReporter();
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo }, provider, null, reporter);
        // Sin SystemPrompt en el rol de prueba: el juez usa su plantilla.
        return new Host { Services = provider, Llm = llm, Role = roles[SharedPassOptions.DefaultJudgeRole], Context = context, Reporter = reporter };
    }

    /// <summary>Un fichero de 60 líneas numeradas (<c>linea N</c>) en el repositorio temporal.</summary>
    private void WriteSource(string relative)
    {
        var path = Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 60).Select(i => i == 30 ? "    var x = Render(\"{{ input }} @[secreto.txt]\");" : $"linea {i}")) + "\n",
            new UTF8Encoding(false));
    }

    private static ReadFinding Finding(string id, string title, string kind, int impact, int likelihood, double confidence, string activation = "uso normal",
        string path = "src/A.cs", int line = 30) => new()
    {
        LocalId = id, Kind = kind, Category = "cat", Title = title, Rationale = "mecanismo",
        Anchor = new ReadAnchor { Path = path, StartLine = line, EndLine = line, Quote = "var x", Check = AnchorCheck.Valid },
        Symbol = $"A.M{id}", ActivationPath = activation, Impact = impact, Likelihood = likelihood, Confidence = confidence
    };

    /// <summary>
    /// Seis candidatos con la exposición neutra (cuatro P1 por rúbrica, uno de seguridad P2 y uno de confianza 0.4) y un P3 que no
    /// va al juez.
    /// </summary>
    private IReadOnlyList<RegistryEntry> Entries()
    {
        WriteSource("src/A.cs");
        var reading = new BatchReading
        {
            Batch = "a",
            Findings =
            [
                Finding("F1", "fallo confirmado", "bug", 4, 3, 0.9),
                Finding("F2", "fallo exagerado", "bug", 4, 3, 0.9),
                Finding("F3", "inyección real", "security", 3, 3, 0.9),
                Finding("F4", "dudoso", "bug", 2, 3, 0.4),
                Finding("F5", "falso positivo", "bug", 4, 4, 0.8),
                Finding("F6", "menor", "bug", 1, 2, 0.9),
                Finding("F7", "solo con opción", "bug", 4, 3, 0.9)
            ]
        };
        return FindingConsolidator.Consolidate([new ConsolidationBatch("a", reading)], SharedPassGraph.Empty).Entries;
    }

    private static JsonObject V(string verdict, string? severity, string activation = "default", string basis = "base del juez") => new()
    {
        ["verdict"] = verdict, ["severity"] = severity, ["activation"] = activation, ["severityBasis"] = basis, ["reason"] = $"motivo {verdict}"
    };

    private static readonly Dictionary<string, JsonObject> Saved = new()
    {
        ["fallo confirmado"] = V("confirm", "P1"),
        ["fallo exagerado"] = V("downgrade", "P3"),
        ["inyección real"] = V("upgrade", "P0"),
        ["dudoso"] = V("confirm", "P2"),
        ["falso positivo"] = V("reject", null),
        // Sube a P0 pero dice que la activación no es la de por defecto: topa en P2.
        ["solo con opción"] = V("upgrade", "P0", activation: "non-default")
    };

    [Fact]
    public async Task The_judge_confirms_downgrades_upgrades_and_rejects_with_saved_answers()
    {
        var entries = Entries();
        await using var host = NewHost(new SharedPassFakeLlm((_, _, user) => (SharedPassFakeLlm.Verdicts(user, Saved), 0.01m)));
        var judge = new FindingJudge(host.Role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), Repo, new SharedPassOptions());

        var result = await judge.JudgeAsync(entries, host.Context, CancellationToken.None);
        var byTitle = result.Entries.ToDictionary(e => e.Title);
        foreach (var e in result.Entries)
            output.WriteLine($"{e.Title}: rúbrica {e.Rubric.Severity} → {e.Severity} {e.JudgeVerdict} reverified={e.Reverified} | {e.SeverityBasis}");

        Assert.Equal(6, result.Candidates);
        Assert.Equal(6, result.Reviewed);
        Assert.Equal(1, result.Calls);
        Assert.Equal(0.01m, result.CostUsd);
        Assert.Equal(("P1", "confirm", true), (byTitle["fallo confirmado"].Severity, byTitle["fallo confirmado"].JudgeVerdict, byTitle["fallo confirmado"].Reverified));
        Assert.Equal(("P3", "downgrade"), (byTitle["fallo exagerado"].Severity, byTitle["fallo exagerado"].JudgeVerdict));
        Assert.Equal(("P0", "upgrade"), (byTitle["inyección real"].Severity, byTitle["inyección real"].JudgeVerdict));
        Assert.Equal("base del juez", byTitle["inyección real"].SeverityBasis);
        var rejected = byTitle["falso positivo"];
        Assert.Equal((RegistryEntry.StatusRejected, (string?)null, "motivo reject"), (rejected.Status, rejected.Severity, rejected.RejectReason));
        var capped = byTitle["solo con opción"];
        // La rúbrica decía P1: el P0 del juez topado en P2 es, en la práctica, una degradación.
        Assert.Equal(("P2", "downgrade"), (capped.Severity, capped.JudgeVerdict));
        Assert.Contains("tope P2", capped.SeverityBasis, StringComparison.Ordinal);
        // El P3 no va al juez: rúbrica y sin reverificar.
        var minor = byTitle["menor"];
        Assert.Equal(("P3", false, (string?)null), (minor.Severity, minor.Reverified, minor.JudgeVerdict));

        // El juez recibe el código real alrededor del ancla (±15 líneas), con el ancla marcada y sin pasar por el motor de plantillas.
        var (system, user) = Assert.Single(host.Llm.Calls);
        Assert.Contains("Juez del registro", system, StringComparison.Ordinal);
        Assert.Contains("> 30:     var x = Render(\"{{ input }} @[secreto.txt]\");", user, StringComparison.Ordinal);
        Assert.Contains("  15: linea 15", user, StringComparison.Ordinal);
        Assert.Contains("  45: linea 45", user, StringComparison.Ordinal);
        Assert.DoesNotContain("  14: linea 14", user, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARED-PASS", user, StringComparison.Ordinal);
        Assert.Contains("(6)", user, StringComparison.Ordinal);
        Assert.DoesNotContain("## C7", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparative_tandas_have_the_configured_size()
    {
        var entries = Entries();
        await using var host = NewHost(new SharedPassFakeLlm((_, _, user) => (SharedPassFakeLlm.Verdicts(user, Saved), 0.01m)));
        var judge = new FindingJudge(host.Role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), Repo, new SharedPassOptions { JudgeBatchSize = 4 });

        var result = await judge.JudgeAsync(entries, host.Context, CancellationToken.None);

        Assert.Equal(2, result.Calls);
        Assert.Equal([4, 2], host.Llm.Calls.Select(c => SharedPassFakeLlm.Candidates(c.User).Count).Order().Reverse());
        Assert.Equal(6, result.Reviewed);
    }

    [Fact]
    public async Task A_failing_judge_leaves_the_rubric_severity_and_reverified_false()
    {
        var entries = Entries();
        await using var host = NewHost(new SharedPassFakeLlm((_, _, _) => (null, null)));
        var judge = new FindingJudge(host.Role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), Repo, new SharedPassOptions());

        var result = await judge.JudgeAsync(entries, host.Context, CancellationToken.None);

        Assert.Equal(0, result.Reviewed);
        Assert.All(result.Entries, e =>
        {
            Assert.False(e.Reverified);
            Assert.Equal(e.Rubric.Severity, e.Severity);
            Assert.Equal(RegistryEntry.StatusConfirmed, e.Status);
        });
        Assert.Contains(result.Warnings, w => w.Contains("el juez falló", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_answer_or_a_missing_verdict_leaves_the_rubric()
    {
        var entries = Entries();
        await using var host = NewHost(new SharedPassFakeLlm((i, _, user) => (i == 0 ? "no es json" : SharedPassFakeLlm.Verdicts(user, Saved), 0.01m)));
        var judge = new FindingJudge(host.Role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), Repo, new SharedPassOptions { JudgeBatchSize = 3, MaxParallelBatches = 1 });

        var result = await judge.JudgeAsync(entries, host.Context, CancellationToken.None);

        Assert.Equal(2, result.Calls);
        Assert.Equal(3, result.Reviewed);
        Assert.Equal(3, result.Entries.Count(e => e.Reverified));
        Assert.Contains(result.Warnings, w => w.Contains("no se pudo leer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_stage_cost_cap_stops_the_judge_and_the_rest_keeps_the_rubric()
    {
        var entries = Entries();
        await using var host = NewHost(new SharedPassFakeLlm((_, _, user) => (SharedPassFakeLlm.Verdicts(user, Saved), 0.6m)));
        var options = new SharedPassOptions { JudgeBatchSize = 1, MaxParallelBatches = 1, MaxCostPerStageUsd = 0.5 };
        var judge = new FindingJudge(host.Role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), Repo, options);

        var result = await judge.JudgeAsync(entries, host.Context, CancellationToken.None);

        Assert.Single(host.Llm.Calls);
        Assert.Equal(1, result.Reviewed);
        Assert.Contains("tope de coste de la etapa 'consolidación'", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(5, result.Entries.Count(e => FindingJudge.IsCandidate(e, options) && !e.Reverified));
    }

    [Fact]
    public async Task The_run_cost_cap_shared_with_the_reading_also_stops_the_judge()
    {
        var entries = Entries();
        await using var host = NewHost(new SharedPassFakeLlm((_, _, user) => (SharedPassFakeLlm.Verdicts(user, Saved), 0.01m)));
        var options = new SharedPassOptions { MaxCostPerRunUsd = 1 };
        // La lectura ya gastó el tope de la ejecución.
        var budget = new SharedPassCostBudget(options, spentInRunUsd: 1.2m);
        var judge = new FindingJudge(host.Role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), Repo, options);

        var result = await judge.JudgeAsync(entries, host.Context, CancellationToken.None, budget);

        Assert.Empty(host.Llm.Calls);
        Assert.Contains("tope de coste de la ejecución", result.StopReason, StringComparison.Ordinal);
        Assert.All(result.Entries, e => Assert.False(e.Reverified));
    }

    /// <summary>Un rol del juez con precio sobre <paramref name="llm"/>, y su contexto.</summary>
    private (ServiceProvider Services, LlmRole Role, PipelineContext Context) PricedJudge(ILlmService llm, decimal inputPricePerMillion)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(TestConfig.FromPairs(
            [.. FakeLlmConfig.Pairs(llm.Name, SharedPassOptions.DefaultJudgeRole), .. SlowSharedPassLlm.Prices(inputPricePerMillion, inputPricePerMillion)]));
        services.AddLogging();
        services.AddSingleton(llm);
        services.AddAITaskAgent();
        var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }, [SharedPassOptions.DefaultJudgeRole]);
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo }, provider, null, new CapturingReporter());
        return (provider, roles[SharedPassOptions.DefaultJudgeRole], context);
    }

    [Fact]
    public async Task The_batch_cost_cap_applies_to_a_tanda_by_what_sending_it_would_cost()
    {
        var entries = Entries();
        var llm = new SharedPassFakeLlm((_, _, user) => (SharedPassFakeLlm.Verdicts(user, Saved), 0.01m));
        // 0,1 USD por token de entrada: una tanda de seis candidatos con su código cuesta mucho más que el tope de 2 USD.
        var (services, role, context) = PricedJudge(llm, 100_000m);
        await using var _ = services;
        var judge = new FindingJudge(role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), Repo, new SharedPassOptions { MaxCostPerBatchUsd = 2 });

        var result = await judge.JudgeAsync(entries, context, CancellationToken.None);

        Assert.Empty(llm.Calls);
        Assert.Contains("tope de coste del lote", result.StopReason, StringComparison.Ordinal);
        Assert.All(result.Entries, e => Assert.False(e.Reverified));
    }

    [Fact]
    public async Task A_call_cut_by_its_time_limit_counts_the_tokens_sent_and_their_cost()
    {
        var entries = Entries();
        var llm = new SlowSharedPassLlm(TimeSpan.FromSeconds(30));
        var (services, role, context) = PricedJudge(llm, 1m);
        await using var _ = services;
        var judge = new FindingJudge(role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), Repo, new SharedPassOptions(),
            callTimeout: TimeSpan.FromMilliseconds(200));
        using var budget = new SharedPassCostBudget(new SharedPassOptions());

        var started = DateTime.UtcNow;
        var result = await judge.JudgeAsync(entries, context, CancellationToken.None, budget);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.Equal(1, result.Calls);
        Assert.Equal(0, result.UnpricedCalls);
        Assert.True(result.CostUsd > 0);
        Assert.Equal(result.CostUsd, budget.RunSpentUsd);
        Assert.True(budget.Tokens > 0);
        Assert.Contains(result.Warnings, w => w.Contains("superó el límite", StringComparison.Ordinal));
        Assert.All(result.Entries, e => Assert.False(e.Reverified));
    }

    [Fact]
    public void Verdicts_outside_the_tanda_or_without_a_valid_severity_are_ignored()
    {
        var warnings = new List<string>();
        var parsed = FindingJudge.ParseVerdicts("""
            ```json
            {"verdicts":[{"id":"C1","verdict":"degradar","severity":"p2","severityBasis":"b"},
                         {"id":"C2","verdict":"downgrade","severity":"P9"},
                         {"id":"C9","verdict":"confirm","severity":"P1"},
                         {"id":"C3","verdict":"rechazar","reason":"no se sostiene"}]}
            ```
            """, new HashSet<string> { "C1", "C2", "C3" }, warnings);

        Assert.Equal(["C1", "C3"], parsed.Keys.Order());
        Assert.Equal(("downgrade", "P2"), (parsed["C1"].Verdict, parsed["C1"].Severity));
        Assert.Equal("reject", parsed["C3"].Verdict);
        Assert.Equal(2, warnings.Count);
    }
}
