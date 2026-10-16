namespace CodebaseAnalysis.Core.Preparation;

/// <summary>Un elemento del que se extrae vocabulario: un tipo, con el nombre de su clase y la carpeta de su fichero.</summary>
/// <param name="Name">Nombre simple del tipo.</param>
/// <param name="Directory">Carpeta de su fichero relativa al repositorio, con <c>/</c>.</param>
public sealed record VocabularyMember(string Name, string Directory);

/// <summary>
/// El vocabulario de un repositorio (T9): los términos de los nombres de clase y de carpeta de todos sus tipos, con cuántos
/// tipos lleva cada uno. Se calcula por repositorio, así que ningún término de dominio vive en el código: los únicos
/// términos que se excluyen de antemano son los genéricos técnicos de <see cref="PreparationOptions.StopTokens"/> y los que
/// llevan más de la mitad de los tipos (<see cref="MaxDocumentFrequency"/>), que no distinguen nada en ese repositorio.
/// </summary>
public sealed class VocabularyIndex
{
    /// <summary>Un término que lleva más de esta fracción de los tipos de un repositorio no distingue una capacidad de otra.</summary>
    public const double MaxDocumentFrequency = 0.5;

    private readonly string[][] _tokens;
    private readonly string[] _directories;
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);

    /// <param name="members">Los elementos de los que se extrae vocabulario.</param>
    /// <param name="options">Aporta la lista de términos genéricos.</param>
    /// <param name="useDirectoryTokens">
    /// Si los términos de la carpeta cuentan además de los del nombre. Con false solo cuenta el nombre de la clase, que es lo que
    /// dice de qué trata el tipo: una carpeta dice dónde está.
    /// </param>
    /// <param name="maxTermMembers">
    /// Los miembros que, como mucho, puede llevar un término: uno más extendido que cualquier subsistema posible es un paraguas
    /// (<c>parametrizacion</c> en un repositorio donde casi todo cuelga de ahí) y no distingue nada.
    /// </param>
    public VocabularyIndex(IReadOnlyList<VocabularyMember> members, PreparationOptions options, bool useDirectoryTokens = true, int maxTermMembers = int.MaxValue)
    {
        var stop = new HashSet<string>(options.StopTokens.Select(t => NameTokenizer.Stem(t.Trim().ToLowerInvariant())), StringComparer.Ordinal);
        _directories = [.. members.Select(m => m.Directory)];

        var raw = members
            .Select(m => (useDirectoryTokens ? NameTokenizer.DistinctTokens(m.Name, m.Directory) : NameTokenizer.DistinctTokens(m.Name))
                .Where(t => t.Length >= NameTokenizer.MinTokenLength && !stop.Contains(t)).ToArray())
            .ToArray();

        foreach (var tokens in raw)
        {
            foreach (var token in tokens)
                _documentFrequency[token] = _documentFrequency.GetValueOrDefault(token) + 1;
        }

        // Un término que lleva a un único tipo no relaciona nada, y uno que lleva a casi todos tampoco.
        var ceiling = Math.Max(1, Math.Min(maxTermMembers, (int)Math.Floor(members.Count * MaxDocumentFrequency)));
        _tokens = [.. raw.Select(tokens => tokens.Where(t => _documentFrequency[t] >= 2 && _documentFrequency[t] <= ceiling).ToArray())];
        foreach (var term in _documentFrequency.Where(p => p.Value < 2 || p.Value > ceiling).Select(p => p.Key).ToList())
            _documentFrequency.Remove(term);
    }

    public int MemberCount => _tokens.Length;

    /// <summary>Los términos útiles (ni genéricos, ni únicos, ni omnipresentes) del miembro, en orden de aparición.</summary>
    public IReadOnlyList<string> TokensOf(int member) => _tokens[member];

    /// <summary>En cuántos miembros aparece <paramref name="term"/>; 0 si no es un término útil.</summary>
    public int DocumentFrequency(string term) => _documentFrequency.GetValueOrDefault(term);

    /// <summary>Cuántos de <paramref name="members"/> llevan cada término útil.</summary>
    public Dictionary<string, int> TermCounts(IEnumerable<int> members)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            foreach (var token in _tokens[member])
                counts[token] = counts.GetValueOrDefault(token) + 1;
        }
        return counts;
    }

    /// <summary>
    /// Cuánto se separan, de media, de su carpeta común más profunda las carpetas de los miembros que llevan el término. Es 0 si
    /// todos están en la misma carpeta, 1 si cuelgan de hermanas (<c>shipping/parcel</c> y <c>shipping/ledger</c>) y crece con la
    /// distancia en el árbol: un término que une ramas distantes (<c>web/actions</c> y <c>domain/billing/overdue</c>) es
    /// vocabulario de una capacidad que cruza el árbol, mientras que el de una carpeta compartida es estructura que ya conoce
    /// el recorrido por carpetas.
    /// </summary>
    public double Divergence(string term, IEnumerable<int> members)
    {
        var directories = members.Where(m => _tokens[m].Contains(term)).Select(m => _directories[m]).ToList();
        if (directories.Count == 0)
            return 0;

        var segments = directories.Select(d => d.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var common = segments[0].Length;
        foreach (var other in segments.Skip(1))
        {
            var shared = 0;
            while (shared < common && shared < other.Length && string.Equals(segments[0][shared], other[shared], StringComparison.Ordinal))
                shared++;
            common = shared;
        }

        return segments.Average(s => s.Length - common);
    }

    /// <summary>
    /// El término que mejor define a <paramref name="members"/>: el que lleva al menos el 40 % de ellos (y dos, si hay más de uno)
    /// y es más propio de este grupo, medido como fracción del grupo que lo lleva por fracción de todos los tipos que lo llevan
    /// que están en el grupo. Vacío si ninguno cumple (un grupo de nombres sin raíz común).
    /// </summary>
    public string PrimaryTerm(IReadOnlyCollection<int> members)
    {
        if (members.Count == 0)
            return "";

        var counts = TermCounts(members);
        var minimum = Math.Min(2, members.Count);
        string best = "";
        var bestScore = 0.0;
        foreach (var (term, count) in counts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var coverage = (double)count / members.Count;
            if (count < minimum || coverage < 0.4)
                continue;
            var score = coverage * count / DocumentFrequency(term);
            if (score > bestScore + 1e-12)
            {
                best = term;
                bestScore = score;
            }
        }
        return best;
    }
}

/// <summary>
/// Fusión por vocabulario de las comunidades del grafo (T9): dos comunidades que son «sobre» el mismo término se funden
/// aunque cuelguen de ramas distintas del árbol de paquetes. Es la señal que reúne una capacidad transversal al árbol, que el
/// grafo parte cuando sus ramas se llaman poco entre sí.
/// </summary>
/// <remarks>
/// <para>Dos comunidades se funden si hay un término útil que llevan al menos <see cref="PreparationOptions.VocabularyMergeThreshold"/>
/// de los miembros de <b>cada una</b> y las carpetas de los que lo llevan se separan al menos
/// <see cref="MinBranchDivergence"/> niveles de su carpeta común (<see cref="VocabularyIndex.Divergence"/>): sin esta segunda
/// condición una carpeta compartida (<c>billing</c>, padre de dos capacidades) fundiría capacidades distintas solo por colgar
/// del mismo sitio.</para>
/// <para>Se funde primero el par con el término más compartido y se recalcula con la comunidad resultante, así que una
/// cadena A-B, B-C solo se cierra si B y C siguen compartiéndolo tras fundir A con B. Con empates manda el menor
/// número de comunidad: el resultado es determinista.</para>
/// </remarks>
public static class VocabularyMerger
{
    /// <summary>Niveles de carpeta que tienen que separar a los miembros con el término de su carpeta común para que cuente como vocabulario transversal.</summary>
    public const double MinBranchDivergence = 2;

    /// <summary>
    /// El grupo de cada comunidad de <paramref name="clusters"/> (cada una, una lista de miembros de <paramref name="index"/>):
    /// números consecutivos desde 0 asignados por la primera comunidad de cada grupo. Una fusión que pasaría de
    /// <paramref name="maxMergedSize"/> miembros no se hace.
    /// </summary>
    public static int[] Merge(VocabularyIndex index, IReadOnlyList<IReadOnlyList<int>> clusters, PreparationOptions options, int maxMergedSize = int.MaxValue)
    {
        var groups = new List<List<int>?>(clusters.Select(c => new List<int>(c)));
        var counts = new List<Dictionary<string, int>?>(groups.Select(g => index.TermCounts(g!)));
        var parent = Enumerable.Range(0, clusters.Count).ToArray();

        var candidates = new Dictionary<(int, int), double>();
        for (var a = 0; a < groups.Count; a++)
            AddCandidates(index, groups, counts, a, options.VocabularyMergeThreshold, maxMergedSize, candidates);

        while (candidates.Count > 0)
        {
            // El par con más cobertura compartida; con empate, el de menor (a, b).
            var best = candidates.OrderByDescending(c => c.Value).ThenBy(c => c.Key.Item1).ThenBy(c => c.Key.Item2).First().Key;
            var (keep, drop) = best;

            groups[keep]!.AddRange(groups[drop]!);
            counts[keep] = index.TermCounts(groups[keep]!);
            groups[drop] = null;
            counts[drop] = null;
            parent[drop] = keep;

            foreach (var stale in candidates.Keys.Where(k => k.Item1 == keep || k.Item2 == keep || k.Item1 == drop || k.Item2 == drop).ToList())
                candidates.Remove(stale);
            AddCandidates(index, groups, counts, keep, options.VocabularyMergeThreshold, maxMergedSize, candidates);
        }

        int Root(int i)
        {
            while (parent[i] != i)
                i = parent[i];
            return i;
        }

        var labels = new Dictionary<int, int>();
        var result = new int[clusters.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var root = Root(i);
            if (!labels.TryGetValue(root, out var label))
                labels[root] = label = labels.Count;
            result[i] = label;
        }
        return result;
    }

    /// <summary>Calcula los pares candidatos entre el grupo <paramref name="a"/> y todos los demás grupos vivos.</summary>
    private static void AddCandidates(
        VocabularyIndex index, List<List<int>?> groups, List<Dictionary<string, int>?> counts, int a, double threshold, int maxMergedSize,
        Dictionary<(int, int), double> candidates)
    {
        var groupA = groups[a];
        if (groupA == null || groupA.Count == 0)
            return;

        var strongA = counts[a]!.Where(p => (double)p.Value / groupA.Count >= threshold).ToList();
        if (strongA.Count == 0)
            return;

        for (var b = 0; b < groups.Count; b++)
        {
            var groupB = groups[b];
            if (b == a || groupB == null || groupB.Count == 0 || groupA.Count + groupB.Count > maxMergedSize)
                continue;

            var score = 0.0;
            foreach (var (term, countA) in strongA)
            {
                if (!counts[b]!.TryGetValue(term, out var countB))
                    continue;
                var coverage = Math.Min((double)countA / groupA.Count, (double)countB / groupB.Count);
                if (coverage < threshold || coverage <= score)
                    continue;
                if (index.Divergence(term, groupA.Concat(groupB)) >= MinBranchDivergence)
                    score = coverage;
            }

            if (score > 0)
                candidates[(Math.Min(a, b), Math.Max(a, b))] = score;
        }
    }
}
