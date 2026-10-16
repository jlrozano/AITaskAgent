using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>Cómo se obtuvo un lote (<see cref="SharedPassBatch.Split"/>).</summary>
public static class BatchSplitMethods
{
    /// <summary>El subsistema entero cabe en el presupuesto.</summary>
    public const string None = "none";

    /// <summary>Partido por comunidades del grafo de tipos a mayor resolución.</summary>
    public const string Communities = "communities";

    /// <summary>Partido por orden de ficheros, agrupando por carpeta (las comunidades no bastaron).</summary>
    public const string Folders = "folders";
}

/// <summary>Un lote de la pasada compartida: un subsistema del mapa de dominios o una parte de él.</summary>
public sealed record SharedPassBatch
{
    /// <summary>Id del subsistema si es entero; <c>{subsistema}-p{n}</c> si es una parte. Estable entre ejecuciones con la misma entrada.</summary>
    public required string Id { get; init; }

    public required string SubsystemId { get; init; }
    public string SubsystemName { get; init; } = "";
    public string SubsystemDescription { get; init; } = "";

    /// <summary>Uno de <see cref="SubsystemKinds"/>.</summary>
    public string SubsystemKind { get; init; } = SubsystemKinds.Capability;

    public bool IsShared => string.Equals(SubsystemKind, SubsystemKinds.Shared, StringComparison.Ordinal);

    /// <summary>Parte, desde 1, de <see cref="PartCount"/>.</summary>
    public int Part { get; init; } = 1;

    public int PartCount { get; init; } = 1;

    /// <summary>Nivel de dependencia del subsistema (<see cref="BatchPlanner"/>): 0 el compartido, luego de los más usados a los que más usan.</summary>
    public int Level { get; init; }

    /// <summary>Posición en el plan, desde 0.</summary>
    public int Order { get; init; }

    /// <summary>Ficheros del lote, relativos al repositorio con <c>/</c>, en orden ordinal.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// Tokens estimados del paquete entero: sus ficheros (hechos del grafo más código numerado) y, si quien planifica da la
    /// estimación de lo demás (<see cref="PacketBuilder.OverheadEstimator"/>), la cabecera y las relaciones.
    /// </summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Uno de <see cref="BatchSplitMethods"/>.</summary>
    public string Split { get; init; } = BatchSplitMethods.None;
}

/// <summary>Los lotes en orden de lectura y los avisos de la planificación.</summary>
public sealed record BatchPlan
{
    public IReadOnlyList<SharedPassBatch> Batches { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int TotalEstimatedTokens => Batches.Sum(b => b.EstimatedTokens);
}

/// <summary>
/// Reparte los ficheros del mapa de dominios en lotes (D1, determinista): un lote por subsistema; si sus ficheros superan
/// <see cref="SharedPassOptions.MaxBatchTokens"/>, se parte con <see cref="CommunityDetector"/> sobre el subgrafo de sus tipos a
/// resoluciones crecientes (<see cref="SplitResolutions"/>) y, si una parte sigue sin caber, por orden de ficheros agrupando por
/// carpeta. Un fichero que no cabe solo va a un lote propio con aviso. Los ficheros sin subsistema no entran (aviso).
/// </summary>
/// <remarks>
/// <para><b>Orden por niveles.</b> Nivel 0: el subsistema compartido. Después, por rondas: entran a la vez los subsistemas que no
/// dependen (aristas del grafo de tipos) de ninguno de los que quedan; si un ciclo no deja ninguno, entra solo el que menos peso
/// de dependencias tiene hacia los que quedan (empate por id). Dentro de un nivel, por id de subsistema y parte. Así un lote se lee
/// después de lo que usa, salvo en los ciclos.</para>
/// <para><b>Estimación.</b> La da quien llama por fichero (<see cref="PacketBuilder.EstimateFileTokens"/>: caracteres / 4 hacia
/// arriba, la regla del experimento) y, aparte, para la cabecera y las relaciones de cada conjunto de ficheros
/// (<see cref="PacketBuilder.OverheadEstimator"/>), de modo que el presupuesto se mide sobre el paquete entero.</para>
/// </remarks>
public static class BatchPlanner
{
    /// <summary>Resoluciones de Louvain con que se intenta partir un subsistema que no cabe, de menor a mayor (1 es la del mapa).</summary>
    public static readonly IReadOnlyList<double> SplitResolutions = [2.0, 4.0, 8.0];

    public static BatchPlan Plan(DomainMap map, TypeGraph graph, Func<string, int> fileTokens, SharedPassOptions options) =>
        Plan(map, graph, fileTokens, null, options);

    /// <summary>
    /// Como la otra sobrecarga, con <c>overheadTokens</c>: lo que el paquete de un conjunto de ficheros de un subsistema lleva además de sus ficheros (cabecera y relaciones,
    /// <see cref="PacketBuilder.OverheadEstimator"/>): con él, <see cref="SharedPassOptions.MaxBatchTokens"/> se mide sobre el
    /// paquete entero. null: solo los ficheros.
    /// </summary>
    public static BatchPlan Plan(
        DomainMap map, TypeGraph graph, Func<string, int> fileTokens, Func<Subsystem, IReadOnlyCollection<string>, int>? overheadTokens, SharedPassOptions options)
    {
        var warnings = new List<string>();
        var budget = options.MaxBatchTokens > 0 ? options.MaxBatchTokens : int.MaxValue;
        var tokens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int TokensOf(string file) => tokens.TryGetValue(file, out var t) ? t : tokens[file] = Math.Max(0, fileTokens(file));

        var subsystems = map.Subsystems.Where(s => s.Files.Count > 0).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        if (map.UnassignedFiles.Count > 0)
            warnings.Add($"{map.UnassignedFiles.Count} fichero(s) sin subsistema en el mapa de dominios no entran en ningún lote.");

        var levels = Levels(subsystems, graph);
        var drafts = new List<SharedPassBatch>();
        foreach (var subsystem in subsystems)
        {
            var files = subsystem.Files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();
            int Size(IReadOnlyCollection<string> set) =>
                (int)Math.Min(int.MaxValue, Sum(set, TokensOf) + (long)(overheadTokens == null ? 0 : Math.Max(0, overheadTokens(subsystem, set))));
            List<(List<string> Files, string Method)> parts = Size(files) <= budget
                ? [(files, BatchSplitMethods.None)]
                : new Splitter(graph, Size, budget, warnings).Split(files, 0);
            parts = [.. parts.Where(p => p.Files.Count > 0)
                .Select(p => (Files: p.Files.OrderBy(f => f, StringComparer.Ordinal).ToList(), p.Method))
                .OrderBy(p => p.Files[0], StringComparer.Ordinal)];

            for (var i = 0; i < parts.Count; i++)
            {
                drafts.Add(new SharedPassBatch
                {
                    Id = parts.Count == 1 ? subsystem.Id : $"{subsystem.Id}-p{i + 1}",
                    SubsystemId = subsystem.Id,
                    SubsystemName = subsystem.Name,
                    SubsystemDescription = subsystem.Description,
                    SubsystemKind = subsystem.Kind,
                    Part = i + 1,
                    PartCount = parts.Count,
                    Level = levels[subsystem.Id],
                    Files = parts[i].Files,
                    EstimatedTokens = Size(parts[i].Files),
                    Split = parts[i].Method
                });
            }
        }

        var ordered = drafts.OrderBy(b => b.Level).ThenBy(b => b.SubsystemId, StringComparer.Ordinal).ThenBy(b => b.Part)
            .Select((b, i) => b with { Order = i }).ToList();
        return new BatchPlan { Batches = ordered, Warnings = warnings };
    }

    private static int Sum(IEnumerable<string> files, Func<string, int> tokens) =>
        (int)Math.Min(int.MaxValue, files.Sum(f => (long)tokens(f)));

    /// <summary>Nivel de cada subsistema por id (ver el resumen de la clase).</summary>
    internal static Dictionary<string, int> Levels(IReadOnlyList<Subsystem> subsystems, TypeGraph graph)
    {
        var subsystemOfFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subsystem in subsystems)
        {
            foreach (var file in subsystem.Files)
                subsystemOfFile.TryAdd(file, subsystem.Id);
        }

        var depends = subsystems.ToDictionary(s => s.Id, _ => new Dictionary<string, double>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            var source = graph.Nodes[edge.Source];
            var target = graph.Nodes[edge.Target];
            if (source.IsVirtual || target.IsVirtual
                || !subsystemOfFile.TryGetValue(source.File, out var from) || !subsystemOfFile.TryGetValue(target.File, out var to) || from == to)
                continue;
            depends[from][to] = depends[from].GetValueOrDefault(to) + edge.Weight;
        }

        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasShared = false;
        foreach (var shared in subsystems.Where(s => s.IsShared))
        {
            levels[shared.Id] = 0;
            hasShared = true;
        }

        var remaining = new SortedSet<string>(subsystems.Where(s => !s.IsShared).Select(s => s.Id), StringComparer.Ordinal);
        var round = hasShared ? 1 : 0;
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => !depends[id].Keys.Any(remaining.Contains)).ToList();
            if (ready.Count == 0)
            {
                ready = [remaining.OrderBy(id => depends[id].Where(d => remaining.Contains(d.Key)).Sum(d => d.Value))
                    .ThenBy(id => id, StringComparer.Ordinal).First()];
            }
            foreach (var id in ready)
            {
                levels[id] = round;
                remaining.Remove(id);
            }
            round++;
        }
        return levels;
    }

    /// <summary>La partición de un subsistema que no cabe.</summary>
    private sealed class Splitter(TypeGraph graph, Func<IReadOnlyCollection<string>, int> size, int budget, List<string> warnings)
    {
        private readonly Dictionary<string, List<int>> _typesOfFile = graph.Nodes.Where(n => !n.IsVirtual)
            .GroupBy(n => n.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Index).ToList(), StringComparer.OrdinalIgnoreCase);

        /// <summary>Parte <paramref name="files"/> (ordenados) probando desde la resolución <paramref name="resolution"/> de <see cref="SplitResolutions"/>.</summary>
        public List<(List<string> Files, string Method)> Split(List<string> files, int resolution)
        {
            if (files.Count <= 1 || size(files) <= budget)
            {
                if (files.Count == 1 && size(files) > budget)
                    warnings.Add($"⚠️ El fichero {files[0]} (~{size(files)} tokens) supera por sí solo MaxBatchTokens ({budget}): va en un lote propio.");
                return [(files, BatchSplitMethods.Communities)];
            }

            for (var r = resolution; r < SplitResolutions.Count; r++)
            {
                var groups = CommunityGroups(files, SplitResolutions[r]);
                if (groups.Count < 2)
                    continue;
                var result = new List<(List<string>, string)>();
                foreach (var chunk in Pack(groups))
                {
                    if (size(chunk) > budget)
                        result.AddRange(Split(chunk, r + 1));
                    else
                        result.Add((chunk, BatchSplitMethods.Communities));
                }
                return result;
            }

            return [.. ByFolders(files).Select(chunk => (chunk, BatchSplitMethods.Folders))];
        }

        /// <summary>
        /// Los ficheros agrupados por la comunidad de la mayoría de sus tipos (empate: la de menor número); los que no tienen tipos, con la
        /// comunidad que más ficheros tiene en su carpeta o, si no hay, en un grupo por carpeta. Grupos por su primer fichero.
        /// </summary>
        private List<List<string>> CommunityGroups(List<string> files, double resolution)
        {
            var members = files.SelectMany(f => _typesOfFile.GetValueOrDefault(f) ?? []).Distinct().Order().ToList();
            if (members.Count < 2)
                return [];
            var sub = AffinityGraph.Induce(graph, members, out var indexOf);
            var labels = CommunityDetector.Detect(sub, resolution);
            var labelOfNode = new Dictionary<int, int>();
            for (var i = 0; i < members.Count; i++)
                labelOfNode[members[i]] = labels[indexOf[i]];

            var labelOfFile = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (_typesOfFile.TryGetValue(file, out var types))
                {
                    var label = types.Where(labelOfNode.ContainsKey).GroupBy(t => labelOfNode[t])
                        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).DefaultIfEmpty(-1).First();
                    if (label >= 0)
                        labelOfFile[file] = $"c{label}";
                }
            }
            foreach (var file in files.Where(f => !labelOfFile.ContainsKey(f)).ToList())
            {
                var directory = TypeGraph.DirectoryOf(file);
                var sibling = files.Where(f => labelOfFile.ContainsKey(f) && labelOfFile[f].StartsWith('c') && TypeGraph.DirectoryOf(f) == directory)
                    .GroupBy(f => labelOfFile[f], StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => g.Key).FirstOrDefault();
                labelOfFile[file] = sibling ?? "d:" + directory;
            }

            return [.. files.GroupBy(f => labelOfFile[f], StringComparer.Ordinal).Select(g => g.ToList()).OrderBy(g => g[0], StringComparer.Ordinal)];
        }

        /// <summary>Junta grupos consecutivos mientras quepan; un grupo que no cabe solo queda aparte.</summary>
        private List<List<string>> Pack(IEnumerable<List<string>> groups)
        {
            var chunks = new List<List<string>>();
            var current = new List<string>();
            foreach (var group in groups)
            {
                if (current.Count > 0 && size([.. current, .. group]) > budget)
                {
                    chunks.Add(current);
                    current = [];
                }
                current.AddRange(group);
            }
            if (current.Count > 0)
                chunks.Add(current);
            return chunks;
        }

        /// <summary>Por carpeta (orden ordinal); una carpeta que no cabe se corta por orden de ficheros.</summary>
        private List<List<string>> ByFolders(List<string> files)
        {
            var groups = new List<List<string>>();
            foreach (var folder in files.GroupBy(TypeGraph.DirectoryOf, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var inFolder = folder.OrderBy(f => f, StringComparer.Ordinal).ToList();
                if (size(inFolder) <= budget)
                {
                    groups.Add(inFolder);
                    continue;
                }
                foreach (var file in inFolder)
                {
                    if (size([file]) > budget)
                        warnings.Add($"⚠️ El fichero {file} (~{size([file])} tokens) supera por sí solo MaxBatchTokens ({budget}): va en un lote propio.");
                    groups.Add([file]);
                }
            }
            return Pack(groups);
        }
    }
}
