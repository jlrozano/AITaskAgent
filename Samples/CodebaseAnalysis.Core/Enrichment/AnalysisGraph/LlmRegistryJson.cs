using System.Text.Json;

namespace CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

/// <summary>Lo que se lee de la salida de la lectura de un lote (<c>out/{lote}.json</c>).</summary>
public sealed record LlmBatchOutput
{
    public required string Batch { get; init; }
    public IReadOnlyList<LlmSummaryRecord> Summaries { get; init; } = [];
    public IReadOnlyList<LlmMemberContractRecord> Contracts { get; init; } = [];

    /// <summary>Los hallazgos del lote, aún sin consolidar: sin severidad y con estado <c>candidate</c>.</summary>
    public IReadOnlyList<LlmFindingRecord> Findings { get; init; } = [];
}

/// <summary>
/// Lector de los ficheros del registro de la pasada compartida: <c>registry.json</c> (hallazgos consolidados) y la salida de cada
/// lote (resúmenes de ficheros y tipos, contratos de miembros y hallazgos). Formato del experimento
/// <c>docs/experimentos/2026-10-16-pasada-compartida</c>; los campos que faltan quedan vacíos.
/// </summary>
public static class LlmRegistryJson
{
    /// <summary>Los hallazgos de un <c>registry.json</c> (<c>entries</c>).</summary>
    public static IReadOnlyList<LlmFindingRecord> ParseRegistry(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<LlmFindingRecord>();
        foreach (var e in entries.EnumerateArray())
        {
            var sources = Strings(e, "sources");
            result.Add(new LlmFindingRecord
            {
                Id = Text(e, "id") ?? "",
                Kind = Text(e, "kind") ?? "",
                Category = Text(e, "category") ?? "",
                Title = Text(e, "title") ?? "",
                Rationale = Text(e, "rationale") ?? "",
                Severity = Text(e, "severity"),
                SeverityBasis = Text(e, "severityBasis"),
                Status = Text(e, "status") ?? "confirmed",
                RejectReason = Text(e, "rejectReason"),
                Confidence = Number(e, "confidence"),
                Reverified = e.TryGetProperty("reverified", out var r) && r.ValueKind == JsonValueKind.True,
                Anchor = e.TryGetProperty("anchor", out var a) && a.ValueKind == JsonValueKind.Object ? Anchor(a) : null,
                Instances = Anchors(e, "instances"),
                Symbol = Text(e, "symbol"),
                SeedRef = Text(e, "seedRef"),
                InstanceOf = Text(e, "instanceOf"),
                ContentId = Text(e, "contentId"),
                Sources = sources,
                Batch = sources.Count > 0 && sources[0].IndexOf(':') is > 0 and var cut ? sources[0][..cut] : null
            });
        }
        return result;
    }

    /// <summary>La salida de la lectura de un lote.</summary>
    public static LlmBatchOutput ParseBatchOutput(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var batch = Text(root, "batch") ?? "";
        var summaries = new List<LlmSummaryRecord>();
        var contracts = new List<LlmMemberContractRecord>();
        var findings = new List<LlmFindingRecord>();

        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                if (Text(f, "path") is { Length: > 0 } path && Text(f, "summary") is { Length: > 0 } summary)
                    summaries.Add(new LlmSummaryRecord { Level = LlmSummaryRecord.LevelFile, Key = path, Text = summary, Path = path, Batch = batch });
            }
        }

        if (root.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in types.EnumerateArray())
            {
                var name = Text(t, "name") ?? "";
                var path = Text(t, "path") ?? "";
                if (name.Length == 0 || path.Length == 0)
                    continue;
                if (Text(t, "purpose") is { Length: > 0 } purpose)
                    summaries.Add(new LlmSummaryRecord
                    {
                        Level = LlmSummaryRecord.LevelSymbol, Key = name, Text = purpose, Path = path, Symbol = name,
                        StartLine = Int(t, "startLine"), EndLine = Int(t, "endLine"), Batch = batch
                    });
                if (!t.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var m in members.EnumerateArray())
                {
                    var member = Text(m, "name") ?? "";
                    if (member.Length == 0)
                        continue;
                    var cut = member.IndexOfAny(['(', '<', '[', ' ']);
                    contracts.Add(new LlmMemberContractRecord
                    {
                        Symbol = $"{name}.{(cut >= 0 ? member[..cut] : member)}",
                        Member = member,
                        Path = path,
                        StartLine = Int(m, "startLine"),
                        EndLine = Int(m, "endLine"),
                        Does = Text(m, "does") ?? "",
                        Inputs = Text(m, "inputs") ?? "",
                        Outputs = Text(m, "outputs") ?? "",
                        SideEffects = Text(m, "sideEffects") ?? "",
                        Throws = Text(m, "throws") ?? "",
                        Batch = batch
                    });
                }
            }
        }

        if (root.TryGetProperty("findings", out var raw) && raw.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in raw.EnumerateArray())
            {
                findings.Add(new LlmFindingRecord
                {
                    Id = $"{batch}:{Text(f, "localId")}",
                    Kind = Text(f, "kind") ?? "",
                    Category = Text(f, "category") ?? "",
                    Title = Text(f, "title") ?? "",
                    Rationale = Text(f, "rationale") ?? "",
                    Status = "candidate",
                    Confidence = Number(f, "confidence"),
                    Anchor = f.TryGetProperty("anchor", out var a) && a.ValueKind == JsonValueKind.Object ? Anchor(a) : null,
                    Instances = Anchors(f, "instances"),
                    Symbol = Text(f, "symbol"),
                    SeedRef = Text(f, "seedRef"),
                    Batch = batch
                });
            }
        }

        return new LlmBatchOutput { Batch = batch, Summaries = summaries, Contracts = contracts, Findings = findings };
    }

    private static LlmAnchorRecord Anchor(JsonElement a) => new()
    {
        Path = Text(a, "path") ?? "",
        StartLine = Int(a, "startLine"),
        EndLine = Int(a, "endLine"),
        Quote = Text(a, "quote") ?? ""
    };

    private static List<LlmAnchorRecord> Anchors(JsonElement e, string key) =>
        e.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(Anchor)]
            : [];

    private static string? Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double? Number(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static List<string> Strings(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];
}
