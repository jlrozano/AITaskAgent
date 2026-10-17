using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace DotNetEnricher.Tests;

/// <summary>
/// Se salta si falta algo de la máquina: codebase-memory-mcp, el BdpGraphEnricher.CLI instalado o el repositorio de
/// evaluación. Cada uno se puede indicar por variable de entorno (ver <see cref="ParityEnvironment"/>).
/// </summary>
public sealed class ParityFactAttribute : FactAttribute
{
    public ParityFactAttribute()
    {
        if (ParityEnvironment.SkipReason is { } reason)
            Skip = reason;
    }
}

internal static class ParityEnvironment
{
    public const string CbmVariable = "DOTNETENRICHER_PARITY_CBM";
    public const string BdpCliVariable = "DOTNETENRICHER_PARITY_BDP_CLI";
    public const string RepoVariable = "DOTNETENRICHER_PARITY_REPO";

    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string? Cbm { get; } = FirstExisting(Environment.GetEnvironmentVariable(CbmVariable),
        Path.Combine(LocalAppData, "Programs", "codebase-memory-mcp", "codebase-memory-mcp.exe"));

    public static string? BdpCli { get; } = FirstExisting(Environment.GetEnvironmentVariable(BdpCliVariable),
        Path.Combine(Profile, "source", "BdpGraphEnricher", "src", "BdpGraphEnricher.CLI", "bin", "Release", "net10.0", "BdpGraphEnricher.CLI.exe"),
        Path.Combine(Profile, "source", "BdpGraphEnricher", "src", "BdpGraphEnricher.CLI", "bin", "Debug", "net10.0", "BdpGraphEnricher.CLI.exe"));

    public static string? Repo { get; } = new[] { Environment.GetEnvironmentVariable(RepoVariable), Path.Combine(Profile, "eval-repos", "csharp-simple") }
        .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));

    /// <summary>El CLI compilado junto a los tests (referencia de proyecto): el apphost si está, si no el .dll con <c>dotnet</c>.</summary>
    public static (string File, string[] Prefix) DotNetEnricher =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "DotNetEnricher.exe")) && OperatingSystem.IsWindows()
            ? (Path.Combine(AppContext.BaseDirectory, "DotNetEnricher.exe"), [])
            : ("dotnet", [Path.Combine(AppContext.BaseDirectory, "DotNetEnricher.dll")]);

    public static string? SkipReason =>
        Cbm is null ? $"Sin codebase-memory-mcp (define {CbmVariable})."
        : BdpCli is null ? $"Sin BdpGraphEnricher.CLI compilado (define {BdpCliVariable})."
        : Repo is null ? $"Sin el repositorio de evaluación csharp-simple (define {RepoVariable})."
        : null;

    private static string? FirstExisting(params string?[] paths) =>
        paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
}

/// <summary>
/// Paridad con el enriquecedor original: indexa una copia de csharp-simple con cbm, enriquece una copia de la BD con el
/// BdpGraphEnricher.CLI instalado y otra con DotNetEnricher, y compara nodos, aristas y propiedades. Solo se admiten las
/// diferencias buscadas: lo que se quitó (vectorización, ADR, documentación, lo de BDP), los CsProject (reales en lugar
/// del segundo segmento del QN), las marcas <c>enriched_by</c> que ahora se suman, el <c>origin</c> que un parche ya no
/// cambia y el <c>file_path</c> relativo de los nodos creados, más lo que aportan las referencias exactas, los campos, los
/// patrones de error y los comentarios (<see cref="IntendedProperties"/> y <see cref="IsIntendedNewEdge"/>, cada uno con su
/// motivo). La copia va a %LOCALAPPDATA%: bajo %TEMP% el original excluye todos los ficheros (filtra por la ruta absoluta
/// y «temp» es un segmento excluido).
/// </summary>
public sealed class ParityTests(ITestOutputHelper output)
{
    private static readonly string[] RemovedEnrichers =
        ["DocDescribesEnricher", "AdrConsolidationEnricher", "ConfigBindingEnricher", "VectorizationEnricher"];

    private static readonly string[] RemovedEdgeTypes = ["DOC_DESCRIBES", "RESOLVES_ADAPTER"];

    /// <summary>
    /// Propiedades que cambian a propósito frente al original, por etiqueta de nodo. Todo lo demás tiene que coincidir.
    /// El original las calculaba con las aristas CALLS/USAGE de cbm, que resuelve por nombre; ahora salen de las
    /// referencias exactas de Roslyn (tanda 2 del enriquecimiento con Roslyn).
    /// </summary>
    private static readonly (string[] Labels, string[] Properties, string Why)[] IntendedProperties =
    [
        (["Method"], ["fan_in", "fan_out", "hotspot"],
            "fan-in/fan-out exactos: miembros distintos que referencian al método (con interfaces y overrides) y símbolos que nombra; el hotspot depende de fan_in"),
        (["Class"], ["fan_in_total"], "suma del fan_in de sus métodos"),
        (["Method", "Class"], ["is_reachable", "dead_code_candidate", "dead_code_confidence", "dead_code_reason", "dead_code_suppressed_by"],
            "alcanzabilidad por referencias exactas: ya no da por muertos los Dtos ni los endpoints que cbm no enlazó por nombre, y sí los métodos privados sin referencias y el servicio registrado que nadie consume"),
        (["DiRegistration"], ["consumer_count", "unused_registration"],
            "consumidores del servicio contados con Roslyn, sin la llamada de registro ni la lista de bases de la implementación"),
        (["Method", "Class", "Field"], ["detected_issues", "issue_rules"], "patrones de error por método (tanda 3): compilador y reglas IOperation"),
        (["Route", "Method"], ["auth_required", "auth_inferred"], "auth_required = false explícito cuando no se declara autenticación ni hay protección global (tanda 3)"),
        (["File"], ["encoding"], "codificación con la que Roslyn leyó el fichero (tanda 4)"),
        (["Method", "Class", "Field"], ["hardcoded_secrets", "reflection_targets"],
            "constantes resueltas por el compilador (tanda 5): credenciales en claro con el valor enmascarado y el nombre de tipo de Type.GetType/Activator, que además mantiene vivas a las clases que nombra"),
        (["Method", "Class", "Field", "File"], ["docstring", "docstring_source", "todo_markers", "todo_count", "commented_code", "commented_code_lines"],
            "comentarios con la trivia de Roslyn: docstring completo o comentario anterior, TODO/FIXME/HACK y código comentado (tanda 4)"),
        (["Class", "Interface", "Enum"], ["type_role", "is_public", "cs_project"],
            "papel del tipo (contrato, implementación, infraestructura, modelo), visibilidad externa y proyecto C#, para la vista de contratos del chat"),
        (["Method"], ["throws", "rethrows", "rethrow_loses_stack", "swallows_exceptions", "swallowed_catches"],
            "excepciones por método con el modelo semántico (ThrowsEnricher, paquete B1a)"),
        (["Class", "Interface"], ["afferent_coupling", "efferent_coupling", "instability", "community_id", "community_name", "in_cycle"],
            "acoplamiento y comunidades sobre las CALLS exactas (paquete B1a): la llamada por interfaz va a la interfaz y no a la implementación que cbm adivinaba por nombre"),
        (["Method"], ["sensitive_apis", "sensitive_categories", "concurrency_markers", "concurrency_kinds", "body_hash", "contract_hash"],
            "APIs sensibles, marcas de concurrencia y huellas por método (paquete B1b)"),
        (["Class", "Interface", "Enum"], ["type_hash"], "huella del tipo (paquete B1b)"),
        (["Field"], ["is_static_mutable", "is_thread_safe_type"], "estado estático mutable y tipo seguro para hilos (paquete B1b)")
    ];

    /// <summary>
    /// Procedencia uniforme (paquete B1b): toda escritura lleva <c>enricher_version</c>, y las aristas <c>origin</c>; el
    /// original no las escribía. Se admite su aparición, nunca un valor que cambie.
    /// </summary>
    private static bool IsProvenanceFill(string property, JsonNode? before) =>
        before is null && property is "enricher_version" or "origin";

    /// <summary>
    /// Constructores: el original no los parcheaba (SemanticMethodEnricher, ContractEnricher y AttributeEnricher solo
    /// miraban los métodos ordinarios) y se quedaban sin accessibility ni contrato. Se admite lo que el original no escribía
    /// en ellos (valor ausente antes), nunca un valor distinto.
    /// </summary>
    private static bool IsConstructorFill(Node node, Node other, JsonNode? before) =>
        before is null && node.Label == "Method" && (bool?)other.Properties["is_constructor"] == true;

    private static bool IsIntendedProperty(string label, string property) =>
        IntendedProperties.Any(i => i.Labels.Contains(label) && i.Properties.Contains(property));

    /// <summary>Nodos nuevos a propósito: los campos (nodos Field con la forma del analizador Java).</summary>
    private static bool IsIntendedNewNode(Node node) => node.Label == "Field";

    /// <summary>
    /// Aristas nuevas a propósito: DEFINES, READS y WRITES hacia un campo; OVERRIDES entre métodos; e IMPLEMENTS a una
    /// interfaz que llega por herencia (<c>is_inherited</c>), porque ahora salen todas las interfaces y no solo las declaradas.
    /// </summary>
    private static bool IsIntendedNewEdge(EdgeKey key, JsonObject properties) => key.Type switch
    {
        "DEFINES" or "READS" or "WRITES" => key.Target.StartsWith("Field:", StringComparison.Ordinal),
        "OVERRIDES" => true,
        "IMPLEMENTS" => properties["is_inherited"] is not null,
        "CALLS" or "THROWS" => IsExact(properties),
        _ => false
    };

    /// <summary>
    /// CALLS y THROWS exactas del compilador (CallEnricher y ThrowsEnricher, paquete B1a): sustituyen a las de cbm que salen
    /// de los ficheros C#, que resuelve por nombre. Una arista de cbm de esos tipos puede faltar después, y una con el mismo
    /// par pasa a tener las propiedades de la exacta.
    /// </summary>
    private static bool IsExact(JsonObject properties) => (bool?)properties["exact"] == true;

    private static readonly string[] ReplacedCbmEdgeTypes = ["CALLS", "THROWS"];

    /// <summary>Propias del proyecto C# y de su pertenencia: se comparan aparte.</summary>
    private static readonly string[] ProjectEdgeTypes = ["BELONGS_TO_PROJECT", "PROJECT_DEPENDS_ON"];

    [ParityFact]
    public async Task DotNetEnricher_writes_what_BdpGraphEnricher_wrote_except_the_intended_changes()
    {
        var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotNetEnricherParity", Guid.NewGuid().ToString("N")[..8]);
        var repo = Path.Combine(work, "csharp-simple");
        string? cacheDb = null;
        try
        {
            CopyRepository(ParityEnvironment.Repo!, repo);
            var (project, indexed) = await IndexAsync(repo);
            cacheDb = indexed;
            var solution = Directory.GetFiles(repo, "*.sln").Single();

            var cbmDb = Path.Combine(work, "cbm.db");
            File.Copy(indexed, cbmDb);
            var bdpDb = Path.Combine(work, "bdp.db");
            var dneDb = Path.Combine(work, "dne.db");
            File.Copy(cbmDb, bdpDb);
            File.Copy(cbmDb, dneDb);

            var bdp = await RunAsync(ParityEnvironment.BdpCli!, "enrich", "--solution", solution, "--db", bdpDb, "--project", project);
            Assert.True(bdp.ExitCode == 0, bdp.Output);
            var (cli, prefix) = ParityEnvironment.DotNetEnricher;
            // Sin restaurar: BdpGraphEnricher no restauraba y el baseline se compara sin paquetes resueltos (con ellos cambian
            // return_type_fqn y overloads, a propósito: ver RestoreTests y la verificación con csharp-simple).
            var dne = await RunAsync(cli, [.. prefix, "enrich", "--solution", solution, "--db", dneDb, "--repo-root", repo, "--verbose", "--restore", "never"]);
            output.WriteLine(dne.Output);
            Assert.True(dne.ExitCode == 0, dne.Output);

            var baseline = Graph.Load(cbmDb);
            var before = Graph.Load(bdpDb);
            var after = Graph.Load(dneDb);
            var differences = Compare(baseline, before, after, repo).ToList();
            foreach (var d in differences) output.WriteLine(d);
            Assert.Empty(differences);

            // Lo que cambia a propósito, comprobado en positivo.
            var projects = after.Nodes.Values.Where(n => n.Label == "CsProject").Select(n => n.Name).Order().ToList();
            Assert.Equal(["Orders.Api", "Orders.Core", "Orders.Tests"], projects);
            Assert.Contains(after.Edges.Keys, e => e.Type == "PROJECT_DEPENDS_ON" && e.Source.EndsWith("Orders.Api", StringComparison.Ordinal) && e.Target.EndsWith("Orders.Core", StringComparison.Ordinal));
            Assert.All(after.Nodes.Values.Where(n => n.Label == "DiRegistration"), n => Assert.False(Path.IsPathRooted(n.FilePath)));
            Assert.True(before.Edges.Count(e => e.Key.Type == "HANDLES") == after.Edges.Count(e => e.Key.Type == "HANDLES"));

            // Lo que aportan las tandas 2 a 4, comprobado en positivo sobre las semillas de csharp-simple (las que no dependen de
            // que los paquetes NuGet estén restaurados: la copia va sin bin ni obj).
            var registration = after.Nodes.Single(n => n.Value.Label == "DiRegistration" && n.Key.EndsWith("IInvoiceService->Orders.Core.Services.InvoiceService", StringComparison.Ordinal)).Value;
            Assert.True((bool)registration.Properties["unused_registration"]!);   // SEED-12a
            Assert.Equal(0, (int)registration.Properties["consumer_count"]!);
            Assert.DoesNotContain(after.Nodes.Values, n => n.Label == "DiRegistration" && n.Name.Contains("IOrderService") && n.Properties["unused_registration"] is not null);

            var lastProcessed = after.Nodes.Single(n => n.Value.Label == "Field" && n.Key.EndsWith("._lastProcessedId", StringComparison.Ordinal));   // SEED-15
            Assert.Equal("_lastProcessedId", (string?)lastProcessed.Value.Properties["field_name"]);
            Assert.DoesNotContain(after.Edges.Keys, e => e.Type == "READS" && e.Target == lastProcessed.Key);
            Assert.Contains(after.Edges.Keys, e => e.Type == "WRITES" && e.Target == lastProcessed.Key);

            Assert.Contains(after.Nodes, n => n.Key.EndsWith(".OrderService.BuildReference", StringComparison.Ordinal)
                                              && (string?)n.Value.Properties["dead_code_confidence"] == "high");   // SEED-10a: privado sin referencias
            bool Flags(string method, string rule) => after.Nodes.Single(n => n.Value.Label == "Method" && n.Key.EndsWith("." + method, StringComparison.Ordinal))
                .Value.Properties["issue_rules"]?.AsArray().Any(v => (string?)v == rule) == true;
            Assert.True(Flags("OrderService.FireAndForgetAudit", "async-void"));      // SEED-03a
            Assert.True(Flags("OrderService.ExportCsv", "blocking-async"));           // SEED-03b
            Assert.True(Flags("OrderService.NotifyCustomer", "empty-catch"));         // SEED-04
            Assert.True(Flags("OrderService.CreateOrderAsync", "null-deref"));        // SEED-05
            Assert.True(Flags("OrderStatsCache.OrderStatsCache", "captive-dependency"));   // SEED-12b
            Assert.False((bool)after.Nodes.Single(n => n.Key == "Route:__route__GET__/admin/customers").Value.Properties["auth_required"]!);   // SEED-13

            // Paquete B1a: CALLS exactas en lugar de las de cbm, constructores completos y excepciones. Las cifras quedan en
            // la salida del test (y en enrichment_runs.details) para documentar la sustitución.
            bool FromCs(EdgeKey e) => after.Nodes[e.Source].FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            var cbmCalls = baseline.Edges.Keys.Where(e => e.Type == "CALLS" && baseline.Nodes[e.Source].FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
            var exactCalls = after.Edges.Where(e => e.Key.Type == "CALLS" && IsExact(e.Value)).Select(e => e.Key).ToHashSet();
            Assert.NotEmpty(exactCalls);
            Assert.All(after.Edges.Where(e => exactCalls.Contains(e.Key)).Select(e => e.Value),
                p => Assert.True((string?)p["origin"] == "compiler" && p["line"] is not null && p["callee"] is not null));
            var withEquivalent = cbmCalls.Count(exactCalls.Contains);
            var details = JsonNode.Parse(ReadRunDetails(dneDb))!;
            var calls = details["enricher_details"]!["calls"]!;
            output.WriteLine($"CALLS exactas: {exactCalls.Count}; de cbm: {cbmCalls.Count} ({withEquivalent} con equivalente exacto, {cbmCalls.Count - withEquivalent} sin él); detalle de la pasada: {calls.ToJsonString()}");
            string Short(string key) => key[(key.IndexOf(".src.", StringComparison.Ordinal) is var i and >= 0 ? i + 5 : key.IndexOf(':') + 1)..];
            foreach (var e in cbmCalls.Where(e => !exactCalls.Contains(e)))
                output.WriteLine($"  cbm sin equivalente: {Short(e.Source)} -> {Short(e.Target)} {baseline.Edges[e]["strategy"]}");
            foreach (var e in exactCalls.Where(e => !cbmCalls.Contains(e)))
                output.WriteLine($"  exacta nueva: {Short(e.Source)} -> {Short(e.Target)} {after.Edges[e]["kinds"]?.ToJsonString()}");
            // Sin restaurar, los ficheros con símbolos sin resolver conservan las de cbm sin una exacta desde el mismo origen a un
            // método del mismo nombre; las demás de cbm se sustituyen.
            var kept = after.Edges.Where(e => e.Key.Type == "CALLS" && FromCs(e.Key) && !IsExact(e.Value)).Select(e => e.Key).ToList();
            foreach (var e in kept)
                output.WriteLine($"  cbm conservada (símbolos sin resolver): {Short(e.Source)} -> {Short(e.Target)}");
            Assert.Equal(kept.Count, (int)calls["cbm_kept_unresolved"]!);
            Assert.All(kept, e => Assert.Contains(e, cbmCalls));
            Assert.Equal(cbmCalls.Count, (int)calls["cbm_replaced"]! + kept.Count);
            Assert.Equal(withEquivalent, (int)calls["cbm_with_equivalent"]!);
            Assert.Equal(exactCalls.Count, (int)calls["exact"]!);

            Assert.All(after.Nodes.Values.Where(n => n.Label == "Method" && (bool?)n.Properties["is_constructor"] == true),
                n => Assert.NotNull(n.Properties["accessibility"]));
            Assert.Contains(after.Nodes.Values, n => n.Label == "Method" && n.Properties["throws"]?.AsArray().Count > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(() => Directory.Delete(work, recursive: true));
            if (cacheDb != null)
                foreach (var f in new[] { cacheDb, cacheDb + "-wal", cacheDb + "-shm" })
                    TryDelete(() => File.Delete(f));
        }
    }

    private static IEnumerable<string> Compare(Graph baseline, Graph before, Graph after, string repoRoot)
    {
        bool Removed(JsonObject p) => Marks(p).Any(m => RemovedEnrichers.Contains(m));
        bool Comparable(Node n) => n.Label != "CsProject" && !Removed(n.Properties);

        foreach (var (key, node) in before.Nodes.Where(n => Comparable(n.Value)))
        {
            if (!after.Nodes.TryGetValue(key, out var other)) { yield return $"nodo que falta: {key}"; continue; }

            if (node.FilePath != other.FilePath
                && !(Path.IsPathRooted(node.FilePath) && !Path.IsPathRooted(other.FilePath)
                     && string.Equals(Path.GetFullPath(node.FilePath), Path.GetFullPath(Path.Combine(repoRoot, other.FilePath)), StringComparison.OrdinalIgnoreCase)))
                yield return $"file_path de {key}: '{node.FilePath}' → '{other.FilePath}'";

            foreach (var property in node.Properties.Select(p => p.Key).Union(other.Properties.Select(p => p.Key)))
            {
                if (property is "enriched_at" or "doc_coverage" or "documented_defs" or "total_defs") continue;
                var a = node.Properties[property];
                var b = other.Properties[property];
                if (JsonNode.DeepEquals(a, b)) continue;
                if (IsIntendedProperty(node.Label, property)) continue;
                if (IsConstructorFill(node, other, a)) continue;
                if (property == "enricher_version" && a is null) continue;
                if (property == "enriched_by" && Marks(node.Properties).Where(m => !RemovedEnrichers.Contains(m)).All(Marks(other.Properties).Contains))
                    continue; // ahora se suman: las del original tienen que estar todas
                if (property == "origin" && (string?)a == "enricher" && b is null && baseline.Nodes.ContainsKey(key))
                    continue; // un parche ya no marca como creado por el enriquecedor un nodo de cbm
                yield return $"propiedad {property} de {key}: {a?.ToJsonString() ?? "∅"} → {b?.ToJsonString() ?? "∅"}";
            }
        }
        foreach (var key in after.Nodes.Where(n => Comparable(n.Value) && !IsIntendedNewNode(n.Value)).Select(n => n.Key).Except(before.Nodes.Keys))
            yield return $"nodo nuevo: {key}";

        bool ComparableEdge(EdgeKey key, JsonObject p) =>
            !ProjectEdgeTypes.Contains(key.Type) && !RemovedEdgeTypes.Contains(key.Type) && !Removed(p);
        foreach (var (key, properties) in before.Edges.Where(e => ComparableEdge(e.Key, e.Value)))
        {
            if (!after.Edges.TryGetValue(key, out var other))
            {
                if (!ReplacedCbmEdgeTypes.Contains(key.Type)) yield return $"arista que falta: {key}";
                continue;
            }
            if (ReplacedCbmEdgeTypes.Contains(key.Type) && IsExact(other)) continue;
            foreach (var property in properties.Select(p => p.Key).Union(other.Select(p => p.Key)).Where(p => p != "enriched_at"))
                if (!JsonNode.DeepEquals(properties[property], other[property]) && !IsProvenanceFill(property, properties[property]))
                    yield return $"propiedad {property} de la arista {key}: {properties[property]?.ToJsonString()} → {other[property]?.ToJsonString()}";
        }
        foreach (var key in after.Edges.Where(e => ComparableEdge(e.Key, e.Value) && !IsIntendedNewEdge(e.Key, e.Value)).Select(e => e.Key).Except(before.Edges.Keys))
            yield return $"arista nueva: {key}";
    }

    private static IEnumerable<string> Marks(JsonObject properties) => properties["enriched_by"] switch
    {
        JsonArray array => array.Select(v => (string?)v).OfType<string>(),
        JsonValue value when value.TryGetValue<string>(out var single) => [single],
        _ => []
    };

    private sealed record Node(string Label, string Name, string FilePath, JsonObject Properties);

    private sealed record EdgeKey(string Source, string Target, string Type);

    private sealed record Graph(Dictionary<string, Node> Nodes, Dictionary<EdgeKey, JsonObject> Edges)
    {
        public static Graph Load(string path)
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            var byId = new Dictionary<long, string>();
            var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, label, name, qualified_name, file_path, properties FROM nodes";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = $"{r.GetString(1)}:{r.GetString(3)}";
                    byId[r.GetInt64(0)] = key;
                    nodes[key] = new Node(r.GetString(1), r.GetString(2), r.IsDBNull(4) ? "" : r.GetString(4), Parse(r.IsDBNull(5) ? null : r.GetString(5)));
                }
            }
            var edges = new Dictionary<EdgeKey, JsonObject>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT source_id, target_id, type, properties FROM edges";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    edges[new EdgeKey(byId[r.GetInt64(0)], byId[r.GetInt64(1)], r.GetString(2))] = Parse(r.IsDBNull(3) ? null : r.GetString(3));
            }
            return new Graph(nodes, edges);
        }

        private static JsonObject Parse(string? json) =>
            string.IsNullOrWhiteSpace(json) ? [] : JsonNode.Parse(json) as JsonObject ?? [];
    }

    private static string ReadRunDetails(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT details FROM enrichment_runs ORDER BY id DESC LIMIT 1";
        return (string)cmd.ExecuteScalar()!;
    }

    private static void CopyRepository(string source, string target)
    {
        string[] skipped = [".codebase", "reports", "logs", "bin", "obj", ".git", ".vs"];
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            if (skipped.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)) continue;
            CopyRepository(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }

    /// <summary>Indexa con cbm como <c>DetectAndIndexGraphStep</c> y devuelve el proyecto y la BD de su caché.</summary>
    private static async Task<(string Project, string Database)> IndexAsync(string repo)
    {
        var run = await RunAsync(ParityEnvironment.Cbm!, "cli", "index_repository",
            $"{{\"repo_path\": \"{Path.GetFullPath(repo).Replace('\\', '/')}\"}}");
        Assert.True(run.ExitCode == 0, run.Output);
        var line = run.Output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('{') && l.Contains("\"project\"", StringComparison.Ordinal));
        var project = (string)JsonNode.Parse(line)!["project"]!;
        var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codebase-memory-mcp", project + ".db");
        Assert.True(File.Exists(db), db);
        return (project, db);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, params string[] arguments)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
