using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>El paquete Markdown de un lote, lo que lee el modelo en D2.</summary>
public sealed record SharedPassPacket
{
    public required string BatchId { get; init; }
    public required string Markdown { get; init; }

    /// <summary>Tokens estimados del paquete entero (caracteres / 4 hacia arriba).</summary>
    public int EstimatedTokens { get; init; }

    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Ficheros que no se pudieron leer o se recortaron.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Ficheros recortados por <see cref="SharedPassOptions.MaxFileChars"/>, con la última línea que muestra el paquete: lo que empieza
    /// después no se exige en la cobertura (<see cref="BatchExpectations.From"/>).
    /// </summary>
    public IReadOnlyDictionary<string, int> ShownLines { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>El plan y los paquetes de todos sus lotes, en el orden del plan.</summary>
public sealed record SharedPassPacketSet(BatchPlan Plan, IReadOnlyList<SharedPassPacket> Packets, IReadOnlyList<string> Warnings)
{
    public int TotalEstimatedTokens => Packets.Sum(p => p.EstimatedTokens);
}

/// <summary>
/// Construye el paquete de un lote (D1, determinista: misma entrada, mismos bytes). Secciones, en este orden:
/// <list type="number">
/// <item>cabecera: nombre e id del lote, parte, subsistema, descripción del mapa de dominios y nº de ficheros;</item>
/// <item>hechos del grafo por fichero: una línea <c>- T</c> por tipo (rango, rol, bases, acoplamiento, ciclos, DI, código muerto,
/// <c>type_hash</c>) y una <c>- M</c> por método (firma, rango, complejidad, fan-in/out, accesibilidad, código muerto, issues,
/// <c>throws</c>, <c>swallows_exceptions</c>, <c>sensitive_apis</c>, <c>concurrency_markers</c>, <c>body_hash</c>,
/// <c>contract_hash</c>); lo que el grafo no tiene se omite;</item>
/// <item>relaciones con el exterior del lote (<see cref="SharedPassGraph.DependencyEdgeTypes"/>), entrantes y salientes, con el id del
/// lote del otro extremo entre corchetes y <c>CALLS(exacta)</c> si la arista es del compilador; como mucho
/// <see cref="SharedPassOptions.MaxRelationsPerDirection"/> de cada, en orden ordinal;</item>
/// <item>código numerado <c>N: línea</c> con la línea real, leído con <see cref="SourceEncoding"/>; un fichero que no se lee lleva
/// <c>(no legible: …)</c> y uno que pasa de <see cref="SharedPassOptions.MaxFileChars"/> se corta con aviso.</item>
/// </list>
/// Las huellas (<c>*_hash</c>) van recortadas a <see cref="HashPrefixLength"/> caracteres: identifican, la completa sigue en el grafo.
/// </summary>
public sealed class PacketBuilder
{
    public const int HashPrefixLength = 12;

    private readonly SharedPassGraph _graph;
    private readonly string _repositoryRoot;
    private readonly SharedPassOptions _options;
    private readonly Encoding _defaultEncoding;
    private readonly Dictionary<string, FileSections> _files = new(StringComparer.OrdinalIgnoreCase);
    // Los lotes se leen en paralelo y al partir uno se rehacen paquetes: la caché se comparte entre hilos.
    private readonly Lock _filesLock = new();

    private sealed record FileSections(IReadOnlyList<string> Facts, IReadOnlyList<string> Code, long Chars, string? Warning, int? ShownLines);

    /// <param name="graph">Hechos del grafo (<see cref="SharedPassGraph.LoadAsync(string, CancellationToken)"/>).</param>
    /// <param name="repositoryRoot">Raíz del repositorio: las rutas del mapa de dominios son relativas a ella.</param>
    /// <param name="options">Topes del paquete.</param>
    /// <param name="defaultEncoding">
    /// Para los ficheros que no son UTF-8 válido; null = <see cref="SourceEncoding.ResolveReadFallback"/> del repositorio (lo que usan
    /// las herramientas de lectura).
    /// </param>
    public PacketBuilder(SharedPassGraph graph, string repositoryRoot, SharedPassOptions options, Encoding? defaultEncoding = null)
    {
        _graph = graph;
        _repositoryRoot = repositoryRoot;
        _options = options;
        _defaultEncoding = defaultEncoding ?? SourceEncoding.ResolveReadFallback(repositoryRoot, null);
    }

    /// <summary>Tokens estimados de un fichero en el paquete: sus hechos del grafo más su código numerado, caracteres / 4 hacia arriba.</summary>
    public int EstimateFileTokens(string file) => (int)Math.Min(int.MaxValue, (Sections(file).Chars + 3) / 4);

    /// <summary>
    /// El paquete de <paramref name="batch"/>. <paramref name="plan"/> son todos los lotes: dicen a qué lote pertenece el otro
    /// extremo de cada relación (los ficheros fuera del plan no cuentan).
    /// </summary>
    public SharedPassPacket Build(SharedPassBatch batch, IReadOnlyList<SharedPassBatch> plan)
    {
        var files = batch.Files.OrderBy(f => f, StringComparer.Ordinal).ToList();
        var warnings = new List<string>();
        var shownLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var title = batch.PartCount > 1 ? $"{batch.SubsystemName} (parte {batch.Part} de {batch.PartCount})" : batch.SubsystemName;
        var lines = HeaderLines(title, batch.Id, batch.SubsystemId, batch.IsShared, batch.Level, batch.SubsystemDescription, files.Count);
        foreach (var file in files)
            lines.AddRange(Sections(file).Facts);

        AppendRelations(lines, batch, plan);

        lines.Add(CodeHeading);
        foreach (var file in files)
        {
            var sections = Sections(file);
            lines.AddRange(sections.Code);
            if (sections.Warning != null)
                warnings.Add(sections.Warning);
            if (sections.ShownLines is int shown)
                shownLines[file] = shown;
        }

        var markdown = string.Join("\n", lines);
        return new SharedPassPacket
        {
            BatchId = batch.Id,
            Markdown = markdown,
            EstimatedTokens = (int)Math.Min(int.MaxValue, (markdown.Length + 3L) / 4),
            Files = files,
            Warnings = warnings,
            ShownLines = shownLines
        };
    }

    /// <summary>
    /// Todo D1 de una vez: lee el grafo de tipos y los hechos de <paramref name="databasePath"/>, planifica los lotes de
    /// <paramref name="map"/> y construye sus paquetes. No escribe nada.
    /// </summary>
    public static async Task<SharedPassPacketSet> BuildAllAsync(
        string databasePath, string repositoryRoot, DomainMap map, SharedPassOptions options, CancellationToken ct, Encoding? defaultEncoding = null)
    {
        var mapFiles = new HashSet<string>(map.Subsystems.SelectMany(s => s.Files), StringComparer.OrdinalIgnoreCase);
        var typeGraph = await TypeGraphBuilder.ReadAsync(databasePath, mapFiles.Contains, ct);
        var graph = await SharedPassGraph.LoadAsync(databasePath, ct);
        var builder = new PacketBuilder(graph, repositoryRoot, options, defaultEncoding);
        var plan = BatchPlanner.Plan(map, typeGraph, builder.EstimateFileTokens, builder.OverheadEstimator(map), options);
        var packets = plan.Batches.Select(b => builder.Build(b, plan.Batches)).ToList();
        var warnings = graph.Warnings.Concat(typeGraph.Warnings).Concat(plan.Warnings).Concat(packets.SelectMany(p => p.Warnings)).ToList();
        return new SharedPassPacketSet(plan, packets, warnings);
    }

    private const string CodeHeading = "\n## Código fuente (numerado; la numeración es la línea real del fichero)\n";

    private static List<string> HeaderLines(string title, string id, string subsystemId, bool shared, int level, string description, int fileCount) =>
    [
        $"# Lote: {title} (id `{id}`)\n",
        $"Subsistema: `{subsystemId}`{(shared ? " (infraestructura compartida)" : "")}; nivel {level}.\n",
        $"Descripción del mapa de dominios: {description}\n",
        $"Ficheros: {fileCount}\n",
        "\n## Hechos del grafo (compilador/indexador; las CALLS sin «exacta» se resolvieron por nombre)\n"
    ];

    /// <summary>
    /// Para <see cref="BatchPlanner"/>: lo que el paquete de un conjunto de ficheros de <paramref name="map"/> lleva además de sus
    /// ficheros (<see cref="EstimateFileTokens"/>), es decir la cabecera, los títulos de sección y las relaciones con los ficheros de
    /// fuera del conjunto, en tokens estimados (caracteres / 4 hacia arriba). Es una cota, no el valor exacto: el lote del otro
    /// extremo aún no se conoce y se cuenta con el id del subsistema más <c>-p99</c>, y el título y el nivel con su máximo.
    /// </summary>
    public Func<Subsystem, IReadOnlyCollection<string>, int> OverheadEstimator(DomainMap map)
    {
        var subsystemOfFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subsystem in map.Subsystems)
        {
            foreach (var file in subsystem.Files)
                subsystemOfFile.TryAdd(file, subsystem.Id);
        }

        // Las aristas entre ficheros del mapa, por fichero de cada extremo.
        var edgesOfFile = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _graph.Edges.Count; i++)
        {
            if (_graph.Node(_graph.Edges[i].Source) is not { } source || _graph.Node(_graph.Edges[i].Target) is not { } target
                || !subsystemOfFile.ContainsKey(source.File) || !subsystemOfFile.ContainsKey(target.File))
                continue;
            foreach (var file in new[] { source.File, target.File }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!edgesOfFile.TryGetValue(file, out var list))
                    edgesOfFile[file] = list = [];
                list.Add(i);
            }
        }

        return (subsystem, files) =>
        {
            var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var chars = HeaderLines(subsystem.Name + " (parte 99 de 99)", subsystem.Id + "-p99", subsystem.Id, subsystem.IsShared, 99,
                subsystem.Description, set.Count).Sum(l => (long)l.Length + 1) + CodeHeading.Length + 1;

            var incoming = new HashSet<string>(StringComparer.Ordinal);
            var outgoing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in set)
            {
                foreach (var index in edgesOfFile.GetValueOrDefault(file) ?? [])
                {
                    var edge = _graph.Edges[index];
                    var source = _graph.Node(edge.Source)!;
                    var target = _graph.Node(edge.Target)!;
                    var inTarget = set.Contains(target.File);
                    if (set.Contains(source.File) == inTarget)
                        continue;
                    var type = edge.Exact && edge.Type == "CALLS" ? "CALLS(exacta)" : edge.Type;
                    var other = subsystemOfFile[inTarget ? source.File : target.File] + "-p99";
                    (inTarget ? incoming : outgoing).Add(inTarget
                        ? $"- [{other}] {_graph.ShortName(source.QualifiedName)} -{type}-> {_graph.ShortName(target.QualifiedName)}"
                        : $"- {_graph.ShortName(source.QualifiedName)} -{type}-> [{other}] {_graph.ShortName(target.QualifiedName)}");
                }
            }

            chars += RelationChars(incoming) + RelationChars(outgoing) + RelationHeadingsChars;
            return (int)Math.Min(int.MaxValue, (chars + 3) / 4);
        };

        long RelationChars(HashSet<string> lines)
        {
            var max = _options.MaxRelationsPerDirection;
            return lines.Count <= max
                ? lines.Sum(l => (long)l.Length + 1)
                : lines.Select(l => (long)l.Length + 1).OrderDescending().Take(max).Sum() + 30;
        }
    }

    /// <summary>Los títulos de la sección de relaciones, con sus recuentos (holgura incluida).</summary>
    private const int RelationHeadingsChars = 140;

    // ───────────────────────────── Relaciones ─────────────────────────────

    private void AppendRelations(List<string> lines, SharedPassBatch batch, IReadOnlyList<SharedPassBatch> plan)
    {
        var batchOfFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in plan)
        {
            foreach (var file in other.Files)
                batchOfFile.TryAdd(file, other.Id);
        }

        var incoming = new SortedSet<(string Batch, string Who, string Type, string What)>(RelationComparer.Instance);
        var outgoing = new SortedSet<(string Batch, string Who, string Type, string What)>(RelationComparer.Instance);
        foreach (var edge in _graph.Edges)
        {
            if (_graph.Node(edge.Source) is not { } source || _graph.Node(edge.Target) is not { } target
                || !batchOfFile.TryGetValue(source.File, out var from) || !batchOfFile.TryGetValue(target.File, out var to) || from == to)
                continue;
            var type = edge.Exact && edge.Type == "CALLS" ? "CALLS(exacta)" : edge.Type;
            if (to == batch.Id)
                incoming.Add((from, _graph.ShortName(source.QualifiedName), type, _graph.ShortName(target.QualifiedName)));
            else if (from == batch.Id)
                outgoing.Add((to, _graph.ShortName(target.QualifiedName), type, _graph.ShortName(source.QualifiedName)));
        }

        var max = _options.MaxRelationsPerDirection;
        lines.Add($"\n## Relaciones con otros lotes\n\nEntrantes ({incoming.Count}; quién de fuera usa este lote):\n");
        lines.AddRange(incoming.Take(max).Select(r => $"- [{r.Batch}] {r.Who} -{r.Type}-> {r.What}"));
        if (incoming.Count > max)
            lines.Add($"- (… y {incoming.Count - max} más)");
        lines.Add($"\nSalientes ({outgoing.Count}; qué de fuera usa este lote):\n");
        lines.AddRange(outgoing.Take(max).Select(r => $"- {r.What} -{r.Type}-> [{r.Batch}] {r.Who}"));
        if (outgoing.Count > max)
            lines.Add($"- (… y {outgoing.Count - max} más)");
    }

    private sealed class RelationComparer : IComparer<(string, string, string, string)>
    {
        public static readonly RelationComparer Instance = new();

        public int Compare((string, string, string, string) x, (string, string, string, string) y)
        {
            var c = string.CompareOrdinal(x.Item1, y.Item1);
            if (c == 0) c = string.CompareOrdinal(x.Item2, y.Item2);
            if (c == 0) c = string.CompareOrdinal(x.Item3, y.Item3);
            if (c == 0) c = string.CompareOrdinal(x.Item4, y.Item4);
            return c;
        }
    }

    // ───────────────────────────── Ficheros ─────────────────────────────

    private FileSections Sections(string file)
    {
        lock (_filesLock)
        {
            if (_files.TryGetValue(file, out var cached))
                return cached;
        }

        var facts = FactLines(file);
        var (code, warning, shown) = CodeLines(file);
        var chars = facts.Sum(l => (long)l.Length + 1) + code.Sum(l => (long)l.Length + 1);
        var sections = new FileSections(facts, code, chars, warning, shown);
        lock (_filesLock)
        {
            // Si otro hilo lo calculó a la vez, se queda el primero: misma entrada, mismo resultado.
            return _files.TryAdd(file, sections) ? sections : _files[file];
        }
    }

    private List<string> FactLines(string file)
    {
        var nodes = _graph.FactsOf(file);
        if (nodes.Count == 0)
            return [];
        var lines = new List<string> { $"\n### {file}\n" };
        foreach (var node in nodes)
            lines.Add(node.IsMethod ? "- M " + MethodFacts(node) : "- T " + TypeFacts(node));
        return lines;
    }

    private string MethodFacts(GraphNodeFact node)
    {
        var p = node.Properties;
        var segments = _graph.ShortName(node.QualifiedName).Split('.');
        var owner = segments.Length >= 2 ? segments[^2] + "." : "";
        var bits = new List<string>
        {
            $"{owner}{node.Name}{Collapse(Text(p["signature"]))}",
            $"L{node.StartLine}-{node.EndLine}"
        };
        Add(bits, "cc", p["cyclomatic"] ?? p["complexity"]);
        Add(bits, "fan_in", p["fan_in"]);
        Add(bits, "fan_out", p["fan_out"]);
        if (Text(p["accessibility"]) is { Length: > 0 } accessibility)
            bits.Add(accessibility);
        AddDeadCode(bits, p);
        foreach (var issue in Items(p["detected_issues"]))
            bits.Add($"ISSUE[{issue}]");
        if (Items(p["throws"]) is { Count: > 0 } throws)
            bits.Add($"throws[{string.Join(", ", throws)}]");
        if (IsTrue(p["swallows_exceptions"]))
        {
            var catches = (p["swallowed_catches"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(c => $"{Text(c["kind"])}@L{Text(c["line"])}").ToList();
            bits.Add(catches.Count > 0 ? $"swallows[{string.Join(", ", catches)}]" : "swallows");
        }
        var sensitive = (p["sensitive_apis"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(a => $"{Text(a["category"])}:{Text(a["api"])}@L{Text(a["line"])}").ToList();
        if (sensitive.Count > 0)
            bits.Add($"sensitive[{string.Join(", ", sensitive)}]");
        else if (Items(p["sensitive_categories"]) is { Count: > 0 } categories)
            bits.Add($"sensitive[{string.Join(", ", categories)}]");
        var markers = (p["concurrency_markers"] as JsonArray ?? []).Select(m => m is JsonObject o
            ? $"{Text(o["kind"])}{(Text(o["api"]) is { Length: > 0 } api && api != Text(o["kind"]) ? ":" + api : "")}@L{Text(o["line"])}"
            : Text(m)).Where(m => m.Length > 0).ToList();
        if (markers.Count > 0)
            bits.Add($"concurrency[{string.Join(", ", markers)}]");
        AddHash(bits, "body#", p["body_hash"]);
        AddHash(bits, "contract#", p["contract_hash"]);
        return string.Join(" | ", bits);
    }

    private static string TypeFacts(GraphNodeFact node)
    {
        var p = node.Properties;
        var bits = new List<string> { $"{node.Label} {node.Name}", $"L{node.StartLine}-{node.EndLine}" };
        Add(bits, "role", p["type_role"]);
        if (Items(p["base_classes"]) is { Count: > 0 } bases)
            bits.Add($"base=[{string.Join(", ", bases)}]");
        Add(bits, "Ca", p["afferent_coupling"]);
        Add(bits, "Ce", p["efferent_coupling"]);
        if (IsTrue(p["in_cycle"]))
            bits.Add($"in_cycle=[{string.Join(", ", Items(p["cycle_members"]))}]");
        if (Text(p["di_lifetime"]) is { Length: > 0 } lifetime)
            bits.Add($"DI={lifetime}->{string.Join(", ", Items(p["di_registered_as"]))}");
        AddDeadCode(bits, p);
        AddHash(bits, "type#", p["type_hash"]);
        return string.Join(" | ", bits);
    }

    private static void AddDeadCode(List<string> bits, JsonObject p)
    {
        if (IsTrue(p["dead_code_candidate"]))
            bits.Add($"DEAD?({Text(p["dead_code_confidence"])}: {Text(p["dead_code_reason"])})");
    }

    private static void Add(List<string> bits, string name, JsonNode? value)
    {
        if (Text(value) is { Length: > 0 } text)
            bits.Add($"{name}={text}");
    }

    private static void AddHash(List<string> bits, string prefix, JsonNode? value)
    {
        if (Text(value) is { Length: > 0 } hash)
            bits.Add(prefix + (hash.Length > HashPrefixLength ? hash[..HashPrefixLength] : hash));
    }

    /// <summary>Un valor como texto: cadenas tal cual, números en cultura invariante, el resto en JSON compacto; vacío si falta.</summary>
    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("0.####", CultureInfo.InvariantCulture),
        JsonArray a => string.Join(", ", a.Select(Text)),
        _ => node.ToJsonString()
    };

    /// <summary>Los elementos de un array (o el valor suelto) como texto, sin vacíos.</summary>
    private static List<string> Items(JsonNode? node) => node switch
    {
        null => [],
        JsonArray a => [.. a.Select(Text).Where(t => t.Length > 0)],
        _ when Text(node) is { Length: > 0 } t => [t],
        _ => []
    };

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<double>(out var d) && d != 0));

    /// <summary>Espacios y saltos de línea seguidos a uno solo (las firmas de cbm traen los saltos del fuente).</summary>
    private static string Collapse(string text) =>
        string.Join(' ', text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Replace("( ", "(").Replace(" )", ")");

    /// <summary>El código numerado de <paramref name="file"/>, el aviso si lo hay y, si se recortó, la última línea mostrada.</summary>
    private (List<string> Lines, string? Warning, int? ShownLines) CodeLines(string file)
    {
        string text;
        try
        {
            var path = Path.Combine(_repositoryRoot, file.Replace('/', Path.DirectorySeparatorChar));
            text = SourceEncoding.ReadFile(path, _defaultEncoding).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var reason = ex is FileNotFoundException or DirectoryNotFoundException ? "no existe" : ex.Message;
            return ([$"\n### FILE {file}\n(no legible: {reason})"], $"⚠️ {file}: no se pudo leer ({reason}); el lote va sin su código.", null);
        }

        var source = SplitLines(text);
        var shown = source.Count;
        if (_options.MaxFileChars > 0 && text.Length > _options.MaxFileChars)
        {
            var total = 0L;
            shown = 0;
            while (shown < source.Count && total + source[shown].Length + 1 <= _options.MaxFileChars)
                total += source[shown++].Length + 1;
        }

        var lines = new List<string>(shown + 3);
        lines.Add(shown < source.Count
            ? $"\n### FILE {file}  ({source.Count} líneas; se muestran las {shown} primeras por MaxFileChars)\n```"
            : $"\n### FILE {file}  ({source.Count} líneas)\n```");
        for (var i = 0; i < shown; i++)
            lines.Add($"{i + 1}: {source[i]}");
        lines.Add("```");
        if (shown >= source.Count)
            return (lines, null, null);
        lines.Add($"(… faltan las líneas {shown + 1}-{source.Count})");
        return (lines, $"⚠️ {file}: {text.Length} caracteres, más que MaxFileChars ({_options.MaxFileChars}); el paquete lleva las líneas 1-{shown}.", shown);
    }

    /// <summary>Líneas separadas por <c>\r\n</c>, <c>\n</c> o <c>\r</c>; sin la vacía final que deja un salto de línea al acabar.</summary>
    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
                continue;
            lines.Add(text[start..i]);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }
}
