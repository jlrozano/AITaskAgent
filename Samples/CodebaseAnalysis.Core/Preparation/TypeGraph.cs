namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Un nodo del grafo agregado a nivel de tipo (T7): una clase, interfaz o enumerado de producción, o un nodo virtual (una
/// tabla) que solo sirve para que dos tipos que tocan la misma se atraigan en la detección de comunidades.
/// </summary>
/// <param name="Index">Posición en <see cref="TypeGraph.Nodes"/>: los nodos van ordenados por <paramref name="Key"/>, así que es estable.</param>
/// <param name="Key">Identificador único y ordinal: el nombre cualificado del grafo, o <c>table:NOMBRE</c> en los virtuales.</param>
/// <param name="Name">Nombre simple del tipo o de la tabla.</param>
/// <param name="Label">Etiqueta del nodo en el grafo (<c>Class</c>, <c>Interface</c>, <c>Enum</c>...), vacía en los virtuales.</param>
/// <param name="File">Fichero relativo al repositorio con <c>/</c>; vacío en los virtuales.</param>
/// <param name="IsVirtual">True si no es un tipo (no pertenece a ningún fichero y no entra en el mapa).</param>
public sealed record TypeNode(int Index, string Key, string Name, string Label, string File, bool IsVirtual)
{
    /// <summary>Carpeta del fichero (sin la barra final); vacía en la raíz y en los virtuales.</summary>
    public string Directory => TypeGraph.DirectoryOf(File);
}

/// <summary>Una dependencia dirigida entre dos tipos con su peso agregado (suma de las aristas de método a método y de tipo a tipo).</summary>
public readonly record struct TypeEdge(int Source, int Target, double Weight);

/// <summary>
/// El grafo de dependencias entre los tipos de producción: nodos ordenados de forma ordinal por su clave y aristas
/// dirigidas ordenadas por (origen, destino), de modo que el mismo contenido da siempre la misma estructura sea cual sea el
/// orden en que se construyó. Es la entrada de <see cref="CommunityDetector"/>.
/// </summary>
public sealed class TypeGraph
{
    public static readonly TypeGraph Empty = new([], [], []);

    internal TypeGraph(IReadOnlyList<TypeNode> nodes, IReadOnlyList<TypeEdge> edges, IReadOnlyList<string> warnings)
    {
        Nodes = nodes;
        Edges = edges;
        Warnings = warnings;
    }

    /// <summary>Nodos ordenados por <see cref="TypeNode.Key"/> de forma ordinal; <see cref="TypeNode.Index"/> es su posición.</summary>
    public IReadOnlyList<TypeNode> Nodes { get; }

    /// <summary>Aristas dirigidas, sin bucles, con peso positivo, ordenadas por origen y destino.</summary>
    public IReadOnlyList<TypeEdge> Edges { get; }

    /// <summary>Lecturas del grafo que fallaron; la parte afectada quedó vacía.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Tipos reales (sin los nodos virtuales).</summary>
    public int TypeCount => Nodes.Count(n => !n.IsVirtual);

    /// <summary>Peso total de las aristas.</summary>
    public double TotalWeight => Edges.Sum(e => e.Weight);

    /// <summary>
    /// True si el grafo no tiene dependencias suficientes para fiarse de ellas (un repositorio sin referencias resueltas, o
    /// con tan pocas que casi todos los tipos están aislados): entonces manda la estructura de carpetas.
    /// </summary>
    public bool IsPoor
    {
        get
        {
            var types = TypeCount;
            if (types == 0)
                return true;
            var connected = new bool[Nodes.Count];
            foreach (var edge in Edges)
            {
                connected[edge.Source] = true;
                connected[edge.Target] = true;
            }
            // Menos de la mitad de los tipos con alguna dependencia: el grafo no describe el código.
            var connectedTypes = Nodes.Count(n => !n.IsVirtual && connected[n.Index]);
            return connectedTypes * 2 < types;
        }
    }

    /// <summary>Carpeta de una ruta relativa con <c>/</c> (sin la barra final); vacía si está en la raíz.</summary>
    public static string DirectoryOf(string file)
    {
        var slash = file.LastIndexOf('/');
        return slash < 0 ? "" : file[..slash];
    }

    /// <summary>
    /// Constructor del grafo: acepta nodos y aristas en cualquier orden y deja el resultado canónico. Una clave repetida
    /// devuelve el mismo nodo; las aristas entre los mismos nodos se suman.
    /// </summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
        private readonly List<(string Key, string Name, string Label, string File, bool IsVirtual)> _nodes = [];
        private readonly Dictionary<long, double> _edges = [];
        private readonly List<string> _warnings = [];

        /// <summary>Añade un nodo (o devuelve el que ya tiene esa clave) y devuelve su posición provisional.</summary>
        public int AddNode(string key, string name, string label, string file, bool isVirtual = false)
        {
            if (_byKey.TryGetValue(key, out var existing))
                return existing;
            var index = _nodes.Count;
            _byKey[key] = index;
            _nodes.Add((key, name, label, file, isVirtual));
            return index;
        }

        /// <summary>Suma <paramref name="weight"/> a la arista dirigida entre dos posiciones provisionales; ignora bucles y pesos no positivos.</summary>
        public void AddEdge(int source, int target, double weight)
        {
            if (source == target || weight <= 0)
                return;
            var key = ((long)source << 32) | (uint)target;
            _edges[key] = _edges.GetValueOrDefault(key) + weight;
        }

        /// <summary>Suma <paramref name="weight"/> a la arista entre dos claves ya añadidas; ignora las desconocidas.</summary>
        public void AddEdge(string sourceKey, string targetKey, double weight)
        {
            if (_byKey.TryGetValue(sourceKey, out var source) && _byKey.TryGetValue(targetKey, out var target))
                AddEdge(source, target, weight);
        }

        public void Warn(string warning) => _warnings.Add(warning);

        public TypeGraph Build()
        {
            // Permutación de las posiciones provisionales a las canónicas (orden ordinal de la clave).
            var order = Enumerable.Range(0, _nodes.Count).OrderBy(i => _nodes[i].Key, StringComparer.Ordinal).ToArray();
            var canonical = new int[_nodes.Count];
            for (var position = 0; position < order.Length; position++)
                canonical[order[position]] = position;

            var nodes = new TypeNode[_nodes.Count];
            for (var position = 0; position < order.Length; position++)
            {
                var (key, name, label, file, isVirtual) = _nodes[order[position]];
                nodes[position] = new TypeNode(position, key, name, label, file, isVirtual);
            }

            var edges = _edges
                .Select(e => new TypeEdge(canonical[(int)(e.Key >> 32)], canonical[(int)(e.Key & 0xFFFFFFFF)], e.Value))
                .OrderBy(e => e.Source).ThenBy(e => e.Target)
                .ToArray();

            return new TypeGraph(nodes, edges, [.. _warnings]);
        }
    }
}
