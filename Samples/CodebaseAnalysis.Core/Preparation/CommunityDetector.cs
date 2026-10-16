namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Detección de comunidades del grafo de tipos (T8): Louvain (maximización de la modularidad) sobre el grafo no dirigido que
/// resulta de sumar las dependencias de ambos sentidos. Es determinista sin necesitar una semilla aleatoria: los nodos se
/// recorren siempre por su posición (que <see cref="TypeGraph"/> fija por orden ordinal de la clave), los empates se rompen por
/// el menor número de comunidad y todo el cálculo usa arrays, nunca un diccionario cuyo orden de recorrido no esté definido.
/// </summary>
/// <remarks>
/// La modularidad ya descuenta los nodos con muchísimas dependencias (una clase de excepciones o de registro, que todo el
/// mundo usa): el peso esperado entre dos nodos crece con el grado de ambos, de modo que un nodo central no arrastra consigo
/// a todo lo que lo usa. Los nodos sin ninguna dependencia quedan solos; quien llama decide qué hacer con ellos.
/// </remarks>
public static class CommunityDetector
{
    private const double Epsilon = 1e-10;
    private const int MaxLocalPasses = 100;
    private const int MaxLevels = 50;

    /// <summary>
    /// Una comunidad por cada nodo de <paramref name="graph"/> (misma posición): números consecutivos desde 0, asignados por
    /// el menor nodo de cada comunidad, de modo que la numeración es canónica.
    /// </summary>
    /// <param name="graph">El grafo de tipos.</param>
    /// <param name="resolution">
    /// 1 es la modularidad clásica; más alto da comunidades más pequeñas y más bajo, más grandes.
    /// </param>
    public static int[] Detect(TypeGraph graph, double resolution = 1.0)
    {
        var count = graph.Nodes.Count;
        if (count == 0)
            return [];

        var level = Level.FromTypeGraph(graph);
        var membership = Enumerable.Range(0, count).ToArray(); // nodo original -> nodo del nivel actual

        for (var iteration = 0; iteration < MaxLevels; iteration++)
        {
            var community = LocalMoving(level, resolution);
            var communities = Renumber(community);
            if (communities == level.Count)
                break; // nadie se movió: el nivel no se puede simplificar más

            for (var i = 0; i < membership.Length; i++)
                membership[i] = community[membership[i]];
            level = level.Aggregate(community, communities);
        }

        return CanonicalLabels(membership);
    }

    /// <summary>Fase de movimiento local: cada nodo pasa a la comunidad vecina que más aumenta la modularidad.</summary>
    private static int[] LocalMoving(Level level, double resolution)
    {
        var n = level.Count;
        var community = Enumerable.Range(0, n).ToArray();
        var total = (double[])level.Strength.Clone(); // grado ponderado total de cada comunidad
        var twoM = level.TwoM;
        if (twoM <= 0)
            return community;

        var weightTo = new double[n];
        var touched = new List<int>();

        for (var pass = 0; pass < MaxLocalPasses; pass++)
        {
            var moved = false;
            for (var node = 0; node < n; node++)
            {
                var current = community[node];
                var strength = level.Strength[node];

                touched.Clear();
                for (var k = level.Offsets[node]; k < level.Offsets[node + 1]; k++)
                {
                    var neighbourCommunity = community[level.Neighbours[k]];
                    if (weightTo[neighbourCommunity] == 0)
                        touched.Add(neighbourCommunity);
                    weightTo[neighbourCommunity] += level.Weights[k];
                }

                total[current] -= strength;
                var best = current;
                var bestGain = weightTo[current] - resolution * total[current] * strength / twoM;
                foreach (var candidate in touched)
                {
                    var gain = weightTo[candidate] - resolution * total[candidate] * strength / twoM;
                    if (gain > bestGain + Epsilon || (Math.Abs(gain - bestGain) <= Epsilon && candidate < best && gain >= bestGain - Epsilon))
                    {
                        best = candidate;
                        bestGain = gain;
                    }
                }

                total[best] += strength;
                community[node] = best;
                moved |= best != current;

                foreach (var candidate in touched)
                    weightTo[candidate] = 0;
                weightTo[current] = 0;
            }

            if (!moved)
                break;
        }

        return community;
    }

    /// <summary>Renumera <paramref name="community"/> (en sitio) a 0..k-1 por el menor nodo de cada una; devuelve k.</summary>
    private static int Renumber(int[] community)
    {
        var map = new Dictionary<int, int>();
        for (var i = 0; i < community.Length; i++)
        {
            if (!map.TryGetValue(community[i], out var id))
                map[community[i]] = id = map.Count;
            community[i] = id;
        }
        return map.Count;
    }

    private static int[] CanonicalLabels(int[] membership)
    {
        var labels = (int[])membership.Clone();
        Renumber(labels);
        return labels;
    }

    /// <summary>Un nivel del algoritmo: un grafo no dirigido en formato de listas de adyacencia ordenadas (CSR).</summary>
    private sealed class Level
    {
        public int Count { get; private init; }
        public int[] Offsets { get; private init; } = [];
        public int[] Neighbours { get; private init; } = [];
        public double[] Weights { get; private init; } = [];

        /// <summary>Peso de cada nodo contando sus bucles dos veces, como los grados de un grafo no dirigido.</summary>
        public double[] Strength { get; private init; } = [];

        public double TwoM { get; private init; }

        public static Level FromTypeGraph(TypeGraph graph)
        {
            var n = graph.Nodes.Count;
            var adjacency = new List<(int Neighbour, double Weight)>[n];
            for (var i = 0; i < n; i++)
                adjacency[i] = [];
            foreach (var edge in graph.Edges)
            {
                adjacency[edge.Source].Add((edge.Target, edge.Weight));
                adjacency[edge.Target].Add((edge.Source, edge.Weight));
            }
            return FromAdjacency(adjacency, new double[n]);
        }

        /// <summary>Construye el nivel a partir de listas de adyacencia sin ordenar ni fusionar y de los bucles de cada nodo.</summary>
        private static Level FromAdjacency(List<(int Neighbour, double Weight)>[] adjacency, double[] loops)
        {
            var n = adjacency.Length;
            var offsets = new int[n + 1];
            var neighbours = new List<int>();
            var weights = new List<double>();
            var strength = new double[n];
            var twoM = 0.0;

            for (var i = 0; i < n; i++)
            {
                var list = adjacency[i];
                list.Sort((a, b) => a.Neighbour.CompareTo(b.Neighbour));
                for (var j = 0; j < list.Count;)
                {
                    var neighbour = list[j].Neighbour;
                    var weight = 0.0;
                    while (j < list.Count && list[j].Neighbour == neighbour)
                        weight += list[j++].Weight;
                    neighbours.Add(neighbour);
                    weights.Add(weight);
                    strength[i] += weight;
                }
                strength[i] += 2 * loops[i];
                offsets[i + 1] = neighbours.Count;
                twoM += strength[i];
            }

            return new Level
            {
                Count = n,
                Offsets = offsets,
                Neighbours = [.. neighbours],
                Weights = [.. weights],
                Strength = strength,
                TwoM = twoM
            };
        }

        /// <summary>El nivel siguiente: un nodo por comunidad, con los pesos entre comunidades y los internos como bucles.</summary>
        public Level Aggregate(int[] community, int communities)
        {
            var adjacency = new List<(int Neighbour, double Weight)>[communities];
            var loops = new double[communities];
            for (var c = 0; c < communities; c++)
                adjacency[c] = [];

            for (var node = 0; node < Count; node++)
            {
                for (var k = Offsets[node]; k < Offsets[node + 1]; k++)
                {
                    var neighbour = Neighbours[k];
                    var from = community[node];
                    var to = community[neighbour];
                    if (from == to)
                    {
                        // Cada arista interna aparece dos veces (una por extremo): medio peso cada vez.
                        if (neighbour != node)
                            loops[from] += Weights[k] / 2;
                    }
                    else
                    {
                        adjacency[from].Add((to, Weights[k]));
                    }
                }
                // El bucle que ya tuviera el nodo se hereda entero.
                loops[community[node]] += (Strength[node] - NeighbourWeight(node)) / 2;
            }

            return FromAdjacency(adjacency, loops);
        }

        private double NeighbourWeight(int node)
        {
            var sum = 0.0;
            for (var k = Offsets[node]; k < Offsets[node + 1]; k++)
                sum += Weights[k];
            return sum;
        }
    }
}
