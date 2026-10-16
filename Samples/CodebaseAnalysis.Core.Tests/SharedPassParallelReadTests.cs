using System.Diagnostics;
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
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.SharedPass;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Paralelismo de D2 (<see cref="BatchReader.ReadAllAsync"/>) con un LLM falso con retardo: todos los lotes a la vez con el tope de
/// <see cref="SharedPassOptions.MaxParallelBatches"/>, sin esperar por niveles, los más grandes primero; el resultado en el orden del
/// plan; los topes de coste y llamadas con lotes en vuelo; y la espera tras un error reintentable del proveedor (429).
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed partial class SharedPassParallelReadTests(ITestOutputHelper output) : IDisposable
{
    private const int BatchCount = 12;

    private readonly TempDirectory _temp = new();
    private string Repo => Path.Combine(_temp.Path, "repo");
    private string Db => Path.Combine(_temp.Path, "codebase.db");

    public void Dispose() => _temp.Dispose();

    // ───────────────────────────── Repositorio y plan ─────────────────────────────

    /// <summary>Tamaño distinto por lote (en líneas), sin relación con el orden del plan: el orden de arranque es otro.</summary>
    private static int LinesOf(int i) => (i * 5 % BatchCount + 1) * 20;

    /// <summary>
    /// <see cref="BatchCount"/> subsistemas de un fichero cada uno (sin nodos en el grafo: la cobertura es solo el fichero) y el plan
    /// con un nivel distinto por lote, como en un repositorio con un nivel por subsistema: leer por niveles serían 12 rondas.
    /// </summary>
    private async Task<SharedPassPacketSet> PlanAsync(SharedPassOptions options)
    {
        using (await CbmTestGraph.CreateAsync(Db, "demo"))
        {
        }
        var subsystems = new List<Subsystem>();
        for (var i = 0; i < BatchCount; i++)
        {
            var file = $"src/s{i:00}/F{i:00}.cs";
            var path = Path.Combine(Repo, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", Enumerable.Range(0, LinesOf(i)).Select(n => $"// línea {n} de F{i:00}")) + "\n", new UTF8Encoding(false));
            subsystems.Add(new Subsystem { Id = $"s{i:00}", Name = $"S{i:00}", Description = "desc", Kind = SubsystemKinds.Capability, Files = [file] });
        }
        var set = await PacketBuilder.BuildAllAsync(Db, Repo, new DomainMap { Subsystems = subsystems }, options, CancellationToken.None);
        return set with { Plan = set.Plan with { Batches = [.. set.Plan.Batches.Select((b, i) => b with { Level = i })] } };
    }

    private static string ReadingOf(string batchId, string file) => new JsonObject
    {
        ["batch"] = batchId,
        ["files"] = new JsonArray(new JsonObject { ["path"] = file, ["role"] = "comments", ["summary"] = $"Comentarios de {batchId}." }),
        ["types"] = new JsonArray(),
        ["findings"] = new JsonArray(),
        ["seedVerdicts"] = new JsonArray(),
        ["relations"] = new JsonArray(),
        ["openQuestions"] = new JsonArray()
    }.ToJsonString();

    // ───────────────────────────── LLM falso ─────────────────────────────

    [GeneratedRegex(@"\(id `(?<id>[^`]+)`\)")]
    private static partial Regex BatchIdPattern();

    [GeneratedRegex(@"### FILE (?<file>\S+)")]
    private static partial Regex FilePattern();

    /// <summary>
    /// Responde a cada lote con la lectura de su fichero tras <c>delay(lote)</c>; <c>fail(i, lote)</c>, si da algo, es el resultado
    /// de la llamada i (desde 0). Cuenta las llamadas en vuelo y guarda el orden de arranque.
    /// </summary>
    private sealed class DelayedReaderLlm(Func<string, TimeSpan> delay, decimal? cost, Func<int, string, LlmResult?>? fail = null) : ILlmService
    {
        private readonly Lock _lock = new();
        private readonly List<string> _started = [];
        private int _inFlight;

        public string Name => "reader-fake";
        public int MaxInFlight { get; private set; }
        public IReadOnlyList<string> Started { get { lock (_lock) { return [.. _started]; } } }

        public async Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? "";
            var id = BatchIdPattern().Match(user).Groups["id"].Value;
            int index;
            lock (_lock)
            {
                index = _started.Count;
                _started.Add(id);
                MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
            }
            try
            {
                await Task.Delay(delay(id), cancellationToken);
                if (fail?.Invoke(index, id) is { } failure)
                    return failure;
                var file = FilePattern().Match(user).Groups["file"].Value;
                return LlmResult.Success([new LlmResponse { Content = ReadingOf(id, file), FinishReason = FinishReason.Stop }],
                    tokensUsed: 100, promptTokens: 60, completionTokens: 40, costUsd: cost, model: "fake-model");
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                }
            }
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private sealed record Run(SharedPassReadResult Result, SharedPassPacketSet Set, DelayedReaderLlm Llm, SharedPassCostBudget Budget, List<TimeSpan> Waits, TimeSpan Elapsed);

    private async Task<Run> ReadAsync(DelayedReaderLlm llm, SharedPassOptions options, SharedPassPacketSet? set = null)
    {
        set ??= await PlanAsync(options);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(TestConfig.FromPairs(FakeLlmConfig.Pairs("reader-fake", SharedPassOptions.DefaultReaderRole)));
        services.AddLogging();
        services.AddSingleton<ILlmService>(llm);
        services.AddAITaskAgent();
        await using var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        var role = LlmRoleFactory.BuildRoles(provider, _ => null, _ => { }, [SharedPassOptions.DefaultReaderRole])[SharedPassOptions.DefaultReaderRole];
        var context = AnalysisRunner.CreateContext(new AnalysisPipelineInput { RepositoryPath = Repo, DatabasePath = Db }, provider, null, new CapturingReporter());
        var graph = await SharedPassGraph.LoadAsync(Db, CancellationToken.None);
        var waits = new List<TimeSpan>();
        var reader = new BatchReader(role, AnalysisPipelineFactory.CreateDefaultTemplateProvider(), graph, Repo, options)
        {
            Delay = (wait, _) =>
            {
                lock (waits)
                    waits.Add(wait);
                return Task.CompletedTask;
            }
        };
        var budget = new SharedPassCostBudget(options);
        var clock = Stopwatch.StartNew();
        var result = await reader.ReadAllAsync(set, context, CancellationToken.None, budget);
        clock.Stop();
        output.WriteLine($"elapsed={clock.ElapsedMilliseconds} ms maxInFlight={llm.MaxInFlight} started={string.Join(",", llm.Started)}");
        foreach (var b in result.Batches)
            output.WriteLine($"{b.BatchId}: {b.Status} calls={b.Calls} gaps={b.Gaps.Count} stop={b.StopReason}");
        return new Run(result, set, llm, budget, waits, clock.Elapsed);
    }

    /// <summary>Lo que se compara entre dos ejecuciones: todo el resultado, en su orden.</summary>
    private static string Fingerprint(SharedPassReadResult result) => string.Join("\n",
        result.Warnings.Concat(result.Batches.SelectMany(b => new[] { $"{b.BatchId} {b.Status} {b.Calls} {b.CostUsd} {b.InputHash} {b.StopReason}", b.Reading.ToJson() }
            .Concat(b.Warnings).Concat(b.Gaps.Select(g => g.ToString())).Concat(b.Rejected.Select(r => r.ToString())))));

    // ───────────────────────────── Tests ─────────────────────────────

    [Fact]
    public async Task Twelve_batches_on_twelve_levels_take_about_three_rounds_with_four_in_parallel()
    {
        var delay = TimeSpan.FromMilliseconds(400);
        var run = await ReadAsync(new DelayedReaderLlm(_ => delay, 0.01m), new SharedPassOptions { MaxParallelBatches = 4 });

        Assert.Equal(BatchCount, run.Result.Batches.Count);
        Assert.All(run.Result.Batches, b => Assert.Equal(BatchReadStatus.Complete, b.Status));
        Assert.Equal(4, run.Llm.MaxInFlight);
        // Tres rondas son ~1,2 s; por niveles serían doce (~4,8 s). Margen para una máquina cargada, lejos de las doce.
        Assert.True(run.Elapsed < delay * 7, $"tardó {run.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task The_largest_batches_start_first()
    {
        var options = new SharedPassOptions { MaxParallelBatches = 4 };
        var set = await PlanAsync(options);
        var run = await ReadAsync(new DelayedReaderLlm(_ => TimeSpan.FromMilliseconds(150), 0.01m), options, set);

        var bySize = set.Packets.OrderByDescending(p => p.EstimatedTokens).Select(p => p.BatchId).ToList();
        Assert.NotEqual(set.Plan.Batches.Select(b => b.Id), bySize);
        // Los cuatro primeros salen a la vez (su orden entre ellos depende del planificador de hilos); cada ronda, los siguientes.
        for (var round = 0; round < BatchCount / 4; round++)
            Assert.Equal(bySize.Skip(round * 4).Take(4).Order(), run.Llm.Started.Skip(round * 4).Take(4).Order());
    }

    [Fact]
    public async Task The_result_is_in_plan_order_and_identical_to_a_serial_read_whatever_finishes_first()
    {
        // Los lotes del final del plan acaban antes: si el resultado siguiera el orden de llegada, saldría al revés.
        TimeSpan Delay(string id) => TimeSpan.FromMilliseconds(20 + 25 * (BatchCount - int.Parse(id[1..], System.Globalization.CultureInfo.InvariantCulture)));
        var parallel = await ReadAsync(new DelayedReaderLlm(Delay, 0.01m), new SharedPassOptions { MaxParallelBatches = 4 });
        var serial = await ReadAsync(new DelayedReaderLlm(Delay, 0.01m), new SharedPassOptions { MaxParallelBatches = 1 }, parallel.Set);

        Assert.Equal(parallel.Set.Plan.Batches.Select(b => b.Id), parallel.Result.Batches.Select(b => b.BatchId));
        Assert.Equal(1, serial.Llm.MaxInFlight);
        Assert.Equal(Fingerprint(serial.Result), Fingerprint(parallel.Result));
    }

    [Fact]
    public async Task The_stage_cost_cap_lets_the_batches_in_flight_finish_and_leaves_the_rest_unread_with_its_reason()
    {
        // 0,6 USD por llamada y tope de etapa de 1 USD: salen cuatro a la vez (ninguna ha costado aún), terminan, y no sale ninguna más.
        var run = await ReadAsync(new DelayedReaderLlm(_ => TimeSpan.FromMilliseconds(300), 0.6m),
            new SharedPassOptions { MaxParallelBatches = 4, MaxCostPerStageUsd = 1 });

        Assert.Equal(4, run.Llm.Started.Count);
        Assert.Equal(4, run.Result.Batches.Count(b => b.Status == BatchReadStatus.Complete));
        Assert.Equal(2.4m, run.Budget.StageSpentUsd);
        Assert.Equal(4, run.Budget.Calls);
        Assert.True(run.Result.StoppedByCostLimit);
        var unread = run.Result.Batches.Where(b => b.Status != BatchReadStatus.Complete).ToList();
        Assert.Equal(BatchCount - 4, unread.Count);
        Assert.All(unread, b =>
        {
            Assert.Equal(BatchReadStatus.NotRead, b.Status);
            Assert.Equal(0, b.Calls);
            Assert.Contains("tope de coste de la etapa", b.StopReason, StringComparison.Ordinal);
            Assert.Equal(run.Set.Plan.Batches.Single(p => p.Id == b.BatchId).Files, b.Gaps.Select(g => g.File));
        });
    }

    [Fact]
    public async Task The_call_cap_is_exact_with_batches_in_parallel()
    {
        // Sin precio: el tope de llamadas es la defensa. Con cuatro lotes en paralelo y un tope de 3, salen 3, no 4.
        var run = await ReadAsync(new DelayedReaderLlm(_ => TimeSpan.FromMilliseconds(200), cost: null),
            new SharedPassOptions { MaxParallelBatches = 4, MaxCallsPerRun = 3 });

        Assert.Equal(3, run.Llm.Started.Count);
        Assert.Equal(3, run.Budget.Calls);
        Assert.Equal(3, run.Result.Calls);
        Assert.Equal(3, run.Result.Batches.Count(b => b.Status == BatchReadStatus.Complete));
        Assert.All(run.Result.Batches.Where(b => b.Status != BatchReadStatus.Complete), b =>
        {
            Assert.True(b.Status is BatchReadStatus.NotRead or BatchReadStatus.StoppedByCost, b.Status.ToString());
            Assert.Equal(0, b.Calls);
            Assert.Contains("tope de llamadas de la ejecución", b.StopReason, StringComparison.Ordinal);
            Assert.NotEmpty(b.Gaps);
        });
    }

    [Fact]
    public async Task A_retriable_provider_error_waits_before_each_retry_and_every_call_counts()
    {
        // Las dos primeras llamadas reciben un 429 que el proveedor ya no reintenta más; la tercera responde.
        var llm = new DelayedReaderLlm(_ => TimeSpan.Zero, 0.01m,
            (i, _) => i < 2 ? LlmResult.Failure(new HttpRequestException("HTTP 429: rate limit"), isRetriable: true) : null);
        var options = new SharedPassOptions { MaxParallelBatches = 1, MaxReaderRetries = 2 };
        var set = await PlanAsync(options);
        var single = set with { Plan = set.Plan with { Batches = [set.Plan.Batches[0]] }, Packets = [set.Packets[0]] };

        var run = await ReadAsync(llm, options, single);

        var batch = Assert.Single(run.Result.Batches);
        Assert.Equal(BatchReadStatus.Complete, batch.Status);
        Assert.Equal(3, batch.Calls);
        Assert.Equal(3, run.Budget.Calls);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)], run.Waits);
        Assert.Equal(2, batch.Warnings.Count(w => w.Contains("429", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_non_retriable_error_is_retried_without_waiting_and_none_waits_after_the_last_attempt()
    {
        var options = new SharedPassOptions { MaxParallelBatches = 1, MaxReaderRetries = 2 };
        var set = await PlanAsync(options);
        var single = set with { Plan = set.Plan with { Batches = [set.Plan.Batches[0]] }, Packets = [set.Packets[0]] };

        var fatal = await ReadAsync(new DelayedReaderLlm(_ => TimeSpan.Zero, 0.01m,
            (_, _) => LlmResult.Failure(new HttpRequestException("HTTP 400: bad request"), isRetriable: false)), options, single);
        Assert.Equal(BatchReadStatus.Failed, Assert.Single(fatal.Result.Batches).Status);
        Assert.Equal(3, fatal.Budget.Calls);
        Assert.Empty(fatal.Waits);

        // Siempre 429: espera antes del segundo y del tercer intento, no después del último.
        var limited = await ReadAsync(new DelayedReaderLlm(_ => TimeSpan.Zero, 0.01m,
            (_, _) => LlmResult.Failure(new HttpRequestException("HTTP 429: rate limit"), isRetriable: true)), options, single);
        Assert.Equal(BatchReadStatus.Failed, Assert.Single(limited.Result.Batches).Status);
        Assert.Equal(3, limited.Budget.Calls);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)], limited.Waits);
    }
}
