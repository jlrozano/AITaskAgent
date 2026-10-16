using System.Security.Cryptography;
using System.Text;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>Lo que necesita <see cref="DomainMapBuilder"/>: los ficheros de producción, el grafo de tipos y las entradas.</summary>
public sealed record DomainMapInput
{
    /// <summary>Ficheros de producción con sus líneas (<see cref="ProductionLines.Files"/> del inventario).</summary>
    public IReadOnlyList<FileLines> ProductionFiles { get; init; } = [];

    /// <summary>El grafo de tipos de esos ficheros (<see cref="TypeGraphBuilder"/>); vacío si no hay grafo.</summary>
    public TypeGraph Graph { get; init; } = TypeGraph.Empty;

    /// <summary>Puntos de entrada del inventario (con el fichero que los atiende).</summary>
    public IReadOnlyList<InventoryEntryPoint> EntryPoints { get; init; } = [];

    /// <summary>
    /// Las tablas que el SQL escrito en cadenas nombra, con su fichero (<see cref="Support.SqlTableScanner"/>): cada subsistema cuenta las de
    /// sus ficheros, además de las que el grafo une a sus tipos. Vacío si no se escaneó.
    /// </summary>
    public IReadOnlyList<Support.SqlTableReference> SqlTables { get; init; } = [];

    public string? GraphFingerprint { get; init; }

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// El mapa de dominios determinista (T10): reparte cada fichero de producción en un subsistema o en <c>unassigned</c> con tres
/// señales del propio repositorio, combinadas porque ninguna basta sola (spec §4.2):
/// <list type="number">
/// <item>el <b>grafo</b>: comunidades de Louvain sobre las dependencias entre tipos (<see cref="CommunityDetector"/>), con las
/// tablas compartidas y los pares acción-formulario de Struts como dependencias añadidas;</item>
/// <item>el <b>vocabulario</b>: las comunidades que son «sobre» el mismo término se funden aunque cuelguen de ramas distantes
/// del árbol (<see cref="VocabularyMerger"/>);</item>
/// <item>la <b>estructura</b>: sin dependencias suficientes manda el árbol de carpetas (<see cref="StructuralClusterer"/>), y los
/// subsistemas por debajo de <see cref="PreparationOptions.MinSubsystemFiles"/> se absorben en el vecino más ligado o, sin
/// vínculos, en el de la carpeta más cercana.</item>
/// </list>
/// Los nombres que devuelve son derivados (término dominante o carpeta): nombrarlos bien es cosa de <see cref="DomainNaming"/>.
/// </summary>
public static class DomainMapBuilder
{
    /// <summary>Fragmentos de nombre de tipo que delatan una integración con un sistema externo (técnicos, no de negocio).</summary>
    private static readonly HashSet<string> IntegrationTokens = new(StringComparer.Ordinal)
    {
        "client", "gateway", "adapter", "connector", "proxy", "jms", "kafka", "soap", "rest", "http", "ftp", "smtp", "mail",
        "queue", "publisher", "subscriber", "producer", "consumer"
    };

    /// <summary>Identificador del subsistema de infraestructura compartida (<see cref="SubsystemKinds.Shared"/>).</summary>
    public const string SharedId = "shared";

    /// <summary>Nombre derivado del subsistema de infraestructura compartida.</summary>
    public const string SharedName = "Infraestructura compartida";

    public const int MaxRoots = 8;
    public const int MaxKeyTypes = 8;
    public const int MaxEntryPointsPerSubsystem = 50;
    public const int MaxTablesPerSubsystem = 50;

    private sealed class Cluster(int id, List<int> files)
    {
        public int Id { get; } = id;
        public List<int> Files { get; } = files;
        public bool Alive { get; set; } = true;

        /// <summary>El cluster de la infraestructura compartida (<see cref="SharedInfrastructure"/>): no recibe ficheros sueltos.</summary>
        public bool IsShared { get; init; }
    }

    public static DomainMap Build(DomainMapInput input, PreparationOptions options)
    {
        var warnings = new List<string>(input.Graph.Warnings);
        var files = input.ProductionFiles.Select(f => f.Path).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in input.ProductionFiles)
            lines[file.Path] = file.Lines;
        var fileIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Length; i++)
            fileIndex[files[i]] = i;

        if (files.Length == 0)
        {
            warnings.Add("⚠️ El inventario no tiene ficheros de producción: el mapa de dominios queda vacío.");
            return new DomainMap { GeneratedAt = input.GeneratedAt, GraphFingerprint = input.GraphFingerprint, Basis = DomainMapBases.StructureOnly, Warnings = warnings };
        }

        // Tipos de producción (los nodos virtuales son tablas) y su fichero.
        var graph = input.Graph;
        var fileOfNode = new int[graph.Nodes.Count];
        var membersOfFile = new List<int>[files.Length];
        for (var i = 0; i < files.Length; i++)
            membersOfFile[i] = [];
        for (var node = 0; node < graph.Nodes.Count; node++)
        {
            var type = graph.Nodes[node];
            fileOfNode[node] = !type.IsVirtual && fileIndex.TryGetValue(type.File, out var f) ? f : -1;
            if (fileOfNode[node] >= 0)
                membersOfFile[fileOfNode[node]].Add(node);
        }

        var useGraph = !graph.IsPoor && fileOfNode.Any(f => f >= 0);
        var cap = MaxSubsystemFiles(files.Length, options);
        var vocabulary = new VocabularyIndex(
            [.. files.Select(f => new VocabularyMember(Path.GetFileNameWithoutExtension(f), TypeGraph.DirectoryOf(f)))], options, maxTermMembers: cap);

        List<Cluster> clusters;
        if (useGraph)
        {
            clusters = GraphClusters(graph, files, fileOfNode, membersOfFile, vocabulary, options, cap);
        }
        else
        {
            warnings.Add("⚠️ El grafo no tiene dependencias suficientes entre tipos de producción: el mapa de dominios sale de la estructura de carpetas.");
            clusters = [.. StructuralClusterer.GroupByFolders(files, options.MinSubsystemFiles)
                .Select((group, id) => new Cluster(id, [.. group.Select(f => fileIndex[f])]))];
        }

        var clusterOfFile = Enumerable.Repeat(-1, files.Length).ToArray();
        foreach (var cluster in clusters)
        {
            foreach (var file in cluster.Files)
                clusterOfFile[file] = cluster.Id;
        }

        var adjacency = BuildAdjacency(graph, fileOfNode, files.Length);
        AbsorbSmall(clusters, clusterOfFile, files, membersOfFile, adjacency, fileOfNode, options);
        if (useGraph)
            ExtractShared(input, options, clusters, clusterOfFile, fileIndex, fileOfNode, membersOfFile);
        AssignLoose(clusters, clusterOfFile, files, vocabulary);

        return Finish(input, options, warnings, useGraph, clusters, clusterOfFile, files, lines, fileOfNode, membersOfFile, vocabulary);
    }

    // ───────────────────────────── Clusters del grafo ─────────────────────────────

    /// <summary>Tamaño máximo de un subsistema: la fracción configurada de los ficheros de producción, con un suelo.</summary>
    public static int MaxSubsystemFiles(int totalFiles, PreparationOptions options) =>
        Math.Max(PreparationOptions.MinSubsystemCap, (int)Math.Ceiling(options.MaxSubsystemShare * totalFiles));

    /// <summary>Resoluciones con que se intenta dividir una comunidad que pasa del tamaño máximo, de menor a mayor.</summary>
    private static readonly double[] SplitResolutions = [1.0, 2.0, 4.0];

    private const int MaxSplitDepth = 8;

    private static List<Cluster> GraphClusters(
        TypeGraph graph, string[] files, int[] fileOfNode, List<int>[] membersOfFile, VocabularyIndex vocabulary, PreparationOptions options, int cap)
    {
        // 1. Comunidades del grafo de afinidad (dependencias atenuadas por grado más el vocabulario de los nombres de clase).
        var affinity = AffinityGraph.Build(graph, options, cap, out var indexOf);
        var detected = CommunityDetector.Detect(affinity);

        var groups = Enumerable.Range(0, graph.Nodes.Count).Where(n => fileOfNode[n] >= 0)
            .GroupBy(n => detected[indexOf[n]]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();

        // 2. Una comunidad mayor que el tamaño máximo se divide en las suyas: un subsistema que ocupa un cuarto del repositorio no
        // sirve para investigar por partes.
        var split = new List<List<int>>();
        foreach (var group in groups)
            Split(group, affinity, indexOf, fileOfNode, cap, 0, split);

        // 3. Cada fichero con tipos va a la comunidad de la mayoría de sus tipos (con empate, a la de menor número).
        var groupOfNode = new int[graph.Nodes.Count];
        Array.Fill(groupOfNode, -1);
        for (var g = 0; g < split.Count; g++)
        {
            foreach (var node in split[g])
                groupOfNode[node] = g;
        }

        var byGroup = new SortedDictionary<int, List<int>>();
        for (var file = 0; file < files.Length; file++)
        {
            if (membersOfFile[file].Count == 0)
                continue;
            var group = membersOfFile[file].Where(m => groupOfNode[m] >= 0).GroupBy(m => groupOfNode[m])
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).DefaultIfEmpty(-1).First();
            if (group < 0)
                continue;
            if (!byGroup.TryGetValue(group, out var list))
                byGroup[group] = list = [];
            list.Add(file);
        }

        // 4. Fusión por vocabulario sobre las comunidades, sin pasar del tamaño máximo.
        var communities = byGroup.Values.ToList();
        var groupOf = VocabularyMerger.Merge(vocabulary, [.. communities.Select(c => (IReadOnlyList<int>)c)], options, cap);
        var merged = new SortedDictionary<int, List<int>>();
        for (var i = 0; i < communities.Count; i++)
        {
            if (!merged.TryGetValue(groupOf[i], out var list))
                merged[groupOf[i]] = list = [];
            list.AddRange(communities[i]);
        }

        return [.. merged.Values.Select((list, id) => new Cluster(id, [.. list.Order()]))];
    }

    /// <summary>
    /// Añade a <paramref name="result"/> los grupos en que se divide <paramref name="nodes"/> si ocupa más de
    /// <paramref name="cap"/> ficheros: vuelve a buscar comunidades solo en su subgrafo, con una resolución cada vez mayor
    /// hasta que se parta de verdad, y repite con las partes. Si no se puede partir, se queda como está.
    /// </summary>
    private static void Split(
        List<int> nodes, TypeGraph affinity, int[] indexOf, int[] fileOfNode, int cap, int depth, List<List<int>> result)
    {
        if (depth >= MaxSplitDepth || nodes.Select(n => fileOfNode[n]).Distinct().Count() <= cap)
        {
            result.Add(nodes);
            return;
        }

        var sub = AffinityGraph.Induce(affinity, [.. nodes.Select(n => indexOf[n])], out var subIndexOf);
        foreach (var resolution in SplitResolutions)
        {
            var labels = CommunityDetector.Detect(sub, resolution);
            var parts = nodes.Select((node, i) => (Node: node, Label: labels[subIndexOf[i]]))
                .GroupBy(x => x.Label).OrderBy(g => g.Key).Select(g => g.Select(x => x.Node).ToList()).ToList();
            // Una división que solo recorta un par de tipos sueltos no avanza: se prueba con más resolución.
            if (parts.Count < 2 || parts.Max(p => p.Count) > 0.95 * nodes.Count)
                continue;
            foreach (var part in parts)
                Split(part, affinity, indexOf, fileOfNode, cap, depth + 1, result);
            return;
        }

        result.Add(nodes);
    }

    /// <summary>Adyacencia simétrica entre ficheros con tipos: el peso de las dependencias entre sus tipos.</summary>
    private static Dictionary<int, double>[] BuildAdjacency(TypeGraph graph, int[] fileOfNode, int fileCount)
    {
        var adjacency = new Dictionary<int, double>[fileCount];
        for (var i = 0; i < fileCount; i++)
            adjacency[i] = [];
        foreach (var edge in graph.Edges)
        {
            var from = fileOfNode[edge.Source];
            var to = fileOfNode[edge.Target];
            if (from < 0 || to < 0 || from == to)
                continue;
            adjacency[from][to] = adjacency[from].GetValueOrDefault(to) + edge.Weight;
            adjacency[to][from] = adjacency[to].GetValueOrDefault(from) + edge.Weight;
        }
        return adjacency;
    }

    // ───────────────────────────── Absorción de los pequeños ─────────────────────────────

    /// <summary>
    /// Los clusters con menos de <see cref="PreparationOptions.MinSubsystemFiles"/> ficheros (o menos que el mayor, si este es
    /// menor que el mínimo) se absorben, de menor a mayor, en el cluster con el que más pesan las dependencias; sin ninguna, en el
    /// de la carpeta más cercana; sin vínculo de ningún tipo, sus ficheros quedan sin asignar.
    /// </summary>
    private static void AbsorbSmall(
        List<Cluster> clusters, int[] clusterOfFile, string[] files, List<int>[] membersOfFile,
        Dictionary<int, double>[] adjacency, int[] fileOfNode, PreparationOptions options)
    {
        while (true)
        {
            // El mínimo se recalcula con el mayor cluster que queda: en un repositorio diminuto nunca puede ser mayor que él.
            var largest = clusters.Where(c => c.Alive).Select(c => c.Files.Count).DefaultIfEmpty(0).Max();
            var minimum = Math.Min(options.MinSubsystemFiles, largest);
            var small = clusters.Where(c => c.Alive && c.Files.Count < minimum).OrderBy(c => c.Files.Count).ThenBy(c => c.Id).FirstOrDefault();
            if (small == null)
                return;

            var target = BestTarget(small, clusters, clusterOfFile, files, adjacency);
            small.Alive = false;
            foreach (var file in small.Files)
            {
                clusterOfFile[file] = target?.Id ?? -1;
                target?.Files.Add(file);
            }
            target?.Files.Sort();
        }
    }

    private static Cluster? BestTarget(Cluster small, List<Cluster> clusters, int[] clusterOfFile, string[] files, Dictionary<int, double>[] adjacency)
    {
        // 1. Dependencias: el cluster al que más pesan las del pequeño.
        var weights = new SortedDictionary<int, double>();
        foreach (var file in small.Files)
        {
            foreach (var (neighbour, weight) in adjacency[file])
            {
                var cluster = clusterOfFile[neighbour];
                if (cluster >= 0 && cluster != small.Id)
                    weights[cluster] = weights.GetValueOrDefault(cluster) + weight;
            }
        }
        if (weights.Count > 0)
            return clusters[weights.OrderByDescending(w => w.Value).ThenBy(w => w.Key).First().Key];

        // 2. Carpetas: el cluster con más ficheros en la misma carpeta; si no, en la carpeta padre o en una hija.
        var directories = small.Files.Select(f => TypeGraph.DirectoryOf(files[f])).ToHashSet(StringComparer.Ordinal);
        var same = new SortedDictionary<int, int>();
        var related = new SortedDictionary<int, int>();
        for (var file = 0; file < files.Length; file++)
        {
            var cluster = clusterOfFile[file];
            if (cluster < 0 || cluster == small.Id)
                continue;
            var directory = TypeGraph.DirectoryOf(files[file]);
            if (directories.Contains(directory))
                same[cluster] = same.GetValueOrDefault(cluster) + 1;
            else if (directories.Any(d => IsAncestor(d, directory) || IsAncestor(directory, d)))
                related[cluster] = related.GetValueOrDefault(cluster) + 1;
        }

        var pool = same.Count > 0 ? same : related;
        return pool.Count == 0 ? null : clusters[pool.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key];
    }

    private static bool IsAncestor(string ancestor, string path) =>
        ancestor.Length < path.Length && path.StartsWith(ancestor, StringComparison.Ordinal) && (ancestor.Length == 0 || path[ancestor.Length] == '/');

    // ───────────────────────────── Infraestructura compartida ─────────────────────────────

    /// <summary>
    /// Saca de sus comunidades los ficheros de infraestructura compartida (<see cref="SharedInfrastructure"/>) y los reúne en un único cluster
    /// marcado como tal. Se hace con las comunidades ya formadas y absorbidas, que son las que cuenta como «comunidades distintas».
    /// </summary>
    private static void ExtractShared(
        DomainMapInput input, PreparationOptions options, List<Cluster> clusters, int[] clusterOfFile, Dictionary<string, int> fileIndex,
        int[] fileOfNode, List<int>[] membersOfFile)
    {
        var communities = clusters.Count(c => c.Alive && c.Files.Count > 0);
        var withEntries = input.EntryPoints.Select(e => fileIndex.TryGetValue(e.File, out var f) ? f : -1).Where(f => f >= 0).ToHashSet();
        var sharedFiles = SharedInfrastructure.Detect(input.Graph, fileOfNode, membersOfFile, clusterOfFile, communities, withEntries, options);
        if (sharedFiles.Count == 0)
            return;

        var shared = new Cluster(clusters.Count, [.. sharedFiles]) { IsShared = true };
        foreach (var file in sharedFiles)
        {
            clusters[clusterOfFile[file]].Files.Remove(file);
            clusterOfFile[file] = shared.Id;
        }
        foreach (var cluster in clusters.Where(c => c.Alive && c.Files.Count == 0))
            cluster.Alive = false;
        clusters.Add(shared);
    }

    // ───────────────────────────── Ficheros sin cluster ─────────────────────────────

    /// <summary>
    /// Los ficheros que no tienen tipos en el grafo (JSP, JavaScript, lo que cbm no resolvió) van al subsistema cuyo vocabulario
    /// concentra los términos de su nombre y su carpeta; sin uno claro, al de más ficheros en su carpeta; si no, quedan sin asignar.
    /// </summary>
    private static void AssignLoose(List<Cluster> clusters, int[] clusterOfFile, string[] files, VocabularyIndex vocabulary)
    {
        var loose = Enumerable.Range(0, files.Length).Where(f => clusterOfFile[f] < 0).ToList();
        if (loose.Count == 0)
            return;

        // Los clusters ya formados sin los ficheros sueltos: la asignación de uno no influye en la del siguiente.
        var counts = clusters.Where(c => c.Alive && !c.IsShared).ToDictionary(c => c.Id, c => vocabulary.TermCounts(c.Files));
        var assignments = new List<(int File, int Cluster)>();

        foreach (var file in loose)
        {
            var scores = new SortedDictionary<int, double>();
            foreach (var term in vocabulary.TokensOf(file))
            {
                var documentFrequency = vocabulary.DocumentFrequency(term);
                foreach (var (cluster, termCounts) in counts)
                {
                    if (termCounts.TryGetValue(term, out var count) && count >= 2)
                        scores[cluster] = scores.GetValueOrDefault(cluster) + (double)count / documentFrequency;
                }
            }

            var ranked = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).ToList();
            if (ranked.Count > 0 && ranked[0].Value >= 0.5 && (ranked.Count == 1 || ranked[0].Value - ranked[1].Value > 1e-9))
            {
                assignments.Add((file, ranked[0].Key));
                continue;
            }

            var directory = TypeGraph.DirectoryOf(files[file]);
            var siblings = clusters.Where(c => c.Alive && !c.IsShared)
                .Select(c => (Cluster: c.Id, Count: c.Files.Count(f => TypeGraph.DirectoryOf(files[f]) == directory)))
                .Where(s => s.Count > 0).OrderByDescending(s => s.Count).ThenBy(s => s.Cluster).ToList();
            if (siblings.Count > 0)
                assignments.Add((file, siblings[0].Cluster));
        }

        foreach (var (file, cluster) in assignments)
        {
            clusterOfFile[file] = cluster;
            clusters[cluster].Files.Add(file);
        }
        foreach (var cluster in clusters)
            cluster.Files.Sort();
    }

    /// <summary>
    /// <paramref name="map"/> con las tablas de <paramref name="sqlTables"/> sumadas a las de cada subsistema por los ficheros que lo componen
    /// (y <c>Metrics.Tables</c> recontado). Es lo que recibe un mapa guardado y reutilizado, que se calculó antes de que existiera el
    /// escaneo del SQL de las cadenas o con otro resultado. Sin referencias devuelve el mismo mapa.
    /// </summary>
    public static DomainMap WithSqlTables(DomainMap map, IReadOnlyList<Support.SqlTableReference> sqlTables)
    {
        if (sqlTables.Count == 0)
            return map;

        var byFile = sqlTables.GroupBy(t => t.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Name).ToList(), StringComparer.OrdinalIgnoreCase);
        return map with
        {
            Subsystems =
            [
                .. map.Subsystems.Select(subsystem =>
                {
                    var set = new SortedSet<string>(subsystem.Tables, StringComparer.OrdinalIgnoreCase);
                    foreach (var file in subsystem.Files)
                    {
                        foreach (var name in byFile.GetValueOrDefault(file) ?? [])
                            set.Add(name);
                    }
                    return set.Count == subsystem.Tables.Count
                        ? subsystem
                        : subsystem with { Tables = [.. set.Take(MaxTablesPerSubsystem)], Metrics = subsystem.Metrics with { Tables = set.Count } };
                })
            ]
        };
    }

    // ───────────────────────────── Subsistemas ─────────────────────────────

    private static DomainMap Finish(
        DomainMapInput input, PreparationOptions options, List<string> warnings, bool useGraph, List<Cluster> clusters,
        int[] clusterOfFile, string[] files, Dictionary<string, int> lines, int[] fileOfNode, List<int>[] membersOfFile, VocabularyIndex vocabulary)
    {
        var graph = input.Graph;
        var alive = clusters.Where(c => c.Alive && c.Files.Count > 0).ToList();

        // Centralidad: cuánto del peso total de las dependencias entre subsistemas llega a cada uno.
        var incoming = new Dictionary<int, double>();
        var crossTotal = 0.0;
        foreach (var edge in graph.Edges)
        {
            var from = fileOfNode[edge.Source] >= 0 ? clusterOfFile[fileOfNode[edge.Source]] : -1;
            var to = fileOfNode[edge.Target] >= 0 ? clusterOfFile[fileOfNode[edge.Target]] : -1;
            if (from < 0 || to < 0 || from == to)
                continue;
            incoming[to] = incoming.GetValueOrDefault(to) + edge.Weight;
            crossTotal += edge.Weight;
        }

        // Tablas por cluster: los nodos virtuales unidos a los tipos del cluster y, además, las que el SQL de las cadenas de sus ficheros
        // nombra (con el grafo pobre en tablas es la única fuente). Sin distinguir mayúsculas: la del grafo, que se lee primero, manda.
        var tables = new Dictionary<int, SortedSet<string>>();
        foreach (var edge in graph.Edges)
        {
            if (!graph.Nodes[edge.Target].IsVirtual || fileOfNode[edge.Source] < 0)
                continue;
            var cluster = clusterOfFile[fileOfNode[edge.Source]];
            if (cluster < 0)
                continue;
            if (!tables.TryGetValue(cluster, out var set))
                tables[cluster] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(graph.Nodes[edge.Target].Name);
        }

        var sqlTablesByFile = input.SqlTables.GroupBy(t => t.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Name).ToList(), StringComparer.OrdinalIgnoreCase);
        if (sqlTablesByFile.Count > 0)
        {
            foreach (var cluster in alive)
            {
                foreach (var file in cluster.Files)
                {
                    if (!sqlTablesByFile.TryGetValue(files[file], out var names))
                        continue;
                    if (!tables.TryGetValue(cluster.Id, out var set))
                        tables[cluster.Id] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in names)
                        set.Add(name);
                }
            }
        }

        // Grado ponderado de cada tipo, para elegir los tipos clave.
        var degree = new double[graph.Nodes.Count];
        foreach (var edge in graph.Edges)
        {
            degree[edge.Source] += edge.Weight;
            degree[edge.Target] += edge.Weight;
        }

        var entryByFile = input.EntryPoints.Where(e => e.File.Length > 0)
            .GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var drafts = new List<(Cluster Cluster, string BaseId, Subsystem Subsystem)>();
        foreach (var cluster in alive)
        {
            var clusterFiles = cluster.Files.Select(f => files[f]).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var types = cluster.Files.SelectMany(f => membersOfFile[f]).ToList();
            var roots = RootsOf(clusterFiles);
            var term = cluster.IsShared ? "" : vocabulary.PrimaryTerm(cluster.Files);
            var entries = clusterFiles.SelectMany(f => entryByFile.GetValueOrDefault(f) ?? [])
                .OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal)
                .ThenBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line).Take(MaxEntryPointsPerSubsystem).ToList();
            var entryCount = clusterFiles.Sum(f => entryByFile.GetValueOrDefault(f)?.Count ?? 0);

            var subsystem = new Subsystem
            {
                Id = "",
                Name = "",
                NameOrigin = SubsystemNameOrigins.Derived,
                Kind = cluster.IsShared ? SubsystemKinds.Shared : SubsystemKinds.Capability,
                SourceSet = SourceSets.Production,
                Roots = roots,
                DominantTerm = term,
                Files = clusterFiles,
                KeyTypes = [.. types.OrderByDescending(t => degree[t]).ThenBy(t => graph.Nodes[t].Key, StringComparer.Ordinal)
                    .Take(MaxKeyTypes).Select(t => DisplayName(graph.Nodes[t]))],
                EntryPoints = entries,
                Tables = [.. (tables.GetValueOrDefault(cluster.Id) ?? []).Take(MaxTablesPerSubsystem)],
                Metrics = new SubsystemMetrics
                {
                    Files = clusterFiles.Count,
                    Loc = clusterFiles.Sum(f => (long)lines.GetValueOrDefault(f)),
                    Centrality = crossTotal > 0 ? incoming.GetValueOrDefault(cluster.Id) / crossTotal : 0,
                    EntryPoints = entryCount,
                    EntryPointKinds = clusterFiles.SelectMany(f => entryByFile.GetValueOrDefault(f) ?? []).Select(e => e.Kind).Distinct(StringComparer.Ordinal).Count(),
                    Tables = tables.GetValueOrDefault(cluster.Id)?.Count ?? 0,
                    States = types.Count(t => graph.Nodes[t].Label == "Enum"),
                    Integrations = types.Count(t => NameTokenizer.Tokenize(graph.Nodes[t].Name).Any(IntegrationTokens.Contains)),
                    DynamicUseSites = 0
                }
            };
            drafts.Add((cluster, cluster.IsShared ? SharedId : BaseIdOf(subsystem), subsystem));
        }

        // Ids estables: la base (término dominante o carpeta) y, si dos clusters coinciden, el hash de sus ficheros en todos ellos.
        // El identificador del compartido es siempre «shared»: si una capacidad coincide con él, es esa la que se distingue con el hash.
        var duplicated = drafts.GroupBy(d => d.BaseId, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var named = drafts.Select(d =>
        {
            var id = duplicated.Contains(d.BaseId) && !d.Cluster.IsShared ? $"{d.BaseId}-{HashOf(d.Subsystem.Files)}" : d.BaseId;
            return d.Subsystem with { Id = id };
        }).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

        named = DeriveNames(named);
        var unassigned = Enumerable.Range(0, files.Length).Where(f => clusterOfFile[f] < 0).Select(f => files[f]).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (unassigned.Count > 0)
            warnings.Add($"{unassigned.Count} fichero(s) de producción sin subsistema: sin dependencias ni carpeta que los acerque a ninguno.");

        return new DomainMap
        {
            GeneratedAt = input.GeneratedAt,
            GraphFingerprint = input.GraphFingerprint,
            Basis = useGraph ? DomainMapBases.Graph : DomainMapBases.StructureOnly,
            Subsystems = named,
            UnassignedFiles = unassigned,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Carpetas raíz de un conjunto de ficheros: las carpetas más altas que contienen alguno (una carpeta cuyo ancestro ya está
    /// no se lista), de más a menos ficheros y por nombre, como mucho <see cref="MaxRoots"/>.
    /// </summary>
    internal static IReadOnlyList<string> RootsOf(IReadOnlyList<string> files)
    {
        var counts = files.GroupBy(TypeGraph.DirectoryOf, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var topmost = counts.Keys.Where(d => !counts.Keys.Any(other => IsAncestor(other, d))).ToList();
        return [.. topmost
            .Select(d => (Directory: d, Count: counts.Where(c => c.Key == d || IsAncestor(d, c.Key)).Sum(c => c.Value)))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Directory, StringComparer.Ordinal)
            .Take(MaxRoots).Select(r => r.Directory)];
    }

    /// <summary>Identificador base: el término dominante o, sin él, los dos últimos tramos de la carpeta raíz principal.</summary>
    private static string BaseIdOf(Subsystem subsystem)
    {
        var text = subsystem.DominantTerm.Length > 0
            ? subsystem.DominantTerm
            : string.Join('-', (subsystem.Roots.Count > 0 ? subsystem.Roots[0] : "").Split('/', StringSplitOptions.RemoveEmptyEntries).TakeLast(2));
        var slug = Slug(text);
        return slug.Length > 0 ? slug : "root";
    }

    private static string Slug(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    private static string HashOf(IReadOnlyList<string> files) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', files)))).ToLowerInvariant()[..6];

    /// <summary>
    /// Nombre derivado de cada subsistema: el término dominante en mayúscula inicial o, sin él, su carpeta raíz. Si dos
    /// coinciden, se distinguen con el identificador.
    /// </summary>
    internal static List<Subsystem> DeriveNames(List<Subsystem> subsystems)
    {
        var names = subsystems.Select(DerivedName).ToList();
        var duplicated = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. subsystems.Select((s, i) => s with { Name = duplicated.Contains(names[i]) ? $"{names[i]} ({s.Id})" : names[i] })];
    }

    internal static string DerivedName(Subsystem subsystem)
    {
        if (subsystem.IsShared)
            return SharedName;
        if (subsystem.DominantTerm.Length > 0)
            return char.ToUpperInvariant(subsystem.DominantTerm[0]) + subsystem.DominantTerm[1..];
        if (subsystem.Roots.Count > 0 && subsystem.Roots[0].Length > 0)
            return string.Join('/', subsystem.Roots[0].Split('/', StringSplitOptions.RemoveEmptyEntries).TakeLast(2));
        return subsystem.Id;
    }

    /// <summary>Nombre legible de un tipo: su carpeta como paquete (sin <c>src/main/java</c>) y su nombre.</summary>
    private static string DisplayName(TypeNode type)
    {
        var directory = type.Directory;
        foreach (var prefix in new[] { "src/main/java/", "src/main/kotlin/", "src/main/scala/", "src/main/", "src/" })
        {
            if (directory.StartsWith(prefix, StringComparison.Ordinal))
            {
                directory = directory[prefix.Length..];
                break;
            }
        }
        directory = directory.Replace('/', '.');
        return directory.Length == 0 ? type.Name : $"{directory}.{type.Name}";
    }
}
