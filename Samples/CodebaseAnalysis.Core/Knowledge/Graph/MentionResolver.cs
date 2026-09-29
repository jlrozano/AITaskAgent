using CodebaseAnalysis.Core.Documents;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Knowledge.Graph;

/// <summary>Un nodo de código candidato a ser mencionado.</summary>
public sealed record CodeNode(long Id, string Label, string Name, string QualifiedName, string FilePath, int StartLine, int EndLine);

/// <summary>Una mención resuelta: la arista <c>DOC_DESCRIBES</c> que se crea.</summary>
public sealed record ResolvedMention(long TargetId, string MentionType, string Mention, double Confidence, bool Ambiguous);

/// <summary>Tipos de mención (plan v2 §8.1.2).</summary>
public static class MentionTypes
{
    public const string CodeSpan = "code_span";
    public const string WikiLink = "wiki_link";
    public const string Path = "path";
    public const string Identifier = "identifier";
}

/// <summary>
/// Resolución determinista y genérica de menciones de código en el texto de una sección (plan v2 §8.1.2): spans en
/// línea, <c>[[wiki]]</c>, rutas con <c>:línea</c> opcional e identificadores PascalCase o snake_case. Con más de tres
/// candidatos no se enlaza; con dos o tres, todos con <c>ambiguous</c> y la confianza a la mitad. Sin listas de nombres
/// de ningún repositorio.
/// </summary>
public sealed partial class MentionResolver
{
    public const int MaxEdgesPerSection = 50;
    public const int MaxEdgesPerSource = 500;
    public const int MaxCandidates = 3;

    /// <summary>Etiquetas de código que se pueden enlazar.</summary>
    public static IReadOnlyList<string> CodeLabels { get; } =
    [
        "Class", "Interface", "Struct", "Record", "Enum", "Trait", "Function", "Method", "Property", "Sub", "Module", "Form",
        "Route", "AccessTable", "AccessQuery", "Table", "Query", "CsProject", "DiRegistration", "Flow"
    ];

    private static readonly HashSet<string> CodeExtensions = DocumentTypeRegistry.All
        .Where(t => t.Family == DocumentFamilies.Code)
        .SelectMany(t => t.Extensions)
        .Select(e => e.TrimStart('.'))
        .Concat(["json", "xml", "yaml", "yml", "html", "md"])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly List<CodeNode> _nodes;
    private readonly Dictionary<string, List<CodeNode>> _byNameIgnoreCase;
    private readonly Dictionary<string, List<CodeNode>> _byName;

    public MentionResolver(IEnumerable<CodeNode> nodes)
    {
        _nodes = nodes.OrderBy(n => n.Id).ToList();
        _byNameIgnoreCase = new Dictionary<string, List<CodeNode>>(StringComparer.OrdinalIgnoreCase);
        _byName = new Dictionary<string, List<CodeNode>>(StringComparer.Ordinal);
        foreach (var node in _nodes)
        {
            Add(_byNameIgnoreCase, node.Name, node);
            Add(_byName, node.Name, node);
            var last = LastSegment(node.QualifiedName);
            if (!string.Equals(last, node.Name, StringComparison.OrdinalIgnoreCase))
                Add(_byNameIgnoreCase, last, node);
        }
    }

    /// <summary>Carga los nodos de código del grafo.</summary>
    public static async Task<MentionResolver> LoadAsync(SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            SELECT id, label, name, qualified_name, coalesce(nullif(file_path, ''), file, ''),
                   coalesce(nullif(start_line, 0), line_start, 0), coalesce(nullif(end_line, 0), line_end, 0)
            FROM nodes WHERE label IN ({string.Join(", ", CodeLabels.Select(l => $"'{l}'"))});";
        var nodes = new List<CodeNode>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new CodeNode(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Replace('\\', '/'),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5), reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
        }
        return new MentionResolver(nodes);
    }

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"`([^`\n]{1,200})`")]
    private static partial Regex CodeSpanRegex();

    [GeneratedRegex(@"\[\[([^\]\n|]{1,200})(?:\|[^\]\n]*)?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"(?<![\w/.\-])((?:[\w.\-]+/)*[\w\-]+(?:\.[\w\-]+)*\.(?<ext>[A-Za-z]{1,10}))(?::(?<line>\d{1,7}))?")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"\b(?:[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+|[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+)\b")]
    private static partial Regex IdentifierRegex();

    /// <summary>Aristas de una sección, en orden de id de nodo y con el tope por sección.</summary>
    public IReadOnlyList<ResolvedMention> Resolve(string sectionText)
    {
        var found = new Dictionary<long, ResolvedMention>();
        var prose = new List<string>();
        var inFence = false;
        foreach (var line in sectionText.Split('\n'))
        {
            if (FenceRegex().IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }
            if (!inFence)
                prose.Add(line);
        }

        var text = string.Join('\n', prose);
        foreach (Match m in CodeSpanRegex().Matches(text))
        {
            var span = m.Groups[1].Value.Trim();
            if (TryPath(span, out var path, out var line))
                AddAll(found, MentionTypes.Path, span, ByPath(path, line), 0.9);
            else
                AddAll(found, MentionTypes.CodeSpan, span, BySymbol(span), 0.95);
        }

        foreach (Match m in WikiLinkRegex().Matches(text))
            AddAll(found, MentionTypes.WikiLink, m.Groups[1].Value.Trim(), BySymbol(m.Groups[1].Value.Trim()), 0.95);

        // Fuera de spans: rutas e identificadores.
        var outside = CodeSpanRegex().Replace(WikiLinkRegex().Replace(text, " "), " ");
        foreach (Match m in PathRegex().Matches(outside))
        {
            if (!CodeExtensions.Contains(m.Groups["ext"].Value) || !m.Value.Contains('/') && !m.Groups["line"].Success)
                continue;
            int? line = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null;
            AddAll(found, MentionTypes.Path, m.Value, ByPath(m.Groups[1].Value, line), 0.9);
        }

        foreach (Match m in IdentifierRegex().Matches(PathRegex().Replace(outside, " ")))
        {
            if (m.Value.Length < 6)
                continue;
            AddAll(found, MentionTypes.Identifier, m.Value, _byName.TryGetValue(m.Value, out var exact) ? exact : [], 0.8);
        }

        return found.Values.OrderBy(r => r.TargetId).Take(MaxEdgesPerSection).ToList();
    }

    private static void AddAll(Dictionary<long, ResolvedMention> found, string type, string mention, IReadOnlyList<CodeNode> candidates, double confidence)
    {
        var distinct = candidates.DistinctBy(c => c.Id).ToList();
        if (distinct.Count == 0 || distinct.Count > MaxCandidates)
            return;

        var ambiguous = distinct.Count > 1;
        foreach (var node in distinct)
        {
            var resolved = new ResolvedMention(node.Id, type, mention, ambiguous ? confidence / 2 : confidence, ambiguous);
            if (!found.TryGetValue(node.Id, out var existing) || existing.Confidence < resolved.Confidence)
                found[node.Id] = resolved;
        }
    }

    /// <summary>Por nombre o sufijo de <c>qualified_name</c>, sin distinguir mayúsculas; <c>Foo()</c> y <c>Foo&lt;T&gt;</c> cuentan como <c>Foo</c>.</summary>
    private IReadOnlyList<CodeNode> BySymbol(string span)
    {
        var symbol = span.Trim();
        var paren = symbol.IndexOf('(');
        if (paren > 0) symbol = symbol[..paren];
        var generic = symbol.IndexOf('<');
        if (generic > 0) symbol = symbol[..generic];
        symbol = symbol.Trim().Replace("::", ".");
        if (symbol.Length == 0 || symbol.Any(char.IsWhiteSpace))
            return [];

        if (!symbol.Contains('.'))
            return _byNameIgnoreCase.TryGetValue(symbol, out var list) ? list : [];

        return _nodes.Where(n => string.Equals(n.QualifiedName, symbol, StringComparison.OrdinalIgnoreCase)
                                 || n.QualifiedName.EndsWith("." + symbol, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(n.Name, symbol, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Nodos del fichero que termina en <paramref name="path"/> por segmentos. Con línea, el de menor rango que la
    /// contiene; sin ella, los de primer nivel (los que no están dentro de otro del mismo fichero).
    /// </summary>
    private IReadOnlyList<CodeNode> ByPath(string path, int? line)
    {
        path = path.Replace('\\', '/').TrimStart('.', '/');
        var inFile = _nodes.Where(n => n.FilePath.Length > 0
                                       && (string.Equals(n.FilePath, path, StringComparison.OrdinalIgnoreCase)
                                           || n.FilePath.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (inFile.Count == 0)
            return [];

        if (line is int l)
        {
            var containing = inFile.Where(n => n.StartLine <= l && l <= Math.Max(n.EndLine, n.StartLine)).ToList();
            if (containing.Count == 0)
                return [];
            var smallest = containing.Min(n => n.EndLine - n.StartLine);
            return containing.Where(n => n.EndLine - n.StartLine == smallest).ToList();
        }

        return inFile.Where(n => !inFile.Any(o => o.Id != n.Id && o.FilePath == n.FilePath
                                                   && o.StartLine <= n.StartLine && n.EndLine <= o.EndLine
                                                   && o.EndLine - o.StartLine > n.EndLine - n.StartLine))
            .ToList();
    }

    private static bool TryPath(string span, out string path, out int? line)
    {
        path = string.Empty;
        line = null;
        var m = PathRegex().Match(span);
        if (!m.Success || m.Index != 0 || m.Length != span.Length || !CodeExtensions.Contains(m.Groups["ext"].Value))
            return false;
        if (!span.Contains('/') && !m.Groups["line"].Success)
            return false;

        path = m.Groups[1].Value;
        line = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null;
        return true;
    }

    private static string LastSegment(string qualifiedName)
    {
        var index = qualifiedName.LastIndexOfAny(['.', ':', '/', '#']);
        return index >= 0 && index < qualifiedName.Length - 1 ? qualifiedName[(index + 1)..] : qualifiedName;
    }

    private static void Add(Dictionary<string, List<CodeNode>> index, string key, CodeNode node)
    {
        if (string.IsNullOrEmpty(key))
            return;
        if (!index.TryGetValue(key, out var list))
            index[key] = list = [];
        list.Add(node);
    }
}
