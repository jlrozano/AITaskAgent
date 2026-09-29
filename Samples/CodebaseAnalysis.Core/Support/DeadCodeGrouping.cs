using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Support;

/// <summary>Un candidato de código muerto para agrupar: la fila de la faceta y lo que la agrupación necesita de ella.</summary>
/// <param name="Row">La fila tal como sale en la faceta.</param>
/// <param name="Name">Nombre que se tokeniza: el del tipo o, en un campo, el del tipo que lo declara.</param>
/// <param name="File">Ruta relativa al repositorio, con <c>/</c>.</param>
/// <param name="Package">Paquete o espacio de nombres, si se conoce (del <c>fqn</c>).</param>
/// <param name="ClassNames">Nombres completos con que la configuración podría nombrar el tipo (su <c>fqn</c>, su QN).</param>
public sealed record GroupCandidate(IReadOnlyDictionary<string, object?> Row, string Name, string File, string? Package, IReadOnlyList<string> ClassNames);

/// <summary>Un grupo de candidatos con un patrón común.</summary>
public sealed record CandidateGroup(
    string Kind,
    string Pattern,
    string? NamePrefix,
    string? NameSuffix,
    string? Directory,
    string? Package,
    IReadOnlyList<GroupCandidate> Members,
    IReadOnlyList<(string Directory, int Count)> TopDirectories)
{
    public bool LikelyDynamic => Evidence.Count > 0;

    public List<JsonObject> Evidence { get; } = [];
}

/// <summary>
/// Capa 2 de la propuesta (d): agrupa los candidatos de código muerto que siguen un patrón, porque un grupo grande con un
/// nombre o un paquete común es señal de uso dinámico (una factoría que compone el nombre), y los resume para no listar
/// cientos de filas. Algoritmo determinista:
/// <list type="number">
/// <item>cada nombre se parte en tokens CamelCase (<c>SQLCustomerJdbcImpl</c> → <c>SQL</c>, <c>Customer</c>, <c>Jdbc</c>,
/// <c>Impl</c>); sus claves son cada prefijo y cada sufijo de tokens que no es el nombre entero;</item>
/// <item>se toma la clave con más candidatos sin agrupar, si llega a <see cref="MinGroupSize"/> (a igualdad, la de más
/// tokens, el sufijo antes que el prefijo y el orden ordinal), y se alarga un token mientras la clave más larga conserve al
/// menos <see cref="ExtensionShare"/> de sus miembros: así sale el sufijo común más largo (<c>JdbcImpl</c> y no
/// <c>Impl</c>) y los que no lo tienen vuelven a la bolsa;</item>
/// <item>se repite hasta que ninguna clave llega al umbral; con los que quedan, los directorios con al menos
/// <see cref="MinGroupSize"/> candidatos forman grupo por directorio;</item>
/// <item>cada grupo lleva el prefijo y el sufijo comunes a todos sus miembros, el directorio común más largo y el paquete
/// común.</item>
/// </list>
/// El cruce con <see cref="DynamicInstantiationScan"/> marca <c>likely_dynamic</c> un grupo cuyo sufijo, prefijo o paquete
/// aparece en un sitio de instanciación (su línea, las anteriores o un literal de su fichero) o del que la configuración
/// nombra algún miembro. Es una heurística y la faceta lo dice.
/// </summary>
public static partial class DeadCodeGrouping
{
    /// <summary>Candidatos a partir de los que un patrón forma grupo y se resume.</summary>
    public const int MinGroupSize = 10;

    /// <summary>Parte de los miembros que tiene que conservar una clave más larga para sustituir a la corta.</summary>
    public const double ExtensionShare = 0.8;

    /// <summary>Ejemplos por grupo.</summary>
    public const int ExamplesPerGroup = 3;

    /// <summary>Longitud mínima de un sufijo o de un prefijo para buscarlo en un sitio de instanciación.</summary>
    private const int MinAffixChars = 4;

    public const string HeuristicNote =
        "Heurística determinista: los candidatos con un prefijo o sufijo común del nombre (tokens CamelCase) o un mismo directorio, " +
        "desde " + "{0}" + " candidatos, van resumidos en un grupo con su patrón, su recuento y 3 ejemplos, y no se listan uno a uno. " +
        "Un grupo grande con patrón común es señal de uso dinámico (una factoría que compone el nombre, reflexión o configuración). " +
        "likely_dynamic = true dice que el patrón aparece en un punto de instanciación por nombre o que la configuración nombra " +
        "algún miembro, con la evidencia; no prueba uso ni desuso: hay que verificarlo en el código.";

    private sealed record Key(bool Suffix, IReadOnlyList<string> Tokens)
    {
        public string Text => string.Concat(Tokens);
    }

    public static IReadOnlyList<string> Tokenize(string name) =>
        [.. TokenPattern().Matches(name).Select(m => m.Value)];

    /// <summary>Agrupa <paramref name="candidates"/>; los que no entran en ningún grupo salen en <paramref name="ungrouped"/>, en su orden.</summary>
    public static IReadOnlyList<CandidateGroup> Group(IReadOnlyList<GroupCandidate> candidates, out IReadOnlyList<GroupCandidate> ungrouped)
    {
        var tokens = candidates.ToDictionary(c => c, c => Tokenize(c.Name));
        var remaining = new List<GroupCandidate>(candidates);
        var groups = new List<CandidateGroup>();

        while (true)
        {
            var best = BestKey(remaining, tokens, null);
            if (best is not { } chosen || chosen.Members.Count < MinGroupSize)
                break;

            var key = chosen.Key;
            var members = chosen.Members;
            while (BestKey(members, tokens, key) is { } longer && longer.Members.Count >= ExtensionShare * members.Count)
            {
                key = longer.Key;
                members = longer.Members;
            }
            if (members.Count < MinGroupSize)
                break;

            groups.Add(Describe("name", members, tokens));
            var taken = members.ToHashSet();
            remaining.RemoveAll(taken.Contains);
        }

        foreach (var directory in remaining.GroupBy(c => DirectoryOf(c.File), StringComparer.Ordinal)
                     .Where(g => g.Count() >= MinGroupSize)
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList())
        {
            var members = directory.ToList();
            groups.Add(Describe("directory", members, tokens));
            var taken = members.ToHashSet();
            remaining.RemoveAll(taken.Contains);
        }

        ungrouped = remaining;
        return [.. groups.OrderByDescending(g => g.Members.Count).ThenBy(g => g.Pattern, StringComparer.Ordinal)];
    }

    /// <summary>
    /// La clave con más miembros entre <paramref name="pool"/>; con <paramref name="extending"/>, solo las del mismo lado que
    /// la alargan un token.
    /// </summary>
    private static (Key Key, List<GroupCandidate> Members)? BestKey(
        IEnumerable<GroupCandidate> pool, Dictionary<GroupCandidate, IReadOnlyList<string>> tokens, Key? extending)
    {
        var counts = new Dictionary<(bool, string), (Key Key, List<GroupCandidate> Members)>();
        foreach (var candidate in pool)
        {
            var t = tokens[candidate];
            for (var k = 1; k < t.Count; k++)
            {
                foreach (var suffix in new[] { true, false })
                {
                    if (extending != null && (suffix != extending.Suffix || k != extending.Tokens.Count + 1))
                        continue;
                    var keyTokens = suffix ? t.Skip(t.Count - k).ToList() : t.Take(k).ToList();
                    if (extending != null && !(suffix ? keyTokens.Skip(1) : keyTokens.Take(k - 1)).SequenceEqual(extending.Tokens, StringComparer.Ordinal))
                        continue;
                    var id = (suffix, string.Join('\u0001', keyTokens));
                    if (!counts.TryGetValue(id, out var entry))
                        counts[id] = entry = (new Key(suffix, keyTokens), []);
                    entry.Members.Add(candidate);
                }
            }
        }

        return counts.Count == 0
            ? null
            : counts.Values
                .OrderByDescending(e => e.Members.Count)
                .ThenByDescending(e => e.Key.Tokens.Count)
                .ThenByDescending(e => e.Key.Suffix)
                .ThenBy(e => e.Key.Text, StringComparer.Ordinal)
                .First();
    }

    private static CandidateGroup Describe(string kind, List<GroupCandidate> members, Dictionary<GroupCandidate, IReadOnlyList<string>> tokens)
    {
        var lists = members.Select(m => tokens[m]).ToList();
        var shortest = lists.Min(l => l.Count);
        var prefix = new List<string>();
        for (var i = 0; i < shortest - 1 && lists.All(l => l[i] == lists[0][i]); i++)
            prefix.Add(lists[0][i]);
        var suffix = new List<string>();
        for (var i = 1; i <= shortest - prefix.Count && lists.All(l => l[^i] == lists[0][^i]); i++)
            suffix.Insert(0, lists[0][^i]);

        var directory = CommonPrefix(members.Select(m => DirectoryOf(m.File).Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList(), "/");
        var package = members.All(m => !string.IsNullOrEmpty(m.Package))
            ? CommonPrefix(members.Select(m => m.Package!.Split('.')).ToList(), ".")
            : null;
        var namePrefix = prefix.Count > 0 ? string.Concat(prefix) : null;
        var nameSuffix = suffix.Count > 0 ? string.Concat(suffix) : null;
        var sameName = members.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() == 1;
        if (sameName)
        {
            // Todos con el mismo nombre (los campos de un mismo tipo): el patrón es ese nombre.
            namePrefix = null;
            nameSuffix = members[0].Name;
        }
        var pattern = $"{(directory.Length > 0 ? directory + "/**/" : "**/")}{(sameName ? nameSuffix : $"{namePrefix}*{nameSuffix}")}";
        var top = members.GroupBy(m => DirectoryOf(m.File), StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.Ordinal).Take(3).ToList();

        return new CandidateGroup(kind, pattern, namePrefix, nameSuffix, directory.Length > 0 ? directory : null,
            string.IsNullOrEmpty(package) ? null : package, members, top);
    }

    private static string CommonPrefix(List<string[]> paths, string separator)
    {
        if (paths.Count == 0)
            return "";
        var common = new List<string>();
        for (var i = 0; paths.All(p => p.Length > i) && paths.All(p => p[i] == paths[0][i]); i++)
            common.Add(paths[0][i]);
        return string.Join(separator, common);
    }

    private static string DirectoryOf(string file)
    {
        var slash = file.Replace('\\', '/').LastIndexOf('/');
        return slash > 0 ? file[..slash].Replace('\\', '/') : "";
    }

    // ------------------------------------------------------------ cruce con la instanciación dinámica

    /// <summary>
    /// Marca los grupos con evidencia de uso dinámico en <paramref name="scan"/>: el sufijo o el prefijo del grupo (de al menos
    /// <see cref="MinAffixChars"/> caracteres) o su paquete (con al menos dos segmentos o, sin paquete, los dos últimos
    /// segmentos de su directorio con punto) dentro del contexto de un sitio o en un literal de cadena del fichero del sitio;
    /// si no, un literal del contexto que sea un final de tokens del sufijo o un comienzo de tokens del prefijo (la pieza fija
    /// de un nombre compuesto, como <c>+ "Impl"</c>), también de al menos <see cref="MinAffixChars"/> caracteres; y miembros
    /// que la configuración nombra. Los términos se buscan tal cual, distinguiendo mayúsculas.
    /// </summary>
    public static void CrossWith(IReadOnlyList<CandidateGroup> groups, DynamicInstantiationScan? scan)
    {
        if (scan == null)
            return;
        var named = NamedLookup(scan);

        foreach (var group in groups)
        {
            var terms = new List<(string Role, string Term)>();
            if (group.NameSuffix is { Length: >= MinAffixChars } s) terms.Add(("sufijo", s));
            if (group.NamePrefix is { Length: >= MinAffixChars } p) terms.Add(("prefijo", p));
            if (group.Package is { } pkg && pkg.Contains('.'))
                terms.Add(("paquete", pkg));
            else if (group.Directory?.Split('/') is { Length: >= 2 } dirs)
                terms.Add(("directorio", $"{dirs[^2]}.{dirs[^1]}"));

            // Piezas con que una factoría compone el nombre: los finales de tokens del sufijo (Impl de JdbcImpl) y los comienzos
            // del prefijo, como literal junto al sitio (nombre = paquete + modulo + clase + "Impl").
            var pieces = new List<(string Piece, string Of)>();
            if (group.NameSuffix is { } fullSuffix)
            {
                var t = Tokenize(fullSuffix);
                for (var k = 1; k < t.Count; k++)
                    pieces.Add((string.Concat(t.Skip(t.Count - k)), $"el final del sufijo «{fullSuffix}»"));
            }
            if (group.NamePrefix is { } fullPrefix)
            {
                var t = Tokenize(fullPrefix);
                for (var k = 1; k < t.Count; k++)
                    pieces.Add((string.Concat(t.Take(k)), $"el comienzo del prefijo «{fullPrefix}»"));
            }
            pieces.RemoveAll(p => p.Piece.Length < MinAffixChars);

            foreach (var site in scan.Sites)
            {
                string? matched = null;
                foreach (var (role, term) in terms)
                {
                    var where = site.Context.Contains(term, StringComparison.Ordinal) ? "en la línea o las anteriores"
                        : scan.LiteralsBySiteFile.TryGetValue(site.File, out var literals) && literals.Any(l => l.Contains(term, StringComparison.Ordinal))
                            ? "en un literal del mismo fichero" : null;
                    if (where != null)
                    {
                        matched = $"{role} «{term}» {where}";
                        break;
                    }
                }
                if (matched == null && pieces.Count > 0)
                {
                    var contextLiterals = ContextLiteralPattern().Matches(site.Context).Select(m => m.Groups["v"].Value).ToHashSet(StringComparer.Ordinal);
                    if (pieces.FirstOrDefault(p => contextLiterals.Contains(p.Piece)) is { Piece: not null } piece)
                        matched = $"el literal «{piece.Piece}» con que se compone el nombre, en la línea o las anteriores, es {piece.Of}";
                }
                if (matched == null)
                    continue;

                group.Evidence.Add(new JsonObject
                {
                    ["type"] = "instantiation_site",
                    ["file"] = site.File,
                    ["line"] = site.Line,
                    ["kind"] = site.Kind,
                    ["matched"] = matched,
                    ["code"] = site.Code
                });
            }

            var namedMembers = group.Members.Select(m => (Member: m, Named: FirstNamed(m, named))).Where(x => x.Named != null).ToList();
            if (namedMembers.Count > 0)
            {
                group.Evidence.Add(new JsonObject
                {
                    ["type"] = "named_in_configuration",
                    ["members_named"] = namedMembers.Count,
                    ["of"] = group.Members.Count,
                    ["files"] = new JsonArray([.. namedMembers.Select(x => x.Named!.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5)
                        .Select(f => (JsonNode)JsonValue.Create(f)!)]),
                    ["example"] = $"{namedMembers[0].Named!.Name} en {namedMembers[0].Named!.File}:{namedMembers[0].Named!.Line}"
                });
            }
        }
    }

    /// <summary>Primera clase nombrada (en configuración o como literal) que es este candidato, o null.</summary>
    public static NamedClass? FirstNamed(GroupCandidate candidate, IReadOnlyDictionary<string, NamedClass> named)
    {
        foreach (var name in candidate.ClassNames)
        {
            // Por nombre completo o por un sufijo de al menos dos segmentos: el QN de cbm lleva delante la ruta del fichero.
            var parts = name.Replace('$', '.').Split('.');
            for (var i = 0; i <= parts.Length - 2; i++)
            {
                if (named.TryGetValue(string.Join('.', parts[i..]), out var found))
                    return found;
            }
        }
        return null;
    }

    /// <summary>Clases nombradas por nombre, la primera aparición de cada una.</summary>
    public static IReadOnlyDictionary<string, NamedClass> NamedLookup(DynamicInstantiationScan? scan)
    {
        var lookup = new Dictionary<string, NamedClass>(StringComparer.Ordinal);
        foreach (var n in scan?.NamedClasses ?? [])
            lookup.TryAdd(n.Name, n);
        return lookup;
    }

    // ------------------------------------------------------------ faceta

    /// <summary>
    /// La faceta de grupos: <c>[]</c> sin candidatos; si no, el aviso de la heurística, los totales y los grupos con su patrón,
    /// recuento, ejemplos y, si la hay, la evidencia de uso dinámico. <paramref name="example"/> da la fila reducida de cada ejemplo.
    /// </summary>
    public static string ToFacetJson(IReadOnlyList<CandidateGroup> groups, int totalCandidates, string individualFacet, int individualShown,
        Func<GroupCandidate, JsonObject> example, JsonSerializerOptions options)
    {
        if (totalCandidates == 0)
            return "[]";

        var facet = new JsonObject
        {
            ["note"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, HeuristicNote, MinGroupSize),
            ["total_candidates"] = totalCandidates,
            ["grouped_candidates"] = groups.Sum(g => g.Members.Count),
            ["listed_individually"] = $"{individualShown} de los {totalCandidates - groups.Sum(g => g.Members.Count)} sin grupo, en {individualFacet}",
            ["groups"] = new JsonArray([.. groups.Select(g =>
            {
                var node = new JsonObject
                {
                    ["pattern"] = g.Pattern,
                    ["grouped_by"] = g.Kind == "name" ? "prefijo o sufijo común del nombre" : "directorio",
                    ["count"] = g.Members.Count,
                    ["name_prefix"] = g.NamePrefix,
                    ["name_suffix"] = g.NameSuffix,
                    ["directory"] = g.Directory,
                    ["package"] = g.Package,
                    ["top_directories"] = g.Directory == null || g.TopDirectories.Count > 1
                        ? new JsonArray([.. g.TopDirectories.Select(d => (JsonNode)new JsonObject { ["directory"] = d.Directory, ["count"] = d.Count })])
                        : null,
                    ["examples"] = new JsonArray([.. g.Members.Take(ExamplesPerGroup).Select(m => (JsonNode)example(m))]),
                    ["likely_dynamic"] = g.LikelyDynamic
                };
                if (g.LikelyDynamic)
                    node["evidence"] = new JsonArray([.. g.Evidence.Take(5).Select(e => (JsonNode)e.DeepClone())]);
                if (g.Evidence.Count > 5)
                    node["evidence_total"] = g.Evidence.Count;
                return (JsonNode)node;
            })])
        };
        RemoveNulls(facet);
        return facet.ToJsonString(options);
    }

    private static void RemoveNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
                    obj.Remove(key);
                foreach (var (_, value) in obj)
                    RemoveNulls(value);
                break;
            case JsonArray array:
                foreach (var item in array)
                    RemoveNulls(item);
                break;
        }
    }

    [GeneratedRegex(@"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"""(?<v>(?:[^""\\\r\n]|\\.)*)""")]
    private static partial Regex ContextLiteralPattern();
}
