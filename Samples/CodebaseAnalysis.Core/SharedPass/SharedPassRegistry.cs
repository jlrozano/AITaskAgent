using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>
/// Persistencia del registro de la pasada compartida (D3): en el grafo, con el escritor del paquete C
/// (<see cref="LlmRegistryGraphWriter"/>: un <c>Run</c> con ámbito <see cref="Scope"/>, los hallazgos con sus anclas, los resúmenes de
/// ficheros y tipos y los contratos de los miembros), y en <see cref="FileName"/> de la carpeta de informes con el formato del
/// experimento (<c>entries</c>, <c>summary</c>, <c>notes</c>, más <c>openQuestions</c> y <c>run</c>), que
/// <see cref="LlmRegistryJson.ParseRegistry"/> vuelve a leer.
/// </summary>
public static class SharedPassRegistry
{
    /// <summary>
    /// Fichero del registro en la carpeta de informes. Empieza por <c>_</c>, como <c>_run_meta.json</c> y los artefactos de la
    /// preparación: no es un informe del catálogo.
    /// </summary>
    public const string FileName = "_shared_pass_registry.json";

    /// <summary>Ámbito de los Run de la pasada compartida: una pasada nueva marca <c>stale</c> lo de la anterior.</summary>
    public const string Scope = "shared-pass";

    public const string Producer = "shared-pass";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary><c>shared-pass-20261016T101500Z</c>.</summary>
    public static string NewRunId(DateTimeOffset at) => "shared-pass-" + at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Huella (12 hex del sha256) de cada plantilla que exista, por nombre.</summary>
    public static IReadOnlyDictionary<string, string> PromptVersions(ITemplateProvider templates, IEnumerable<string> names)
    {
        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (templates.GetTemplate(name) is { } text)
                versions[name] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        }
        return versions;
    }

    /// <summary>
    /// Lo que se escribe en el grafo: los hallazgos del registro, y de cada lote sus resúmenes de fichero y tipo y los contratos de
    /// sus miembros (los de <see cref="LlmRegistryJson.ParseBatchOutput"/> sobre <see cref="BatchReading.ToJson"/>), con el
    /// <c>input_hash</c> del paquete del lote.
    /// </summary>
    public static LlmRegistryRecord BuildRecord(ConsolidatedRegistry registry, IReadOnlyList<ConsolidationBatch> batches, LlmRunRecord run,
        IReadOnlyDictionary<string, string>? batchDescriptions = null)
    {
        var summaries = new List<LlmSummaryRecord>();
        var contracts = new List<LlmMemberContractRecord>();
        foreach (var batch in batches)
        {
            var output = LlmRegistryJson.ParseBatchOutput(batch.Reading.ToJson());
            var hash = batch.InputHash.Length > 0 ? batch.InputHash : null;
            summaries.AddRange(output.Summaries.Select(s => s with { Batch = batch.BatchId, InputHash = hash }));
            contracts.AddRange(output.Contracts.Select(c => c with { Batch = batch.BatchId, InputHash = hash }));
        }

        return new LlmRegistryRecord
        {
            Run = run,
            Batches = [.. batches.Select(b => new LlmBatchRecord { Id = b.BatchId, Description = batchDescriptions?.GetValueOrDefault(b.BatchId) ?? "" })],
            Findings = [.. registry.Entries.Select(ToRecord)],
            // Dos resúmenes con la misma clave en un Run chocarían en el qualified_name del nodo: se queda el primero.
            Summaries = [.. summaries.DistinctBy(s => (s.Level, s.Key))],
            Contracts = contracts
        };
    }

    public static LlmFindingRecord ToRecord(RegistryEntry e) => new()
    {
        Id = e.Id,
        ContentId = e.ContentId,
        Kind = e.Kind,
        Category = e.Category,
        Title = e.Title,
        Rationale = e.Rationale,
        Severity = e.Status == RegistryEntry.StatusRejected ? null : e.Severity,
        SeverityBasis = e.SeverityBasis,
        Status = e.Status,
        RejectReason = e.RejectReason,
        Confidence = e.Confidence,
        Reverified = e.Reverified,
        Anchor = Anchor(e.Anchor),
        Instances = [.. e.Instances.Select(Anchor)],
        Symbol = e.Symbol,
        SeedRef = e.SeedRef,
        Sources = e.Sources,
        Batch = e.Batch.Length > 0 ? e.Batch : null,
        InputHash = e.InputHash.Length > 0 ? e.InputHash : null
    };

    private static LlmAnchorRecord Anchor(ReadAnchor a) => new()
    {
        Path = a.Path,
        StartLine = a.StartLine,
        EndLine = a.EndLine,
        Quote = a.Quote,
        Validated = a.Validated
    };

    /// <summary>Lo que se cuenta de la ejecución en el <c>summary</c> del registro, además de los recuentos.</summary>
    public sealed record RunSummary
    {
        public string RunId { get; init; } = "";
        public int Batches { get; init; }
        public int ReaderCalls { get; init; }
        public decimal ReaderCostUsd { get; init; }
        public int CoverageGaps { get; init; }
        public int RejectedByAnchor { get; init; }
        public JudgeResult? Judge { get; init; }
        public string? StopReason { get; init; }
    }

    /// <summary>El JSON del registro (formato del experimento, ampliado).</summary>
    public static string ToJson(ConsolidatedRegistry registry, RunSummary? run = null)
    {
        static JsonObject Anchor(ReadAnchor a) => new() { ["path"] = a.Path, ["startLine"] = a.StartLine, ["endLine"] = a.EndLine, ["quote"] = a.Quote, ["validated"] = a.Validated };
        static JsonArray Strings(IEnumerable<string> items) => [.. items.Select(i => (JsonNode?)JsonValue.Create(i))];

        var entries = new JsonArray();
        foreach (var e in registry.Entries)
        {
            entries.Add(new JsonObject
            {
                ["id"] = e.Id,
                ["contentId"] = e.ContentId,
                ["kind"] = e.Kind,
                ["category"] = e.Category,
                ["title"] = e.Title,
                ["rationale"] = e.Rationale,
                ["severity"] = e.Status == RegistryEntry.StatusRejected ? null : e.Severity,
                ["severityBasis"] = e.SeverityBasis,
                ["status"] = e.Status,
                ["rejectReason"] = e.RejectReason,
                ["anchor"] = Anchor(e.Anchor),
                ["instances"] = new JsonArray([.. e.Instances.Select(i => (JsonNode?)Anchor(i))]),
                ["symbol"] = e.Symbol,
                ["seedRef"] = e.SeedRef,
                ["sources"] = Strings(e.Sources),
                ["reverified"] = e.Reverified,
                ["judgeVerdict"] = e.JudgeVerdict,
                ["confidence"] = e.Confidence,
                ["impact"] = e.Impact,
                ["likelihood"] = e.Likelihood,
                ["activationPath"] = e.ActivationPath,
                ["exposure"] = new JsonObject
                {
                    ["score"] = e.Exposure.Score,
                    ["node"] = e.Exposure.Node,
                    ["entryPoint"] = e.Exposure.EntryPoint,
                    ["reachable"] = e.Exposure.Reachable,
                    ["reachedFromEntryPoint"] = e.Exposure.ReachedFromEntryPoint,
                    ["transitiveFanIn"] = e.Exposure.TransitiveFanIn,
                    ["basis"] = e.Exposure.Basis
                },
                ["rubric"] = new JsonObject
                {
                    ["severity"] = e.Rubric.Severity,
                    ["score"] = e.Rubric.Score,
                    ["basis"] = e.Rubric.Basis,
                    ["cap"] = e.Rubric.CapReason
                },
                ["batch"] = e.Batch,
                ["inputHash"] = e.InputHash
            });
        }

        var confirmed = registry.Entries.Where(e => e.Status != RegistryEntry.StatusRejected).ToList();
        var summary = new JsonObject
        {
            ["total"] = registry.Entries.Count,
            ["confirmed"] = confirmed.Count,
            ["rejected"] = registry.Entries.Count - confirmed.Count,
            ["bySeverity"] = Counts(confirmed.Select(e => e.Severity ?? "")),
            ["byKind"] = Counts(confirmed.Select(e => e.Kind)),
            ["byCategory"] = Counts(confirmed.Select(e => e.Category)),
            ["mergedFrom"] = registry.MergedFrom,
            ["reverifiedCount"] = registry.Entries.Count(e => e.Reverified),
            ["openQuestions"] = registry.OpenQuestions.Count,
            ["openQuestionsAnswered"] = registry.OpenQuestions.Count(q => q.Status == OpenQuestionResolution.StatusAnswered),
            ["openQuestionsWithContext"] = registry.OpenQuestions.Count(q => q.Status == OpenQuestionResolution.StatusContext)
        };
        if (run != null)
        {
            summary["runId"] = run.RunId;
            summary["batches"] = run.Batches;
            summary["readerCalls"] = run.ReaderCalls;
            summary["readerCostUsd"] = run.ReaderCostUsd;
            summary["coverageGaps"] = run.CoverageGaps;
            summary["rejectedByAnchor"] = run.RejectedByAnchor;
            if (run.Judge is { } judge)
            {
                summary["judge"] = new JsonObject
                {
                    ["candidates"] = judge.Candidates,
                    ["reviewed"] = judge.Reviewed,
                    ["calls"] = judge.Calls,
                    ["costUsd"] = judge.CostUsd,
                    ["unpricedCalls"] = judge.UnpricedCalls,
                    ["stopReason"] = judge.StopReason
                };
            }
            summary["stopReason"] = run.StopReason;
        }

        var root = new JsonObject
        {
            ["entries"] = entries,
            ["summary"] = summary,
            ["notes"] = Strings(registry.Notes),
            ["openQuestions"] = new JsonArray([.. registry.OpenQuestions.Select(q => (JsonNode?)new JsonObject
            {
                ["batch"] = q.Batch, ["symbol"] = q.Symbol, ["question"] = q.Question, ["needs"] = Strings(q.Needs),
                ["status"] = q.Status, ["answer"] = q.Answer, ["answeredBy"] = q.AnsweredBy
            })])
        };
        return root.ToJsonString(Json) + "\n";
    }

    private static JsonObject Counts(IEnumerable<string> keys)
    {
        var obj = new JsonObject();
        foreach (var group in keys.Where(k => k.Length > 0).GroupBy(k => k).OrderBy(g => g.Key, StringComparer.Ordinal))
            obj[group.Key] = group.Count();
        return obj;
    }

    /// <summary>Escribe <see cref="FileName"/> en <paramref name="reportsPath"/>; devuelve el aviso si no se pudo, sin lanzar.</summary>
    public static string? TryWrite(string reportsPath, string json)
    {
        if (string.IsNullOrWhiteSpace(reportsPath))
            return "⚠️ No hay carpeta de informes: el registro de la pasada compartida no se guarda en disco.";
        var path = Path.Combine(reportsPath, FileName);
        try
        {
            Directory.CreateDirectory(reportsPath);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"⚠️ No se pudo escribir '{path}': {ex.Message}";
        }
    }
}
