using System.Text;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>Una semilla del paquete (<c>ISSUE[…]</c> o <c>DEAD?</c>) que debe recibir veredicto.</summary>
public sealed record ExpectedSeed(string File, string Symbol, string Marker)
{
    /// <summary>Como la cita el paquete: <c>Tipo.Miembro ISSUE[código]</c> o <c>Tipo DEAD?</c>.</summary>
    public string Text => $"{Symbol} {Marker}";
}

/// <summary>Lo que la lectura de un conjunto de ficheros debe cubrir, sacado de los mismos hechos del grafo que el paquete.</summary>
public sealed record BatchExpectations
{
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Por fichero, los nombres de sus tipos (nodos de <c>TypeGraphBuilder.TypeLabels</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Types { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Por fichero, los métodos como (tipo dueño, nombre); el dueño es "" si no se conoce.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string Owner, string Name)>> Methods { get; init; } = new Dictionary<string, IReadOnlyList<(string, string)>>();

    public IReadOnlyList<ExpectedSeed> Seeds { get; init; } = [];

    /// <param name="graph">Hechos del grafo.</param>
    /// <param name="files">Ficheros de la lectura.</param>
    /// <param name="shownLines">
    /// Ficheros recortados en el paquete (<see cref="SharedPassPacket.ShownLines"/>) con su última línea mostrada: sus tipos, métodos y
    /// semillas que empiezan después no se exigen, porque el modelo no los ha visto.
    /// </param>
    public static BatchExpectations From(SharedPassGraph graph, IEnumerable<string> files, IReadOnlyDictionary<string, int>? shownLines = null)
    {
        var list = files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var types = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var methods = new Dictionary<string, IReadOnlyList<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        var seeds = new List<ExpectedSeed>();
        foreach (var file in list)
        {
            var facts = graph.FactsOf(file);
            if (shownLines != null && shownLines.TryGetValue(file, out var lastShown))
                facts = [.. facts.Where(n => n.StartLine <= lastShown)];
            types[file] = [.. facts.Where(n => !n.IsMethod).Select(n => n.Name).Distinct(StringComparer.Ordinal)];
            var fileMethods = new List<(string, string)>();
            foreach (var node in facts)
            {
                var symbol = node.Name;
                if (node.IsMethod)
                {
                    var segments = graph.ShortName(node.QualifiedName).Split('.');
                    var owner = segments.Length >= 2 ? segments[^2] : "";
                    fileMethods.Add((owner, node.Name));
                    symbol = owner.Length > 0 ? $"{owner}.{node.Name}" : node.Name;
                    foreach (var issue in Items(node.Properties["detected_issues"]))
                        seeds.Add(new ExpectedSeed(file, symbol, $"ISSUE[{issue}]"));
                }
                if (IsTrue(node.Properties["dead_code_candidate"]))
                    seeds.Add(new ExpectedSeed(file, symbol, "DEAD?"));
            }
            methods[file] = [.. fileMethods.Distinct()];
        }
        return new BatchExpectations { Files = list, Types = types, Methods = methods, Seeds = seeds.Distinct().ToList() };
    }

    private static IEnumerable<string> Items(JsonNode? node) => node switch
    {
        JsonArray a => a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : v.ToJsonString()).Where(s => s.Length > 0),
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => [s],
        _ => []
    };

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<double>(out var d) && d != 0));
}

/// <summary>El resultado de validar una lectura: lo que vale, los hallazgos rechazados y los errores que quedan.</summary>
public sealed record ReadingValidation(BatchReading Valid, IReadOnlyList<RejectedFinding> Rejected, IReadOnlyList<ReaderIssue> Issues, IReadOnlyList<string> Warnings)
{
    public bool IsComplete => Issues.Count == 0;
}

/// <summary>Un hallazgo que no pasó la validación del ancla, con el motivo. No se descarta en silencio: queda en el resultado.</summary>
public sealed record RejectedFinding(ReadFinding Finding, string Reason);

/// <summary>
/// La validación en cascada de D2 (port del <c>validate.py</c> del experimento de la pasada compartida) tras la de esquema
/// (<see cref="BatchReadingJson.Parse"/>):
/// <list type="number">
/// <item><b>cobertura</b>: cada fichero del lote en <c>files</c>; cada tipo y cada método del grafo con su entrada (por ruta y
/// nombre, sin la firma; las sobrecargas cuentan como una; un constructor vale también como <c>ctor</c>/<c>.ctor</c>/
/// <c>constructor</c>); cada semilla con veredicto (el texto del veredicto contiene el tipo, el nombre y el código del ISSUE, sin
/// el detalle que sigue a «:», o «DEAD»; dos ISSUE con el mismo código en un método los cubre un veredicto);</item>
/// <item><b>ancla literal</b>: la cita, sin espacios, está en las líneas <c>[start-3, end+3]</c> del fichero; si está en otro punto,
/// se reubican las líneas (la aparición más cercana) y se marca <see cref="AnchorCheck.Relocated"/>; si no aparece, el hallazgo se
/// rechaza. Una cita con la numeración del paquete (<c>12: …</c>) se acepta sin ella. Una instancia o el ancla de una relación que
/// no valen se quitan con aviso;</item>
/// <item><b>símbolo</b>: <see cref="ReadFinding.SymbolInGraph"/> dice si <c>Tipo.Miembro</c> existe en el grafo (no rechaza: el
/// escritor del registro lo resuelve de nuevo y cuenta los que no enlaza).</item>
/// </list>
/// Las rutas se leen de la raíz del repositorio y nunca fuera de ella.
/// </summary>
public sealed class ReadingValidator
{
    /// <summary>Holgura de líneas alrededor del rango del ancla (la del experimento).</summary>
    public const int LineSlack = 3;

    /// <summary>Caracteres (sin espacios) que ha de tener una cita para reubicarla: una más corta aparece en cualquier parte.</summary>
    public const int MinRelocationChars = 8;

    private readonly string _root;
    private readonly Encoding _encoding;
    private readonly SharedPassGraph _graph;
    private readonly Dictionary<string, SourceIndex?> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<GraphNodeFact>> _nodesByName;

    public ReadingValidator(string repositoryRoot, SharedPassGraph graph, Encoding? defaultEncoding = null)
    {
        _root = Path.GetFullPath(repositoryRoot);
        _graph = graph;
        _encoding = defaultEncoding ?? SourceEncoding.ResolveReadFallback(repositoryRoot, null);
        _nodesByName = [];
        foreach (var file in graph.FactFiles)
        {
            foreach (var node in graph.FactsOf(file))
            {
                if (!_nodesByName.TryGetValue(node.Name, out var list))
                    _nodesByName[node.Name] = list = [];
                list.Add(node);
            }
        }
    }

    public ReadingValidation Validate(BatchReading reading, BatchExpectations expected)
    {
        var issues = new List<ReaderIssue>();
        var warnings = new List<string>();

        // Cobertura de ficheros.
        var covered = new HashSet<string>(reading.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var file in expected.Files.Where(f => !covered.Contains(f)))
            issues.Add(new ReaderIssue(ReaderIssueKind.MissingFile, $"Falta el fichero {file} en 'files'.", file, file));

        // Cobertura de tipos y miembros.
        var typeKeys = new HashSet<(string, string)>(reading.Types.Select(t => (t.Path.ToLowerInvariant(), BaseName(t.Name))));
        var memberKeys = new HashSet<(string, string)>();
        foreach (var type in reading.Types)
        {
            var path = type.Path.ToLowerInvariant();
            foreach (var member in type.Members)
            {
                var name = MemberName(member.Name);
                memberKeys.Add((path, name));
                if (name is "ctor" or ".ctor" or "constructor" or "constructor(es)" || name.Equals("constructores", StringComparison.OrdinalIgnoreCase))
                    memberKeys.Add((path, BaseName(type.Name)));
            }
        }
        foreach (var file in expected.Files)
        {
            var path = file.ToLowerInvariant();
            foreach (var type in expected.Types.GetValueOrDefault(file) ?? [])
            {
                if (!typeKeys.Contains((path, BaseName(type))))
                    issues.Add(new ReaderIssue(ReaderIssueKind.MissingType, $"Falta el tipo {type} de {file} en 'types'.", file, type));
            }
            foreach (var (owner, name) in expected.Methods.GetValueOrDefault(file) ?? [])
            {
                if (!memberKeys.Contains((path, name)))
                {
                    var symbol = owner.Length > 0 ? $"{owner}.{name}" : name;
                    issues.Add(new ReaderIssue(ReaderIssueKind.MissingMember, $"Falta el miembro {symbol} de {file} en 'types[].members'.", file, symbol));
                }
            }
        }

        // Veredictos de las semillas.
        foreach (var seed in expected.Seeds.Where(s => !reading.SeedVerdicts.Any(v => Covers(v.Seed, s))))
            issues.Add(new ReaderIssue(ReaderIssueKind.MissingSeedVerdict, $"Falta el veredicto de la semilla «{seed.Text}» ({seed.File}).", seed.File, seed.Text));

        // Anclas de los hallazgos.
        var findings = new List<ReadFinding>();
        var rejected = new List<RejectedFinding>();
        foreach (var finding in reading.Findings)
        {
            var (anchor, reason) = Check(finding.Anchor);
            if (anchor == null)
            {
                rejected.Add(new RejectedFinding(finding, reason!));
                issues.Add(new ReaderIssue(ReaderIssueKind.Anchor,
                    $"El hallazgo {finding.LocalId} tiene un ancla inválida ({finding.Anchor.Path}:{finding.Anchor.StartLine}-{finding.Anchor.EndLine}): {reason}. " +
                    "Corrígela con una cita literal de esas líneas.", finding.Anchor.Path, finding.LocalId));
                continue;
            }

            var instances = new List<ReadAnchor>();
            foreach (var instance in finding.Instances)
            {
                var (checkedInstance, instanceReason) = Check(instance);
                if (checkedInstance != null)
                    instances.Add(checkedInstance);
                else
                    warnings.Add($"Hallazgo {finding.LocalId}: se quita la instancia {instance.Path}:{instance.StartLine}-{instance.EndLine} ({instanceReason}).");
            }
            findings.Add(finding with { Anchor = anchor, Instances = instances, SymbolInGraph = SymbolExists(finding.Symbol) });
        }

        var relations = new List<ReadRelation>();
        foreach (var relation in reading.Relations)
        {
            if (relation.Anchor == null)
            {
                relations.Add(relation);
                continue;
            }
            var (anchor, reason) = Check(relation.Anchor);
            if (anchor == null)
                warnings.Add($"Relación {relation.From} -{relation.Type}-> {relation.To}: se quita su ancla ({reason}).");
            relations.Add(relation with { Anchor = anchor });
        }

        return new ReadingValidation(reading with { Findings = findings, Relations = relations }, rejected, issues, warnings);
    }

    /// <summary>Si el veredicto <paramref name="seed"/> cubre la semilla esperada.</summary>
    internal static bool Covers(string seed, ExpectedSeed expected)
    {
        var dot = expected.Symbol.LastIndexOf('.');
        var (owner, name) = dot > 0 ? (expected.Symbol[..dot], expected.Symbol[(dot + 1)..]) : ("", expected.Symbol);
        if (!seed.Contains(name, StringComparison.OrdinalIgnoreCase) || (owner.Length > 0 && !seed.Contains(owner, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (expected.Marker == "DEAD?")
            return seed.Contains("DEAD", StringComparison.OrdinalIgnoreCase) || seed.Contains("muerto", StringComparison.OrdinalIgnoreCase);
        // El código del issue sin su detalle (cbm escribe «empty-catch: catch (X) vacío… (línea 113)»).
        var code = expected.Marker["ISSUE[".Length..^1].Split(':', 2)[0].Trim();
        return seed.Contains(code, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>El nombre de un miembro sin firma ni dueño: <c>Tipo.Render(string)</c> → <c>Render</c>.</summary>
    internal static string MemberName(string name)
    {
        var cut = name.IndexOfAny(['(', '<', '[', ' ', ':']);
        var head = (cut >= 0 ? name[..cut] : name).Trim();
        if (head.Length == 0)
            head = name.Trim();
        var dot = head.LastIndexOf('.');
        return dot >= 0 && dot < head.Length - 1 ? head[(dot + 1)..] : head;
    }

    /// <summary>El nombre de un tipo sin genéricos ni espacio de nombres.</summary>
    private static string BaseName(string name)
    {
        var cut = name.IndexOfAny(['<', '(', '[', ' ']);
        var head = (cut >= 0 ? name[..cut] : name).Trim();
        var dot = head.LastIndexOf('.');
        return dot >= 0 && dot < head.Length - 1 ? head[(dot + 1)..] : head;
    }

    private bool? SymbolExists(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || _nodesByName.Count == 0)
            return null;
        var head = symbol.Split('(', 2)[0].Trim();
        var segments = head.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Split('<', 2)[0]).ToArray();
        if (segments.Length == 0)
            return null;
        if (!_nodesByName.TryGetValue(segments[^1], out var nodes))
            return false;
        if (segments.Length == 1)
            return true;
        var owner = segments[^2];
        return nodes.Any(n => _graph.ShortName(n.QualifiedName).Split('.') is var parts && parts.Length >= 2 && parts[^2] == owner);
    }

    // ───────────────────────────── Anclas ─────────────────────────────

    /// <summary>El ancla comprobada (<see cref="AnchorCheck.Valid"/> o reubicada), o null con el motivo.</summary>
    internal (ReadAnchor? Anchor, string? Reason) Check(ReadAnchor anchor)
    {
        var source = Source(anchor.Path);
        if (source == null)
            return (null, "el fichero no existe en el repositorio");
        var quote = Normalize(anchor.Quote);
        if (quote.Length == 0)
            return (null, "la cita está vacía");

        var (s, e) = (anchor.StartLine, anchor.EndLine);
        if (s >= 1 && e >= s && e <= source.LineCount && source.Window(s - LineSlack, e + LineSlack).Contains(quote, StringComparison.Ordinal))
            return (anchor with { Check = AnchorCheck.Valid }, null);

        // La numeración del paquete copiada en la cita.
        var unnumbered = Normalize(StripNumbering(anchor.Quote));
        if (unnumbered.Length > 0 && unnumbered != quote && s >= 1 && e >= s && e <= source.LineCount
            && source.Window(s - LineSlack, e + LineSlack).Contains(unnumbered, StringComparison.Ordinal))
            return (anchor with { Quote = StripNumbering(anchor.Quote), Check = AnchorCheck.Valid }, null);

        foreach (var candidate in unnumbered.Length > 0 && unnumbered != quote ? new[] { quote, unnumbered } : [quote])
        {
            if (candidate.Length < MinRelocationChars || source.Locate(candidate, Math.Max(1, s)) is not { } found)
                continue;
            return (anchor with
            {
                StartLine = found.Start,
                EndLine = found.End,
                Quote = candidate == quote ? anchor.Quote : StripNumbering(anchor.Quote),
                Check = AnchorCheck.Relocated,
                OriginalStartLine = s,
                OriginalEndLine = e
            }, null);
        }

        return (null, s < 1 || e < s || e > source.LineCount
            ? $"el rango no existe (el fichero tiene {source.LineCount} líneas) y la cita no aparece en él"
            : "la cita no aparece en el fichero");
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Quita <c>123: </c> del principio de cada línea si todas lo llevan.</summary>
    private static string StripNumbering(string quote)
    {
        var lines = quote.Replace("\r\n", "\n").Split('\n');
        var numbered = System.Text.RegularExpressions.Regex.IsMatch(lines[0], @"^\s*\d+:\s?")
            && lines.Where(l => l.Trim().Length > 0).All(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*\d+:\s?"));
        return numbered ? string.Join("\n", lines.Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\s*\d+:\s?", ""))) : quote;
    }

    private SourceIndex? Source(string path)
    {
        lock (_lock)
        {
            if (_sources.TryGetValue(path, out var cached))
                return cached;
            return _sources[path] = Load(path);
        }
    }

    private SourceIndex? Load(string path)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
            var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return null;
            return new SourceIndex(PacketBuilder.SplitLines(SourceEncoding.ReadFile(full, _encoding).Text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Las líneas de un fichero sin espacios, concatenadas, con la línea de cada carácter.</summary>
    private sealed class SourceIndex
    {
        private readonly string _text;
        private readonly int[] _lineOf;
        private readonly int[] _lineStart;

        public SourceIndex(IReadOnlyList<string> lines)
        {
            LineCount = lines.Count;
            var sb = new StringBuilder();
            var lineOf = new List<int>();
            _lineStart = new int[lines.Count + 2];
            for (var i = 0; i < lines.Count; i++)
            {
                _lineStart[i + 1] = sb.Length;
                foreach (var c in lines[i])
                {
                    if (char.IsWhiteSpace(c))
                        continue;
                    sb.Append(c);
                    lineOf.Add(i + 1);
                }
            }
            _lineStart[lines.Count + 1] = sb.Length;
            _text = sb.ToString();
            _lineOf = [.. lineOf];
        }

        public int LineCount { get; }

        /// <summary>El texto sin espacios de las líneas <paramref name="from"/>-<paramref name="to"/> (recortadas al fichero).</summary>
        public string Window(int from, int to)
        {
            from = Math.Clamp(from, 1, Math.Max(1, LineCount));
            to = Math.Clamp(to, from, Math.Max(1, LineCount));
            return LineCount == 0 ? "" : _text[_lineStart[from].._lineStart[to + 1]];
        }

        /// <summary>Las líneas de la aparición de <paramref name="quote"/> más cercana a <paramref name="near"/>, o null.</summary>
        public (int Start, int End)? Locate(string quote, int near)
        {
            (int, int)? best = null;
            var bestDistance = int.MaxValue;
            for (var at = _text.IndexOf(quote, StringComparison.Ordinal); at >= 0; at = _text.IndexOf(quote, at + 1, StringComparison.Ordinal))
            {
                var (start, end) = (_lineOf[at], _lineOf[at + quote.Length - 1]);
                var distance = Math.Abs(start - near);
                if (distance < bestDistance)
                    (best, bestDistance) = ((start, end), distance);
            }
            return best;
        }
    }
}
