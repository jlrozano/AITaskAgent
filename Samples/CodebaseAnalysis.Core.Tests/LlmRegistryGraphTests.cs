using System.Text.Json.Nodes;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Enrichment.AnalysisGraph;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Paquete C del plan 2026-10-16: los nodos y aristas del registro del LLM (Finding, Anchor, Summary, Batch, Run), los contratos
/// <c>llm_*</c> de los métodos y las vistas <c>findings</c> y <c>llm_contracts</c>. Los datos son los reales del experimento
/// (<c>registry.json</c> y <c>out/*.json</c>); el grafo es sintético, con la forma de cbm y las rutas de <c>Framework</c>.
/// </summary>
public sealed class LlmRegistryGraphTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string ExperimentDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Fixtures", "shared-pass-2026-10-16");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Fixtures/shared-pass-2026-10-16 no está en ningún directorio superior");
    }

    private static IReadOnlyList<LlmFindingRecord> Registry() =>
        LlmRegistryJson.ParseRegistry(File.ReadAllText(Path.Combine(ExperimentDir(), "registry.json")));

    private static LlmBatchOutput Batch(string name) =>
        LlmRegistryJson.ParseBatchOutput(File.ReadAllText(Path.Combine(ExperimentDir(), "out", name + ".json")));

    private static LlmRunRecord Run(string id = "run-1", string scope = "framework") => new()
    {
        RunId = id,
        Scope = scope,
        StartedAt = DateTimeOffset.Parse("2026-10-16T10:00:00Z"),
        Models = ["gpt-sol"],
        PromptVersions = new Dictionary<string, string> { ["E3"] = "v1", ["E4"] = "v1" }
    };

    private async Task<CbmTestGraph> EmptyGraphAsync() => await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "fw");

    /// <summary>Un grafo mínimo con los dos ficheros del primer hallazgo del registro (las rutas del registro van sin la raíz <c>Framework/</c>).</summary>
    private async Task<(CbmTestGraph G, long Render, long TinyFile)> TemplateGraphAsync()
    {
        var g = await EmptyGraphAsync();
        const string tiny = "Framework/Support/Template/TinyTemplateRendered.cs";
        const string resolver = "Framework/LLM/Support/PromptTemplateResolver.cs";
        var tinyFile = await g.NodeAsync("File", "TinyTemplateRendered.cs", "fw.Support.Template.TinyTemplateRendered.cs", tiny);
        await g.NodeAsync("File", "PromptTemplateResolver.cs", "fw.LLM.Support.PromptTemplateResolver.cs", resolver);
        await g.NodeAsync("Class", "TinyTemplateRendered", "fw.Support.Template.TinyTemplateRendered", tiny, 1, 400);
        var render = await g.NodeAsync("Method", "RenderInternal", "fw.Support.Template.TinyTemplateRendered.RenderInternal", tiny, 100, 150);
        await g.NodeAsync("Method", "ResolveFileInclude", "fw.Support.Template.TinyTemplateRendered.ResolveFileInclude", tiny, 240, 270);
        await g.NodeAsync("Class", "PromptTemplateResolver", "fw.LLM.Support.PromptTemplateResolver", resolver, 1, 120);
        return (g, render, tinyFile);
    }

    /// <summary>Un grafo con un fichero, su tipo y un método por miembro de cada tipo de la salida de un lote.</summary>
    private static async Task<int> AddBatchNodesAsync(CbmTestGraph g, LlmBatchOutput batch)
    {
        var types = batch.Summaries.Where(s => s.Level == LlmSummaryRecord.LevelSymbol).ToList();
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in batch.Summaries.Select(s => s.Path!).Concat(batch.Contracts.Select(c => c.Path)).Distinct())
        {
            if (files.Add(path))
                await g.NodeAsync("File", Path.GetFileName(path), "fw.file." + path.Replace('/', '.'), "Framework/" + path);
        }
        foreach (var type in types)
            await g.NodeAsync("Class", type.Symbol!, $"fw.{type.Symbol}", "Framework/" + type.Path, type.StartLine, type.EndLine);
        var methods = 0;
        foreach (var contract in batch.Contracts)
        {
            var name = contract.Symbol[(contract.Symbol.LastIndexOf('.') + 1)..];
            // Los miembros son únicos por línea (los nombres se repiten en sobrecargas): el qualified_name lleva la línea.
            await g.NodeAsync("Method", name, $"fw.{contract.Symbol}({contract.StartLine})", "Framework/" + contract.Path, contract.StartLine, contract.EndLine);
            methods++;
        }
        return methods;
    }

    private async Task<string> DomainAsync(string args, string databasePath)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.CbmDbPath, databasePath);
        var result = await new GraphDomainTool().ExecuteAsync(args, context, "ChatAssistant", NullLogger.Instance);
        output.WriteLine($"--- graph_domain {args}\n{result}");
        return result;
    }

    private static int Rows(string markdown, string prefix) => markdown.Split('\n').Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

    // ───────────────────────────── registro: hallazgos y anclas ─────────────────────────────

    [Fact]
    public async Task The_real_registry_is_imported_with_nodes_edges_and_provenance()
    {
        var findings = Registry();
        Assert.Equal(187, findings.Count);
        var (g, render, tinyFile) = await TemplateGraphAsync();
        using var _ = g;
        var codeBefore = await g.CountAsync("SELECT COUNT(1) FROM nodes");

        var stats = await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath,
            new LlmRegistryRecord { Run = Run(), Findings = findings }, CancellationToken.None);

        Assert.Equal(187, stats.Findings);
        Assert.Equal(187, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding'"));
        var expectedAnchors = findings.Sum(f => (f.Anchor is null ? 0 : 1) + f.Instances.Count);
        Assert.Equal(expectedAnchors, stats.Anchors);
        Assert.Equal(expectedAnchors, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Anchor'"));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Run'"));
        Assert.Equal(codeBefore + 187 + expectedAnchors + 1 + stats.Batches, await g.CountAsync("SELECT COUNT(1) FROM nodes"));

        // REG-001: ancla principal más tres instancias, las cuatro en ficheros del grafo (la ruta del registro sin la raíz Framework/).
        var reg1 = (await g.NodeIdAsync("llm_finding:run-1:REG-001"))!.Value;
        Assert.Equal(4, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @s AND type = 'AT'", ("@s", reg1)));
        Assert.Equal("P0", await g.TextAsync("SELECT json_extract(properties, '$.severity') FROM nodes WHERE id = @id", ("@id", reg1)));
        Assert.Equal(3, await g.CountAsync(
            "SELECT COUNT(1) FROM edges i JOIN edges a ON a.target_id = i.source_id AND a.type = 'AT' WHERE a.source_id = @s AND i.type = 'IN' AND i.target_id = @f",
            ("@s", reg1), ("@f", tinyFile)));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE source_id = @s AND target_id = @t AND type = 'ABOUT' AND json_extract(properties, '$.resolution') = 'symbol'",
            ("@s", reg1), ("@t", render)));

        // Un hallazgo rechazado conserva su estado y no tiene severidad.
        Assert.Equal(5, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.status') = 'rejected'"));
        Assert.Equal(0, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.status') = 'rejected' AND json_extract(properties, '$.severity') IS NOT NULL"));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.severity') = 'P0'"));
        Assert.Equal(7, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.severity') = 'P1'"));
        Assert.Equal(10, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.kind') = 'security'"));

        // Procedencia en nodos y aristas, y el nodo es de origen LLM sin fichero (los lectores por fichero no lo ven).
        Assert.Equal(0, await g.CountAsync(
            "SELECT COUNT(1) FROM nodes WHERE label IN ('Finding', 'Anchor', 'Run', 'Batch') AND (json_extract(properties, '$.origin') <> 'llm' " +
            "OR json_extract(properties, '$.run_id') <> 'run-1' OR json_extract(properties, '$.producer') IS NULL OR file_path <> '')"));
        Assert.Equal("Support/Template/TinyTemplateRendered.cs", await g.TextAsync("SELECT json_extract(properties, '$.file') FROM nodes WHERE id = @id", ("@id", reg1)));
        Assert.Equal(109, await g.CountAsync("SELECT json_extract(properties, '$.start_line') FROM nodes WHERE id = @id", ("@id", reg1)));
        Assert.Equal(115, await g.CountAsync("SELECT json_extract(properties, '$.end_line') FROM nodes WHERE id = @id", ("@id", reg1)));
        Assert.Equal("LlmRegistry", await g.TextAsync("SELECT json_extract(properties, '$.enriched_by[0]') FROM nodes WHERE id = @id", ("@id", reg1)));
        Assert.Equal(0, await g.CountAsync(
            "SELECT COUNT(1) FROM edges WHERE type IN ('AT', 'IN', 'ABOUT', 'IN_RUN', 'IN_BATCH') " +
            "AND (json_extract(properties, '$.origin') <> 'llm' OR json_extract(properties, '$.run_id') <> 'run-1' OR json_extract(properties, '$.enriched_by[0]') <> 'LlmRegistry')"));
        Assert.Equal("[\"gpt-sol\"]", await g.TextAsync("SELECT json_extract(properties, '$.models') FROM nodes WHERE label = 'Run'"));
        Assert.Equal("v1", await g.TextAsync("SELECT json_extract(properties, '$.prompt_versions.E3') FROM nodes WHERE label = 'Run'"));
    }

    [Fact]
    public async Task Rewriting_a_run_replaces_it_without_duplicating_nodes_or_edges()
    {
        var findings = Registry();
        var (g, _, _) = await TemplateGraphAsync();
        using var _ = g;
        var record = new LlmRegistryRecord { Run = Run(), Findings = findings, Batches = [new LlmBatchRecord { Id = "llm-configuration" }] };

        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, record, CancellationToken.None);
        var nodes = await g.CountAsync("SELECT COUNT(1) FROM nodes");
        var edges = await g.CountAsync("SELECT COUNT(1) FROM edges");
        var stats = await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, record, CancellationToken.None);

        Assert.Equal(nodes, await g.CountAsync("SELECT COUNT(1) FROM nodes"));
        Assert.Equal(edges, await g.CountAsync("SELECT COUNT(1) FROM edges"));
        Assert.True(stats.PreviousRemoved > 187);
        Assert.Equal(0, stats.StaleMarked);
        Assert.Equal(0, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.stale') IS NOT NULL"));

        // Menos hallazgos en la reescritura: los que ya no están desaparecen con sus anclas y aristas.
        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, record with { Findings = [.. findings.Take(10)] }, CancellationToken.None);
        Assert.Equal(10, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding'"));
        Assert.Equal(0, await g.CountAsync("SELECT COUNT(1) FROM edges e WHERE e.type = 'AT' AND NOT EXISTS (SELECT 1 FROM nodes n WHERE n.id = e.source_id)"));
        Assert.Equal(0, await g.CountAsync("SELECT COUNT(1) FROM edges e WHERE NOT EXISTS (SELECT 1 FROM nodes n WHERE n.id = e.source_id) OR NOT EXISTS (SELECT 1 FROM nodes n WHERE n.id = e.target_id)"));
    }

    [Fact]
    public async Task A_new_run_marks_the_previous_one_of_the_same_scope_as_stale_and_keeps_other_scopes()
    {
        var findings = Registry().Take(20).ToList();
        var (g, _, _) = await TemplateGraphAsync();
        using var _ = g;

        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord { Run = Run("run-a", "framework"), Findings = findings }, CancellationToken.None);
        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord { Run = Run("run-b", "other"), Findings = findings.Take(5).ToList() }, CancellationToken.None);
        var stats = await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath,
            new LlmRegistryRecord { Run = Run("run-c", "framework"), Findings = findings.Take(8).ToList() }, CancellationToken.None);

        Assert.True(stats.StaleMarked > 20);
        // No se borra nada: los tres Run conviven; solo los de run-a quedan stale, y dicen quién los sustituyó.
        Assert.Equal(20 + 5 + 8, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding'"));
        Assert.Equal(20, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.stale') = 1 AND json_extract(properties, '$.stale_by') = 'run-c'"));
        Assert.Equal(0, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.run_id') IN ('run-b', 'run-c') AND json_extract(properties, '$.stale') IS NOT NULL"));
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Run' AND json_extract(properties, '$.run_id') = 'run-a' AND json_extract(properties, '$.stale') = 1"));
    }

    // ───────────────────────────── contratos, resúmenes y semillas ─────────────────────────────

    [Fact]
    public async Task Member_contracts_of_a_real_batch_are_written_as_llm_properties_of_the_method_nodes()
    {
        var batch = Batch("event");
        Assert.NotEmpty(batch.Contracts);
        var g = await EmptyGraphAsync();
        using var _ = g;
        var methods = await AddBatchNodesAsync(g, batch);
        // Las propiedades previas del nodo de código se conservan.
        using (var update = g.Connection.CreateCommand())
        {
            update.CommandText = "UPDATE nodes SET properties = '{\"cyclomatic\":3,\"origin\":\"enricher\"}' WHERE label = 'Method'";
            await update.ExecuteNonQueryAsync();
        }

        var stats = await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord
        {
            Run = Run(),
            Batches = [new LlmBatchRecord { Id = "event" }],
            Summaries = batch.Summaries,
            Contracts = batch.Contracts
        }, CancellationToken.None);

        Assert.Equal(methods, stats.ContractsWritten);
        Assert.Equal(0, stats.ContractsUnresolved);
        Assert.Equal(methods, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.llm_run') = 'run-1'"));
        var first = batch.Contracts[0];
        var node = (await g.NodeIdAsync($"fw.{first.Symbol}({first.StartLine})"))!.Value;
        Assert.Equal(first.Does, await g.TextAsync("SELECT json_extract(properties, '$.llm_does') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal(first.Inputs, await g.TextAsync("SELECT json_extract(properties, '$.llm_inputs') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal(first.Outputs, await g.TextAsync("SELECT json_extract(properties, '$.llm_outputs') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal(first.SideEffects, await g.TextAsync("SELECT json_extract(properties, '$.llm_side_effects') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal(first.Throws, await g.TextAsync("SELECT json_extract(properties, '$.llm_throws') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal("llm", await g.TextAsync("SELECT json_extract(properties, '$.llm_origin') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal("enricher", await g.TextAsync("SELECT json_extract(properties, '$.origin') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal(3, await g.CountAsync("SELECT json_extract(properties, '$.cyclomatic') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal(first.Path, await g.TextAsync("SELECT json_extract(properties, '$.llm_file') FROM nodes WHERE id = @id", ("@id", node)));
        Assert.Equal(first.StartLine, await g.CountAsync("SELECT json_extract(properties, '$.llm_start_line') FROM nodes WHERE id = @id", ("@id", node)));

        // Un resumen por fichero y por tipo, enlazados con SUMMARIZES; los miembros y los tipos quedan en el lote.
        Assert.Equal(batch.Summaries.Count, stats.Summaries);
        Assert.Equal(batch.Summaries.Count, stats.SummariesLinked);
        Assert.Equal(batch.Summaries.Count(s => s.Level == "file"), await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Summary' AND json_extract(properties, '$.level') = 'file'"));
        Assert.Equal(methods, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'IN_BATCH' AND source_id IN (SELECT id FROM nodes WHERE label = 'Method')"));

        // Reescribir el Run deja el mismo número de contratos y de aristas.
        var edges = await g.CountAsync("SELECT COUNT(1) FROM edges");
        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord
        {
            Run = Run(), Batches = [new LlmBatchRecord { Id = "event" }], Summaries = batch.Summaries, Contracts = batch.Contracts
        }, CancellationToken.None);
        Assert.Equal(edges, await g.CountAsync("SELECT COUNT(1) FROM edges"));
        Assert.Equal(methods, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.llm_run') = 'run-1'"));
    }

    [Fact]
    public async Task Contracts_that_the_new_run_does_not_rewrite_become_stale()
    {
        var batch = Batch("event");
        var g = await EmptyGraphAsync();
        using var _ = g;
        await AddBatchNodesAsync(g, batch);
        var half = batch.Contracts.Take(batch.Contracts.Count / 2).ToList();

        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord { Run = Run("run-1"), Contracts = batch.Contracts }, CancellationToken.None);
        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord { Run = Run("run-2"), Contracts = half }, CancellationToken.None);

        Assert.Equal(half.Count, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.llm_run') = 'run-2' AND json_extract(properties, '$.llm_stale') IS NULL"));
        Assert.Equal(batch.Contracts.Count - half.Count, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.llm_run') = 'run-1' AND json_extract(properties, '$.llm_stale') = 1"));
    }

    [Fact]
    public async Task The_facet_totals_do_not_count_the_llm_registry_nodes_or_edges()
    {
        var batch = Batch("event");
        var g = await EmptyGraphAsync();
        using var _ = g;
        await AddBatchNodesAsync(g, batch);
        var before = await FacetsAsync(g.DatabasePath);
        Assert.Equal(await g.CountAsync("SELECT COUNT(1) FROM nodes"), before.TotalNodes);

        // Hallazgos, resúmenes y contratos de un lote: nodos Finding/Anchor/Summary/Batch/Run y aristas IN_BATCH desde los métodos.
        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord
        {
            Run = Run(), Batches = [new LlmBatchRecord { Id = "event" }], Findings = batch.Findings, Summaries = batch.Summaries, Contracts = batch.Contracts
        }, CancellationToken.None);
        Assert.True(await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE json_extract(properties, '$.origin') = 'llm'") > 0);
        Assert.True(await g.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'IN_BATCH' AND source_id IN (SELECT id FROM nodes WHERE label = 'Method')") > 0);

        var after = await FacetsAsync(g.DatabasePath);
        Assert.Equal((before.TotalNodes, before.TotalEdges), (after.TotalNodes, after.TotalEdges));
        Assert.Equal(before.MetricsSummary, after.MetricsSummary);
    }

    private async Task<ReportFacetInput> FacetsAsync(string databasePath)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput { RepositoryPath = _temp.Path, DatabasePath = databasePath });
        context.Set(AnalysisContextKeys.Reporter, new CapturingReporter());
        // TotalNodes de la entrada a propósito con el grafo entero contado: el paso pone los del código.
        return await new SqlFacetExtractorStep().ExecuteAsync(new GraphStatusResult { DatabasePath = databasePath, TotalNodes = int.MaxValue }, context, 1, null, CancellationToken.None);
    }

    [Fact]
    public async Task Findings_from_a_batch_link_their_seed_and_symbol()
    {
        var batch = Batch("conversation");
        var seeded = batch.Findings.Where(f => !string.IsNullOrEmpty(f.SeedRef)).ToList();
        Assert.NotEmpty(seeded);
        var g = await EmptyGraphAsync();
        using var _ = g;
        await AddBatchNodesAsync(g, batch);

        var stats = await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord
        {
            Run = Run(), Findings = batch.Findings, Contracts = batch.Contracts, Summaries = batch.Summaries
        }, CancellationToken.None);

        output.WriteLine($"findings={stats.Findings} about={stats.AboutEdges} unlinked={stats.FindingsUnlinked} fromSeed={stats.FromSeedEdges}");
        Assert.Equal(batch.Findings.Count, stats.Findings);
        Assert.True(stats.AboutEdges >= batch.Findings.Count - stats.FindingsUnlinked);
        Assert.True(stats.FromSeedEdges >= 1);
        // F-ids de lote: «lote:Fn»; sin severidad todavía (la asigna la consolidación).
        Assert.Equal(0, await g.CountAsync("SELECT COUNT(1) FROM nodes WHERE label = 'Finding' AND json_extract(properties, '$.severity') IS NOT NULL"));
        Assert.Equal(stats.FromSeedEdges, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'FROM_SEED' AND json_extract(properties, '$.seed') IS NOT NULL"));
        Assert.Equal("PipelineContext.SendEventAsync", LlmRegistryGraphWriter.SeedSymbol("PipelineContext.SendEventAsync ISSUE[empty-catch linea 185]"));
        Assert.Equal("ToolCallXmlParser", LlmRegistryGraphWriter.SeedSymbol("DEAD?(medium) ToolCallXmlParser (clase)"));
        Assert.Equal("Foo.Bar", LlmRegistryGraphWriter.SeedSymbol("DEAD? Foo.Bar(string x)"));
    }

    [Fact]
    public async Task Instance_of_subsystem_and_system_summaries_are_linked_when_they_have_a_target()
    {
        var g = await EmptyGraphAsync();
        using var _ = g;
        var subsystem = await g.NodeAsync("Subsystem", "Core", "subsystem:core");
        var findings = new[]
        {
            new LlmFindingRecord { Id = "REG-001", Kind = "bug", Title = "uno", Severity = "P2", Status = "confirmed" },
            new LlmFindingRecord { Id = "REG-002", Kind = "bug", Title = "dos", Severity = "P3", Status = "confirmed", InstanceOf = "REG-001" },
            new LlmFindingRecord { Id = "REG-003", Kind = "debt", Title = "tres", Status = "confirmed", InstanceOf = "REG-999" }
        };
        var summaries = new[]
        {
            new LlmSummaryRecord { Level = "subsystem", Key = "core", Text = "Núcleo", SubsystemId = "core" },
            new LlmSummaryRecord { Level = "subsystem", Key = "ghost", Text = "No existe", SubsystemId = "ghost" },
            new LlmSummaryRecord { Level = "system", Key = "all", Text = "Todo el sistema" }
        };

        var stats = await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath,
            new LlmRegistryRecord { Run = Run(), Findings = findings, Summaries = summaries }, CancellationToken.None);

        Assert.Equal(1, stats.InstanceOfEdges); // REG-999 no está en el Run: queda solo la propiedad
        Assert.Equal("REG-999", await g.TextAsync("SELECT json_extract(properties, '$.instance_of') FROM nodes WHERE qualified_name = 'llm_finding:run-1:REG-003'"));
        Assert.Equal(3, stats.Summaries);
        Assert.Equal(1, stats.SummariesLinked);
        Assert.Equal(1, await g.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'SUMMARIZES' AND target_id = @t", ("@t", subsystem)));
        Assert.Equal(0, stats.FindingsUnlinked); // sin símbolo no hay ABOUT ni cuentan como sin enlazar
        Assert.Equal(0, stats.AboutEdges);
    }

    // ───────────────────────────── lectores existentes y vistas ─────────────────────────────

    [Fact]
    public async Task The_default_readers_do_not_see_the_registry_and_the_existing_counts_do_not_change()
    {
        var (g, render, _) = await TemplateGraphAsync();
        using var _ = g;
        var codeLabels = "'Class', 'Method', 'File'";
        var before = await g.CountAsync($"SELECT COUNT(1) FROM nodes WHERE label IN ({codeLabels})");
        var docBefore = await g.ListAsync("SELECT label FROM (" + CompilerFactsFacets.DocCoverageSql.TrimEnd().TrimEnd(';') + ") ORDER BY label", r => r.GetString(0));
        var neighborsBefore = await NeighborsAsync(g, "fw.Support.Template.TinyTemplateRendered.RenderInternal");

        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord { Run = Run(), Findings = Registry() }, CancellationToken.None);

        Assert.Equal(before, await g.CountAsync($"SELECT COUNT(1) FROM nodes WHERE label IN ({codeLabels})"));
        Assert.Equal(docBefore, await g.ListAsync("SELECT label FROM (" + CompilerFactsFacets.DocCoverageSql.TrimEnd().TrimEnd(';') + ") ORDER BY label", r => r.GetString(0)));
        Assert.True(await g.CountAsync("SELECT COUNT(1) FROM edges WHERE type = 'ABOUT' AND target_id = @t", ("@t", render)) >= 1);

        // graph_neighbors: el hallazgo está ligado con ABOUT al método, que es un tipo de arista por defecto, y aun así no sale.
        Assert.Equal(neighborsBefore, await NeighborsAsync(g, "fw.Support.Template.TinyTemplateRendered.RenderInternal"));

        // graph_search sin etiqueta no lo encuentra; pidiendo la etiqueta, sí.
        var search = await SearchAsync(g.DatabasePath, "{\"name_pattern\":\"REG-001\"}");
        Assert.DoesNotContain("REG-001", search);
        var byLabel = await SearchAsync(g.DatabasePath, "{\"label\":\"Finding\",\"name_pattern\":\"REG-001\"}");
        Assert.Contains("REG-001", byLabel);
    }

    private async Task<string> NeighborsAsync(CbmTestGraph g, string qualifiedName)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.CbmDbPath, g.DatabasePath);
        return await new GraphNeighborsTool().ExecuteAsync($"{{\"qualified_name\":\"{qualifiedName}\"}}", context, "ChatAssistant", NullLogger.Instance);
    }

    private static async Task<string> SearchAsync(string databasePath, string args)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.CbmDbPath, databasePath);
        return await new GraphSearchTool().ExecuteAsync(args, context, "ChatAssistant", NullLogger.Instance);
    }

    [Fact]
    public async Task The_findings_view_filters_by_severity_kind_and_symbol_and_hides_stale_runs()
    {
        var (g, _, _) = await TemplateGraphAsync();
        using var _ = g;
        var findings = Registry();
        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord { Run = Run(), Findings = findings }, CancellationToken.None);

        var all = await DomainAsync("{\"view\":\"findings\",\"limit\":500}", g.DatabasePath);
        Assert.Equal(187, Rows(all, "| REG-"));
        Assert.True(all.IndexOf("REG-001", StringComparison.Ordinal) < all.IndexOf("| REG-183", StringComparison.Ordinal)); // P0 antes que los rechazados

        var p0 = await DomainAsync("{\"view\":\"findings\",\"severity\":\"P0\"}", g.DatabasePath);
        Assert.Equal(1, Rows(p0, "| REG-"));
        Assert.Contains("Base de la severidad", p0); // pocos resultados: se detallan, con el ancla
        Assert.Contains("Support/Template/TinyTemplateRendered.cs:109-115", p0);

        var security = await DomainAsync("{\"view\":\"findings\",\"kind\":\"security\",\"limit\":100}", g.DatabasePath);
        Assert.Equal(findings.Count(f => f.Kind == "security"), Rows(security, "| REG-"));

        var bySymbol = await DomainAsync("{\"view\":\"findings\",\"name\":\"TinyTemplateRendered\",\"limit\":100}", g.DatabasePath);
        Assert.Equal(findings.Count(f => f.Symbol is { } s && s.StartsWith("TinyTemplateRendered.", StringComparison.Ordinal)), Rows(bySymbol, "| REG-"));
        var exact = await DomainAsync("{\"view\":\"findings\",\"name\":\"TinyTemplateRendered.RenderInternal\",\"limit\":100}", g.DatabasePath);
        Assert.Contains("REG-001", exact);

        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord { Run = Run("run-2"), Findings = findings.Take(3).ToList() }, CancellationToken.None);
        var afterRerun = await DomainAsync("{\"view\":\"findings\",\"limit\":500}", g.DatabasePath);
        Assert.Equal(3, Rows(afterRerun, "| REG-"));
        Assert.Contains("obsoletos", afterRerun);
    }

    [Fact]
    public async Task The_llm_contracts_view_shows_the_contract_of_a_method_or_of_a_type()
    {
        var batch = Batch("event");
        var g = await EmptyGraphAsync();
        using var _ = g;
        await AddBatchNodesAsync(g, batch);
        await LlmRegistryGraphWriter.WriteAsync(g.DatabasePath, new LlmRegistryRecord { Run = Run(), Contracts = batch.Contracts }, CancellationToken.None);

        var list = await DomainAsync("{\"view\":\"llm_contracts\",\"limit\":500}", g.DatabasePath);
        Assert.Equal(batch.Contracts.Count, Rows(list, "| `Method`"));

        var typeName = batch.Contracts[0].Symbol[..batch.Contracts[0].Symbol.IndexOf('.')];
        var ofType = await DomainAsync($"{{\"view\":\"llm_contracts\",\"name\":\"{typeName}\",\"limit\":500}}", g.DatabasePath);
        Assert.Equal(batch.Contracts.Count(c => c.Symbol.StartsWith(typeName + ".", StringComparison.Ordinal)), Rows(ofType, "### `"));
        Assert.Contains(batch.Contracts[0].Does, ofType);

        var none = await DomainAsync("{\"view\":\"llm_contracts\",\"name\":\"NoExiste\"}", g.DatabasePath);
        Assert.Contains("Ningún método", none);
    }

    [Fact]
    public async Task All_real_batch_outputs_parse_with_every_member_contract()
    {
        var names = Directory.GetFiles(Path.Combine(ExperimentDir(), "out"), "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        Assert.Equal(14, names.Count);
        var batches = names.Select(Batch).ToList();
        Assert.Equal(912, batches.Sum(b => b.Contracts.Count));
        Assert.All(batches.SelectMany(b => b.Contracts), c => Assert.False(string.IsNullOrWhiteSpace(c.Does)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Writing_into_the_cbm_cache_database_is_refused()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codebase-memory-mcp", "x.db");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LlmRegistryGraphWriter.WriteAsync(cache, new LlmRegistryRecord { Run = Run() }, CancellationToken.None));
    }
}
