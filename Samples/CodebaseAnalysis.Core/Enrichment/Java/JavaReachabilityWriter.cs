using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;

namespace CodebaseAnalysis.Core.Enrichment.Java;

/// <summary>Recuentos de <see cref="JavaReachabilityWriter.ApplyAsync"/>.</summary>
public sealed record JavaReachabilityStats
{
    /// <summary>Tipos (clases y enums) y métodos que entran en el análisis: los que escribió el analizador Java.</summary>
    public int Types { get; init; }

    public int Methods { get; init; }

    /// <summary>Raíces vivas por origen: <c>main</c>, <c>annotation</c>, <c>struts</c>, <c>config</c>, <c>jsp</c>, <c>cbm</c>.</summary>
    public IReadOnlyDictionary<string, int> Roots { get; init; } = new Dictionary<string, int>();

    public int Reachable { get; init; }

    public int DeadHigh { get; init; }
    public int DeadMedium { get; init; }
    public int DeadLow { get; init; }

    /// <summary>De los candidatos, los métodos privados sin ninguna referencia dentro de una clase viva.</summary>
    public int DeadPrivateMethods { get; init; }

    /// <summary>De <see cref="DeadLow"/>, los que se conservan por una posible instanciación por reflexión.</summary>
    public int KeptForReflection { get; init; }

    /// <summary>Aristas entre nodos de Java que recorre el BFS.</summary>
    public int Edges { get; init; }

    public int DeadTotal => DeadHigh + DeadMedium + DeadLow;
}

/// <summary>
/// Alcanzabilidad y código muerto de Java (tanda 2 de la auditoría del 2026-09-30), con las mismas propiedades y la misma
/// semántica que <c>ReachabilityEnricher</c> en C#: <c>is_reachable</c> y, si no se alcanza, <c>dead_code_candidate</c> con
/// <c>dead_code_confidence</c> (<c>high</c>/<c>medium</c>/<c>low</c>) y <c>dead_code_reason</c>. Corre al final de
/// <c>LanguageEnricherStep</c>, cuando ya están escritos los nodos y las aristas de javac (<c>CALLS</c>, <c>USES_TYPE</c>,
/// <c>INHERITS</c>, <c>IMPLEMENTS</c>, <c>OVERRIDES</c>, <c>DEFINES_METHOD</c>) y las de Struts (<c>HANDLES</c> y las <c>Route</c> con
/// su <c>action_type</c> y su <c>form_type</c>). No escribe nodos ni aristas y no deja fila en <c>enrichment_runs</c>: cada pasada
/// recalcula y reemplaza las marcas de la anterior.
/// <para>
/// BFS. Raíces vivas: los <c>main</c>; los métodos <c>is_entry_point</c>/<c>is_test</c> de cbm; lo que lleva una anotación de
/// framework (clase o método); las clases que atienden una ruta de Struts (<c>HANDLES</c>) o son su <c>action_type</c> o su
/// <c>form_type</c>; las clases que nombra un XML de configuración (<c>web.xml</c>, <c>struts-config</c>, Spring…) o un literal de
/// <c>Class.forName("…")</c>; y las que referencia una JSP (por FQN, o por nombre simple dentro de un paquete importado).
/// Propagación: una referencia (<c>CALLS</c>, <c>USES_TYPE</c>, <c>READS</c>, <c>WRITES</c>) de un miembro o un tipo a otro; un
/// método vivo ⇒ su clase; una clase viva ⇒ sus métodos no privados, sus campos, su superclase, las interfaces que extiende y las
/// clases que la implementan (interfaz viva ⇒ implementaciones, que es lo que hace C#); un tipo anidado vivo ⇒ el que lo contiene. Un
/// método privado (ordinario, sin anotación de framework, no <c>main</c> ni de serialización) solo vive si lo referencia código
/// vivo: uno sin referencias dentro de una clase viva es candidato de confianza alta. El resto se decide por componente: una clase
/// es muerta solo si nada vivo la nombra, y sus métodos con ella.
/// </para>
/// <para>
/// Lo que puede instanciarse sin que el código lo nombre se conserva como dudoso (<c>low</c>): una clase que extiende o
/// implementa un tipo de una librería (la puede registrar el framework), o cuyo FQN empieza por un literal de paquete de un sitio
/// de <see cref="DynamicInstantiationScanner"/> (<c>Class.forName(PAQUETE + nombre + "Impl")</c>). Las raíces de test no están en el
/// grafo (P13): lo que solo usan los tests sale como candidato. Los <c>META-INF/services</c> y los <c>.properties</c> no se leen.
/// </para>
/// </summary>
public static partial class JavaReachabilityWriter
{
    public const string HighConfidence = "high";
    public const string MediumConfidence = "medium";
    public const string LowConfidence = "low";

    private static readonly string[] OwnedKeys =
        ["is_reachable", "dead_code_candidate", "dead_code_confidence", "dead_code_reason", "dead_code_suppressed_by"];

    /// <summary>Anotaciones que no hacen vivo a nadie: no dicen que un framework lo gestione.</summary>
    private static readonly HashSet<string> TrivialAnnotations = new(StringComparer.Ordinal)
    {
        "Override", "Deprecated", "SuppressWarnings", "SafeVarargs", "FunctionalInterface", "Nullable", "NonNull", "Nonnull", "NotNull"
    };

    /// <summary>Métodos privados que el runtime de serialización invoca por reflexión.</summary>
    private static readonly HashSet<string> SerializationHooks = new(StringComparer.Ordinal)
    {
        "writeObject", "readObject", "readObjectNoData", "readResolve", "writeReplace"
    };

    private sealed class Node
    {
        public long Id;
        public string Label = "";
        public string Name = "";
        public string? FilePath;
        public string? Raw;
        public bool IsType, IsMethod, IsField;
        public string? Fqn, Kind, Access, OuterFqn, OwnerFqn, SuperFqn;
        public List<string> Interfaces = [];
        public List<string> Annotations = [];
        public bool Abstract, Static, Ctor, IsOverride, EntryPoint, IsTest;
        public string[] ParamTypes = [];
        public long? Owner;
        public bool PrivateCandidate;

        /// <summary>Nombres de clase que el compilador resolvió en <c>Class.forName</c> o <c>loadClass</c> de este nodo (<c>a.b.*Impl</c>).</summary>
        public List<string> ReflectionTargets = [];
    }

    /// <exception cref="InvalidOperationException">Si la BD es la de cbm.</exception>
    public static async Task<JavaReachabilityStats> ApplyAsync(
        string dbPath, RepositoryScanResult? scan, DynamicInstantiationScan? reflection, CancellationToken ct)
    {
        using var conn = await EnrichmentMarks.OpenForBulkWriteAsync(dbPath, ct);
        using var tx = conn.BeginTransaction();
        var project = await GraphProject.ResolveAsync(conn, "Codebase", ct, tx);

        // Las marcas de la pasada anterior se retiran antes de leer los nodos.
        using (var reset = Command(conn, tx, $"""
            UPDATE nodes SET properties = json_remove(properties, {string.Join(", ", OwnedKeys.Select(k => $"'$.{k}'"))})
            WHERE project = @project AND file_path LIKE '%.java' AND json_valid(properties)
              AND ({string.Join(" OR ", OwnedKeys.Select(k => $"json_type(properties, '$.{k}') IS NOT NULL"))})
            """))
        {
            reset.Parameters.AddWithValue("@project", project);
            await reset.ExecuteNonQueryAsync(ct);
        }

        var nodes = new Dictionary<long, Node>();
        var routes = new List<(string? ActionType, string? FormType)>();
        using (var select = Command(conn, tx, """
            SELECT id, label, name, file_path, properties FROM nodes
            WHERE project = @project AND (file_path LIKE '%.java' OR label IN ('Route', 'Jsp'))
            """))
        {
            select.Parameters.AddWithValue("@project", project);
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var label = reader.GetString(1);
                var raw = reader.IsDBNull(4) ? null : reader.GetString(4);
                if (label is "Route" or "Jsp")
                {
                    if (label == "Route")
                        ReadRoute(raw, routes);
                    continue;
                }
                if (Parse(reader.GetInt64(0), label, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), raw) is { } node)
                    nodes[node.Id] = node;
            }
        }

        var types = nodes.Values.Where(n => n.IsType).ToList();
        var typesByFqn = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var type in types.OrderBy(t => t.Id))
            typesByFqn.TryAdd(type.Fqn!, type);

        // Dueño de cada método y de cada campo: el FQN que escribió el analizador, y DEFINES_METHOD si falta.
        foreach (var node in nodes.Values.Where(n => n.IsMethod || n.IsField))
        {
            if (node.OwnerFqn != null && typesByFqn.TryGetValue(node.OwnerFqn, out var owner))
                node.Owner = owner.Id;
        }

        var adjacency = new Dictionary<long, List<long>>();
        void AddEdge(long source, long target)
        {
            if (source == target)
                return;
            if (!adjacency.TryGetValue(source, out var list))
                adjacency[source] = list = [];
            list.Add(target);
        }

        var edgeCount = 0;
        var handlers = new HashSet<long>();
        using (var select = Command(conn, tx, """
            SELECT source_id, target_id, type FROM edges
            WHERE project = @project
              AND type IN ('CALLS', 'USES_TYPE', 'READS', 'WRITES', 'INHERITS', 'IMPLEMENTS', 'OVERRIDES', 'DEFINES_METHOD', 'HANDLES')
            """))
        {
            select.Parameters.AddWithValue("@project", project);
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var source = reader.GetInt64(0);
                var target = reader.GetInt64(1);
                var type = reader.GetString(2);
                if (type == "HANDLES")
                {
                    if (nodes.ContainsKey(source))
                        handlers.Add(source);
                    continue;
                }
                if (!nodes.TryGetValue(source, out var from) || !nodes.TryGetValue(target, out var to))
                    continue;
                switch (type)
                {
                    case "DEFINES_METHOD":
                        if (to.IsMethod && from.IsType)
                            to.Owner ??= from.Id;
                        continue;
                    case "INHERITS":
                        AddEdge(source, target);
                        // Una interfaz que extiende a otra: viva la de arriba, viven las que la extienden (y así sus implementaciones).
                        if (from.Kind is "interface" or "annotation")
                            AddEdge(target, source);
                        break;
                    case "IMPLEMENTS":
                        AddEdge(source, target);
                        AddEdge(target, source); // interfaz viva ⇒ sus implementaciones
                        break;
                    case "OVERRIDES":
                        AddEdge(source, target); // un método vivo que sobrescribe a otro mantiene vivo al de arriba
                        break;
                    default:
                        AddEdge(source, target);
                        break;
                }
                edgeCount++;
            }
        }

        // Un método PrivateCandidate solo vive si lo referencia código vivo; el resto, con su clase.
        foreach (var node in nodes.Values.Where(n => n.IsMethod))
            node.PrivateCandidate = IsPrivateCandidate(node);
        foreach (var node in nodes.Values.Where(n => n.IsMethod && n.Owner is { } owner && nodes.ContainsKey(owner)))
        {
            var owner = node.Owner!.Value;
            AddEdge(node.Id, owner);
            if (!node.PrivateCandidate)
                AddEdge(owner, node.Id);
        }
        foreach (var node in nodes.Values.Where(n => n.IsField && n.Owner is not null))
            AddEdge(node.Owner!.Value, node.Id);
        foreach (var type in types)
        {
            if (type.SuperFqn != null && typesByFqn.TryGetValue(type.SuperFqn, out var superType))
                AddEdge(type.Id, superType.Id);
            if (type.OuterFqn != null && typesByFqn.TryGetValue(type.OuterFqn, out var outer))
                AddEdge(type.Id, outer.Id);
        }

        // Raíces.
        var roots = new Dictionary<long, string>();
        void Root(long id, string origin)
        {
            if (nodes.ContainsKey(id) && !roots.ContainsKey(id))
                roots[id] = origin;
        }

        foreach (var node in nodes.Values)
        {
            if (node.IsMethod && node.Name == "main" && node.Static && node.ParamTypes is [var only] && only.StartsWith("java.lang.String", StringComparison.Ordinal))
                Root(node.Id, "main");
            else if (node.IsMethod && (node.EntryPoint || node.IsTest))
                Root(node.Id, "cbm");
            else if ((node.IsType || node.IsMethod) && node.Annotations.Any(a => !TrivialAnnotations.Contains(a)))
                Root(node.Id, "annotation");
        }
        foreach (var id in handlers)
            Root(id, "struts");
        foreach (var (actionType, formType) in routes)
        {
            foreach (var name in new[] { actionType, formType })
            {
                if (name != null && FindType(typesByFqn, name) is { } type)
                    Root(type.Id, "struts");
            }
        }

        var namedByConfiguration = new HashSet<long>();
        var namedByReflection = new HashSet<long>();

        // Los nombres de clase que javac resolvió como constantes (reflection_targets): fijos o con «*» por cada tramo no constante.
        // Lo que el patrón puede nombrar se instancia por reflexión, así que no es código muerto.
        foreach (var target in nodes.Values.SelectMany(n => n.ReflectionTargets).Distinct(StringComparer.Ordinal))
        {
            foreach (var type in MatchingTypes(target, typesByFqn))
            {
                Root(type.Id, "reflection");
                namedByReflection.Add(type.Id);
            }
        }

        if (reflection != null)
        {
            foreach (var named in reflection.NamedClasses)
            {
                if (FindType(typesByFqn, named.Name) is { } type)
                {
                    Root(type.Id, named.Source == DynamicInstantiationScan.SourceXml ? "config" : "literal");
                    namedByConfiguration.Add(type.Id);
                }
            }
        }
        if (scan != null)
        {
            foreach (var id in JspReferencedTypes(scan, typesByFqn))
                Root(id, "jsp");
        }

        // BFS.
        var reachable = new HashSet<long>(roots.Keys);
        var queue = new Queue<long>(roots.Keys);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var next))
                continue;
            foreach (var target in next)
            {
                if (reachable.Add(target))
                    queue.Enqueue(target);
            }
        }

        // Marcado.
        var sites = reflection?.Sites ?? [];
        var literals = reflection?.LiteralsBySiteFile ?? new Dictionary<string, IReadOnlyList<string>>();
        var library = new Dictionary<long, string?>();
        var stats = new Counters();
        var updates = new List<(Node Node, JsonObject Patch)>();
        var deadType = new Dictionary<long, (string Confidence, string Reason)>();

        // Tipos primero: los métodos de una clase dudosa salen también como dudosos.
        foreach (var type in types.Where(t => t.Kind is "class" or "enum"))
        {
            if (reachable.Contains(type.Id))
            {
                var patch = new JsonObject { ["is_reachable"] = true };
                if ((namedByConfiguration.Contains(type.Id) || namedByReflection.Contains(type.Id)) && roots.TryGetValue(type.Id, out var by)
                    && by is "config" or "literal" or "reflection")
                    patch["dead_code_suppressed_by"] = by switch { "config" => "config-xml", "literal" => "class-literal", _ => "reflection-target" };
                updates.Add((type, patch));
                stats.Reachable++;
                continue;
            }

            var (confidence, reason) = ClassifyType(type, typesByFqn, library, sites, literals, out var reflective);
            deadType[type.Id] = (confidence, reason);
            updates.Add((type, Dead(confidence, reason)));
            stats.Count(confidence);
            if (reflective)
                stats.KeptForReflection++;
        }

        foreach (var method in nodes.Values.Where(n => n.IsMethod && n.Owner is { } o && nodes.ContainsKey(o)))
        {
            var owner = nodes[method.Owner!.Value];
            if (reachable.Contains(method.Id))
            {
                updates.Add((method, new JsonObject { ["is_reachable"] = true }));
                stats.Reachable++;
            }
            else if (owner.Kind is "interface" or "annotation")
            {
                // Un método de interfaz es un contrato, como la interfaz: no es candidato.
                updates.Add((method, new JsonObject { ["is_reachable"] = false }));
            }
            else
            {
                var (confidence, reason) = ClassifyMethod(method, owner, reachable.Contains(owner.Id), deadType);
                updates.Add((method, Dead(confidence, reason)));
                stats.Count(confidence);
                if (confidence == HighConfidence && method.PrivateCandidate && reachable.Contains(owner.Id))
                    stats.DeadPrivateMethods++;
            }
        }

        using var update = Command(conn, tx, "UPDATE nodes SET properties = @props WHERE id = @id");
        var props = update.Parameters.Add("@props", SqliteType.Text);
        var idParameter = update.Parameters.Add("@id", SqliteType.Integer);
        foreach (var (node, patch) in updates)
        {
            var properties = EnrichmentMarks.ParseProperties(node.Raw);
            foreach (var (key, value) in patch)
                properties[key] = value?.DeepClone();
            props.Value = properties.ToJsonString();
            idParameter.Value = node.Id;
            await update.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();

        var rootCounts = roots.Values.GroupBy(v => v, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new JavaReachabilityStats
        {
            Types = types.Count,
            Methods = nodes.Values.Count(n => n.IsMethod),
            Roots = new SortedDictionary<string, int>(rootCounts, StringComparer.Ordinal),
            Reachable = stats.Reachable,
            DeadHigh = stats.High,
            DeadMedium = stats.Medium,
            DeadLow = stats.Low,
            DeadPrivateMethods = stats.DeadPrivateMethods,
            KeptForReflection = stats.KeptForReflection,
            Edges = edgeCount
        };
    }

    private sealed class Counters
    {
        public int Reachable, High, Medium, Low, DeadPrivateMethods, KeptForReflection;

        public void Count(string confidence)
        {
            switch (confidence)
            {
                case HighConfidence: High++; break;
                case MediumConfidence: Medium++; break;
                default: Low++; break;
            }
        }
    }

    private static JsonObject Dead(string confidence, string reason) => new()
    {
        ["is_reachable"] = false,
        ["dead_code_candidate"] = true,
        ["dead_code_confidence"] = confidence,
        ["dead_code_reason"] = reason
    };

    // ---------------------------------------------------------------- clasificación

    private static (string Confidence, string Reason) ClassifyType(
        Node type, Dictionary<string, Node> typesByFqn, Dictionary<long, string?> library,
        IReadOnlyList<InstantiationSite> sites, IReadOnlyDictionary<string, IReadOnlyList<string>> literals, out bool reflective)
    {
        reflective = false;
        if (ReflectionHint(type, sites, literals) is { } hint)
        {
            reflective = true;
            return (LowConfidence, $"posible instanciación por reflexión: {hint}; se conserva como dudosa");
        }
        if (LibrarySupertype(type, typesByFqn, library, []) is { } supertype)
            return (LowConfidence, $"clase que extiende o implementa un tipo de una librería ({supertype}): puede registrarla el framework (web.xml, anotaciones, SPI) sin que el código la nombre");

        return IsNonPublic(type.Access)
            ? (HighConfidence, $"clase {type.Access} sin conexión a ningún punto de entrada (ni usos, ni DI, ni acciones de Struts, ni JSP ni configuración)")
            : (MediumConfidence, "clase sin conexión a puntos de entrada (posible API pública o instanciación por reflexión)");
    }

    private static (string Confidence, string Reason) ClassifyMethod(
        Node method, Node owner, bool ownerReachable, Dictionary<long, (string Confidence, string Reason)> deadType)
    {
        if (method.PrivateCandidate && ownerReachable)
            return (HighConfidence, "método privado sin ninguna referencia desde código vivo del proyecto (referencias exactas de javac; solo la reflexión podría llamarlo)");

        if (deadType.TryGetValue(owner.Id, out var dead) && dead.Confidence == LowConfidence)
            return (LowConfidence, "método de una clase que se conserva como dudosa: " + dead.Reason);

        if (method.IsOverride || method.Abstract)
            return (LowConfidence, "método que sobrescribe o implementa otro (o abstracto): puede invocarse por el contrato base o por una interfaz sin arista directa");

        return IsNonPublic(method.Access)
            ? (HighConfidence, $"método {method.Access ?? "no público"} en un componente sin conexión a puntos de entrada")
            : (MediumConfidence, "método público sin conexión a puntos de entrada: posible API externa o invocación por reflexión");
    }

    private static bool IsNonPublic(string? access) => access is "private" or "package" or "protected";

    /// <summary>
    /// Un método privado que puede darse por muerto sin más: ordinario, sin anotaciones de framework (pueden registrarlo por
    /// reflexión), que no sea <c>main</c>, un gancho de serialización ni el que sobrescribe a otro.
    /// </summary>
    private static bool IsPrivateCandidate(Node method) =>
        method is { Ctor: false, Access: "private", IsOverride: false }
        && method.Name != "main"
        && !SerializationHooks.Contains(method.Name)
        && method.Annotations.All(TrivialAnnotations.Contains);

    /// <summary>El tipo de librería del que desciende la clase (por su superclase o sus interfaces, también a través de las del proyecto).</summary>
    private static string? LibrarySupertype(Node type, Dictionary<string, Node> typesByFqn, Dictionary<long, string?> cache, HashSet<long> visiting)
    {
        if (cache.TryGetValue(type.Id, out var known))
            return known;
        if (!visiting.Add(type.Id))
            return null;

        string? found = null;
        foreach (var fqn in (type.SuperFqn != null ? [type.SuperFqn] : Array.Empty<string>()).Concat(type.Interfaces))
        {
            if (typesByFqn.TryGetValue(fqn, out var parent))
            {
                found ??= LibrarySupertype(parent, typesByFqn, cache, visiting);
            }
            else if (!fqn.StartsWith("java.", StringComparison.Ordinal))
            {
                found ??= fqn;
            }
        }
        cache[type.Id] = found;
        return found;
    }

    /// <summary>
    /// «fichero:línea (llamada) compone el nombre con el prefijo '…'» si un sitio de instanciación dinámica tiene un literal de
    /// paquete (con un punto y que termina en punto, o que es el paquete de la clase) y el FQN de la clase empieza por él.
    /// </summary>
    private static string? ReflectionHint(
        Node type, IReadOnlyList<InstantiationSite> sites, IReadOnlyDictionary<string, IReadOnlyList<string>> literals)
    {
        foreach (var site in sites)
        {
            if (!literals.TryGetValue(site.File, out var values))
                continue;
            foreach (var value in values)
            {
                if (value.Length < 6 || !value.Contains('.') || value.Contains(' '))
                    continue;
                var prefix = value.EndsWith('.') ? value : value + ".";
                if (type.Fqn!.StartsWith(prefix, StringComparison.Ordinal))
                    return $"{site.File}:{site.Line} ({site.Kind}) compone el nombre de la clase con el prefijo '{value}'";
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- lectura

    private static Node? Parse(long id, string label, string name, string? filePath, string? raw)
    {
        if (raw == null || !raw.StartsWith('{'))
            return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool Flag(string key) => root.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number) && number != 0);
            List<string> Strings(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [];

            var node = new Node
            {
                Id = id, Label = label, Name = name, FilePath = filePath, Raw = raw,
                Access = Text("accessibility"), Abstract = Flag("is_abstract"), Static = Flag("is_static"),
                Annotations = Strings("annotations"), EntryPoint = Flag("is_entry_point"), IsTest = Flag("is_test"),
                ReflectionTargets = Strings("reflection_targets")
            };

            if (Text("fqn") is { Length: > 0 } fqn && Text("kind") is { } kind)
            {
                node.IsType = true;
                node.Fqn = fqn;
                node.Kind = kind;
                node.OuterFqn = Text("outer_fqn");
                node.SuperFqn = Text("super_fqn");
                node.Interfaces = Strings("interfaces_fqn");
                return node;
            }

            if (label is "Method" or "Function" && root.TryGetProperty("param_types", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                node.IsMethod = true;
                node.Ctor = Flag("is_constructor");
                node.IsOverride = Flag("is_override");
                node.ParamTypes = [.. Strings("param_types")];
                node.OwnerFqn = Text("owner_fqn");
                return node;
            }

            if (label == "Field" && Text("field_name") != null && Text("owner_fqn") is { } fieldOwner)
            {
                node.IsField = true;
                node.OwnerFqn = fieldOwner;
                return node;
            }
        }
        return null;
    }

    private static void ReadRoute(string? raw, List<(string?, string?)> routes)
    {
        if (raw == null || !raw.StartsWith('{'))
            return;
        try
        {
            if (JsonNode.Parse(raw) is JsonObject properties)
                routes.Add((properties["action_type"]?.GetValue<string>(), properties["form_type"]?.GetValue<string>()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Propiedades que no son las esperadas: esa ruta no aporta raíces.
        }
    }

    /// <summary><c>a.B</c> o <c>a.B$C</c> (un nombre de configuración) como el FQN de javac.</summary>
    /// <summary>Los tipos que nombra un <c>reflection_target</c>: el FQN exacto o, con «*», los que casan con el patrón.</summary>
    private static IEnumerable<Node> MatchingTypes(string target, Dictionary<string, Node> typesByFqn)
    {
        if (!target.Contains('*'))
            return FindType(typesByFqn, target) is { } exact ? [exact] : [];
        var pattern = new Regex("^" + string.Join(".*", target.Split('*').Select(Regex.Escape)) + "$", RegexOptions.CultureInvariant);
        return typesByFqn.Values.Where(t => pattern.IsMatch(t.Fqn!));
    }

    private static Node? FindType(Dictionary<string, Node> typesByFqn, string name)
    {
        var fqn = name.Trim().Replace('$', '.');
        if (typesByFqn.TryGetValue(fqn, out var type))
            return type;
        // a.B.CONSTANTE o a.B.metodo: se prueba quitando segmentos por la derecha.
        for (var dot = fqn.LastIndexOf('.'); dot > 0; dot = fqn.LastIndexOf('.', dot - 1))
        {
            if (typesByFqn.TryGetValue(fqn[..dot], out type))
                return type;
        }
        return null;
    }

    /// <summary>
    /// Los tipos que nombra el texto de cada JSP: por FQN (<c>page import</c>, <c>useBean</c>, código de un scriptlet) y, dentro
    /// de los paquetes que importa con <c>.*</c>, por nombre simple.
    /// </summary>
    private static IEnumerable<long> JspReferencedTypes(RepositoryScanResult scan, Dictionary<string, Node> typesByFqn)
    {
        var bySimpleName = typesByFqn.Values.Where(t => !t.Fqn!.Contains('$')).GroupBy(t => t.Fqn![(t.Fqn!.LastIndexOf('.') + 1)..]).ToDictionary(g => g.Key, g => g.ToList());
        var found = new HashSet<long>();
        foreach (var path in scan.JspFiles)
        {
            string text;
            try
            {
                text = File.ReadAllText(scan.FullPath(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (Match m in QualifiedNamePattern().Matches(text))
            {
                if (FindType(typesByFqn, m.Value) is { } type)
                    found.Add(type.Id);
            }

            var packages = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match import in PageImportPattern().Matches(text))
            {
                foreach (var entry in import.Groups["v"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (entry.EndsWith(".*", StringComparison.Ordinal))
                        packages.Add(entry[..^2]);
                }
            }
            if (packages.Count == 0)
                continue;
            foreach (Match m in SimpleNamePattern().Matches(text))
            {
                if (!bySimpleName.TryGetValue(m.Value, out var candidates))
                    continue;
                foreach (var candidate in candidates)
                {
                    var package = candidate.Fqn![..candidate.Fqn!.LastIndexOf('.')];
                    if (packages.Contains(package))
                        found.Add(candidate.Id);
                }
            }
        }
        return found;
    }

    [GeneratedRegex(@"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)+[A-Z][A-Za-z0-9_$]*(?:\.[A-Za-z0-9_$]+)*")]
    private static partial Regex QualifiedNamePattern();

    [GeneratedRegex(@"\bimport\s*=\s*""(?<v>[^""]*)""")]
    private static partial Regex PageImportPattern();

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_]*\b")]
    private static partial Regex SimpleNamePattern();

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
}
