namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// El grafo sobre el que se buscan las comunidades (T8-T9): el grafo de dependencias entre tipos con dos correcciones que lo
/// acercan a lo que un humano llama «una capacidad».
/// </summary>
/// <remarks>
/// <para><b>Vocabulario como dependencia.</b> Cada término de los nombres de clase (<c>Parcel</c>, <c>Ledger</c>...) que
/// comparten varios tipos de producción entra como un nodo virtual unido a todos ellos. Así una capacidad que cruza el árbol
/// de paquetes (acciones y formularios en una rama, dominio en otra) queda unida por lo que sus clases dicen ser, y no por
/// llamarse entre sí a través del marco de acciones que todas comparten. Solo cuenta un término si es <i>coherente en el código</i>:
/// al menos la mitad de los tipos que lo llevan se llaman o se usan entre sí. Un término genérico (<c>List</c>, <c>Create</c>)
/// junta tipos que no tienen nada que ver y no pasa ese filtro. Los términos son los del propio repositorio; los genéricos
/// técnicos (<see cref="PreparationOptions.StopTokens"/>) y los que llevan a más de la mitad de los tipos no cuentan.</para>
/// <para><b>Nodos omnipresentes.</b> Un tipo con muchísimas más dependencias que la media (excepciones, registro, utilidades)
/// uniría por sí solo todo lo que lo usa: sus aristas pesan menos cuanto mayor es su grado, de modo que la modularidad no lo
/// toma por el centro de una comunidad.</para>
/// </remarks>
public static class AffinityGraph
{
    /// <summary>Peso del vínculo de un tipo con un término coherente, en aristas medias: el nombre pesa más que una llamada suelta.</summary>
    public const double TermWeightFactor = 5;

    /// <summary>Peso del vínculo de un tipo con su carpeta, en aristas medias: la estructura es un prior débil, el grafo y el nombre mandan.</summary>
    public const double FolderWeightFactor = 1;

    /// <summary>Fracción de los tipos de un término que tienen que relacionarse con otro del mismo término para que cuente.</summary>
    public const double MinTermCohesion = 0.5;

    /// <summary>
    /// El grafo de afinidad: los nodos de <paramref name="graph"/> (con las mismas claves) más un nodo virtual por término
    /// coherente (<c>term:xxx</c>). <paramref name="indexOf"/> da, para cada posición del grafo original, la que tiene en el nuevo
    /// (las posiciones cambian porque los nodos se reordenan por clave). Un término que llevan más de
    /// <paramref name="maxTermMembers"/> tipos no entra: es un paraguas, no una capacidad.
    /// </summary>
    public static TypeGraph Build(TypeGraph graph, PreparationOptions options, int maxTermMembers, out int[] indexOf)
    {
        var nodes = graph.Nodes;
        var builder = new TypeGraph.Builder();
        foreach (var node in nodes)
            builder.AddNode(node.Key, node.Name, node.Label, node.File, node.IsVirtual);

        // Grado distinto de cada tipo (vecinos en cualquier sentido): lo que mide cuánto de omnipresente es.
        var neighbours = new HashSet<int>[nodes.Count];
        for (var i = 0; i < neighbours.Length; i++)
            neighbours[i] = [];
        foreach (var edge in graph.Edges)
        {
            if (nodes[edge.Source].IsVirtual || nodes[edge.Target].IsVirtual)
                continue;
            neighbours[edge.Source].Add(edge.Target);
            neighbours[edge.Target].Add(edge.Source);
        }

        var typeDegrees = Enumerable.Range(0, nodes.Count).Where(i => !nodes[i].IsVirtual && neighbours[i].Count > 0).Select(i => neighbours[i].Count).ToList();
        var meanDegree = typeDegrees.Count == 0 ? 1.0 : typeDegrees.Average();

        // Las aristas de código, atenuadas por el grado de sus extremos (sin cambiar la escala para un tipo de grado medio).
        var weights = new List<double>();
        foreach (var edge in graph.Edges)
        {
            var damping = 1.0;
            if (!nodes[edge.Source].IsVirtual && !nodes[edge.Target].IsVirtual)
                damping = meanDegree * meanDegree / ((double)Math.Max(1, neighbours[edge.Source].Count) * Math.Max(1, neighbours[edge.Target].Count));
            var weight = edge.Weight * Math.Min(1.0, damping);
            builder.AddEdge(nodes[edge.Source].Key, nodes[edge.Target].Key, weight);
            if (!nodes[edge.Source].IsVirtual && !nodes[edge.Target].IsVirtual)
                weights.Add(weight);
        }

        var meanWeight = weights.Count == 0 ? 1.0 : weights.Average();
        AddTermNodes(graph, options, maxTermMembers, builder, neighbours, meanWeight);
        AddFolderNodes(graph, builder, meanWeight);

        var result = builder.Build();
        var byKey = result.Nodes.ToDictionary(n => n.Key, n => n.Index, StringComparer.Ordinal);
        indexOf = [.. nodes.Select(n => byKey[n.Key])];
        return result;
    }

    private static void AddTermNodes(TypeGraph graph, PreparationOptions options, int maxTermMembers, TypeGraph.Builder builder, HashSet<int>[] neighbours, double meanWeight)
    {
        var nodes = graph.Nodes;
        var types = Enumerable.Range(0, nodes.Count).Where(i => !nodes[i].IsVirtual).ToArray();
        if (types.Length < 2)
            return;

        // Los términos de los nombres de clase: el nombre es una señal fuerte de qué trata el tipo.
        var names = new VocabularyIndex([.. types.Select(i => new VocabularyMember(nodes[i].Name, nodes[i].Directory))], options, useDirectoryTokens: false, maxTermMembers: maxTermMembers);
        var nameTerms = AddTerms(names, types, "term:", TermWeightFactor, nodes, builder, neighbours, meanWeight, null);

        // Los de la ruta de la carpeta, con su propio peso (más débil): una carpeta dice dónde está el tipo, pero une a una capacidad lo que su
        // nombre no dice. Nodos aparte (<c>pterm:</c>) para que el filtro de coherencia de cada fuente se evalúe por separado, y solo para los
        // términos que además son nombre coherente de alguna clase: así el nombre de una capacidad (<c>notificacion/</c>, con clases
        // <c>Notificacion*</c>) arrastra a los tipos de su carpeta, pero el de una capa (<c>web/</c>, <c>domain/</c>), que ninguna clase lleva
        // y que cruza todas las capacidades, no las funde por colgar de la misma rama.
        if (options.PathTermWeightFactor > 0)
        {
            var paths = new VocabularyIndex([.. types.Select(i => new VocabularyMember("", nodes[i].Directory))], options, useDirectoryTokens: true, maxTermMembers: maxTermMembers);
            AddTerms(paths, types, "pterm:", options.PathTermWeightFactor, nodes, builder, neighbours, meanWeight, nameTerms);
        }
    }

    /// <summary>
    /// Un nodo virtual por término coherente de <paramref name="vocabulary"/> (cuyos miembros son <paramref name="types"/>, en el mismo orden),
    /// unido a los tipos que lo llevan con un peso de <paramref name="factor"/> aristas medias atenuado para los términos que llevan a muchos.
    /// Con <paramref name="onlyTerms"/> solo entran esos términos. Devuelve los términos con nodo.
    /// </summary>
    private static HashSet<string> AddTerms(
        VocabularyIndex vocabulary, int[] types, string keyPrefix, double factor, IReadOnlyList<TypeNode> nodes, TypeGraph.Builder builder,
        HashSet<int>[] neighbours, double meanWeight, HashSet<string>? onlyTerms)
    {
        var added = new HashSet<string>(StringComparer.Ordinal);
        var membersOfTerm = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        for (var m = 0; m < types.Length; m++)
        {
            foreach (var term in vocabulary.TokensOf(m))
            {
                if (!membersOfTerm.TryGetValue(term, out var list))
                    membersOfTerm[term] = list = [];
                list.Add(types[m]);
            }
        }

        // Peso del vínculo de un tipo con su término: varias aristas medias (el nombre es una señal fuerte, pero no
        // más que varias llamadas), atenuado para los términos que llevan a muchos tipos.
        var baseWeight = Math.Max(1.0, factor * meanWeight);
        var marked = new bool[nodes.Count];
        foreach (var (term, members) in membersOfTerm)
        {
            if (onlyTerms != null && !onlyTerms.Contains(term))
                continue;
            foreach (var member in members)
                marked[member] = true;
            var connected = members.Count(m => neighbours[m].Any(n => marked[n]));
            foreach (var member in members)
                marked[member] = false;

            if ((double)connected / members.Count < MinTermCohesion)
                continue;

            var weight = baseWeight / (1 + Math.Log10(Math.Max(1.0, members.Count / 8.0)));
            var key = keyPrefix + term;
            builder.AddNode(key, term, "", "", isVirtual: true);
            foreach (var member in members)
                builder.AddEdge(nodes[member].Key, key, weight);
            added.Add(term);
        }
        return added;
    }

    /// <summary>
    /// La estructura como prior débil: cada carpeta con varios tipos es un nodo virtual unido a todos ellos. Sin dependencias
    /// que las separen, las clases de una misma carpeta quedan juntas (el marco de acciones y las utilidades, que no llevan
    /// nombre de capacidad, forman así su propio grupo en lugar de repartirse entre las capacidades que las usan), pero pesa
    /// menos que el nombre: una carpeta de acciones de varias capacidades no las funde.
    /// </summary>
    private static void AddFolderNodes(TypeGraph graph, TypeGraph.Builder builder, double meanWeight)
    {
        var baseWeight = Math.Max(0.5, FolderWeightFactor * meanWeight);
        foreach (var group in graph.Nodes.Where(n => !n.IsVirtual).GroupBy(n => n.Directory, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (members.Count < 2)
                continue;
            var weight = baseWeight / (1 + Math.Log10(Math.Max(1.0, members.Count / 8.0)));
            var key = "dir:" + group.Key;
            builder.AddNode(key, group.Key, "", "", isVirtual: true);
            foreach (var member in members)
                builder.AddEdge(member.Key, key, weight);
        }
    }

    /// <summary>
    /// El subgrafo de <paramref name="graph"/> inducido por <paramref name="members"/>: sus aristas entre sí y los nodos
    /// virtuales (términos, carpetas, tablas) que unen al menos a dos de ellos. <paramref name="indexOf"/> da la posición de cada
    /// miembro (en el orden recibido) en el subgrafo. Es lo que se vuelve a particionar cuando una comunidad es demasiado grande.
    /// </summary>
    public static TypeGraph Induce(TypeGraph graph, IReadOnlyList<int> members, out int[] indexOf)
    {
        var inside = new HashSet<int>(members);
        var touches = new Dictionary<int, HashSet<int>>();
        foreach (var edge in graph.Edges)
        {
            if (graph.Nodes[edge.Target].IsVirtual && inside.Contains(edge.Source))
                Touch(touches, edge.Target, edge.Source);
            else if (graph.Nodes[edge.Source].IsVirtual && inside.Contains(edge.Target))
                Touch(touches, edge.Source, edge.Target);
        }
        var virtuals = touches.Where(t => t.Value.Count >= 2).Select(t => t.Key).ToHashSet();

        var builder = new TypeGraph.Builder();
        foreach (var node in members.Concat(virtuals.Order()))
            builder.AddNode(graph.Nodes[node].Key, graph.Nodes[node].Name, graph.Nodes[node].Label, graph.Nodes[node].File, graph.Nodes[node].IsVirtual);
        foreach (var edge in graph.Edges)
        {
            if ((inside.Contains(edge.Source) || virtuals.Contains(edge.Source)) && (inside.Contains(edge.Target) || virtuals.Contains(edge.Target)))
                builder.AddEdge(graph.Nodes[edge.Source].Key, graph.Nodes[edge.Target].Key, edge.Weight);
        }

        var sub = builder.Build();
        var byKey = sub.Nodes.ToDictionary(n => n.Key, n => n.Index, StringComparer.Ordinal);
        indexOf = [.. members.Select(m => byKey[graph.Nodes[m].Key])];
        return sub;
    }

    private static void Touch(Dictionary<int, HashSet<int>> touches, int virtualNode, int member)
    {
        if (!touches.TryGetValue(virtualNode, out var set))
            touches[virtualNode] = set = [];
        set.Add(member);
    }
}
