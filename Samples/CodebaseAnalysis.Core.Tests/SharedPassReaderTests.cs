using System.Text;
using System.Text.Json.Nodes;
using AITaskAgent.Configuration;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.SharedPass;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// D2 de la pasada compartida (<see cref="BatchReader"/>) con un LLM falso: el paso real (plantillas, rol y resultado del
/// framework) sobre un grafo de cbm sintético y un repositorio temporal; y la salida real del experimento (<c>out/json.json</c>)
/// contra los ficheros de <c>m\fw</c> si existen. Ningún test llama a un LLM real.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class SharedPassReaderTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string Repo => Path.Combine(_temp.Path, "repo");
    private string Db => Path.Combine(_temp.Path, "codebase.db");

    public void Dispose() => _temp.Dispose();

    private const string AlphaCode = """
        namespace Demo;
        public class Alpha
        {
            public void Run()
            {
                try { File.Delete(_path); }
                catch { }
            }

            public void Stop() => _running = false;
        }
        """;

    private const string BetaCode = """
        class Beta
        {
            void Go()
            {
                new Alpha().Run();
            }
        }
        """;

    private void WriteFile(string relative, string text)
    {
        var path = Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Un subsistema <c>core</c> con dos ficheros: <c>Alpha</c> (Run con un ISSUE, Stop) y <c>Beta</c> (Go; el tipo es candidato a
    /// código muerto). Con <paramref name="second"/>, otro subsistema <c>other</c> del mismo nivel con <c>Gamma</c>.
    /// </summary>
    private async Task<DomainMap> RepoAsync(bool second = false)
    {
        using (var graph = await CbmTestGraph.CreateAsync(Db, "demo"))
        {
            var alpha = await graph.NodeAsync("Class", "Alpha", "demo.Alpha", "src/a/Alpha.cs", 2, 11);
            var run = await graph.NodeAsync("Method", "Run", "demo.Alpha.Run", "src/a/Alpha.cs", 4, 8, """{"detected_issues":["empty-catch"]}""");
            var stop = await graph.NodeAsync("Method", "Stop", "demo.Alpha.Stop", "src/a/Alpha.cs", 10, 10);
            await graph.EdgeAsync(alpha, run, "DEFINES_METHOD");
            await graph.EdgeAsync(alpha, stop, "DEFINES_METHOD");
            var beta = await graph.NodeAsync("Class", "Beta", "demo.Beta", "src/b/Beta.cs", 1, 7,
                """{"dead_code_candidate":true,"dead_code_confidence":"medium","dead_code_reason":"sin referencias"}""");
            var go = await graph.NodeAsync("Method", "Go", "demo.Beta.Go", "src/b/Beta.cs", 3, 6);
            await graph.EdgeAsync(beta, go, "DEFINES_METHOD");
            await graph.EdgeAsync(go, run, "CALLS");
            if (second)
            {
                var gamma = await graph.NodeAsync("Class", "Gamma", "demo.Gamma", "src/g/Gamma.cs", 1, 1);
                _ = gamma;
            }
        }
        WriteFile("src/a/Alpha.cs", AlphaCode);
        WriteFile("src/b/Beta.cs", BetaCode);
        Subsystem core = new() { Id = "core", Name = "Core", Description = "desc", Kind = SubsystemKinds.Capability, Files = ["src/a/Alpha.cs", "src/b/Beta.cs"] };
        if (!second)
            return new DomainMap { Subsystems = [core] };
        WriteFile("src/g/Gamma.cs", "class Gamma { }");
        return new DomainMap
        {
            Subsystems = [core, new Subsystem { Id = "other", Name = "Other", Description = "desc", Kind = SubsystemKinds.Capability, Files = ["src/g/Gamma.cs"] }]
        };
    }

    // ───────────────────────────── Respuestas ─────────────────────────────

    private static JsonObject AlphaFile() => new() { ["path"] = "src/a/Alpha.cs", ["role"] = "service", ["summary"] = "Alpha borra un fichero." };
    private static JsonObject BetaFile() => new() { ["path"] = "src/b/Beta.cs", ["role"] = "caller", ["summary"] = "Beta llama a Alpha." };

    private static JsonObject Member(string name, int start, int end) => new()
    {
        ["name"] = name, ["startLine"] = start, ["endLine"] = end, ["does"] = "hace algo", ["inputs"] = "nada", ["outputs"] = "nada",
        ["sideEffects"] = "ninguno", ["throws"] = "ninguno"
    };

    private static JsonObject AlphaType() => new()
    {
        ["name"] = "Alpha", ["kind"] = "class", ["path"] = "src/a/Alpha.cs", ["startLine"] = 2, ["endLine"] = 11, ["purpose"] = "Servicio.",
        ["collaborators"] = new JsonArray(), ["members"] = new JsonArray(Member("Run()", 4, 8), Member("Stop()", 10, 10))
    };

    private static JsonObject BetaType(bool withGo = true) => new()
    {
        ["name"] = "Beta", ["kind"] = "class", ["path"] = "src/b/Beta.cs", ["startLine"] = 1, ["endLine"] = 7, ["purpose"] = "Llamador.",
        ["collaborators"] = new JsonArray(), ["members"] = withGo ? new JsonArray(Member("Go()", 3, 6)) : new JsonArray()
    };

    private static JsonObject Finding(string id, string path, int start, int end, string quote) => new()
    {
        ["localId"] = id, ["kind"] = "bug", ["category"] = "swallowed-exception", ["title"] = "catch vacío", ["rationale"] = "Traga la excepción.",
        ["anchor"] = new JsonObject { ["path"] = path, ["startLine"] = start, ["endLine"] = end, ["quote"] = quote },
        ["instances"] = new JsonArray(), ["symbol"] = "Alpha.Run", ["seedRef"] = "Alpha.Run ISSUE[empty-catch]",
        ["activationPath"] = "Run con un fichero bloqueado", ["impact"] = 3, ["likelihood"] = 2, ["confidence"] = 0.8
    };

    private static JsonObject AlphaSeed() => new() { ["seed"] = "Alpha.Run ISSUE[empty-catch]", ["verdict"] = "confirmed", ["reason"] = "catch vacío en L7." };
    private static JsonObject BetaSeed() => new() { ["seed"] = "Beta DEAD?(medium)", ["verdict"] = "needs_context", ["reason"] = "uso externo posible." };

    private static string Reading(IEnumerable<JsonObject> files, IEnumerable<JsonObject> types, IEnumerable<JsonObject> findings, IEnumerable<JsonObject> seeds) =>
        new JsonObject
        {
            ["batch"] = "core",
            ["files"] = new JsonArray([.. files]),
            ["types"] = new JsonArray([.. types]),
            ["findings"] = new JsonArray([.. findings]),
            ["seedVerdicts"] = new JsonArray([.. seeds]),
            ["relations"] = new JsonArray(new JsonObject
            {
                ["from"] = "Beta.Go", ["to"] = "Alpha.Run", ["type"] = "CALLS",
                ["anchor"] = new JsonObject { ["path"] = "src/b/Beta.cs", ["startLine"] = 5, ["endLine"] = 5, ["quote"] = "new Alpha().Run();" }
            }),
            ["openQuestions"] = new JsonArray()
        }.ToJsonString();

    private static string FullReading(params JsonObject[] findings) => Reading(
        [AlphaFile(), BetaFile()], [AlphaType(), BetaType()],
        findings.Length > 0 ? findings : [Finding("F1", "src/a/Alpha.cs", 7, 7, "catch { }")], [AlphaSeed(), BetaSeed()]);

    // ───────────────────────────── Infraestructura ─────────────────────────────

    /// <summary>LLM falso: <c>script(i, mensaje de usuario)</c> da la respuesta de la llamada i (desde 0).</summary>
    private sealed class ScriptedReaderLlm(Func<int, string, (string Content, FinishReason Finish, decimal? Cost)> script) : ILlmService
    {
        private readonly Lock _lock = new();
        private readonly List<string> _users = [];
        private readonly List<string> _systems = [];

        public string Name => "reader-fake";
        public IReadOnlyList<string> Users { get { lock (_lock) { return [.. _users]; } } }
        public IReadOnlyList<string> Systems { get { lock (_lock) { return [.. _systems]; } } }

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? "";
            int index;
            lock (_lock)
            {
                index = _users.Count;
                _users.Add(user);
                _systems.Add(request.SystemPrompt ?? request.Conversation.History.Messages.FirstOrDefault(m => m.Role == LlmConstants.MessageRoles.System)?.Content ?? "");
            }
            var (content, finish, cost) = script(index, user);
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = content, FinishReason = finish }],
                tokensUsed: 100, promptTokens: 60, completionTokens: 40, costUsd: cost, model: "fake-model"));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private sealed class Host : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required ScriptedReaderLlm Llm { get; init; }
        public required LlmRole Role { get; init; }
        public required PipelineContext Context { get; init; }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private Host NewHost(ScriptedReaderLlm llm)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(TestConfig.FromPairs(FakeLlmConfig.Pairs("reader-fake", SharedPassOptions.DefaultReaderRole)));
        services.AddLogging();
        services.AddSingleton<ILlmService>(llm);
        services.AddAITaskAgent();
        var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }, [SharedPassOptions.DefaultReaderRole]);
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo, DatabasePath = Db }, provider, null, new CapturingReporter());
        return new Host { Services = provider, Llm = llm, Role = roles[SharedPassOptions.DefaultReaderRole], Context = context };
    }

    private async Task<(SharedPassReadResult Result, Host Host)> ReadAsync(
        Func<int, string, (string, FinishReason, decimal?)> script, SharedPassOptions? options = null, bool second = false)
    {
        options ??= new SharedPassOptions();
        var map = await RepoAsync(second);
        var host = NewHost(new ScriptedReaderLlm(script));
        var set = await PacketBuilder.BuildAllAsync(Db, Repo, map, options, CancellationToken.None);
        var graph = await SharedPassGraph.LoadAsync(Db, CancellationToken.None);
        var reader = new BatchReader(host.Role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), graph, Repo, options);
        var result = await reader.ReadAllAsync(set, host.Context, CancellationToken.None);
        foreach (var b in result.Batches)
        {
            output.WriteLine($"{b.BatchId}: {b.Status} calls={b.Calls} cost={b.CostUsd} gaps={b.Gaps.Count} rejected={b.Rejected.Count} splits={b.Splits}");
            foreach (var w in b.Warnings.Concat(b.Gaps.Select(g => $"GAP {g.Kind} {g.Target}: {g.Reason}")))
                output.WriteLine("  " + w);
        }
        return (result, host);
    }

    private static (string, FinishReason, decimal?) Ok(string content, decimal? cost = 0.01m) => (content, FinishReason.Stop, cost);

    // ───────────────────────────── Tests ─────────────────────────────

    [Fact]
    public async Task A_valid_response_is_read_in_one_call_with_the_packet_in_the_user_message()
    {
        var (result, host) = await ReadAsync((_, _) => Ok(FullReading()));
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.Complete, batch.Status);
        Assert.Equal(1, batch.Calls);
        Assert.Equal(0.01m, batch.CostUsd);
        Assert.Equal(64, batch.InputHash.Length);
        Assert.Empty(batch.Gaps);
        Assert.Empty(batch.Rejected);
        Assert.Equal(["src/a/Alpha.cs", "src/b/Beta.cs"], batch.Reading.Files.Select(f => f.Path));
        Assert.Equal(3, batch.Reading.Types.Sum(t => t.Members.Count));
        var finding = Assert.Single(batch.Reading.Findings);
        Assert.Equal(AnchorCheck.Valid, finding.Anchor.Check);
        Assert.True(finding.SymbolInGraph);
        Assert.Equal(2, batch.Reading.SeedVerdicts.Count);
        Assert.True(Assert.Single(batch.Reading.Relations).Anchor!.Validated);

        var user = Assert.Single(host.Llm.Users);
        Assert.Contains("# Lote: Core (id `core`)", user, StringComparison.Ordinal);
        Assert.Contains("7:         catch { }", user, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARED-PASS", user, StringComparison.Ordinal);
        Assert.Contains("Lector de lotes", Assert.Single(host.Llm.Systems), StringComparison.Ordinal);
        // La lectura se vuelve a escribir en el formato del experimento.
        Assert.Contains("\"localId\": \"F1\"", batch.Reading.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_in_fences_with_text_before_and_after_is_accepted()
    {
        var (result, host) = await ReadAsync((_, _) => Ok($"Aquí está la lectura:\n```json\n{FullReading()}\n```\nEspero que sirva {{ok}}."));
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.Complete, batch.Status);
        Assert.Equal(1, batch.Calls);
    }

    [Fact]
    public async Task Invalid_json_is_retried_with_the_error()
    {
        var (result, host) = await ReadAsync((i, _) => Ok(i == 0 ? "{ \"batch\": \"core\", \"files\": [ esto no es json" : FullReading()));
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.Complete, batch.Status);
        Assert.Equal(2, batch.Calls);
        var retry = host.Llm.Users[1];
        Assert.Contains("### Lo que falta o está mal", retry, StringComparison.Ordinal);
        Assert.Contains("no se pudo leer", retry, StringComparison.Ordinal);
        Assert.Contains("# Lote: Core (id `core`)", retry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_retried_asking_only_for_what_is_missing()
    {
        var (result, host) = await ReadAsync((i, _) => Ok(i == 0
            ? Reading([AlphaFile()], [AlphaType()], [Finding("F1", "src/a/Alpha.cs", 7, 7, "catch { }")], [AlphaSeed()])
            : Reading([BetaFile()], [BetaType()], [], [BetaSeed()])));
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.Complete, batch.Status);
        Assert.Equal(2, batch.Calls);
        Assert.Equal(2, batch.Reading.Files.Count);
        Assert.Single(batch.Reading.Findings);
        var retry = host.Llm.Users[1];
        var errors = retry[retry.IndexOf("### Lo que falta o está mal", StringComparison.Ordinal)..];
        Assert.Contains("Falta el fichero src/b/Beta.cs", errors, StringComparison.Ordinal);
        Assert.Contains("Falta el miembro Beta.Go", errors, StringComparison.Ordinal);
        Assert.Contains("Beta DEAD?", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha", errors, StringComparison.Ordinal);
        var valid = retry[retry.IndexOf("### Lo que ya es válido", StringComparison.Ordinal)..retry.IndexOf("### Lo que falta", StringComparison.Ordinal)];
        Assert.Contains("src/a/Alpha.cs", valid, StringComparison.Ordinal);
        Assert.Contains("F1", valid, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_response_splits_the_batch_in_two_and_reads_each_half()
    {
        var (result, host) = await ReadAsync((i, user) => i == 0
            ? ("{ \"batch\": \"core\", \"files\": [", FinishReason.Length, 0.01m)
            : user.Contains("### FILE src/a/Alpha.cs", StringComparison.Ordinal)
                ? Ok(Reading([AlphaFile()], [AlphaType()], [Finding("F1", "src/a/Alpha.cs", 7, 7, "catch { }")], [AlphaSeed()]))
                : Ok(Reading([BetaFile()], [BetaType()], [], [BetaSeed()])));
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.Complete, batch.Status);
        Assert.Equal(3, batch.Calls);
        Assert.Equal(1, batch.Splits);
        Assert.Equal("core", batch.Reading.Batch);
        Assert.Equal(2, batch.Reading.Files.Count);
        Assert.Equal("s1.F1", Assert.Single(batch.Reading.Findings).LocalId);
        Assert.Contains("(id `core-s1`)", host.Llm.Users[1], StringComparison.Ordinal);
        Assert.DoesNotContain("### FILE src/b/Beta.cs", host.Llm.Users[1], StringComparison.Ordinal);
        Assert.Contains("(id `core-s2`)", host.Llm.Users[2], StringComparison.Ordinal);
        // La relación entre las dos mitades aparece en sus paquetes.
        Assert.Contains("-CALLS-> [core-s1] Alpha.Run", host.Llm.Users[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finding_whose_quote_is_not_in_the_file_is_rejected_after_the_retries()
    {
        var (result, host) = await ReadAsync((_, _) => Ok(FullReading(
            Finding("F1", "src/a/Alpha.cs", 7, 7, "catch { }"),
            Finding("F2", "src/a/Alpha.cs", 4, 4, "public void Explode()"))));
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.Complete, batch.Status);
        Assert.Equal(1 + new SharedPassOptions().MaxReaderRetries, batch.Calls);
        Assert.Equal("F1", Assert.Single(batch.Reading.Findings).LocalId);
        var rejected = Assert.Single(batch.Rejected);
        Assert.Equal("F2", rejected.Finding.LocalId);
        Assert.Contains("no aparece", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains("El hallazgo F2 tiene un ancla inválida", host.Llm.Users[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quote_found_elsewhere_in_the_file_is_relocated()
    {
        var (result, host) = await ReadAsync((_, _) => Ok(FullReading(Finding("F1", "src/a/Alpha.cs", 10, 10, "try { File.Delete(_path); }"))));
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(1, batch.Calls);
        var anchor = Assert.Single(batch.Reading.Findings).Anchor;
        Assert.Equal(AnchorCheck.Relocated, anchor.Check);
        Assert.True(anchor.Validated);
        Assert.Equal((6, 6), (anchor.StartLine, anchor.EndLine));
        Assert.Equal((10, 10), (anchor.OriginalStartLine, anchor.OriginalEndLine));
    }

    [Fact]
    public async Task Coverage_still_missing_after_the_retries_is_a_coverage_gap()
    {
        // Go no llega nunca; solo falla un fichero, así que no hay nada que partir.
        var (result, host) = await ReadAsync((_, _) => Ok(Reading(
            [AlphaFile(), BetaFile()], [AlphaType(), BetaType(withGo: false)], [Finding("F1", "src/a/Alpha.cs", 7, 7, "catch { }")], [AlphaSeed(), BetaSeed()])));
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.Partial, batch.Status);
        Assert.Equal(1 + new SharedPassOptions().MaxReaderRetries, batch.Calls);
        Assert.Equal(0, batch.Splits);
        var gap = Assert.Single(batch.Gaps);
        Assert.Equal((ReaderIssueKind.MissingMember, "src/b/Beta.cs", "Beta.Go"), (gap.Kind, gap.File, gap.Target));
        Assert.Contains("reintento", gap.Reason, StringComparison.Ordinal);
        // Lo válido se conserva.
        Assert.Single(batch.Reading.Findings);
        Assert.Equal(2, batch.Reading.Files.Count);
    }

    [Fact]
    public async Task The_batch_cost_cap_stops_further_calls()
    {
        var (result, host) = await ReadAsync((_, _) => Ok("sin json", 0.6m), new SharedPassOptions { MaxCostPerBatchUsd = 0.5 });
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.StoppedByCost, batch.Status);
        Assert.Equal(1, batch.Calls);
        Assert.True(result.StoppedByCostLimit);
        Assert.Contains("tope de coste del lote", batch.StopReason, StringComparison.Ordinal);
        Assert.Equal(["src/a/Alpha.cs", "src/b/Beta.cs"], batch.Gaps.Select(g => g.File));
    }

    [Fact]
    public async Task The_stage_cost_cap_leaves_the_remaining_batches_unread()
    {
        var (result, host) = await ReadAsync((_, _) => Ok(FullReading(), 0.6m),
            new SharedPassOptions { MaxCostPerStageUsd = 0.5, MaxParallelBatches = 1 }, second: true);
        await using var _ = host;

        Assert.Equal(2, result.Batches.Count);
        Assert.Single(host.Llm.Users);
        Assert.True(result.StoppedByCostLimit);
        var unread = Assert.Single(result.Batches, b => b.Status == BatchReadStatus.NotRead);
        Assert.Equal(0, unread.Calls);
        Assert.Contains("tope de coste de la etapa", unread.StopReason, StringComparison.Ordinal);
        Assert.NotEmpty(unread.Gaps);
    }

    [Fact]
    public async Task Calls_without_a_price_still_count_against_the_call_cap()
    {
        var (result, host) = await ReadAsync((_, _) => Ok("sin json", cost: null), new SharedPassOptions { MaxCallsPerRun = 2, MaxReaderRetries = 5 });
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(2, batch.Calls);
        Assert.Equal(2, batch.UnpricedCalls);
        Assert.Equal(BatchReadStatus.StoppedByCost, batch.Status);
        Assert.Contains("tope de llamadas de la ejecución", batch.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calls_without_a_price_still_count_against_the_token_cap()
    {
        // Cada llamada del LLM falso informa de 100 tokens: con un tope de 150, la segunda es la última.
        var (result, host) = await ReadAsync((_, _) => Ok("sin json", cost: null), new SharedPassOptions { MaxTokensPerRun = 150, MaxReaderRetries = 5 });
        await using var _ = host;

        var batch = Assert.Single(result.Batches);
        Assert.Equal(2, batch.Calls);
        Assert.Contains("tope de tokens de la ejecución", batch.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_after_the_MaxFileChars_cut_are_not_required_in_the_coverage()
    {
        // Con 130 caracteres el paquete de Alpha.cs llega a la línea 9: Stop (línea 10) no se ve y no se exige.
        var alphaWithoutStop = AlphaType();
        alphaWithoutStop["members"] = new JsonArray(Member("Run()", 4, 8));
        var (result, host) = await ReadAsync((_, _) => Ok(Reading(
            [AlphaFile(), BetaFile()], [alphaWithoutStop, BetaType()], [Finding("F1", "src/a/Alpha.cs", 7, 7, "catch { }")], [AlphaSeed(), BetaSeed()])),
            new SharedPassOptions { MaxFileChars = 130 });
        await using var _ = host;

        var user = Assert.Single(host.Llm.Users);
        Assert.Contains("se muestran las 9 primeras por MaxFileChars", user, StringComparison.Ordinal);
        var batch = Assert.Single(result.Batches);
        Assert.Equal(BatchReadStatus.Complete, batch.Status);
        Assert.Equal(1, batch.Calls);
        Assert.Empty(batch.Gaps);
    }

    [Fact]
    public void Schema_errors_drop_only_the_bad_element()
    {
        var issues = new List<ReaderIssue>();
        var json = """
            {"files":[{"path":"a.cs","summary":"x"},{"role":"sin ruta"}],
             "findings":[{"localId":"F1","kind":"critical","title":"t","anchor":{"path":"a.cs","startLine":1,"endLine":1,"quote":"x"},"impact":3,"likelihood":"2","confidence":0.5},
                         {"localId":"F2","kind":"debt","title":"t","anchor":{"path":"a.cs","startLine":"1","endLine":1,"quote":"x"},"impact":3,"likelihood":2,"confidence":0.5,}]}
            """;

        var reading = BatchReadingJson.Parse(json, "b", issues)!;

        Assert.Equal("b", reading.Batch);
        Assert.Single(reading.Files);
        Assert.Equal("F2", Assert.Single(reading.Findings).LocalId);
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Target == "F1" && i.Message.Contains("'kind'", StringComparison.Ordinal));
    }

    // ───────────────────────────── Salida real del experimento ─────────────────────────────

    private const string FrameworkRoot = @"C:\Users\juan.rozano\m\fw";

    private static string ExperimentDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "experimentos", "2026-10-16-pasada-compartida");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("docs/experimentos/2026-10-16-pasada-compartida");
    }

    /// <summary>
    /// La salida del experimento para un lote como respuesta del LLM, contra el grafo y los ficheros reales de <c>m\fw</c>: todas
    /// las anclas valen (literales o reubicadas) y todos los ficheros quedan cubiertos. Sin esa máquina no comprueba nada.
    /// </summary>
    [Theory]
    [InlineData("json")]
    [InlineData("tool")]
    public async Task The_experiment_output_validates_against_the_real_framework_sources(string batchId)
    {
        var db = Path.Combine(FrameworkRoot, ".codebase", "codebase.db");
        var map = File.Exists(db) ? PreparationArtifacts.TryRead<DomainMap>(Path.Combine(FrameworkRoot, "reports"), out _) : null;
        if (map?.Subsystems.FirstOrDefault(s => s.Id == batchId) is not { } subsystem)
        {
            output.WriteLine($"Sin {db}, su _domain_map.json o el subsistema {batchId}: no se comprueba.");
            return;
        }

        var answer = File.ReadAllText(Path.Combine(ExperimentDir(), "out", batchId + ".json"));
        var options = new SharedPassOptions { MaxReaderRetries = 0 };
        var host = NewHost(new ScriptedReaderLlm((_, _) => Ok(answer)));
        await using var disposeHost = host;
        var graph = await SharedPassGraph.LoadAsync(db, CancellationToken.None);
        var single = new DomainMap { Subsystems = [subsystem] };
        var set = await PacketBuilder.BuildAllAsync(db, FrameworkRoot, single, options, CancellationToken.None);
        var reader = new BatchReader(host.Role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), graph, FrameworkRoot, options);

        var batch = Assert.Single((await reader.ReadAllAsync(set, host.Context, CancellationToken.None)).Batches);

        output.WriteLine($"{batch.Status} calls={batch.Calls} files={batch.Reading.Files.Count} findings={batch.Reading.Findings.Count} " +
            $"relocated={batch.Reading.Findings.Count(f => f.Anchor.Check == AnchorCheck.Relocated)} rejected={batch.Rejected.Count} gaps={batch.Gaps.Count}");
        foreach (var line in batch.Gaps.Select(g => $"GAP {g.Kind} {g.Target}").Concat(batch.Warnings))
            output.WriteLine(line);

        Assert.Empty(batch.Rejected);
        Assert.NotEmpty(batch.Reading.Findings);
        Assert.All(batch.Reading.Findings, f => Assert.True(f.Anchor.Validated));
        Assert.Equal(subsystem.Files.Count, batch.Reading.Files.Count);
        Assert.DoesNotContain(batch.Gaps, g => g.Kind == ReaderIssueKind.MissingFile);
    }
}
