using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment.AnalysisGraph;
using CodebaseAnalysis.Core.SharedPass;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// D3 de la pasada compartida, parte determinista: deduplicación y patrones (<see cref="FindingConsolidator"/>), rúbrica y topes
/// (<see cref="SeverityRubric"/>), exposición desde el grafo (<see cref="GraphExposure"/>), preguntas abiertas y persistencia
/// (<see cref="SharedPassRegistry"/>). Los datos son los reales del experimento (<c>findings_all.json</c>, <c>out/*.json</c>).
/// </summary>
public sealed class SharedPassConsolidationTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    internal static string ExperimentDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "experimentos", "2026-10-16-pasada-compartida");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("docs/experimentos/2026-10-16-pasada-compartida");
    }

    /// <summary>Los 230 hallazgos de <c>findings_all.json</c>, un lote por su campo <c>batch</c>, leídos con el lector de D2.</summary>
    internal static List<ConsolidationBatch> FindingsAll()
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(ExperimentDir(), "findings_all.json")))!.AsArray();
        return [.. all.OfType<JsonObject>().GroupBy(f => f["batch"]!.GetValue<string>()).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var json = new JsonObject { ["findings"] = new JsonArray([.. g.Select(f => (JsonNode?)f.DeepClone())]) }.ToJsonString();
            var issues = new List<ReaderIssue>();
            var reading = BatchReadingJson.Parse(json, g.Key, issues)!;
            Assert.Empty(issues);
            return new ConsolidationBatch(g.Key, reading, "hash-" + g.Key);
        })];
    }

    /// <summary>Las lecturas completas de <c>out/*.json</c> (las partes de un lote se suman).</summary>
    internal static List<ConsolidationBatch> Outputs()
    {
        var readings = new SortedDictionary<string, BatchReading>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(ExperimentDir(), "out"), "*.json").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var batch = JsonNode.Parse(text)!["batch"]?.GetValue<string>();
            if (string.IsNullOrEmpty(batch))
                continue;
            var reading = BatchReadingJson.Parse(text, batch, [])!;
            readings[batch] = readings.TryGetValue(batch, out var previous) ? previous.Merge(reading) : reading;
        }
        return [.. readings.Select(r => new ConsolidationBatch(r.Key, r.Value, "hash-" + r.Key))];
    }

    private static ReadFinding Finding(string id, string path, int start, int end, string quote, string kind = "bug", string category = "cat",
        string? symbol = "A.M", int impact = 3, int likelihood = 3, double confidence = 0.8, string activation = "uso normal") => new()
    {
        LocalId = id, Kind = kind, Category = category, Title = $"título {id}", Rationale = "mecanismo",
        Anchor = new ReadAnchor { Path = path, StartLine = start, EndLine = end, Quote = quote, Check = AnchorCheck.Valid },
        Symbol = symbol, ActivationPath = activation, Impact = impact, Likelihood = likelihood, Confidence = confidence
    };

    private static ConsolidationBatch Batch(string id, params ReadFinding[] findings) =>
        new(id, new BatchReading { Batch = id, Findings = findings }, "hash-" + id);

    // ───────────────────────────── Deduplicación y patrones ─────────────────────────────

    [Fact]
    public void Real_findings_keep_the_distinct_defects_of_one_batch_apart()
    {
        var registry = FindingConsolidator.Consolidate(FindingsAll(), SharedPassGraph.Empty);

        output.WriteLine($"{registry.MergedFrom} → {registry.Entries.Count}; " +
            string.Join(", ", SeverityRubric.Severities.Select(s => $"{s} {registry.Entries.Count(e => e.Severity == s)}")));
        foreach (var note in registry.Notes.Take(10))
            output.WriteLine(note);

        Assert.Equal(230, registry.MergedFrom);
        // En los datos reales no hay duplicados entre lotes por símbolo y categoría ni por anclas solapadas: nada se funde.
        Assert.Equal(230, registry.Entries.Count);
        // Tres fallos distintos de StreamingTagParser.ProcessChunkAsync con la misma categoría (líneas 131, 159 y 202).
        Assert.Equal(3, registry.Entries.Count(e => e.Symbol == "StreamingTagParser.ProcessChunkAsync" && e.Category == "tag-parser"));
        Assert.Equal(registry.Entries.Count, registry.Entries.Select(e => e.ContentId).Distinct().Count());
        Assert.Equal(registry.Entries.Count, registry.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal("REG-001", registry.Entries[0].Id);
        // Los ids van por severidad.
        Assert.True(registry.Entries.Select(e => SeverityRubric.Rank(e.Severity)).SequenceEqual(registry.Entries.Select(e => SeverityRubric.Rank(e.Severity)).Order()));
        // La deuda nunca pasa de P2.
        Assert.DoesNotContain(registry.Entries, e => e.Kind == "debt" && SeverityRubric.Rank(e.Severity) < 2);
    }

    [Fact]
    public void A_real_finding_seen_again_from_another_batch_is_merged_with_its_anchor_as_an_instance()
    {
        var batches = FindingsAll();
        var original = batches.Single(b => b.BatchId == "conversation").Reading.Findings.Single(f => f.LocalId == "F1");
        // Otro lote lo ve desde el fichero llamado, con otra categoría y otro símbolo: el ancla principal se solapa con una instancia.
        var seen = original with
        {
            LocalId = "F99",
            Category = "estado-perdido",
            Symbol = "ConversationContext.AddMessage",
            Anchor = original.Instances[0] with { StartLine = original.Instances[0].StartLine + 1 },
            Instances = [],
            Impact = 2
        };
        batches.Add(Batch("zz-other", seen));

        var registry = FindingConsolidator.Consolidate(batches, SharedPassGraph.Empty);

        Assert.Equal(231, registry.MergedFrom);
        Assert.Equal(230, registry.Entries.Count);
        var merged = registry.Entries.Single(e => e.Sources.Contains("conversation:F1"));
        Assert.Equal(["conversation:F1", "zz-other:F99"], merged.Sources);
        // El representante es el de mayor S (el original, impacto 3) y la otra ancla entra en las instancias.
        Assert.Equal("YamlFileConversationStorage.GetConversationAsync", merged.Symbol);
        Assert.Contains(merged.Instances, i => i.StartLine == seen.Anchor.StartLine && i.Path == seen.Anchor.Path);
        Assert.Contains(registry.Notes, n => n.Contains("zz-other:F99", StringComparison.Ordinal) && n.Contains("entre lotes", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_symbol_and_category_merge_across_batches_but_not_inside_one()
    {
        var registry = FindingConsolidator.Consolidate(
        [
            Batch("a", Finding("F1", "x/A.cs", 10, 10, "foo();"), Finding("F2", "x/A.cs", 40, 41, "bar();")),
            Batch("b", Finding("F1", "y/B.cs", 5, 5, "baz();"))
        ], SharedPassGraph.Empty);

        // a:F1 y a:F2 son defectos distintos del mismo método; b:F1 es el mismo símbolo y categoría visto desde otro lote, y se funde
        // con uno solo de ellos (el primero): fundirlo con los dos juntaría lo que el lector separó.
        Assert.Equal(new[] { new[] { "a:F1", "b:F1" }, new[] { "a:F2" } }, registry.Entries.Select(e => e.Sources.ToArray()).OrderBy(s => s[0], StringComparer.Ordinal));
    }

    [Fact]
    public void The_same_pattern_in_two_batches_is_registered_once_with_instances()
    {
        var registry = FindingConsolidator.Consolidate(
        [
            Batch("a", Finding("F1", "x/A.cs", 10, 10, "catch { }", category: "excepcion-tragada", symbol: "A.Run")),
            Batch("b", Finding("F1", "y/B.cs", 7, 7, "catch  {  }", category: "Excepción-Tragada", symbol: "B.Go")),
            // Misma cita y categoría pero otra severidad (impacto mayor): no entra en el patrón.
            Batch("c", Finding("F1", "z/C.cs", 3, 3, "catch { }", category: "excepcion-tragada", symbol: "C.Do", impact: 5, likelihood: 5))
        ], SharedPassGraph.Empty);

        Assert.Equal(2, registry.Entries.Count);
        var pattern = registry.Entries.Single(e => e.Sources.Count == 2);
        Assert.Equal(["a:F1", "b:F1"], pattern.Sources);
        Assert.Equal("y/B.cs", Assert.Single(pattern.Instances).Path);
    }

    [Fact]
    public void Two_runs_and_any_batch_order_give_the_same_registry()
    {
        var first = SharedPassRegistry.ToJson(FindingConsolidator.Consolidate(FindingsAll(), SharedPassGraph.Empty));
        var reversed = FindingsAll();
        reversed.Reverse();
        var second = SharedPassRegistry.ToJson(FindingConsolidator.Consolidate(reversed, SharedPassGraph.Empty));

        Assert.Equal(first, second);
        // El id de contenido no depende del lote ni del orden: categoría, símbolo y cita.
        Assert.Equal(FindingConsolidator.ContentId("Tag-Parser", "A.B", "x  =  1;"), FindingConsolidator.ContentId("tag-parser", "A.B", "x = 1;"));
        Assert.NotEqual(FindingConsolidator.ContentId("tag-parser", "A.B", "x = 1;"), FindingConsolidator.ContentId("tag-parser", "A.C", "x = 1;"));
    }

    // ───────────────────────────── Rúbrica ─────────────────────────────

    [Fact]
    public void An_activation_that_needs_non_default_configuration_or_api_misuse_caps_at_P2()
    {
        var exposure = new ExposureAssessment { Score = 5, Basis = "punto de entrada" };

        var free = SeverityRubric.Evaluate("bug", 5, 4, exposure, "Cualquier petición HTTP con la configuración de fábrica.");
        var nonDefault = SeverityRubric.Evaluate("bug", 5, 4, exposure, "Solo si se configura MaxRetries con un valor no por defecto.");
        var misuse = SeverityRubric.Evaluate("security", 5, 4, exposure, "Requiere un uso erróneo de la API: pasar una ruta absoluta.");
        var english = SeverityRubric.Evaluate("bug", 5, 4, exposure, "Only with a non-default option.");

        Assert.Equal(("P0", 100, (string?)null), (free.Severity, free.Score, free.CapReason));
        Assert.Equal("P2", nonDefault.Severity);
        Assert.True(nonDefault.ActivationCapped);
        Assert.Contains("no por defecto", nonDefault.Basis, StringComparison.Ordinal);
        Assert.Equal("P2", misuse.Severity);
        Assert.Contains("uso erróneo", misuse.CapReason, StringComparison.Ordinal);
        Assert.Equal("P2", english.Severity);
        // Un P3 no sube por el tope.
        Assert.Equal("P3", SeverityRubric.Evaluate("bug", 1, 2, exposure, "si se configura X").Severity);
    }

    [Fact]
    public void Debt_is_at_most_P2_and_P3_with_low_impact()
    {
        var high = new ExposureAssessment { Score = 5 };
        Assert.Equal("P2", SeverityRubric.Evaluate("debt", 5, 5, high, "").Severity);
        Assert.Equal("P3", SeverityRubric.Evaluate("debt", 2, 5, high, "").Severity);
        Assert.Contains("deuda", SeverityRubric.Evaluate("debt", 5, 5, high, "").CapReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 3, 3, "P1")]   // 36
    [InlineData(4, 4, 4, "P0")]   // 64
    [InlineData(3, 5, 4, "P1")]   // 60 pero impacto 3: no P0
    [InlineData(2, 3, 3, "P2")]   // 18
    [InlineData(2, 2, 3, "P3")]   // 12
    public void The_score_thresholds(int impact, int likelihood, int exposure, string expected) =>
        Assert.Equal(expected, SeverityRubric.Evaluate("bug", impact, likelihood, new ExposureAssessment { Score = exposure }, "").Severity);

    // ───────────────────────────── Exposición desde el grafo ─────────────────────────────

    [Fact]
    public async Task Exposure_comes_from_the_graph_entry_points_callers_and_markers()
    {
        var db = Path.Combine(_temp.Path, "codebase.db");
        using (var g = await CbmTestGraph.CreateAsync(db, "demo"))
        {
            await g.NodeAsync("Class", "Api", "demo.Api", "src/Api.cs", 1, 50);
            var handler = await g.NodeAsync("Method", "Handle", "demo.Api.Handle", "src/Api.cs", 3, 10, """{"is_entry_point":true}""");
            var service = await g.NodeAsync("Method", "Save", "demo.Api.Save", "src/Api.cs", 12, 20,
                """{"sensitive_categories":["file-io"],"accessibility":"private"}""");
            await g.NodeAsync("Method", "Old", "demo.Api.Old", "src/Api.cs", 22, 30, """{"is_reachable":false,"accessibility":"public"}""");
            await g.NodeAsync("Method", "Helper", "demo.Api.Helper", "src/Api.cs", 32, 40, """{"accessibility":"private"}""");
            await g.EdgeAsync(handler, service, "CALLS");
        }
        var exposure = new GraphExposure(await SharedPassGraph.LoadAsync(db, CancellationToken.None));

        var entry = exposure.Assess(Finding("F1", "src/Api.cs", 5, 5, "x"));
        var reached = exposure.Assess(Finding("F2", "src/Api.cs", 15, 15, "x", symbol: "Api.Save"));
        var security = exposure.Assess(Finding("F3", "src/Api.cs", 15, 15, "x", kind: "security", symbol: "Api.Save"));
        var dead = exposure.Assess(Finding("F4", "src/Api.cs", 25, 25, "x", symbol: "Api.Old"));
        var orphan = exposure.Assess(Finding("F5", "src/Api.cs", 35, 35, "x", symbol: "Api.Helper"));
        // La ruta del lector puede venir con un prefijo que el grafo no tiene.
        var prefixed = exposure.Assess(Finding("F6", "repo/src/Api.cs", 5, 5, "x"));
        var unknown = exposure.Assess(Finding("F7", "src/Otro.cs", 5, 5, "x"));

        Assert.Equal((5, true), (entry.Score, entry.EntryPoint == true));
        Assert.Equal("Api.Handle", entry.Node);
        Assert.Equal(4, reached.Score);
        Assert.True(reached.ReachedFromEntryPoint);
        Assert.Equal(5, security.Score);
        Assert.Contains("file-io", security.Basis, StringComparison.Ordinal);
        Assert.Equal(1, dead.Score);
        Assert.Equal(1, orphan.Score);
        Assert.Equal(5, prefixed.Score);
        Assert.Equal((SeverityRubric.NeutralExposure, (string?)null), (unknown.Score, unknown.Node));
    }

    // ───────────────────────────── Preguntas abiertas y semillas ─────────────────────────────

    [Fact]
    public void Open_questions_answered_by_another_batch_get_its_contract_and_the_rest_stay_open()
    {
        var batches = Outputs();
        var registry = FindingConsolidator.Consolidate(batches, SharedPassGraph.Empty);

        var questions = registry.OpenQuestions;
        var answered = questions.Where(q => q.Status != OpenQuestionResolution.StatusOpen).ToList();
        output.WriteLine($"{questions.Count} preguntas, {answered.Count(q => q.Status == OpenQuestionResolution.StatusAnswered)} con contrato, " +
            $"{answered.Count(q => q.Status == OpenQuestionResolution.StatusContext)} con contexto");
        foreach (var q in answered.Take(5))
            output.WriteLine($"[{q.Batch}] {q.Symbol} → {q.AnsweredBy}: {q.Answer?[..Math.Min(120, q.Answer.Length)]}");

        Assert.Equal(batches.Sum(b => b.Reading.OpenQuestions.Count), questions.Count);
        Assert.Contains(questions, q => q.Status == OpenQuestionResolution.StatusAnswered && q.AnsweredBy!.StartsWith("contract:", StringComparison.Ordinal));
        Assert.Contains(questions, q => q.Status == OpenQuestionResolution.StatusContext && q.AnsweredBy!.StartsWith("file:", StringComparison.Ordinal));
        Assert.Contains(questions, q => q.Status == OpenQuestionResolution.StatusOpen);
        // La respuesta es siempre de otro lote.
        Assert.All(answered, q => Assert.NotEqual(q.Batch, q.AnsweredBy!.Split(':')[1]));
        Assert.Contains(registry.Notes, n => n.StartsWith("Preguntas abiertas:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_confirmed_seed_without_any_entry_is_noted()
    {
        var reading = new BatchReading
        {
            Batch = "a",
            Findings = [Finding("F1", "x/A.cs", 1, 1, "x();", symbol: "A.Run") with { SeedRef = "A.Run ISSUE[empty-catch]" }],
            SeedVerdicts =
            [
                new ReadSeedVerdict { Seed = "A.Run ISSUE[empty-catch]", Verdict = "confirmed" },
                new ReadSeedVerdict { Seed = "B.Stop ISSUE[async-void]", Verdict = "confirmed" },
                new ReadSeedVerdict { Seed = "C DEAD?(medium)", Verdict = "false_positive" }
            ]
        };

        var registry = FindingConsolidator.Consolidate([new ConsolidationBatch("a", reading)], SharedPassGraph.Empty);

        Assert.Contains(registry.Notes, n => n.Contains("Semillas confirmadas: 2; 1 sin registro", StringComparison.Ordinal));
        Assert.Contains(registry.Notes, n => n.Contains("B.Stop ISSUE[async-void]", StringComparison.Ordinal));
    }

    // ───────────────────────────── Persistencia ─────────────────────────────

    [Fact]
    public async Task The_registry_is_written_to_the_graph_and_the_registry_file_reads_back()
    {
        var db = Path.Combine(_temp.Path, "codebase.db");
        using var g = await CbmTestGraph.CreateAsync(db, "fw");
        await g.NodeAsync("File", "A.cs", "fw.x.A.cs", "x/A.cs");
        await g.NodeAsync("Class", "A", "fw.A", "x/A.cs", 1, 50);
        await g.NodeAsync("Method", "M", "fw.A.M", "x/A.cs", 5, 20);
        var reading = new BatchReading
        {
            Batch = "a",
            Files = [new ReadFile { Path = "x/A.cs", Summary = "Fichero A." }],
            Types = [new ReadType { Name = "A", Path = "x/A.cs", StartLine = 1, EndLine = 50, Purpose = "Tipo A.",
                Members = [new ReadMember { Name = "M()", StartLine = 5, EndLine = 20, Does = "hace M" }] }],
            Findings = [Finding("F1", "x/A.cs", 10, 10, "foo();", impact: 4, likelihood: 4), Finding("F2", "x/A.cs", 12, 12, "bar();", kind: "debt", category: "dup")]
        };
        var batches = new List<ConsolidationBatch> { new("a", reading, "h1") };
        var registry = FindingConsolidator.Consolidate(batches, SharedPassGraph.Empty);
        var run = new LlmRunRecord { RunId = "shared-pass-test", Scope = SharedPassRegistry.Scope, Models = ["fake-model"] };

        var stats = await LlmRegistryGraphWriter.WriteAsync(db, SharedPassRegistry.BuildRecord(registry, batches, run), CancellationToken.None);
        var warning = SharedPassRegistry.TryWrite(_temp.Path, SharedPassRegistry.ToJson(registry));

        Assert.Null(warning);
        Assert.Equal((2, 2), (stats.Findings, stats.Anchors));
        Assert.Equal(1, stats.ContractsWritten);
        Assert.Equal(2, await g.CountAsync("SELECT count(*) FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.content_id') IS NOT NULL"));
        Assert.Equal("hace M", await g.TextAsync("SELECT json_extract(properties, '$.llm_does') FROM nodes WHERE qualified_name = 'fw.A.M'"));
        Assert.Equal("P1", await g.TextAsync("SELECT json_extract(properties, '$.severity') FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.finding_id') = 'REG-001'"));

        var json = File.ReadAllText(Path.Combine(_temp.Path, SharedPassRegistry.FileName));
        var back = LlmRegistryJson.ParseRegistry(json);
        Assert.Equal(registry.Entries.Select(e => (e.Id, e.ContentId, e.Severity)), back.Select(b => (b.Id, b.ContentId!, b.Severity)));
        Assert.Equal("a", back[0].Batch);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("notes").ValueKind);
    }
}
