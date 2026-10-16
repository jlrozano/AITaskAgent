using System.Globalization;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Knowledge.Graph;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

/// <summary>La ejecución de la pasada que escribe el registro (nodo <c>Run</c>).</summary>
public sealed record LlmRunRecord
{
    public required string RunId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Modelos que intervinieron (lectura por lote, consolidación…).</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>Versión de cada prompt o plantilla usada (nombre → versión o huella).</summary>
    public IReadOnlyDictionary<string, string> PromptVersions { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Ámbito del Run: al escribir uno nuevo se marcan <c>stale</c> los nodos de los Run anteriores con el mismo ámbito. Dos
    /// ejecuciones de ámbitos distintos (otro subsistema, otra rama de la pasada) conviven sin pisarse.
    /// </summary>
    public string Scope { get; init; } = "";

    /// <summary>Quién escribe (<c>producer</c> de la procedencia).</summary>
    public string Producer { get; init; } = "shared-pass";
}

/// <summary>Un lote de la pasada (normalmente un subsistema).</summary>
public sealed record LlmBatchRecord
{
    public required string Id { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>El fragmento de código en que se apoya un hallazgo.</summary>
public sealed record LlmAnchorRecord
{
    public required string Path { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Quote { get; init; } = "";

    /// <summary>La cita literal se comprobó contra el código (lo decide quien escribe; el escritor no lee el fichero).</summary>
    public bool Validated { get; init; }
}

/// <summary>Un hallazgo del registro. Todo viene de fuera: el escritor no decide severidad ni estado.</summary>
public sealed record LlmFindingRecord
{
    /// <summary>Id estable del registro (<c>REG-001</c>).</summary>
    public required string Id { get; init; }

    public required string Kind { get; init; }
    public string Category { get; init; } = "";
    public required string Title { get; init; }
    public string Rationale { get; init; } = "";

    /// <summary>P0-P3; null si el hallazgo se rechazó.</summary>
    public string? Severity { get; init; }

    public string? SeverityBasis { get; init; }
    public string Status { get; init; } = "confirmed";
    public string? RejectReason { get; init; }
    public double? Confidence { get; init; }
    public bool Reverified { get; init; }
    public LlmAnchorRecord? Anchor { get; init; }
    public IReadOnlyList<LlmAnchorRecord> Instances { get; init; } = [];

    /// <summary><c>Tipo.Miembro</c> sobre el que trata el hallazgo.</summary>
    public string? Symbol { get; init; }

    /// <summary>Texto de la semilla del grafo (<c>Tipo.Miembro ISSUE[…]</c>, <c>DEAD? …</c>) de la que partió, si partió de una.</summary>
    public string? SeedRef { get; init; }

    /// <summary>Id del hallazgo del que este es una instancia (duplicado fusionado).</summary>
    public string? InstanceOf { get; init; }

    /// <summary>Ids locales de los hallazgos de lote de los que sale (<c>llm-configuration:F12</c>).</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    public string? Batch { get; init; }
    public string? InputHash { get; init; }

    /// <summary>Id estable por contenido (categoría, símbolo y cita), para reconocer el mismo hallazgo entre ejecuciones.</summary>
    public string? ContentId { get; init; }
}

/// <summary>Un resumen en prosa. <see cref="Level"/>: <c>symbol</c>, <c>file</c>, <c>subsystem</c> o <c>system</c>.</summary>
public sealed record LlmSummaryRecord
{
    public const string LevelSymbol = "symbol";
    public const string LevelFile = "file";
    public const string LevelSubsystem = "subsystem";
    public const string LevelSystem = "system";

    public required string Level { get; init; }

    /// <summary>Clave única dentro del Run y el nivel (la ruta, el símbolo, el id del subsistema).</summary>
    public required string Key { get; init; }

    public required string Text { get; init; }
    public string? Path { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string? Symbol { get; init; }
    public string? SubsystemId { get; init; }
    public string? Batch { get; init; }
    public string? InputHash { get; init; }
}

/// <summary>El contrato de un método o propiedad, tal como lo leyó el LLM.</summary>
public sealed record LlmMemberContractRecord
{
    /// <summary><c>Tipo.Miembro</c>.</summary>
    public required string Symbol { get; init; }

    /// <summary>El miembro con su firma resumida (<c>Render(string, object)</c>).</summary>
    public required string Member { get; init; }

    public required string Path { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Does { get; init; } = "";
    public string Inputs { get; init; } = "";
    public string Outputs { get; init; } = "";
    public string SideEffects { get; init; } = "";
    public string Throws { get; init; } = "";
    public string? Batch { get; init; }
    public string? InputHash { get; init; }
}

/// <summary>Todo lo que escribe una pasada: se reescribe entero por Run.</summary>
public sealed record LlmRegistryRecord
{
    public required LlmRunRecord Run { get; init; }
    public IReadOnlyList<LlmBatchRecord> Batches { get; init; } = [];
    public IReadOnlyList<LlmFindingRecord> Findings { get; init; } = [];
    public IReadOnlyList<LlmSummaryRecord> Summaries { get; init; } = [];
    public IReadOnlyList<LlmMemberContractRecord> Contracts { get; init; } = [];
}

/// <summary>Recuentos de <see cref="LlmRegistryGraphWriter.WriteAsync"/>.</summary>
public sealed record LlmRegistryStats
{
    public int Batches { get; init; }
    public int Findings { get; init; }
    public int Anchors { get; init; }

    /// <summary>Anclas cuyo fichero está en el grafo (arista <c>IN</c> hacia su nodo <c>File</c>).</summary>
    public int AnchorsInFile { get; init; }

    public int AboutEdges { get; init; }

    /// <summary>Hallazgos con símbolo que no se pudo resolver ni anclar a un nodo de código.</summary>
    public int FindingsUnlinked { get; init; }

    public int FromSeedEdges { get; init; }
    public int InstanceOfEdges { get; init; }
    public int Summaries { get; init; }
    public int SummariesLinked { get; init; }
    public int ContractsWritten { get; init; }
    public int ContractsUnresolved { get; init; }
    public int InBatchEdges { get; init; }

    /// <summary>Nodos de registro de una escritura anterior del mismo Run que se sustituyeron.</summary>
    public int PreviousRemoved { get; init; }

    /// <summary>Nodos de registro y métodos de Run anteriores del mismo ámbito que se marcaron <c>stale</c>.</summary>
    public int StaleMarked { get; init; }
}

/// <summary>
/// Paquete C del plan 2026-10-16: escribe en la copia del grafo lo que produce el LLM en la pasada compartida. Nodos <c>Run</c>,
/// <c>Batch</c>, <c>Finding</c>, <c>Anchor</c> y <c>Summary</c> (sin fichero, <c>origin = 'llm'</c>), aristas <c>AT</c>, <c>IN</c>,
/// <c>ABOUT</c>, <c>FROM_SEED</c>, <c>INSTANCE_OF</c>, <c>SUMMARIZES</c>, <c>IN_BATCH</c> e <c>IN_RUN</c>, y los contratos de los métodos como
/// propiedades <c>llm_*</c> del propio nodo de código.
/// </summary>
/// <remarks>
/// <para><b>Procedencia</b>: todo nodo y arista lleva <c>origin</c>, <c>producer</c>, <c>run_id</c> y, si se conocen, <c>file</c>,
/// <c>start_line</c>, <c>end_line</c> e <c>input_hash</c>. En los métodos, que son nodos de código con su propio <c>origin</c>, el
/// contrato lleva la procedencia con prefijo (<c>llm_origin</c>, <c>llm_run</c>, <c>llm_producer</c>…).</para>
/// <para><b>Idempotente por Run</b>: reescribir un <c>run_id</c> borra lo que escribió antes (nodos con sus aristas y las
/// <c>llm_*</c> de los métodos) y lo vuelve a escribir. Los nodos de otros Run con el mismo ámbito no se borran: se marcan
/// <c>stale</c> (<c>stale_by</c> = el Run nuevo; <c>llm_stale</c> en los métodos que no se reescriben).</para>
/// <para>El escritor no lee el código ni decide severidad: la cita literal y la severidad vienen validadas de fuera.</para>
/// </remarks>
public static class LlmRegistryGraphWriter
{
    /// <summary>Hallazgos o resúmenes con más de esto de texto se recortan en <c>docstring</c> (el texto completo va en su propiedad).</summary>
    public const int NameChars = 200;

    /// <summary>Candidatos que se enlazan como máximo cuando un símbolo casa con varias sobrecargas del mismo fichero.</summary>
    public const int MaxOverloadLinks = 5;

    private sealed record Provenance(string RunId, string Producer)
    {
        public JsonObject Node(string? file, int startLine, int endLine, string? inputHash)
        {
            var p = new JsonObject { ["producer"] = Producer, ["run_id"] = RunId };
            if (!string.IsNullOrEmpty(file))
                p["file"] = file;
            if (startLine > 0)
                p["start_line"] = startLine;
            if (endLine > 0)
                p["end_line"] = endLine;
            if (!string.IsNullOrEmpty(inputHash))
                p["input_hash"] = inputHash;
            return p;
        }

        public JsonObject Edge(string? file = null, int startLine = 0, int endLine = 0, string? inputHash = null)
        {
            var p = Node(file, startLine, endLine, inputHash);
            p["origin"] = AnalysisGraphLabels.OriginLlm;
            return p;
        }
    }

    /// <exception cref="InvalidOperationException">Si la BD es la de cbm.</exception>
    public static async Task<LlmRegistryStats> WriteAsync(string databasePath, LlmRegistryRecord registry, CancellationToken ct, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        var run = registry.Run;
        var prov = new Provenance(run.RunId, run.Producer);

        await KnowledgeGraphIngestor.WriteGate.WaitAsync(ct);
        try
        {
            using var store = await AnalysisGraphStore.OpenAsync(databasePath, ct);

            var previous = await RemovePreviousRunAsync(store, run.RunId, ct);
            var stale = await MarkOtherRunsStaleAsync(store, run, ct);

            var paths = registry.Findings.SelectMany(f => f.Instances.Select(a => a.Path).Append(f.Anchor?.Path ?? ""))
                .Concat(registry.Summaries.Select(s => s.Path ?? ""))
                .Concat(registry.Contracts.Select(c => c.Path))
                .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var locator = await CodeNodeLocator.LoadForCitationsAsync(store.Connection, store.Transaction, paths, ct);
            var symbols = new SymbolResolver(store, locator);

            var runNode = await store.InsertNodeAsync(AnalysisGraphLabels.Run, $"Run {run.RunId}", AnalysisGraphLabels.RunQualifiedName(run.RunId),
                AnalysisGraphLabels.LlmRegistryEnricher, when, RunProperties(run, prov, when), ct, AnalysisGraphLabels.OriginLlm);

            var batchNodes = new Dictionary<string, long>(StringComparer.Ordinal);
            async Task<long?> BatchAsync(string? id)
            {
                if (string.IsNullOrEmpty(id))
                    return null;
                if (batchNodes.TryGetValue(id, out var existing))
                    return existing;
                var description = registry.Batches.FirstOrDefault(b => b.Id == id)?.Description ?? "";
                var props = prov.Node(null, 0, 0, null);
                props["batch"] = id;
                props["description"] = description;
                var node = await store.InsertNodeAsync(AnalysisGraphLabels.Batch, $"Batch {id}", AnalysisGraphLabels.BatchQualifiedName(run.RunId, id),
                    AnalysisGraphLabels.LlmRegistryEnricher, when, props, ct, AnalysisGraphLabels.OriginLlm);
                await store.InsertEdgeAsync(node, runNode, AnalysisGraphLabels.InRun, AnalysisGraphLabels.LlmRegistryEnricher, prov.Edge(), ct);
                batchNodes[id] = node;
                return node;
            }
            foreach (var batch in registry.Batches)
                await BatchAsync(batch.Id);

            int inBatch = 0;
            async Task LinkBatchAsync(long node, string? batchId)
            {
                if (await BatchAsync(batchId) is { } batchNode
                    && await store.InsertEdgeAsync(node, batchNode, AnalysisGraphLabels.InBatch, AnalysisGraphLabels.LlmRegistryEnricher, prov.Edge(), ct))
                    inBatch++;
            }

            // Hallazgos y anclas.
            var findingNodes = new Dictionary<string, long>(StringComparer.Ordinal);
            int anchors = 0, anchorsInFile = 0, about = 0, unlinked = 0, fromSeed = 0, instanceOf = 0;
            foreach (var finding in registry.Findings.OrderBy(f => f.Id, StringComparer.Ordinal))
            {
                var anchor = finding.Anchor;
                var props = prov.Node(anchor?.Path, anchor?.StartLine ?? 0, anchor?.EndLine ?? 0, finding.InputHash);
                props["finding_id"] = finding.Id;
                if (!string.IsNullOrEmpty(finding.ContentId))
                    props["content_id"] = finding.ContentId;
                props["kind"] = finding.Kind;
                props["category"] = finding.Category;
                props["title"] = finding.Title;
                props["rationale"] = finding.Rationale;
                props["severity"] = finding.Severity;
                props["severity_basis"] = finding.SeverityBasis;
                props["status"] = finding.Status;
                props["reject_reason"] = finding.RejectReason;
                props["confidence"] = finding.Confidence;
                props["reverified"] = finding.Reverified;
                props["symbol"] = finding.Symbol;
                props["seed_ref"] = finding.SeedRef;
                props["instance_of"] = finding.InstanceOf;
                props["batch"] = finding.Batch;
                props["sources"] = AnalysisGraphStore.ToArray(finding.Sources);
                props["instances"] = finding.Instances.Count;
                props["docstring"] = finding.Title;
                var findingNode = await store.InsertNodeAsync(AnalysisGraphLabels.Finding, Truncate($"{finding.Id} · {finding.Title}"),
                    AnalysisGraphLabels.FindingQualifiedName(run.RunId, finding.Id), AnalysisGraphLabels.LlmRegistryEnricher, when, props, ct,
                    AnalysisGraphLabels.OriginLlm);
                findingNodes[finding.Id] = findingNode;
                await store.InsertEdgeAsync(findingNode, runNode, AnalysisGraphLabels.InRun, AnalysisGraphLabels.LlmRegistryEnricher, prov.Edge(), ct);
                await LinkBatchAsync(findingNode, finding.Batch);

                var all = (anchor is null ? [] : new[] { anchor }).Concat(finding.Instances).ToList();
                for (var i = 0; i < all.Count; i++)
                {
                    var a = all[i];
                    var role = anchor is not null && i == 0 ? "primary" : "instance";
                    var file = locator.LocateFile(a.Path);
                    var aProps = prov.Node(a.Path, a.StartLine, a.EndLine, finding.InputHash);
                    aProps["path"] = a.Path;
                    aProps["quote"] = a.Quote;
                    aProps["validated"] = a.Validated;
                    aProps["resolved"] = file != null;
                    aProps["role"] = role;
                    aProps["finding_id"] = finding.Id;
                    var anchorNode = await store.InsertNodeAsync(AnalysisGraphLabels.Anchor, $"{a.Path}:{a.StartLine}-{a.EndLine}",
                        AnalysisGraphLabels.AnchorQualifiedName(run.RunId, finding.Id, i), AnalysisGraphLabels.LlmRegistryEnricher, when, aProps, ct,
                        AnalysisGraphLabels.OriginLlm);
                    anchors++;
                    await store.InsertEdgeAsync(findingNode, anchorNode, AnalysisGraphLabels.At, AnalysisGraphLabels.LlmRegistryEnricher,
                        prov.Edge(a.Path, a.StartLine, a.EndLine, finding.InputHash).WithRole(role), ct);
                    if (file != null && await store.InsertEdgeAsync(anchorNode, file.Id, AnalysisGraphLabels.In, AnalysisGraphLabels.LlmRegistryEnricher,
                            prov.Edge(a.Path, a.StartLine, a.EndLine), ct))
                        anchorsInFile++;
                }

                // El símbolo; si no se resuelve, el nodo de código que contiene el rango del ancla principal.
                if (!string.IsNullOrWhiteSpace(finding.Symbol))
                {
                    var targets = await symbols.ResolveAsync(finding.Symbol, anchor?.Path, ct);
                    var resolution = "symbol";
                    if (targets.Count == 0 && anchor != null && locator.Locate(anchor.Path, anchor.StartLine) is { FileLevel: false } located)
                    {
                        targets = [located.Id];
                        resolution = "anchor";
                    }
                    var linked = false;
                    foreach (var target in targets)
                    {
                        var edge = prov.Edge(anchor?.Path, anchor?.StartLine ?? 0, anchor?.EndLine ?? 0, finding.InputHash);
                        edge["resolution"] = resolution;
                        if (await store.InsertEdgeAsync(findingNode, target, AnalysisGraphLabels.About, AnalysisGraphLabels.LlmRegistryEnricher, edge, ct))
                        {
                            about++;
                            linked = true;
                        }
                    }
                    if (!linked)
                        unlinked++;
                }

                if (!string.IsNullOrWhiteSpace(finding.SeedRef) && SeedSymbol(finding.SeedRef) is { Length: > 0 } seedSymbol)
                {
                    foreach (var target in await symbols.ResolveAsync(seedSymbol, anchor?.Path, ct))
                    {
                        var edge = prov.Edge();
                        edge["seed"] = finding.SeedRef;
                        if (await store.InsertEdgeAsync(findingNode, target, AnalysisGraphLabels.FromSeed, AnalysisGraphLabels.LlmRegistryEnricher, edge, ct))
                            fromSeed++;
                    }
                }
            }

            // INSTANCE_OF entre hallazgos del mismo Run (cuando el destino no está en el registro, la propiedad instance_of lo conserva).
            foreach (var finding in registry.Findings.Where(f => !string.IsNullOrEmpty(f.InstanceOf)))
            {
                if (findingNodes.TryGetValue(finding.Id, out var from) && findingNodes.TryGetValue(finding.InstanceOf!, out var to) && from != to
                    && await store.InsertEdgeAsync(from, to, AnalysisGraphLabels.InstanceOf, AnalysisGraphLabels.LlmRegistryEnricher, prov.Edge(), ct))
                    instanceOf++;
            }

            // Resúmenes.
            int summaries = 0, summariesLinked = 0;
            foreach (var summary in registry.Summaries.OrderBy(s => s.Level, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal))
            {
                var props = prov.Node(summary.Path, summary.StartLine, summary.EndLine, summary.InputHash);
                props["level"] = summary.Level;
                props["key"] = summary.Key;
                props["text"] = summary.Text;
                props["symbol"] = summary.Symbol;
                props["subsystem_id"] = summary.SubsystemId;
                props["batch"] = summary.Batch;
                props["docstring"] = summary.Text.Length > 400 ? summary.Text[..400] + "…" : summary.Text;
                var node = await store.InsertNodeAsync(AnalysisGraphLabels.Summary, Truncate($"{summary.Level} · {summary.Key}"),
                    AnalysisGraphLabels.SummaryQualifiedName(run.RunId, summary.Level, summary.Key), AnalysisGraphLabels.LlmRegistryEnricher, when, props, ct,
                    AnalysisGraphLabels.OriginLlm);
                summaries++;
                await store.InsertEdgeAsync(node, runNode, AnalysisGraphLabels.InRun, AnalysisGraphLabels.LlmRegistryEnricher, prov.Edge(), ct);
                await LinkBatchAsync(node, summary.Batch);

                var targets = await SummaryTargetsAsync(store, symbols, locator, summary, ct);
                foreach (var target in targets)
                {
                    if (await store.InsertEdgeAsync(node, target, AnalysisGraphLabels.Summarizes, AnalysisGraphLabels.LlmRegistryEnricher,
                            prov.Edge(summary.Path, summary.StartLine, summary.EndLine, summary.InputHash), ct))
                        summariesLinked++;
                }
            }

            // Contratos de los miembros: propiedades llm_* del nodo de código.
            int contracts = 0, contractsUnresolved = 0;
            var written = new HashSet<long>();
            foreach (var contract in registry.Contracts)
            {
                var target = await ResolveMemberAsync(symbols, locator, contract, ct);
                if (target is not { } id || !written.Add(id))
                {
                    if (target is null)
                        contractsUnresolved++;
                    continue;
                }
                await WriteContractAsync(store, id, contract, prov, ct);
                contracts++;
                await LinkBatchAsync(id, contract.Batch);
            }

            store.Commit();
            return new LlmRegistryStats
            {
                Batches = batchNodes.Count,
                Findings = findingNodes.Count,
                Anchors = anchors,
                AnchorsInFile = anchorsInFile,
                AboutEdges = about,
                FindingsUnlinked = unlinked,
                FromSeedEdges = fromSeed,
                InstanceOfEdges = instanceOf,
                Summaries = summaries,
                SummariesLinked = summariesLinked,
                ContractsWritten = contracts,
                ContractsUnresolved = contractsUnresolved,
                InBatchEdges = inBatch,
                PreviousRemoved = previous,
                StaleMarked = stale
            };
        }
        finally
        {
            KnowledgeGraphIngestor.WriteGate.Release();
        }
    }

    private static JsonObject RunProperties(LlmRunRecord run, Provenance prov, DateTimeOffset when)
    {
        var props = prov.Node(null, 0, 0, null);
        props["scope"] = run.Scope;
        props["started_at"] = (run.StartedAt ?? when).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        props["models"] = AnalysisGraphStore.ToArray(run.Models);
        var versions = new JsonObject();
        foreach (var (name, version) in run.PromptVersions.OrderBy(v => v.Key, StringComparer.Ordinal))
            versions[name] = version;
        props["prompt_versions"] = versions;
        return props;
    }

    private static string Truncate(string text) => text.Length > NameChars ? text[..NameChars] + "…" : text;

    /// <summary>El símbolo al principio del texto de una semilla (<c>Tipo.Miembro ISSUE[…]</c>, <c>DEAD?(medium) Tipo.Miembro (clase)</c>).</summary>
    internal static string SeedSymbol(string seed)
    {
        foreach (var token in seed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("DEAD?", StringComparison.Ordinal) || token.StartsWith("ISSUE", StringComparison.Ordinal))
                continue;
            var bare = token.Trim('(', ')', '`', ',');
            var cut = bare.IndexOfAny(['(', '[']);
            if (cut == 0)
                continue;
            return cut > 0 ? bare[..cut] : bare;
        }
        return "";
    }

    private static async Task<int> RemovePreviousRunAsync(AnalysisGraphStore store, string runId, CancellationToken ct)
    {
        var (nodes, _) = await EnrichmentMarks.RemoveNodesAsync(store.Connection, store.Transaction,
            $"SELECT id FROM nodes WHERE {RunIdOf("properties")} = @run AND {AnalysisGraphLabels.LlmNodePredicate("nodes")}",
            p => p.AddWithValue("@run", runId), ct);

        // Los contratos que este Run escribió en nodos de código.
        var methods = new List<(long Id, string Properties)>();
        using (var cmd = store.Command($"SELECT id, properties FROM nodes WHERE {PropertyOf("properties", "llm_run")} = @run"))
        {
            cmd.Parameters.AddWithValue("@run", runId);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                methods.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        foreach (var (id, raw) in methods)
            await SaveMethodPropertiesAsync(store, id, ClearContract(EnrichmentMarks.ParseProperties(raw)), ct);
        return nodes + methods.Count;
    }

    private static async Task<int> MarkOtherRunsStaleAsync(AnalysisGraphStore store, LlmRunRecord run, CancellationToken ct)
    {
        var others = new List<string>();
        using (var cmd = store.Command(
                   $"SELECT {RunIdOf("properties")} FROM nodes WHERE label = @label AND {PropertyOf("properties", "scope")} = @scope AND {RunIdOf("properties")} <> @run"))
        {
            cmd.Parameters.AddWithValue("@label", AnalysisGraphLabels.Run);
            cmd.Parameters.AddWithValue("@scope", run.Scope);
            cmd.Parameters.AddWithValue("@run", run.RunId);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                others.Add(reader.GetString(0));
        }

        var marked = 0;
        foreach (var other in others)
        {
            marked += await store.ExecuteAsync($"""
                UPDATE nodes SET properties = json_set(properties, '$.stale', json('true'), '$.stale_by', @by)
                WHERE {RunIdOf("properties")} = @other AND {AnalysisGraphLabels.LlmNodePredicate("nodes")}
                """, ct, p =>
            {
                p.AddWithValue("@by", run.RunId);
                p.AddWithValue("@other", other);
            });
            marked += await store.ExecuteAsync($"""
                UPDATE nodes SET properties = json_set(properties, '$.llm_stale', json('true'), '$.llm_stale_by', @by)
                WHERE {PropertyOf("properties", "llm_run")} = @other
                """, ct, p =>
            {
                p.AddWithValue("@by", run.RunId);
                p.AddWithValue("@other", other);
            });
        }
        return marked;
    }

    private static string RunIdOf(string column) => PropertyOf(column, "run_id");

    private static string PropertyOf(string column, string key) =>
        $"(CASE WHEN json_valid({column}) THEN json_extract({column}, '$.{key}') END)";

    private static JsonObject ClearContract(JsonObject properties)
    {
        foreach (var key in properties.Select(p => p.Key).Where(k => k.StartsWith(AnalysisGraphLabels.LlmPropertyPrefix, StringComparison.Ordinal)).ToList())
            properties.Remove(key);
        return properties;
    }

    private static async Task SaveMethodPropertiesAsync(AnalysisGraphStore store, long id, JsonObject properties, CancellationToken ct) =>
        await store.ExecuteAsync("UPDATE nodes SET properties = @props WHERE id = @id", ct, p =>
        {
            p.AddWithValue("@props", properties.ToJsonString());
            p.AddWithValue("@id", id);
        });

    private static async Task WriteContractAsync(AnalysisGraphStore store, long id, LlmMemberContractRecord contract, Provenance prov, CancellationToken ct)
    {
        string? raw;
        using (var cmd = store.Command("SELECT properties FROM nodes WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("@id", id);
            raw = await cmd.ExecuteScalarAsync(ct) as string;
        }
        var properties = ClearContract(EnrichmentMarks.ParseProperties(raw));
        properties["llm_does"] = contract.Does;
        properties["llm_inputs"] = contract.Inputs;
        properties["llm_outputs"] = contract.Outputs;
        properties["llm_side_effects"] = contract.SideEffects;
        properties["llm_throws"] = contract.Throws;
        properties["llm_origin"] = AnalysisGraphLabels.OriginLlm;
        properties["llm_producer"] = prov.Producer;
        properties["llm_run"] = prov.RunId;
        properties["llm_file"] = contract.Path;
        if (contract.StartLine > 0)
            properties["llm_start_line"] = contract.StartLine;
        if (contract.EndLine > 0)
            properties["llm_end_line"] = contract.EndLine;
        if (!string.IsNullOrEmpty(contract.InputHash))
            properties["llm_input_hash"] = contract.InputHash;
        if (!string.IsNullOrEmpty(contract.Batch))
            properties["llm_batch"] = contract.Batch;
        await SaveMethodPropertiesAsync(store, id, properties, ct);
    }

    /// <summary>
    /// El nodo de un miembro: el más específico que contiene su primera línea si es un miembro con ese nombre; si no, el símbolo
    /// <c>Tipo.Miembro</c> resuelto en el fichero. Un miembro trivial sin nodo (una propiedad automática) queda sin resolver: el
    /// contrato no se escribe en la clase.
    /// </summary>
    private static async Task<long?> ResolveMemberAsync(SymbolResolver symbols, CodeNodeLocator locator, LlmMemberContractRecord contract, CancellationToken ct)
    {
        var name = MemberBaseName(contract.Member);
        if (name.Length == 0)
            return null;
        if (contract.StartLine > 0 && locator.Locate(contract.Path, contract.StartLine) is { FileLevel: false } node && EndsWithMember(node.QualifiedName, name))
            return node.Id;
        var targets = await symbols.ResolveAsync(contract.Symbol, contract.Path, ct);
        return targets.Count == 1 ? targets[0] : null;
    }

    private static string MemberBaseName(string member)
    {
        var cut = member.IndexOfAny(['(', '<', '[', ' ']);
        return (cut >= 0 ? member[..cut] : member).Trim();
    }

    private static bool EndsWithMember(string qualifiedName, string name) =>
        qualifiedName.EndsWith("." + name, StringComparison.Ordinal) || qualifiedName.Contains("." + name + "(", StringComparison.Ordinal);

    private static async Task<List<long>> SummaryTargetsAsync(AnalysisGraphStore store, SymbolResolver symbols, CodeNodeLocator locator,
        LlmSummaryRecord summary, CancellationToken ct)
    {
        switch (summary.Level)
        {
            case LlmSummaryRecord.LevelFile:
                return summary.Path != null && locator.LocateFile(summary.Path) is { } file ? [file.Id] : [];
            case LlmSummaryRecord.LevelSubsystem:
            {
                if (string.IsNullOrEmpty(summary.SubsystemId))
                    return [];
                using var find = store.Command("SELECT id FROM nodes WHERE label = @label AND qualified_name = @qn LIMIT 1");
                find.Parameters.AddWithValue("@label", AnalysisGraphLabels.Subsystem);
                find.Parameters.AddWithValue("@qn", AnalysisGraphLabels.SubsystemQualifiedName(summary.SubsystemId));
                return await find.ExecuteScalarAsync(ct) is long id ? [id] : [];
            }
            case LlmSummaryRecord.LevelSymbol:
            {
                var name = summary.Symbol is { Length: > 0 } s ? s[(s.LastIndexOf('.') + 1)..] : "";
                if (summary.Path != null && summary.StartLine > 0 && name.Length > 0
                    && locator.Locate(summary.Path, summary.StartLine) is { FileLevel: false } node && EndsWithMember(node.QualifiedName, name))
                    return [node.Id];
                return summary.Symbol is { Length: > 0 } symbol ? [.. await symbols.ResolveAsync(symbol, summary.Path, ct)] : [];
            }
            default:
                return [];
        }
    }

    /// <summary>Resuelve <c>Tipo.Miembro</c> (o un nombre de tipo) a nodos de código; el fichero del ancla desempata sobrecargas y homónimos.</summary>
    /// <remarks>
    /// Los nodos de código se leen una vez y se indexan por el último segmento de su <c>qualified_name</c> (sin la lista de parámetros):
    /// cada símbolo mira solo sus candidatos, no todos los nodos. Casa como la consulta con <c>LIKE</c> de antes (que en SQLite no
    /// distingue mayúsculas en ASCII): nombre exacto, sufijo <c>.Símbolo</c> o sobrecarga <c>.Símbolo(</c>.
    /// </remarks>
    private sealed class SymbolResolver(AnalysisGraphStore store, CodeNodeLocator locator)
    {
        private readonly Dictionary<(string Symbol, string File), List<long>> _cache = [];
        private Dictionary<string, List<(long Id, string QualifiedName, string File)>>? _byLastSegment;

        /// <summary>El último segmento de un nombre cualificado, sin lo que sigue al primer <c>(</c>.</summary>
        internal static string LastSegment(string name)
        {
            var cut = name.IndexOf('(');
            var head = cut >= 0 ? name[..cut] : name;
            return head[(head.LastIndexOf('.') + 1)..];
        }

        private async Task<Dictionary<string, List<(long Id, string QualifiedName, string File)>>> IndexAsync(CancellationToken ct)
        {
            if (_byLastSegment != null)
                return _byLastSegment;
            var index = new Dictionary<string, List<(long, string, string)>>(StringComparer.OrdinalIgnoreCase);
            var excluded = string.Join(", ", CodeNodeLocator.NotCode.Concat(["File", "Module"]).Select(l => $"'{l}'"));
            using (var cmd = store.Command($"""
                SELECT n.id, n.qualified_name, replace(coalesce(nullif(n.file_path, ''), n.file, ''), '\', '/')
                FROM nodes n
                WHERE n.label NOT IN ({excluded}) AND NOT {AnalysisGraphLabels.LlmNodePredicate("n")} AND n.qualified_name IS NOT NULL
                ORDER BY n.id
                """))
            {
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var qn = reader.GetString(1);
                    var key = LastSegment(qn);
                    if (!index.TryGetValue(key, out var list))
                        index[key] = list = [];
                    list.Add((reader.GetInt64(0), qn, reader.GetString(2)));
                }
            }
            return _byLastSegment = index;
        }

        public async Task<List<long>> ResolveAsync(string symbol, string? hintPath, CancellationToken ct)
        {
            symbol = symbol.Trim();
            if (symbol.Length == 0)
                return [];
            var hint = hintPath != null ? locator.ResolveFile(hintPath) ?? "" : "";
            if (_cache.TryGetValue((symbol, hint), out var cached))
                return cached;

            var index = await IndexAsync(ct);
            var suffix = "." + symbol;
            var overload = suffix + "(";
            var matches = (index.GetValueOrDefault(LastSegment(symbol)) ?? [])
                .Where(n => string.Equals(n.QualifiedName, symbol, StringComparison.Ordinal)
                    || n.QualifiedName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    || n.QualifiedName.Contains(overload, StringComparison.OrdinalIgnoreCase))
                .Select(n => (n.Id, n.File))
                .ToList();

            var inFile = hint.Length == 0 ? [] : matches.Where(m => string.Equals(m.File, hint, StringComparison.OrdinalIgnoreCase)).ToList();
            List<long> result = inFile.Count > 0 ? inFile.Select(m => m.Id).Take(MaxOverloadLinks).ToList()
                : matches.Count == 1 ? [matches[0].Id]
                : [];
            _cache[(symbol, hint)] = result;
            return result;
        }
    }

    private static JsonObject WithRole(this JsonObject edge, string role)
    {
        edge["role"] = role;
        return edge;
    }
}
