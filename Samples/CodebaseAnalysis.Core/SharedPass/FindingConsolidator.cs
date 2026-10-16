using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment.AnalysisGraph;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>La lectura válida de un lote, como entra en la consolidación (D3).</summary>
public sealed record ConsolidationBatch(string BatchId, BatchReading Reading, string InputHash = "")
{
    /// <summary>Un lote por cada uno de los de la etapa de lectura, en su orden.</summary>
    public static IReadOnlyList<ConsolidationBatch> From(SharedPassReadResult read) =>
        [.. read.Batches.Select(b => new ConsolidationBatch(b.BatchId, b.Reading, b.InputHash))];
}

/// <summary>
/// La exposición de un hallazgo según el grafo (1-5; <see cref="SeverityRubric.NeutralExposure"/> si su ancla no tiene nodo): lo que
/// la rúbrica multiplica por el impacto y la probabilidad del lector. Los campos son los hechos en que se basa, null si el grafo
/// no los trae.
/// </summary>
public sealed record ExposureAssessment
{
    public int Score { get; init; } = SeverityRubric.NeutralExposure;

    /// <summary>Nombre cualificado (sin el proyecto) del nodo del ancla; null si no se encontró.</summary>
    public string? Node { get; init; }

    public bool? EntryPoint { get; init; }

    /// <summary><c>is_reachable</c> del enriquecedor de código muerto.</summary>
    public bool? Reachable { get; init; }

    /// <summary>Algún llamador transitivo es punto de entrada.</summary>
    public bool ReachedFromEntryPoint { get; init; }

    /// <summary>Llamadores transitivos distintos (por <c>CALLS</c>/<c>USAGE</c> y a través de <c>OVERRIDES</c>/<c>IMPLEMENTS</c>).</summary>
    public int? TransitiveFanIn { get; init; }

    public string? Accessibility { get; init; }
    public IReadOnlyList<string> SensitiveCategories { get; init; } = [];
    public IReadOnlyList<string> ConcurrencyMarkers { get; init; } = [];

    /// <summary>Una línea: el factor que fija la exposición.</summary>
    public string Basis { get; init; } = "";
}

/// <summary>La severidad que propone la rúbrica para un hallazgo, con su base.</summary>
/// <param name="Severity">P0-P3.</param>
/// <param name="Score"><c>impact × likelihood × exposición</c> (1-125).</param>
/// <param name="Basis">Una línea con la cuenta, la exposición y el tope si lo hubo.</param>
/// <param name="CapReason">Por qué se topó (activación no por defecto o uso erróneo, o deuda); null si no se topó.</param>
/// <param name="ActivationCapped">El tope fue el de la activación (el juez puede levantarlo diciendo que la activación es la de por defecto).</param>
public sealed record RubricResult(string Severity, int Score, string Basis, string? CapReason, bool ActivationCapped);

/// <summary>
/// La rúbrica de severidad de D3, determinista. <c>S = impact × likelihood × exposición</c>, cada factor en 1-5 (impacto y
/// probabilidad del lector; exposición de <see cref="GraphExposure"/>). P0 si <c>S ≥ 60</c> e impacto ≥ 4; P1 si <c>S ≥ 36</c> e
/// impacto ≥ 3; P2 si <c>S ≥ 18</c>; si no, P3. Con la exposición neutra (3) eso es: P1 desde <c>impact × likelihood = 12</c>, P2
/// desde 6 y P0 solo con exposición ≥ 4.
/// </summary>
/// <remarks>
/// Topes, después de la cuenta: (1) si el <c>activationPath</c> exige una configuración no por defecto o un uso erróneo de la API
/// (<see cref="NonDefaultActivationMarkers"/>, <see cref="MisuseActivationMarkers"/>, sobre el texto en minúsculas y sin tildes), como
/// mucho P2; (2) la deuda (<c>kind = debt</c>) no tiene impacto en fallos: como mucho P2, y P3 con impacto ≤ 2. La detección de la
/// activación por texto es una aproximación: el juez la confirma o la levanta con el código delante.
/// </remarks>
public static class SeverityRubric
{
    public const int NeutralExposure = 3;
    public const int P0Score = 60;
    public const int P1Score = 36;
    public const int P2Score = 18;

    public static readonly IReadOnlyList<string> Severities = ["P0", "P1", "P2", "P3"];

    /// <summary>Frases de un camino de activación que exige una configuración que no es la de por defecto.</summary>
    public static readonly IReadOnlyList<string> NonDefaultActivationMarkers =
    [
        "no por defecto", "distinto del de por defecto", "distinta de la de por defecto", "no predeterminad", "no es el valor por defecto",
        "configuracion no estandar", "configuracion personalizada", "configuracion explicita", "configurado explicitamente",
        "si se activa", "si se habilita", "si se configura", "cuando se configura", "con la opcion activada", "opcion activada",
        "por api directa", "llamada directa a la api", "api directa",
        "non-default", "non default", "not the default", "opt-in", "explicitly configured", "when configured", "if enabled", "direct api"
    ];

    /// <summary>Frases de un camino de activación que exige usar mal la API.</summary>
    public static readonly IReadOnlyList<string> MisuseActivationMarkers =
    [
        "uso incorrecto", "uso erroneo", "mal uso", "uso indebido", "error de autor", "error del llamador", "error de programacion",
        "incumple el contrato", "viola el contrato", "violando el contrato",
        "misuse", "incorrect use", "incorrect usage", "wrong usage", "contract violation", "programmer error"
    ];

    /// <summary>0 = P0 … 3 = P3; 4 para null o un valor desconocido.</summary>
    public static int Rank(string? severity) => severity switch { "P0" => 0, "P1" => 1, "P2" => 2, "P3" => 3, _ => 4 };

    /// <summary>La menos grave de <paramref name="severity"/> y <paramref name="max"/>.</summary>
    public static string Cap(string severity, string max) => Rank(severity) < Rank(max) ? max : severity;

    /// <summary>La severidad de <c>S</c> sin topes.</summary>
    public static string FromScore(int score, int impact) =>
        score >= P0Score && impact >= 4 ? "P0"
        : score >= P1Score && impact >= 3 ? "P1"
        : score >= P2Score ? "P2"
        : "P3";

    /// <summary>Por qué el camino de activación topa en P2; null si no hay marca.</summary>
    public static string? ActivationCapReason(string? activationPath)
    {
        if (string.IsNullOrWhiteSpace(activationPath))
            return null;
        var text = Fold(activationPath);
        if (NonDefaultActivationMarkers.FirstOrDefault(m => text.Contains(m, StringComparison.Ordinal)) is { } nonDefault)
            return $"la activación exige configuración no por defecto («{nonDefault}»)";
        if (MisuseActivationMarkers.FirstOrDefault(m => text.Contains(m, StringComparison.Ordinal)) is { } misuse)
            return $"la activación exige un uso erróneo de la API («{misuse}»)";
        return null;
    }

    public static RubricResult Evaluate(string kind, int impact, int likelihood, ExposureAssessment exposure, string? activationPath)
    {
        var i = Math.Clamp(impact, 1, 5);
        var l = Math.Clamp(likelihood, 1, 5);
        var e = Math.Clamp(exposure.Score, 1, 5);
        var score = i * l * e;
        var raw = FromScore(score, i);
        var severity = raw;
        string? cap = null;
        var activationCapped = false;

        if (ActivationCapReason(activationPath) is { } activation && Rank(severity) < Rank("P2"))
        {
            severity = "P2";
            cap = activation;
            activationCapped = true;
        }
        if (string.Equals(kind, "debt", StringComparison.OrdinalIgnoreCase))
        {
            var debtMax = i <= 2 ? "P3" : "P2";
            if (Rank(severity) < Rank(debtMax))
            {
                severity = debtMax;
                cap = cap == null ? $"deuda sin impacto en fallos (máximo {debtMax})" : $"{cap}; deuda sin impacto en fallos (máximo {debtMax})";
            }
        }

        var basis = string.Format(CultureInfo.InvariantCulture, "rúbrica: S = {0}×{1}×{2} = {3} → {4}; exposición {2}: {5}", i, l, e, score, raw,
            exposure.Basis.Length > 0 ? exposure.Basis : "sin datos");
        if (cap != null)
            basis += $"; tope {severity}: {cap}";
        return new RubricResult(severity, score, basis, cap, activationCapped);
    }

    /// <summary>Minúsculas, sin tildes y con los espacios colapsados.</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsWhiteSpace(c))
            {
                if (!space && sb.Length > 0)
                    sb.Append(' ');
                space = true;
                continue;
            }
            space = false;
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// La exposición de un hallazgo desde el grafo (<see cref="SharedPassGraph"/>): el nodo de código del ancla (el más específico que
/// contiene su primera línea, con preferencia por el miembro del símbolo) y sus hechos: <c>is_entry_point</c>, <c>is_reachable</c>,
/// llamadores transitivos (hasta <see cref="MaxVisited"/>), si alguno es punto de entrada, <c>accessibility</c>,
/// <c>sensitive_categories</c>/<c>sensitive_apis</c> y <c>concurrency_markers</c>.
/// </summary>
/// <remarks>
/// Puntuación: 5 punto de entrada; 1 si <c>is_reachable = false</c>; 4 alcanzable desde un punto de entrada o con ≥ 20 llamadores
/// transitivos; 3 con ≥ 5; 2 con ≥ 1; sin llamadores, 1 si es privado y 2 si no se sabe; una API pública no baja de 3 (la usan
/// desde fuera del repositorio). +1 (hasta 5) si el método toca APIs sensibles y el hallazgo es de seguridad, o si tiene marcas de
/// concurrencia y el hallazgo trata de concurrencia. Sin nodo: 3, neutra.
/// </remarks>
public sealed class GraphExposure
{
    public const int MaxVisited = 2000;

    private static readonly string[] CallerEdges = ["CALLS", "USAGE"];
    private static readonly string[] OverrideEdges = ["OVERRIDES", "IMPLEMENTS"];
    private static readonly string[] ConcurrencyWords = ["concurr", "thread", "race", "lock", "parallel", "hilo", "carrera", "paralel", "atomic"];

    private readonly SharedPassGraph _graph;
    private readonly Lazy<Dictionary<long, List<long>>> _callers;
    private readonly Dictionary<long, (int FanIn, bool FromEntry)> _reach = [];
    private readonly Dictionary<string, string?> _files = new(StringComparer.OrdinalIgnoreCase);

    public GraphExposure(SharedPassGraph graph)
    {
        _graph = graph;
        _callers = new Lazy<Dictionary<long, List<long>>>(BuildCallers);
    }

    public ExposureAssessment Assess(ReadFinding finding)
    {
        var node = Locate(finding.Anchor.Path, finding.Anchor.StartLine, finding.Symbol);
        if (node == null)
            return new ExposureAssessment { Score = SeverityRubric.NeutralExposure, Basis = "sin nodo del grafo en el ancla (neutra)" };

        var p = node.Properties;
        var entry = Flag(p["is_entry_point"]);
        var reachable = Flag(p["is_reachable"]);
        var access = p["accessibility"] is JsonValue av && av.TryGetValue<string>(out var a) ? a.Trim().ToLowerInvariant() : null;
        var sensitive = Strings(p["sensitive_categories"]);
        if (sensitive.Count == 0)
            sensitive = [.. (p["sensitive_apis"] as JsonArray ?? []).OfType<JsonObject>().Select(o => o["category"]?.ToString() ?? "").Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];
        var markers = (p["concurrency_markers"] as JsonArray ?? [])
            .Select(m => m is JsonObject o ? o["kind"]?.ToString() ?? "" : m?.ToString() ?? "").Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var (fanIn, fromEntry) = Reach(node.Id);

        int score;
        string basis;
        if (entry == true)
            (score, basis) = (5, "punto de entrada");
        else if (reachable == false)
            (score, basis) = (1, "no alcanzable (is_reachable = false)");
        else if (fromEntry)
            (score, basis) = (4, $"alcanzable desde un punto de entrada ({fanIn} llamadores transitivos)");
        else if (fanIn >= 20)
            (score, basis) = (4, $"{fanIn} llamadores transitivos");
        else if (fanIn >= 5)
            (score, basis) = (3, $"{fanIn} llamadores transitivos");
        else if (fanIn >= 1)
            (score, basis) = (2, $"{fanIn} llamador(es) transitivo(s)");
        else if (access == "private")
            (score, basis) = (1, "privado y sin llamadores");
        else
            (score, basis) = (2, "sin llamadores en el grafo");

        if (entry != true && reachable != false && access is "public" or "protected" && score < 3)
            (score, basis) = (3, $"{basis}; API {access}");

        var text = SeverityRubric.Fold($"{finding.Category} {finding.Title}");
        if (sensitive.Count > 0 && string.Equals(finding.Kind, "security", StringComparison.OrdinalIgnoreCase) && score < 5)
            (score, basis) = (score + 1, $"{basis}; APIs sensibles ({string.Join(", ", sensitive)})");
        else if (markers.Count > 0 && ConcurrencyWords.Any(w => text.Contains(w, StringComparison.Ordinal)) && score < 5)
            (score, basis) = (score + 1, $"{basis}; concurrencia ({string.Join(", ", markers)})");

        return new ExposureAssessment
        {
            Score = score,
            Node = _graph.ShortName(node.QualifiedName),
            EntryPoint = entry,
            Reachable = reachable,
            ReachedFromEntryPoint = fromEntry,
            TransitiveFanIn = fanIn,
            Accessibility = access,
            SensitiveCategories = sensitive,
            ConcurrencyMarkers = markers,
            Basis = $"{_graph.ShortName(node.QualifiedName)}: {basis}"
        };
    }

    /// <summary>
    /// El nodo de un ancla: entre los tipos y métodos del fichero que contienen <paramref name="line"/>, el miembro del símbolo si
    /// está y si no el de rango más corto; sin ninguno que contenga la línea, el miembro del símbolo en el fichero. El fichero se
    /// busca tal cual y, si no está, por sufijo de ruta.
    /// </summary>
    public GraphNodeFact? Locate(string path, int line, string? symbol)
    {
        if (ResolveFile(path) is not { } file)
            return null;
        var facts = _graph.FactsOf(file);
        if (facts.Count == 0)
            return null;
        var member = string.IsNullOrWhiteSpace(symbol) ? null : ReadingValidator.MemberName(symbol);
        var containing = facts.Where(n => line > 0 && n.StartLine <= line && line <= Math.Max(n.EndLine, n.StartLine))
            .OrderBy(n => n.EndLine - n.StartLine).ThenBy(n => n.IsMethod ? 0 : 1).ThenBy(n => n.Id).ToList();
        if (member != null && containing.FirstOrDefault(n => n.Name == member) is { } named)
            return named;
        if (containing.Count > 0)
            return containing[0];
        return member != null ? facts.FirstOrDefault(n => n.Name == member) : null;
    }

    private string? ResolveFile(string path)
    {
        var normalized = BatchReadingJson.NormalizePath(path);
        if (_files.TryGetValue(normalized, out var cached))
            return cached;
        string? found = _graph.FactsOf(normalized).Count > 0 ? normalized : null;
        found ??= _graph.FactFiles
            .Where(f => f.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase) || normalized.EndsWith("/" + f, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        _files[normalized] = found;
        return found;
    }

    private Dictionary<long, List<long>> BuildCallers()
    {
        var callers = new Dictionary<long, List<long>>();
        void Add(long to, long from)
        {
            if (to == from)
                return;
            if (!callers.TryGetValue(to, out var list))
                callers[to] = list = [];
            list.Add(from);
        }
        foreach (var edge in _graph.Edges)
        {
            if (CallerEdges.Contains(edge.Type, StringComparer.Ordinal))
                Add(edge.Target, edge.Source);
            else if (OverrideEdges.Contains(edge.Type, StringComparer.Ordinal))
                // Quien llama al método de la interfaz o de la base llega a la implementación.
                Add(edge.Source, edge.Target);
        }
        foreach (var list in callers.Values)
            list.Sort();
        return callers;
    }

    private (int FanIn, bool FromEntry) Reach(long id)
    {
        if (_reach.TryGetValue(id, out var cached))
            return cached;
        var callers = _callers.Value;
        var seen = new HashSet<long> { id };
        var queue = new Queue<long>();
        queue.Enqueue(id);
        var fromEntry = false;
        while (queue.Count > 0 && seen.Count <= MaxVisited)
        {
            if (!callers.TryGetValue(queue.Dequeue(), out var list))
                continue;
            foreach (var caller in list)
            {
                if (!seen.Add(caller))
                    continue;
                queue.Enqueue(caller);
                if (_graph.Node(caller) is { } node && Flag(node.Properties["is_entry_point"]) == true)
                    fromEntry = true;
            }
        }
        var result = (seen.Count - 1, fromEntry);
        _reach[id] = result;
        return result;
    }

    private static bool? Flag(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<int>(out var i) => i != 0,
        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var b) => b,
        _ => null
    };

    private static List<string> Strings(JsonNode? node) => node switch
    {
        JsonArray a => [.. a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : v.ToJsonString()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)],
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => [s],
        _ => []
    };
}

/// <summary>Un hallazgo del registro consolidado: uno o varios de los lotes fundidos, con su severidad.</summary>
public sealed record RegistryEntry
{
    /// <summary><c>REG-nnn</c>, por orden de severidad (<see cref="FindingConsolidator.AssignIds"/>).</summary>
    public string Id { get; init; } = "";

    /// <summary>Id estable por contenido (<see cref="FindingConsolidator.ContentId"/>): el mismo hallazgo da el mismo id en otra ejecución.</summary>
    public required string ContentId { get; init; }

    public required string Kind { get; init; }
    public string Category { get; init; } = "";
    public required string Title { get; init; }
    public string Rationale { get; init; } = "";
    public required ReadAnchor Anchor { get; init; }
    public IReadOnlyList<ReadAnchor> Instances { get; init; } = [];
    public string? Symbol { get; init; }
    public string? SeedRef { get; init; }
    public string ActivationPath { get; init; } = "";
    public int Impact { get; init; }
    public int Likelihood { get; init; }
    public double Confidence { get; init; }

    /// <summary>Los hallazgos de lote fundidos (<c>lote:F3</c>), ordenados; el primero no es necesariamente el representante.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Lote del hallazgo representante.</summary>
    public string Batch { get; init; } = "";

    public string InputHash { get; init; } = "";
    public ExposureAssessment Exposure { get; init; } = new();
    public required RubricResult Rubric { get; init; }

    /// <summary>La severidad final: la de la rúbrica o la del juez; null si se rechazó.</summary>
    public string? Severity { get; init; }

    public string SeverityBasis { get; init; } = "";

    /// <summary><c>confirmed</c> o <c>rejected</c>.</summary>
    public string Status { get; init; } = StatusConfirmed;

    public string? RejectReason { get; init; }

    /// <summary>El juez lo revisó con el código delante.</summary>
    public bool Reverified { get; init; }

    /// <summary><c>confirm</c>, <c>downgrade</c>, <c>upgrade</c> o <c>reject</c>; null si el juez no lo vio.</summary>
    public string? JudgeVerdict { get; init; }

    public const string StatusConfirmed = "confirmed";
    public const string StatusRejected = "rejected";
}

/// <summary>Una pregunta abierta de un lote y, si otro lote la responde, con qué.</summary>
public sealed record OpenQuestionResolution
{
    public required string Batch { get; init; }
    public string Symbol { get; init; } = "";
    public required string Question { get; init; }
    public IReadOnlyList<string> Needs { get; init; } = [];

    /// <summary>
    /// <c>answered</c> si otro lote escribió el contrato del símbolo por el que pregunta; <c>context</c> si solo hay el resumen de un
    /// fichero de sus <c>needs</c> leído por otro lote; <c>open</c> si no hay nada. Ninguno comprueba que el texto responda de verdad.
    /// </summary>
    public string Status { get; init; } = StatusOpen;

    /// <summary>El contrato o el resumen del otro lote.</summary>
    public string? Answer { get; init; }

    /// <summary><c>contract:lote:Tipo.Miembro</c> o <c>file:lote:ruta</c>.</summary>
    public string? AnsweredBy { get; init; }

    public const string StatusOpen = "open";
    public const string StatusContext = "context";
    public const string StatusAnswered = "answered";
}

/// <summary>El registro consolidado de una pasada.</summary>
public sealed record ConsolidatedRegistry
{
    public IReadOnlyList<RegistryEntry> Entries { get; init; } = [];
    public IReadOnlyList<OpenQuestionResolution> OpenQuestions { get; init; } = [];

    /// <summary>Decisiones y discrepancias: fusiones entre lotes, semillas confirmadas sin registro, preguntas sin responder.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Hallazgos de lote que entraron.</summary>
    public int MergedFrom { get; init; }
}

/// <summary>
/// D3 de la pasada compartida, parte determinista: funde los hallazgos de los lotes, les calcula la exposición
/// (<see cref="GraphExposure"/>) y la severidad de la rúbrica (<see cref="SeverityRubric"/>), les da ids y resuelve las preguntas
/// abiertas que responde otro lote. El juez (<see cref="FindingJudge"/>) va después.
/// </summary>
/// <remarks>
/// <para><b>Duplicados</b>. Entre lotes distintos: mismo símbolo y misma categoría (sin mayúsculas), o un ancla principal que se
/// solapa en el mismo fichero con cualquier ancla del otro y además mismo <c>kind</c> o misma cita. Dentro de un lote el lector ya
/// agrupa, y el mismo símbolo y categoría con otras líneas son defectos distintos (en los datos del experimento, tres fallos del
/// mismo parser): solo se funde lo que tiene mismo símbolo, misma categoría y anclas principales solapadas (las dos mitades de un
/// lote partido).</para>
/// <para><b>Patrones</b>. Entre lotes, hallazgos de símbolos distintos con el mismo <c>kind</c>, categoría y cita normalizada (de
/// ≥ 8 caracteres) y la misma severidad de rúbrica se registran una vez con el resto en <c>instances</c>.</para>
/// <para><b>Representante</b> de un grupo: el de mayor <c>S</c>, después seguridad &gt; bug &gt; deuda, después mayor confianza y por
/// último el primero por <c>lote:localId</c>. Su texto, impacto, probabilidad y activación son los del registro; la exposición es
/// la mayor del grupo, y la rúbrica se recalcula con ella.</para>
/// <para>Un grupo nunca junta dos hallazgos del mismo lote que no sean duplicados entre sí (el lector los separó a propósito): un
/// hallazgo de otro lote que casa con varios se funde con el primero por <c>lote:localId</c>.</para>
/// <para>Todo es determinista: la misma entrada da el mismo registro (ids incluidos).</para>
/// </remarks>
public static class FindingConsolidator
{
    public const int MinPatternQuoteChars = 8;

    private sealed record Item(int Index, string Gid, string Batch, ReadFinding Finding, string InputHash, ExposureAssessment Exposure, RubricResult Rubric);

    public static ConsolidatedRegistry Consolidate(IReadOnlyList<ConsolidationBatch> batches, SharedPassGraph graph)
    {
        var exposure = new GraphExposure(graph);
        var raw = batches
            .SelectMany(b => b.Reading.Findings.Select(f => (Gid: $"{b.BatchId}:{f.LocalId}", b.BatchId, Finding: f, b.InputHash)))
            .DistinctBy(x => x.Gid)
            .OrderBy(x => x.Gid, StringComparer.Ordinal)
            .ToList();
        var items = raw.Select((x, i) =>
        {
            var e = exposure.Assess(x.Finding);
            return new Item(i, x.Gid, x.BatchId, x.Finding, x.InputHash, e,
                SeverityRubric.Evaluate(x.Finding.Kind, x.Finding.Impact, x.Finding.Likelihood, e, x.Finding.ActivationPath));
        }).ToList();

        var sets = new DisjointSets(items.Count);
        for (var i = 0; i < items.Count; i++)
            for (var j = i + 1; j < items.Count; j++)
                if (Duplicate(items[i], items[j]))
                    TryUnion(items, sets, i, j);

        // Patrones: entre grupos ya fundidos, por representante.
        var groups = Groups(items, sets);
        var reps = groups.Select(g => Representative(g)).ToList();
        for (var i = 0; i < reps.Count; i++)
            for (var j = i + 1; j < reps.Count; j++)
                if (SamePattern(reps[i], reps[j]))
                    TryUnion(items, sets, reps[i].Index, reps[j].Index);
        groups = Groups(items, sets);

        var notes = new List<string>();
        var entries = new List<RegistryEntry>();
        foreach (var group in groups)
        {
            var entry = Build(group);
            if (group.Count > 1)
                notes.Add($"{entry.ContentId}: se funden {string.Join(", ", group.Select(g => g.Gid))} ({(group.Select(g => g.Batch).Distinct().Count() > 1 ? "entre lotes" : "dentro del lote")}).");
            entries.Add(entry);
        }
        entries = UniqueContentIds(entries);

        notes.AddRange(SeedNotes(batches, entries));
        var questions = ResolveOpenQuestions(batches);
        if (questions.Count > 0)
            notes.Add($"Preguntas abiertas: {questions.Count(q => q.Status == OpenQuestionResolution.StatusAnswered)} con el contrato del símbolo leído por otro lote, " +
                $"{questions.Count(q => q.Status == OpenQuestionResolution.StatusContext)} con el resumen de un fichero que necesitan y " +
                $"{questions.Count(q => q.Status == OpenQuestionResolution.StatusOpen)} sin nada (se conservan todas).");

        return new ConsolidatedRegistry
        {
            Entries = AssignIds(entries),
            OpenQuestions = questions,
            Notes = notes,
            MergedFrom = items.Count
        };
    }

    /// <summary>
    /// <c>REG-nnn</c> por orden: confirmados antes que rechazados; después severidad (P0 primero), <c>kind</c> (seguridad, bug,
    /// deuda), ruta y línea del ancla y <see cref="RegistryEntry.ContentId"/>. Devuelve las entradas en ese orden.
    /// </summary>
    public static IReadOnlyList<RegistryEntry> AssignIds(IEnumerable<RegistryEntry> entries)
    {
        var ordered = entries
            .OrderBy(e => e.Status == RegistryEntry.StatusRejected ? 1 : 0)
            .ThenBy(e => SeverityRubric.Rank(e.Severity ?? e.Rubric.Severity))
            .ThenBy(e => KindRank(e.Kind))
            .ThenBy(e => e.Anchor.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Anchor.StartLine)
            .ThenBy(e => e.ContentId, StringComparer.Ordinal)
            .ToList();
        var width = Math.Max(3, ordered.Count.ToString(CultureInfo.InvariantCulture).Length);
        return [.. ordered.Select((e, i) => e with { Id = "REG-" + (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') })];
    }

    /// <summary>16 hex del sha256 de <c>categoría|símbolo|cita</c> normalizados (minúsculas, sin tildes, espacios colapsados).</summary>
    public static string ContentId(string category, string? symbol, string quote)
    {
        var key = $"{SeverityRubric.Fold(category)}|{(symbol ?? "").Trim()}|{NormalizeQuote(quote)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    /// <summary>La cita con los espacios colapsados (sin cambiar mayúsculas: el código las distingue).</summary>
    internal static string NormalizeQuote(string quote) =>
        string.Join(' ', quote.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static int KindRank(string kind) => kind switch { "security" => 0, "bug" => 1, "debt" => 2, _ => 3 };

    private static string Category(ReadFinding f) => SeverityRubric.Fold(f.Category);

    private static bool SameFile(ReadAnchor a, ReadAnchor b) => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    private static bool Overlap(ReadAnchor a, ReadAnchor b) =>
        SameFile(a, b) && a.StartLine <= Math.Max(b.EndLine, b.StartLine) && b.StartLine <= Math.Max(a.EndLine, a.StartLine);

    private static bool Duplicate(Item a, Item b)
    {
        var fa = a.Finding;
        var fb = b.Finding;
        var symbolAndCategory = !string.IsNullOrWhiteSpace(fa.Symbol) && string.Equals(fa.Symbol!.Trim(), fb.Symbol?.Trim(), StringComparison.Ordinal)
            && Category(fa) == Category(fb);
        if (a.Batch == b.Batch)
            return symbolAndCategory && Overlap(fa.Anchor, fb.Anchor);
        if (symbolAndCategory)
            return true;
        var overlap = fb.Instances.Prepend(fb.Anchor).Any(x => Overlap(fa.Anchor, x)) || fa.Instances.Any(x => Overlap(fb.Anchor, x));
        return overlap && (string.Equals(fa.Kind, fb.Kind, StringComparison.Ordinal) || NormalizeQuote(fa.Anchor.Quote) == NormalizeQuote(fb.Anchor.Quote));
    }

    /// <summary>
    /// Funde los grupos de <paramref name="i"/> y <paramref name="j"/> salvo que el resultado junte dos hallazgos del mismo lote que
    /// no son duplicados entre sí: un hallazgo de otro lote se funde con uno solo de los defectos distintos que el lector separó.
    /// Los pares se prueban en orden de <c>lote:localId</c>, así que gana el primero.
    /// </summary>
    private static void TryUnion(List<Item> items, DisjointSets sets, int i, int j)
    {
        var (ri, rj) = (sets.Find(i), sets.Find(j));
        if (ri == rj)
            return;
        var left = items.Where(x => sets.Find(x.Index) == ri).ToList();
        var right = items.Where(x => sets.Find(x.Index) == rj).ToList();
        if (left.Any(a => right.Any(b => a.Batch == b.Batch && !Duplicate(a, b))))
            return;
        sets.Union(i, j);
    }

    private static bool SamePattern(Item a, Item b)
    {
        if (a.Batch == b.Batch || !string.Equals(a.Finding.Kind, b.Finding.Kind, StringComparison.Ordinal) || Category(a.Finding) != Category(b.Finding))
            return false;
        if (string.Equals(a.Finding.Symbol?.Trim(), b.Finding.Symbol?.Trim(), StringComparison.Ordinal) || a.Rubric.Severity != b.Rubric.Severity)
            return false;
        var qa = NormalizeQuote(a.Finding.Anchor.Quote);
        return qa.Length >= MinPatternQuoteChars && qa == NormalizeQuote(b.Finding.Anchor.Quote);
    }

    private static List<List<Item>> Groups(List<Item> items, DisjointSets sets) =>
        [.. items.GroupBy(i => sets.Find(i.Index)).Select(g => g.OrderBy(i => i.Gid, StringComparer.Ordinal).ToList()).OrderBy(g => g[0].Gid, StringComparer.Ordinal)];

    private static Item Representative(IReadOnlyList<Item> group) => group
        .OrderByDescending(i => i.Rubric.Score)
        .ThenBy(i => KindRank(i.Finding.Kind))
        .ThenByDescending(i => i.Finding.Confidence)
        .ThenBy(i => i.Gid, StringComparer.Ordinal)
        .First();

    private static RegistryEntry Build(IReadOnlyList<Item> group)
    {
        var rep = Representative(group);
        var f = rep.Finding;
        var instances = new List<ReadAnchor>();
        var seen = new HashSet<(string, int, int)> { Key(f.Anchor) };
        foreach (var anchor in f.Instances.Concat(group.Where(g => g != rep).SelectMany(g => g.Finding.Instances.Prepend(g.Finding.Anchor))))
        {
            if (seen.Add(Key(anchor)))
                instances.Add(anchor);
        }
        var exposure = group.OrderByDescending(g => g.Exposure.Score).ThenBy(g => g == rep ? 0 : 1).ThenBy(g => g.Gid, StringComparer.Ordinal).First().Exposure;
        var rubric = SeverityRubric.Evaluate(f.Kind, f.Impact, f.Likelihood, exposure, f.ActivationPath);
        return new RegistryEntry
        {
            ContentId = ContentId(f.Category, f.Symbol, f.Anchor.Quote),
            Kind = f.Kind,
            Category = f.Category,
            Title = f.Title,
            Rationale = f.Rationale,
            Anchor = f.Anchor,
            Instances = instances,
            Symbol = f.Symbol,
            SeedRef = f.SeedRef ?? group.Select(g => g.Finding.SeedRef).FirstOrDefault(s => s != null),
            ActivationPath = f.ActivationPath,
            Impact = f.Impact,
            Likelihood = f.Likelihood,
            Confidence = f.Confidence,
            Sources = [.. group.Select(g => g.Gid)],
            Batch = rep.Batch,
            InputHash = rep.InputHash,
            Exposure = exposure,
            Rubric = rubric,
            Severity = rubric.Severity,
            SeverityBasis = rubric.Basis
        };

        static (string, int, int) Key(ReadAnchor a) => (a.Path.ToLowerInvariant(), a.StartLine, a.EndLine);
    }

    /// <summary>Dos grupos con el mismo id de contenido (otro defecto del mismo símbolo con la misma cita) llevan sufijo <c>-2</c>, <c>-3</c>…</summary>
    private static List<RegistryEntry> UniqueContentIds(List<RegistryEntry> entries)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<RegistryEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var n = counts[entry.ContentId] = counts.GetValueOrDefault(entry.ContentId) + 1;
            result.Add(n == 1 ? entry : entry with { ContentId = $"{entry.ContentId}-{n}" });
        }
        return result;
    }

    /// <summary>Semillas confirmadas sin registro y semillas descartadas que acabaron en uno.</summary>
    private static IEnumerable<string> SeedNotes(IReadOnlyList<ConsolidationBatch> batches, IReadOnlyList<RegistryEntry> entries)
    {
        var seeds = entries.Where(e => e.SeedRef != null).Select(e => e.SeedRef!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var symbols = entries.SelectMany(e => new[] { e.Symbol, e.SeedRef is { } s ? LlmRegistryGraphWriter.SeedSymbol(s) : null })
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToHashSet(StringComparer.Ordinal);
        var confirmed = 0;
        var uncovered = new List<string>();
        foreach (var batch in batches)
        {
            foreach (var verdict in batch.Reading.SeedVerdicts.Where(v => v.Verdict == "confirmed"))
            {
                confirmed++;
                if (!seeds.Contains(verdict.Seed.Trim()) && !symbols.Contains(LlmRegistryGraphWriter.SeedSymbol(verdict.Seed)))
                    uncovered.Add($"{batch.BatchId}: {verdict.Seed}");
            }
        }
        if (confirmed > 0)
            yield return $"Semillas confirmadas: {confirmed}; {uncovered.Count} sin registro que las cite ni hallazgo sobre su símbolo.";
        foreach (var seed in uncovered.Order(StringComparer.Ordinal))
            yield return $"Semilla confirmada sin registro: {seed}.";
    }

    /// <summary>
    /// Cada pregunta abierta y, si otro lote leyó el símbolo por el que pregunta, el contrato que escribió (<c>answered</c>); si no,
    /// y alguno de sus <c>needs</c> es un fichero de otro lote, su resumen (<c>context</c>). Lo que no se encuentra queda <c>open</c>.
    /// </summary>
    public static IReadOnlyList<OpenQuestionResolution> ResolveOpenQuestions(IReadOnlyList<ConsolidationBatch> batches)
    {
        var members = new Dictionary<string, (string Batch, string Text)>(StringComparer.Ordinal);
        var files = new Dictionary<string, (string Batch, string Text)>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in batches.OrderBy(b => b.BatchId, StringComparer.Ordinal))
        {
            foreach (var file in batch.Reading.Files.Where(f => f.Summary.Length > 0))
                files.TryAdd(file.Path, (batch.BatchId, file.Summary));
            foreach (var type in batch.Reading.Types)
            {
                if (type.Purpose.Length > 0)
                    members.TryAdd(type.Name, (batch.BatchId, $"{type.Name}: {type.Purpose}"));
                foreach (var member in type.Members)
                    members.TryAdd($"{type.Name}.{ReadingValidator.MemberName(member.Name)}", (batch.BatchId, Contract(type.Name, member)));
            }
        }

        var result = new List<OpenQuestionResolution>();
        foreach (var batch in batches)
        {
            foreach (var question in batch.Reading.OpenQuestions)
            {
                var entry = new OpenQuestionResolution { Batch = batch.BatchId, Symbol = question.Symbol, Question = question.Question, Needs = question.Needs };
                var answer = SymbolKeys(question.Symbol)
                    .Select(k => members.TryGetValue(k, out var m) && m.Batch != batch.BatchId ? (Key: $"contract:{m.Batch}:{k}", m.Text) : default)
                    .FirstOrDefault(a => a.Key != null);
                if (answer.Key == null)
                {
                    answer = question.Needs.Select(BatchReadingJson.NormalizePath)
                        .Select(n => files.FirstOrDefault(f => f.Value.Batch != batch.BatchId
                            && (f.Key.Equals(n, StringComparison.OrdinalIgnoreCase) || f.Key.EndsWith("/" + n, StringComparison.OrdinalIgnoreCase))))
                        .Where(f => f.Key != null)
                        .Select(f => (Key: $"file:{f.Value.Batch}:{f.Key}", Text: $"{f.Key}: {f.Value.Text}"))
                        .FirstOrDefault();
                }
                result.Add(answer.Key == null ? entry : entry with
                {
                    Status = answer.Key.StartsWith("contract:", StringComparison.Ordinal) ? OpenQuestionResolution.StatusAnswered : OpenQuestionResolution.StatusContext,
                    Answer = answer.Text,
                    AnsweredBy = answer.Key
                });
            }
        }
        return result;
    }

    private static string Contract(string type, ReadMember m)
    {
        var parts = new List<string> { $"{type}.{m.Name}" };
        if (m.Does.Length > 0) parts.Add($"hace: {m.Does}");
        if (m.Inputs.Length > 0) parts.Add($"recibe: {m.Inputs}");
        if (m.Outputs.Length > 0) parts.Add($"devuelve: {m.Outputs}");
        if (m.SideEffects.Length > 0) parts.Add($"efectos: {m.SideEffects}");
        if (m.Throws.Length > 0) parts.Add($"lanza: {m.Throws}");
        return string.Join("; ", parts);
    }

    /// <summary><c>A.B / C</c>, <c>A.B, A.C</c> o <c>A.B(x)</c> → <c>A.B</c>, <c>C</c>, <c>A.C</c>…; también el tipo solo.</summary>
    private static IEnumerable<string> SymbolKeys(string symbol)
    {
        var keys = new List<string>();
        foreach (var part in symbol.Split(['/', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var head = part.Split('(', 2)[0].Trim().Trim('`');
            var segments = head.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
                keys.Add($"{segments[^2].Split('<', 2)[0]}.{segments[^1]}");
            if (segments.Length >= 1)
                keys.Add(segments.Length >= 2 ? segments[^2].Split('<', 2)[0] : segments[0].Split('<', 2)[0]);
        }
        return keys.Distinct(StringComparer.Ordinal);
    }

    private sealed class DisjointSets
    {
        private readonly int[] _parent;

        public DisjointSets(int count) => _parent = [.. Enumerable.Range(0, count)];

        public int Find(int x)
        {
            while (_parent[x] != x)
                x = _parent[x] = _parent[_parent[x]];
            return x;
        }

        public void Union(int a, int b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
                _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }
}
