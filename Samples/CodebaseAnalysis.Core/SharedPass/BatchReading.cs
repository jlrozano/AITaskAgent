using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>Cómo quedó un ancla tras la validación (<see cref="ReadingValidator"/>).</summary>
public enum AnchorCheck
{
    /// <summary>Aún sin validar (recién leída del JSON).</summary>
    Unchecked,

    /// <summary>La cita está en <c>[startLine-3, endLine+3]</c>.</summary>
    Valid,

    /// <summary>La cita estaba en otro punto del fichero: las líneas son las de ese punto y las originales quedan aparte.</summary>
    Relocated
}

/// <summary>Un fragmento literal del código: el ancla de un hallazgo, una de sus instancias o la de una relación.</summary>
public sealed record ReadAnchor
{
    public required string Path { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Quote { get; init; } = "";
    public AnchorCheck Check { get; init; }

    /// <summary>Las líneas que dio el modelo, si la validación las cambió (<see cref="AnchorCheck.Relocated"/>).</summary>
    public int? OriginalStartLine { get; init; }

    public int? OriginalEndLine { get; init; }

    /// <summary>Validada contra el código (lo que <c>LlmAnchorRecord.Validated</c> espera).</summary>
    public bool Validated => Check is AnchorCheck.Valid or AnchorCheck.Relocated;
}

public sealed record ReadFile
{
    public required string Path { get; init; }
    public string Role { get; init; } = "";
    public string Summary { get; init; } = "";
}

/// <summary>El contrato de un miembro tal como lo leyó el modelo.</summary>
public sealed record ReadMember
{
    /// <summary>El nombre con la firma resumida (<c>Render(string, object)</c>).</summary>
    public required string Name { get; init; }

    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Does { get; init; } = "";
    public string Inputs { get; init; } = "";
    public string Outputs { get; init; } = "";
    public string SideEffects { get; init; } = "";
    public string Throws { get; init; } = "";
}

public sealed record ReadType
{
    public required string Name { get; init; }
    public string Kind { get; init; } = "";
    public required string Path { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Purpose { get; init; } = "";
    public IReadOnlyList<string> Collaborators { get; init; } = [];
    public IReadOnlyList<ReadMember> Members { get; init; } = [];
}

/// <summary>Un hallazgo de un lote, sin consolidar y sin severidad (la fija D3).</summary>
public sealed record ReadFinding
{
    /// <summary>Id dentro del lote (<c>F1</c>); si el lote se partió, con el prefijo de la mitad (<c>s1.F1</c>).</summary>
    public required string LocalId { get; init; }

    /// <summary>Uno de <see cref="BatchReadingJson.FindingKinds"/>.</summary>
    public required string Kind { get; init; }

    public string Category { get; init; } = "";
    public required string Title { get; init; }
    public string Rationale { get; init; } = "";
    public required ReadAnchor Anchor { get; init; }
    public IReadOnlyList<ReadAnchor> Instances { get; init; } = [];

    /// <summary><c>Tipo.Miembro</c>.</summary>
    public string? Symbol { get; init; }

    /// <summary>Si <see cref="Symbol"/> existe en el grafo; null si no se pudo comprobar (grafo vacío o sin símbolo).</summary>
    public bool? SymbolInGraph { get; init; }

    public string? SeedRef { get; init; }
    public string ActivationPath { get; init; } = "";

    /// <summary>1-5.</summary>
    public int Impact { get; init; }

    /// <summary>1-5.</summary>
    public int Likelihood { get; init; }

    /// <summary>0-1.</summary>
    public double Confidence { get; init; }
}

public sealed record ReadSeedVerdict
{
    public required string Seed { get; init; }

    /// <summary>Uno de <see cref="BatchReadingJson.SeedVerdicts"/>.</summary>
    public required string Verdict { get; init; }

    public string Reason { get; init; } = "";
}

public sealed record ReadRelation
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Type { get; init; }
    public ReadAnchor? Anchor { get; init; }
}

public sealed record ReadOpenQuestion
{
    public string Symbol { get; init; } = "";
    public required string Question { get; init; }
    public IReadOnlyList<string> Needs { get; init; } = [];
}

/// <summary>
/// La lectura de un lote (D2): lo que devuelve el modelo con el esquema de <c>shared_pass_reader_system.md</c>, el mismo de las
/// salidas del experimento (<c>out/*.json</c>). <see cref="ToJson"/> la escribe en ese formato, que es el que lee
/// <c>LlmRegistryJson.ParseBatchOutput</c>.
/// </summary>
public sealed record BatchReading
{
    public required string Batch { get; init; }
    public IReadOnlyList<ReadFile> Files { get; init; } = [];
    public IReadOnlyList<ReadType> Types { get; init; } = [];
    public IReadOnlyList<ReadFinding> Findings { get; init; } = [];
    public IReadOnlyList<ReadSeedVerdict> SeedVerdicts { get; init; } = [];
    public IReadOnlyList<ReadRelation> Relations { get; init; } = [];
    public IReadOnlyList<ReadOpenQuestion> OpenQuestions { get; init; } = [];

    public static BatchReading Empty(string batch) => new() { Batch = batch };

    /// <summary>
    /// <paramref name="update"/> (una corrección o lo que faltaba) sobre esta lectura: ficheros por ruta, tipos por ruta y nombre
    /// (los miembros se suman por nombre), hallazgos por <c>localId</c> y veredictos por semilla, el nuevo gana; relaciones y
    /// preguntas se añaden sin repetir.
    /// </summary>
    public BatchReading Merge(BatchReading update)
    {
        var files = new List<ReadFile>(Files);
        foreach (var file in update.Files)
            Upsert(files, file, f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));

        var types = new List<ReadType>(Types);
        foreach (var type in update.Types)
        {
            var index = types.FindIndex(t => SameType(t, type));
            if (index < 0)
            {
                types.Add(type);
                continue;
            }
            var old = types[index];
            var members = new List<ReadMember>(old.Members);
            foreach (var member in type.Members)
                Upsert(members, member, m => m.Name.Equals(member.Name, StringComparison.Ordinal));
            types[index] = old with
            {
                Kind = type.Kind.Length > 0 ? type.Kind : old.Kind,
                StartLine = type.StartLine > 0 ? type.StartLine : old.StartLine,
                EndLine = type.EndLine > 0 ? type.EndLine : old.EndLine,
                Purpose = type.Purpose.Length > 0 ? type.Purpose : old.Purpose,
                Collaborators = type.Collaborators.Count > 0 ? type.Collaborators : old.Collaborators,
                Members = members
            };
        }

        var findings = new List<ReadFinding>(Findings);
        foreach (var finding in update.Findings)
            Upsert(findings, finding, f => f.LocalId.Equals(finding.LocalId, StringComparison.Ordinal));

        var verdicts = new List<ReadSeedVerdict>(SeedVerdicts);
        foreach (var verdict in update.SeedVerdicts)
            Upsert(verdicts, verdict, v => v.Seed.Trim().Equals(verdict.Seed.Trim(), StringComparison.OrdinalIgnoreCase));

        return this with
        {
            Files = files,
            Types = types,
            Findings = findings,
            SeedVerdicts = verdicts,
            Relations = [.. Relations.Concat(update.Relations).DistinctBy(r => (r.From, r.To, r.Type))],
            OpenQuestions = [.. OpenQuestions.Concat(update.OpenQuestions).DistinctBy(q => (q.Symbol, q.Question))]
        };
    }

    internal static bool SameType(ReadType a, ReadType b) =>
        a.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase) && a.Name.Equals(b.Name, StringComparison.Ordinal);

    private static void Upsert<T>(List<T> list, T item, Predicate<T> same)
    {
        var index = list.FindIndex(same);
        if (index < 0)
            list.Add(item);
        else
            list[index] = item;
    }

    /// <summary>El JSON del experimento (<c>out/{lote}.json</c>), en camelCase y con las anclas sin su estado de validación.</summary>
    public string ToJson()
    {
        static JsonObject Anchor(ReadAnchor a) => new() { ["path"] = a.Path, ["startLine"] = a.StartLine, ["endLine"] = a.EndLine, ["quote"] = a.Quote };
        static JsonArray Strings(IEnumerable<string> items) => [.. items.Select(i => (JsonNode?)JsonValue.Create(i))];

        var root = new JsonObject
        {
            ["batch"] = Batch,
            ["files"] = new JsonArray([.. Files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["role"] = f.Role, ["summary"] = f.Summary })]),
            ["types"] = new JsonArray([.. Types.Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = t.Name, ["kind"] = t.Kind, ["path"] = t.Path, ["startLine"] = t.StartLine, ["endLine"] = t.EndLine,
                ["purpose"] = t.Purpose, ["collaborators"] = Strings(t.Collaborators),
                ["members"] = new JsonArray([.. t.Members.Select(m => (JsonNode?)new JsonObject
                {
                    ["name"] = m.Name, ["startLine"] = m.StartLine, ["endLine"] = m.EndLine, ["does"] = m.Does, ["inputs"] = m.Inputs,
                    ["outputs"] = m.Outputs, ["sideEffects"] = m.SideEffects, ["throws"] = m.Throws
                })])
            })]),
            ["findings"] = new JsonArray([.. Findings.Select(f => (JsonNode?)new JsonObject
            {
                ["localId"] = f.LocalId, ["kind"] = f.Kind, ["category"] = f.Category, ["title"] = f.Title, ["rationale"] = f.Rationale,
                ["anchor"] = Anchor(f.Anchor), ["instances"] = new JsonArray([.. f.Instances.Select(i => (JsonNode?)Anchor(i))]),
                ["symbol"] = f.Symbol, ["seedRef"] = f.SeedRef, ["activationPath"] = f.ActivationPath,
                ["impact"] = f.Impact, ["likelihood"] = f.Likelihood, ["confidence"] = f.Confidence
            })]),
            ["seedVerdicts"] = new JsonArray([.. SeedVerdicts.Select(v => (JsonNode?)new JsonObject { ["seed"] = v.Seed, ["verdict"] = v.Verdict, ["reason"] = v.Reason })]),
            ["relations"] = new JsonArray([.. Relations.Select(r =>
            {
                var o = new JsonObject { ["from"] = r.From, ["to"] = r.To, ["type"] = r.Type };
                if (r.Anchor != null)
                    o["anchor"] = Anchor(r.Anchor);
                return (JsonNode?)o;
            })]),
            ["openQuestions"] = new JsonArray([.. OpenQuestions.Select(q => (JsonNode?)new JsonObject
            {
                ["symbol"] = q.Symbol, ["question"] = q.Question, ["needs"] = Strings(q.Needs)
            })])
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

/// <summary>Un error de la respuesta: lo que se le devuelve al modelo en el reintento y, si persiste, lo que queda como hueco.</summary>
public sealed record ReaderIssue(ReaderIssueKind Kind, string Message, string? File = null, string? Target = null)
{
    /// <summary>Los que son de cobertura (lo que falta, no lo que está mal).</summary>
    public bool IsCoverage => Kind is ReaderIssueKind.MissingFile or ReaderIssueKind.MissingType or ReaderIssueKind.MissingMember or ReaderIssueKind.MissingSeedVerdict;
}

public enum ReaderIssueKind
{
    /// <summary>La respuesta no tiene un objeto JSON legible.</summary>
    InvalidJson,

    /// <summary>Un elemento no cumple el esquema y se descarta.</summary>
    Schema,

    MissingFile,
    MissingType,
    MissingMember,
    MissingSeedVerdict,

    /// <summary>El ancla principal de un hallazgo no se encuentra en el código: el hallazgo se rechaza.</summary>
    Anchor
}

/// <summary>
/// Lectura tolerante de la respuesta del modelo (primer escalón de la validación de D2): quita las vallas <c>```json</c> y el texto
/// de antes y de después, toma el primer objeto JSON equilibrado y lo lee elemento a elemento. Un elemento que no cumple el
/// esquema se descarta con un <see cref="ReaderIssueKind.Schema"/>; el resto se conserva. Los números que llegan como cadena se
/// aceptan.
/// </summary>
public static class BatchReadingJson
{
    public static readonly IReadOnlyList<string> FindingKinds = ["bug", "debt", "security"];
    public static readonly IReadOnlyList<string> SeedVerdicts = ["confirmed", "false_positive", "needs_context"];

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>El objeto JSON de <paramref name="text"/>, o null con el motivo en <paramref name="error"/>.</summary>
    public static JsonObject? ExtractObject(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "la respuesta está vacía";
            return null;
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            error = "la respuesta no contiene ningún objeto JSON";
            return null;
        }

        var end = BalancedEnd(text, start);
        if (end < 0)
        {
            error = "el objeto JSON está incompleto (sin cerrar)";
            return null;
        }

        try
        {
            if (JsonNode.Parse(text[start..(end + 1)], documentOptions: DocumentOptions) is JsonObject obj)
            {
                _ = obj.Count; // JsonObject es perezoso: una clave repetida falla aquí
                return obj;
            }
            error = "la raíz del JSON no es un objeto";
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            error = $"JSON inválido: {ex.Message}";
        }
        return null;
    }

    /// <summary>Índice de la llave que cierra la que abre en <paramref name="start"/>, respetando cadenas y escapes; -1 si no cierra.</summary>
    private static int BalancedEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
            }
        }
        return -1;
    }

    /// <summary>
    /// La lectura de <paramref name="text"/> con el id <paramref name="batch"/> (el del JSON se ignora: manda el del lote), o null si
    /// no hay JSON legible. <paramref name="issues"/> recibe un error por JSON ilegible o por elemento descartado.
    /// </summary>
    public static BatchReading? Parse(string? text, string batch, List<ReaderIssue> issues)
    {
        if (ExtractObject(text, out var error) is not { } root)
        {
            issues.Add(new ReaderIssue(ReaderIssueKind.InvalidJson, $"La respuesta no se pudo leer: {error}. Devuelve un único objeto JSON válido."));
            return null;
        }

        var files = new List<ReadFile>();
        foreach (var (node, i) in Items(root, "files"))
        {
            if (Str(node, "path") is not { Length: > 0 } path)
            {
                issues.Add(Schema($"files[{i}] no tiene 'path'."));
                continue;
            }
            files.Add(new ReadFile { Path = NormalizePath(path), Role = Str(node, "role") ?? "", Summary = Str(node, "summary") ?? "" });
        }

        var types = new List<ReadType>();
        foreach (var (node, i) in Items(root, "types"))
        {
            if (Str(node, "name") is not { Length: > 0 } name || Str(node, "path") is not { Length: > 0 } path)
            {
                issues.Add(Schema($"types[{i}] no tiene 'name' o 'path'."));
                continue;
            }
            var members = new List<ReadMember>();
            foreach (var (m, j) in Items(node, "members"))
            {
                if (Str(m, "name") is not { Length: > 0 } member)
                {
                    issues.Add(Schema($"El miembro {j} del tipo {name} no tiene 'name'.", NormalizePath(path)));
                    continue;
                }
                members.Add(new ReadMember
                {
                    Name = member.Trim(), StartLine = Int(m, "startLine") ?? 0, EndLine = Int(m, "endLine") ?? 0,
                    Does = Str(m, "does") ?? "", Inputs = Str(m, "inputs") ?? "", Outputs = Str(m, "outputs") ?? "",
                    SideEffects = Str(m, "sideEffects") ?? "", Throws = Str(m, "throws") ?? ""
                });
            }
            types.Add(new ReadType
            {
                Name = name.Trim(), Kind = Str(node, "kind") ?? "", Path = NormalizePath(path),
                StartLine = Int(node, "startLine") ?? 0, EndLine = Int(node, "endLine") ?? 0,
                Purpose = Str(node, "purpose") ?? "", Collaborators = Strings(node, "collaborators"), Members = members
            });
        }

        var findings = new List<ReadFinding>();
        var localIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, i) in Items(root, "findings"))
        {
            var id = Str(node, "localId") is { Length: > 0 } given ? given.Trim() : $"F?{i + 1}";
            var label = $"El hallazgo {id}";
            var kind = Str(node, "kind")?.Trim().ToLowerInvariant();
            var anchor = Anchor(node["anchor"]);
            var (impact, likelihood, confidence) = (Int(node, "impact"), Int(node, "likelihood"), Dbl(node, "confidence"));
            var problems = new List<string>();
            if (Str(node, "localId") is not { Length: > 0 })
                problems.Add("no tiene 'localId'");
            else if (!localIds.Add(id))
                problems.Add("repite un 'localId' de otro hallazgo");
            if (kind == null || !FindingKinds.Contains(kind))
                problems.Add($"tiene 'kind' = '{kind}' (debe ser {string.Join(", ", FindingKinds)})");
            if (Str(node, "title") is not { Length: > 0 })
                problems.Add("no tiene 'title'");
            if (anchor == null)
                problems.Add("no tiene 'anchor' con 'path', 'startLine', 'endLine' y 'quote'");
            if (impact is not (>= 1 and <= 5))
                problems.Add("tiene 'impact' fuera de 1-5");
            if (likelihood is not (>= 1 and <= 5))
                problems.Add("tiene 'likelihood' fuera de 1-5");
            if (confidence is not (>= 0 and <= 1))
                problems.Add("tiene 'confidence' fuera de 0-1");
            if (problems.Count > 0)
            {
                issues.Add(Schema($"{label} {string.Join("; ", problems)}.", anchor?.Path, id));
                continue;
            }

            findings.Add(new ReadFinding
            {
                LocalId = id, Kind = kind!, Category = Str(node, "category") ?? "", Title = Str(node, "title")!, Rationale = Str(node, "rationale") ?? "",
                Anchor = anchor!, Instances = [.. Items(node, "instances").Select(x => Anchor(x.Node)).OfType<ReadAnchor>()],
                Symbol = Str(node, "symbol") is { Length: > 0 } symbol ? symbol.Trim() : null,
                SeedRef = Str(node, "seedRef") is { Length: > 0 } seed && !seed.Equals("null", StringComparison.OrdinalIgnoreCase) ? seed : null,
                ActivationPath = Str(node, "activationPath") ?? "", Impact = impact!.Value, Likelihood = likelihood!.Value, Confidence = confidence!.Value
            });
        }

        var verdicts = new List<ReadSeedVerdict>();
        foreach (var (node, i) in Items(root, "seedVerdicts"))
        {
            var verdict = Str(node, "verdict")?.Trim().ToLowerInvariant();
            if (Str(node, "seed") is not { Length: > 0 } seed || verdict == null || !SeedVerdicts.Contains(verdict))
            {
                issues.Add(Schema($"seedVerdicts[{i}] no tiene 'seed' o su 'verdict' no es {string.Join(", ", SeedVerdicts)}."));
                continue;
            }
            verdicts.Add(new ReadSeedVerdict { Seed = seed.Trim(), Verdict = verdict, Reason = Str(node, "reason") ?? "" });
        }

        var relations = new List<ReadRelation>();
        foreach (var (node, i) in Items(root, "relations"))
        {
            if (Str(node, "from") is not { Length: > 0 } from || Str(node, "to") is not { Length: > 0 } to || Str(node, "type") is not { Length: > 0 } type)
            {
                issues.Add(Schema($"relations[{i}] no tiene 'from', 'to' o 'type'."));
                continue;
            }
            relations.Add(new ReadRelation { From = from, To = to, Type = type, Anchor = Anchor(node["anchor"]) });
        }

        var questions = new List<ReadOpenQuestion>();
        foreach (var (node, _) in Items(root, "openQuestions"))
        {
            if (Str(node, "question") is { Length: > 0 } question)
                questions.Add(new ReadOpenQuestion { Symbol = Str(node, "symbol") ?? "", Question = question, Needs = Strings(node, "needs") });
        }

        return new BatchReading
        {
            Batch = batch, Files = files, Types = types, Findings = findings, SeedVerdicts = verdicts, Relations = relations, OpenQuestions = questions
        };
    }

    private static ReaderIssue Schema(string message, string? file = null, string? target = null) =>
        new(ReaderIssueKind.Schema, message, file, target);

    /// <summary>Barras normales, sin <c>./</c> inicial.</summary>
    internal static string NormalizePath(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        return p;
    }

    private static ReadAnchor? Anchor(JsonNode? node)
    {
        if (node is not JsonObject o || Str(o, "path") is not { Length: > 0 } path || Str(o, "quote") is not { } quote || quote.Trim().Length == 0)
            return null;
        return new ReadAnchor { Path = NormalizePath(path), StartLine = Int(o, "startLine") ?? 0, EndLine = Int(o, "endLine") ?? 0, Quote = quote };
    }

    private static IEnumerable<(JsonObject Node, int Index)> Items(JsonNode? parent, string key) =>
        (parent as JsonObject)?[key] is JsonArray array ? array.Select((n, i) => (n as JsonObject, i)).Where(x => x.Item1 != null).Select(x => (x.Item1!, x.i)) : [];

    private static string? Str(JsonNode? parent, string key) => (parent as JsonObject)?[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() is JsonValueKind.Number => v.ToJsonString(),
        _ => null
    };

    private static int? Int(JsonNode? parent, string key) => (parent as JsonObject)?[key] switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null
    };

    private static double? Dbl(JsonNode? parent, string key) => (parent as JsonObject)?[key] switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) && double.IsFinite(d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
        _ => null
    };

    private static List<string> Strings(JsonNode? parent, string key) => (parent as JsonObject)?[key] switch
    {
        JsonArray a => [.. a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : v.ToJsonString()).Where(s => s.Length > 0)],
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => [s],
        _ => []
    };
}
