using System.Text.Json;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// Lo que <see cref="JavaGraphWriter"/> hace con un resultado de schema 3 (tandas 2 a 4 de la auditoría del 2026-09-30):
/// <c>OVERRIDES</c>, <c>IMPLEMENTS</c> heredados, <c>fan_in</c>/<c>fan_out</c>/<c>hotspot</c>, <c>detected_issues</c>, comentarios,
/// <c>encoding</c> y la sustitución de los <c>docstring</c> de cbm. Sin JDK: el resultado del analizador va escrito a mano.
/// </summary>
public class JavaGraphWriterSchema3Tests
{
    private const string Project = "P";
    private const string I = "src/p/I.java", A = "src/p/A.java", B = "src/p/B.java", J = "src/p/J.java", C = "src/p/C.java", Z = "src/p/Z.java";

    private static readonly string[] Files = [I, A, B, J, C, Z];

    private static readonly DateTimeOffset StartedAt = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static string Qn(string path, string rest) => $"{Project}.{path[..path.LastIndexOf('.')].Replace('/', '.')}.{rest}";

    private static T[] Arr<T>(params T[] items) => items;

    private static string Line(object record) => JsonSerializer.Serialize(record);

    private static object File(string path, string encoding) => new { k = "file", path, encoding, errors = 0 };

    private static object Type(string path, string name, string kind, object? super = null, object[]? interfaces = null, string? outer = null,
        string? doc = null, string? docSource = null) => new
    {
        k = "type", path, chain = outer == null ? new[] { name } : new[] { outer[(outer.LastIndexOf('.') + 1)..], name },
        fqn = outer == null ? $"p.{name}" : $"{outer}.{name}", kind, line = 3, end = 40, access = "public", @abstract = kind == "interface",
        annotations = Array.Empty<string>(), doc, docSource, outer, super, interfaces = interfaces ?? []
    };

    private static object Ref(string fqn) => new { fqn, origin = "project" };

    private static object Method(string path, string owner, string name, int line, string[]? overrides = null, int cyclomatic = 1, string? doc = null,
        string? docSource = null, bool isOverride = false) => new
    {
        k = "method", path, chain = new[] { owner }, ownerFqn = $"p.{owner}", name, ctor = false, line, end = line + 2,
        key = $"p.{owner}#{name}()", @params = Array.Empty<string>(), paramSimple = Array.Empty<string>(), paramNames = Array.Empty<string>(),
        ret = "void", retSimple = "void", access = "public", @static = false, @abstract = false, annotations = Array.Empty<string>(), doc, docSource,
        cyclomatic, isOverride = isOverride || overrides is { Length: > 0 }, overrides = overrides ?? []
    };

    private static object Call(string path, int line, string from, string fromType, string to) => new
    {
        k = "call", path, line, from, fromType, to, origin = "project", kind = "call", reason = (string?)null
    };

    private static object Field(string path, string owner, string name, int line) => new
    {
        k = "field", path, chain = new[] { owner }, ownerFqn = $"p.{owner}", name, line, end = line, typeFqn = "int", access = "private",
        @static = false, final = false, enumConstant = false, doc = (string?)null, docSource = (string?)null
    };

    private static object Issue(string path, string? from, string? fromField, string fromType, string rule, string text, int line) => new
    {
        k = "issue", path, from, fromField, fromType, rule, text, line
    };

    private static object TypeRef(string path, string from, string fromType, string to, int line) => new
    {
        k = "ref", path, from, fromType, fromField = (string?)null, to, kind = "type", access = (string?)null, contexts = new[] { "local" },
        origin = "project", count = 1, lines = new[] { line }
    };

    private static object FieldRef(string path, string from, string fromType, string to, string access, int line) => new
    {
        k = "ref", path, from, fromType, fromField = (string?)null, to, kind = "field", access, contexts = Array.Empty<string>(),
        origin = "project", count = 1, lines = new[] { line }
    };

    private static object Comment(string path, string owner, string? key, object[]? todos = null, object[]? code = null) => new
    {
        k = "comment", path, owner, key, todos = todos ?? [], code = code ?? []
    };

    private static object Constant(string path, string? from, string? fromField, string fromType, string what, string kind, string? name, int line, string value) => new
    {
        k = "const", path, from, fromField, fromType, what, kind, name, line, value
    };

    private static object Todo(string kind, int line, string text) => new { kind, line, text };

    private static object Code(int start, int end, string preview) => new { start_line = start, end_line = end, lines = end - start + 1, preview };

    private static object Summary(int schema = 3) => new
    {
        k = "summary", schema, analyzerVersion = schema.ToString(), javaVersion = "17.0.20.1", files = Files.Length, parseMs = 1, analyzeMs = 1, scanMs = 1,
        heapPeakMb = 1, batches = 1, wallMs = 1, calls = new Dictionary<string, int> { ["project"] = 4, ["jdk"] = 0, ["jar"] = 0, ["unresolved"] = 0 },
        unresolvedByReason = new Dictionary<string, int>(), missingPackages = Array.Empty<string>(),
        diagnostics = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 0 }
    };

    /// <summary>
    /// I, J (extiende I), A (implementa I), B (extiende A), C (implementa J), Z (llama) y un solo problema por regla. Las líneas
    /// de los métodos son distintas en cada fichero para que los parches no se confundan.
    /// </summary>
    private static List<string> ResultLines(int schema = 3)
    {
        var lines = new List<object>
        {
            File(I, "UTF-8"), File(A, "ISO-8859-1"), File(B, "UTF-8"), File(J, "UTF-8"), File(C, "windows-1252"), File(Z, "UTF-8"),
            Type(I, "I", "interface"),
            Method(I, "I", "run", 4, doc: "Contrato de run.", docSource: "comment"),
            Type(J, "J", "interface", interfaces: [Ref("p.I")]),
            Type(A, "A", "class", interfaces: [Ref("p.I")], doc: "Clase A con texto decodificado: áéí.", docSource: "comment"),
            Type(B, "B", "class", super: Ref("p.A")),
            Type(C, "C", "class", interfaces: [Ref("p.J")]),
            Type(B, "Inner", "class", outer: "p.B"),
            Type(Z, "Z", "class"),
            Method(A, "A", "run", 10, overrides: ["p.I#run()"], cyclomatic: 4, doc: "Ejecuta A.", docSource: "comment"),
            Method(A, "A", "helper", 20),
            Method(A, "A", "plain", 30),
            Field(A, "A", "count", 6),
            Method(B, "B", "run", 10, overrides: ["p.A#run()"], cyclomatic: 2),
            Method(B, "B", "other", 20),
            Method(B, "B", "b", 30),
            Method(C, "C", "run", 10, overrides: ["p.I#run()"]),
            Method(Z, "Z", "z", 5),
            // A.run llama a A.helper y usa I y el campo count; Z.z llama a A.run; B.other llama a I.run; B.b llama a B.run.
            Call(A, 11, "p.A#run()", "p.A", "p.A#helper()"),
            Call(Z, 6, "p.Z#z()", "p.Z", "p.A#run()"),
            Call(B, 21, "p.B#other()", "p.B", "p.I#run()"),
            Call(B, 31, "p.B#b()", "p.B", "p.B#run()"),
            TypeRef(A, "p.A#run()", "p.A", "p.I", 12),
            FieldRef(A, "p.A#run()", "p.A", "p.A#count", "readwrite", 13),
            // Hallazgos: dos en un método (se ordenan por línea y luego por regla), uno en un campo, uno en un tipo y uno sin nodo.
            Issue(A, "p.A#run()", null, "p.A", "string-identity", "comparación de String con '=='", 15),
            Issue(A, "p.A#run()", null, "p.A", "empty-catch", "catch (Exception) vacío", 11),
            Issue(A, "p.A#run()", null, "p.A", "empty-catch", "catch (Exception) vacío", 11),
            Issue(A, null, "p.A#count", "p.A", "obsolete-api", "'Date.getYear()' está marcado como obsoleto", 6),
            Issue(A, null, null, "p.A", "undisposed-resource", "'in' (InputStream) se abre", 8),
            Issue(A, "p.A#noSuchMethod()", null, "p.A", "empty-catch", "catch (Exception) vacío", 9),
            // Comentarios.
            Comment(A, "method", "p.A#run()", [Todo("TODO", 12, "revisar"), Todo("HACK", 11, "temporal")], [Code(14, 16, "int x = 1;")]),
            Comment(A, "type", "p.A", [Todo("FIXME", 4, "el año bisiesto")]),
            Comment(A, "file", null, code: [Code(1, 2, "foo();")]),
            Comment(A, "method", "p.A#noSuchMethod()", [Todo("TODO", 9, "sin nodo")]),
            // Constantes resueltas: credenciales (una con nombre de referencia, que se descarta) y nombres de clase de la reflexión.
            Constant(A, "p.A#run()", null, "p.A", "secret", "identifier", "password", 17, "Or*** (11 caracteres)"),
            Constant(A, "p.A#run()", null, "p.A", "secret", "call-argument", "getConnection", 19, "ar*** (10 caracteres)"),
            Constant(A, "p.A#run()", null, "p.A", "secret", "call-argument", "getConnection", 19, "ar*** (10 caracteres)"),
            Constant(A, "p.A#run()", null, "p.A", "secret", "identifier", "userSecretsId", 18, "6c*** (36 caracteres)"),
            Constant(A, null, "p.A#count", "p.A", "secret", "connection-string", "count", 6, "Su*** (11 caracteres)"),
            Constant(A, "p.A#noSuchMethod()", null, "p.A", "secret", "identifier", "password", 9, "zz*** (9 caracteres)"),
            Constant(B, "p.B#other()", null, "p.B", "reflection", "Class.forName", null, 22, "p.export.*Impl"),
            Constant(B, "p.B#other()", null, "p.B", "reflection", "loadClass", null, 23, "p.Fixed"),
            Constant(B, "p.B#other()", null, "p.B", "reflection", "Class.forName", null, 24, "p.export.*Impl"),
            Constant(B, "p.B#noSuchMethod()", null, "p.B", "reflection", "Class.forName", null, 25, "p.Lost"),
            Summary(schema)
        };
        return [.. lines.Select(Line)];
    }

    private static async Task<CbmTestGraph> CreateGraphAsync(string db, bool withStaleAndDocs = true)
    {
        var g = await CbmTestGraph.CreateAsync(db, Project);
        foreach (var path in Files)
        {
            await g.NodeAsync("File", Path.GetFileName(path), Qn(path, "__file__"), path, 0, 0, "{\"extension\":\".java\"}");
            await g.NodeAsync("Module", path, Qn(path, "").TrimEnd('.'), path, 1, 40);
        }
        foreach (var (path, name, label) in new[]
                 {
                     (I, "I", "Interface"), (J, "J", "Interface"), (A, "A", "Class"), (B, "B", "Class"), (C, "C", "Class"), (Z, "Z", "Class")
                 })
            await g.NodeAsync(label, name, Qn(path, name), path, 3, 40);

        async Task<long> MethodAsync(string path, string owner, string name, int line, string properties = "{}")
        {
            var id = await g.NodeAsync("Method", name, Qn(path, $"{owner}.{name}"), path, line, line + 2, properties);
            return id;
        }

        // cbm guarda el método de la interfaz dos veces, con el mismo docstring mal leído: un Method y un Function gemelo.
        await MethodAsync(I, "I", "run", 4, withStaleAndDocs ? "{\"docstring\":\"Contrato de \\ufffd run\"}" : "{}");
        await g.NodeAsync("Function", "run", Qn(I, "run"), I, 4, 6, withStaleAndDocs ? "{\"docstring\":\"Contrato de \\ufffd run\"}" : "{}");

        // cbm lee los comentarios // con la codificación equivocada: el docstring trae U+FFFD.
        await MethodAsync(A, "A", "run", 10, withStaleAndDocs ? "{\"docstring\":\"Ejecuta \\ufffdA\"}" : "{}");
        await MethodAsync(A, "A", "helper", 20, withStaleAndDocs ? "{\"docstring\":\"Un comentario \\ufffd sin registro\"}" : "{}");
        await MethodAsync(A, "A", "plain", 30, withStaleAndDocs
            ? "{\"detected_issues\":[\"empty-catch: de una pasada anterior (línea 1)\"],\"issue_rules\":[\"empty-catch\"],\"fan_in\":99,"
                + "\"hardcoded_secrets\":[{\"kind\":\"identifier\",\"name\":\"x\",\"line\":1,\"preview_masked\":\"zz*** (3 caracteres)\"}],\"reflection_targets\":[\"old.Target\"]}"
            : "{}");
        return g;
    }

    private static async Task<(JavaGraphWriteResult Result, CbmTestGraph Graph)> ApplyAsync(TempDirectory temp, int schema = 3, bool withStaleAndDocs = true,
        string[]? lines = null)
    {
        var db = Path.Combine(temp.Path, "codebase.db");
        var graph = await CreateGraphAsync(db, withStaleAndDocs);
        var path = Path.Combine(temp.Path, "java-analysis.jsonl");
        await System.IO.File.WriteAllLinesAsync(path, lines ?? [.. ResultLines(schema)], new System.Text.UTF8Encoding(false));
        var result = await JavaGraphWriter.ApplyAsync(new JavaGraphWriteRequest { DatabasePath = db, AnalyzedFiles = Files, ConfigFingerprint = "cfg", StartedAt = StartedAt },
            JavaAnalysisReader.Read(path), CancellationToken.None);
        return (result, graph);
    }

    private static async Task<JsonObject> PropsAsync(CbmTestGraph graph, string qn) =>
        JsonNode.Parse((await graph.TextAsync("SELECT properties FROM nodes WHERE qualified_name = @qn", ("@qn", qn)))!)!.AsObject();

    private static string MethodQn(string path, string owner, string name) => Qn(path, $"{owner}.{name}");

    private static Task<List<(string Source, string Target, JsonObject Properties)>> EdgesAsync(CbmTestGraph graph, string type) =>
        graph.ListAsync("""
            SELECT s.qualified_name, t.qualified_name, e.properties FROM edges e
            JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = @type ORDER BY s.qualified_name, t.qualified_name
            """, r => (r.GetString(0), r.GetString(1), JsonNode.Parse(r.GetString(2))!.AsObject()), ("@type", type));

    [Fact]
    public async Task Overrides_become_method_to_method_edges_marked_as_presentational()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var edges = await EdgesAsync(graph, "OVERRIDES");
        Assert.Equal(Arr(
            (MethodQn(A, "A", "run"), MethodQn(I, "I", "run")),
            (MethodQn(B, "B", "run"), MethodQn(A, "A", "run")),
            (MethodQn(C, "C", "run"), MethodQn(I, "I", "run"))
        ), edges.Select(e => (e.Source, e.Target)));
        Assert.All(edges, e =>
        {
            Assert.True(e.Properties["presentational"]!.GetValue<bool>());
            Assert.Equal("[\"JavaAnalyzer\"]", e.Properties["enriched_by"]!.ToJsonString());
        });
        Assert.Equal(3, result.Stats.OverridesEdges);
        Assert.Equal(3, result.Outcome.Details is { } d ? JsonNode.Parse(d)!["edges"]!["OVERRIDES"]!.GetValue<int>() : -1);

        Assert.True((await PropsAsync(graph, MethodQn(A, "A", "run")))["is_override"]!.GetValue<bool>());
        Assert.False((await PropsAsync(graph, MethodQn(A, "A", "plain")))["is_override"]!.GetValue<bool>());
        Assert.Equal("p.A", (await PropsAsync(graph, MethodQn(A, "A", "run")))["owner_fqn"]!.GetValue<string>());
        Assert.Equal("p.B", (await PropsAsync(graph, Qn(B, "B.Inner")))["outer_fqn"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_type_node_gets_its_role_and_visibility()
    {
        using var temp = new TempDirectory();
        var (_, graph) = await ApplyAsync(temp);
        using var _g = graph;

        foreach (var (path, name, role) in new[] { (I, "I", "contract"), (J, "J", "contract"), (A, "A", "implementation"), (C, "C", "implementation"), (Z, "Z", "implementation") })
        {
            var props = await PropsAsync(graph, Qn(path, name));
            Assert.Equal(role, props["type_role"]!.GetValue<string>());
            Assert.True(props["is_public"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task Interfaces_received_from_the_superclass_or_a_super_interface_are_implemented_with_is_inherited()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var implements = await EdgesAsync(graph, "IMPLEMENTS");
        Assert.Equal(Arr(
            (Qn(A, "A"), Qn(I, "I"), false),
            (Qn(B, "B"), Qn(I, "I"), true),
            (Qn(C, "C"), Qn(J, "J"), false),
            (Qn(C, "C"), Qn(I, "I"), true)
        ).OrderBy(e => e.Item1, StringComparer.Ordinal).ThenBy(e => e.Item2, StringComparer.Ordinal),
            implements.Select(e => (e.Source, e.Target, e.Properties.ContainsKey("is_inherited"))));
        Assert.All(implements.Where(e => e.Properties.ContainsKey("is_inherited")), e => Assert.True(e.Properties["is_inherited"]!.GetValue<bool>()));
        Assert.Equal(2, result.Stats.InheritedImplementsEdges);
        Assert.Equal(2, result.Stats.ImplementsEdges);
    }

    [Fact]
    public async Task Fan_in_counts_distinct_callers_of_the_method_or_of_what_it_overrides_and_hotspot_follows()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        // A.run: lo llama Z.z, y B.other llama a I.run, que A.run implementa: 2. Lo que A.run nombra: helper, I y count: 3.
        var run = await PropsAsync(graph, MethodQn(A, "A", "run"));
        Assert.Equal((2, 3), (run["fan_in"]!.GetValue<int>(), run["fan_out"]!.GetValue<int>()));
        Assert.Equal(Math.Round(4 * Math.Log(1 + 2), 2), run["hotspot"]!.GetValue<double>());

        // B.run: lo llama B.b y, por A.run e I.run, también Z.z y B.other: 3; sin contarse a sí mismo.
        var bRun = await PropsAsync(graph, MethodQn(B, "B", "run"));
        Assert.Equal((3, 0), (bRun["fan_in"]!.GetValue<int>(), bRun["fan_out"]!.GetValue<int>()));
        Assert.Equal(Math.Round(2 * Math.Log(1 + 3), 2), bRun["hotspot"]!.GetValue<double>());

        // Sin llamadas: 0, y un fan_in que dejó otra pasada no sobrevive.
        var plain = await PropsAsync(graph, MethodQn(A, "A", "plain"));
        Assert.Equal((0, 0, 0.0), (plain["fan_in"]!.GetValue<int>(), plain["fan_out"]!.GetValue<int>(), plain["hotspot"]!.GetValue<double>()));
        Assert.Equal(9, result.Stats.MethodsWithMetrics);
    }

    [Fact]
    public async Task Detected_issues_are_sorted_texts_with_the_line_and_issue_rules_the_distinct_rules()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var run = await PropsAsync(graph, MethodQn(A, "A", "run"));
        Assert.Equal(Arr(
            "empty-catch: catch (Exception) vacío (línea 11)",
            "string-identity: comparación de String con '==' (línea 15)"
        ), run["detected_issues"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(Arr("empty-catch", "string-identity"), run["issue_rules"]!.AsArray().Select(n => n!.GetValue<string>()));

        // El campo y el tipo reciben los suyos; el método que el analizador no emitió queda contado.
        var count = await PropsAsync(graph, Qn(A, "A.count"));
        Assert.Equal(Arr("obsolete-api: 'Date.getYear()' está marcado como obsoleto (línea 6)"), count["detected_issues"]!.AsArray().Select(n => n!.GetValue<string>()));
        var type = await PropsAsync(graph, Qn(A, "A"));
        Assert.Equal(Arr("undisposed-resource"), type["issue_rules"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(1, result.Stats.IssuesUnmatched);
        Assert.Equal(3, result.Stats.NodesWithIssues);
        Assert.Equal(new Dictionary<string, long> { ["empty-catch"] = 1, ["obsolete-api"] = 1, ["string-identity"] = 1, ["undisposed-resource"] = 1 },
            result.Stats.IssuesByRule);

        // Lo que escribió una pasada anterior y esta ya no encuentra se retira.
        var plain = await PropsAsync(graph, MethodQn(A, "A", "plain"));
        Assert.False(plain.ContainsKey("detected_issues"));
        Assert.False(plain.ContainsKey("issue_rules"));
    }

    [Fact]
    public async Task Comments_go_to_the_owner_the_analyzer_names_and_an_unknown_one_falls_back_to_the_file_node()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var run = await PropsAsync(graph, MethodQn(A, "A", "run"));
        // Ordenadas por línea: HACK (11) antes que TODO (12).
        Assert.Equal(Arr("HACK", "TODO"), run["todo_markers"]!.AsArray().Select(n => n!["kind"]!.GetValue<string>()));
        Assert.Equal(11, run["todo_markers"]![0]!["line"]!.GetValue<int>());
        Assert.Equal("temporal", run["todo_markers"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(2, run["todo_count"]!.GetValue<int>());
        Assert.Equal("[{\"start_line\":14,\"end_line\":16,\"lines\":3,\"preview\":\"int x = 1;\"}]", run["commented_code"]!.ToJsonString());
        Assert.Equal(3, run["commented_code_lines"]!.GetValue<int>());

        var type = await PropsAsync(graph, Qn(A, "A"));
        Assert.Equal("el año bisiesto", type["todo_markers"]![0]!["text"]!.GetValue<string>());
        Assert.False(type.ContainsKey("commented_code"));

        // El comentario del fichero y el del método sin nodo acaban en el nodo File de A.java.
        var file = await PropsAsync(graph, Qn(A, "__file__"));
        Assert.Equal(1, file["todo_count"]!.GetValue<int>());
        Assert.Equal("sin nodo", file["todo_markers"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(2, file["commented_code_lines"]!.GetValue<int>());
        Assert.Equal(4, result.Stats.TodoMarkers);
        Assert.Equal(2, result.Stats.CommentedCodeBlocks);
    }

    [Fact]
    public async Task Resolved_constants_are_written_as_masked_hardcoded_secrets_and_sorted_reflection_targets_on_their_owner()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        // Sin duplicados, ordenadas por línea; la de nombre de referencia (userSecretsId) no se escribe; la huérfana, tampoco.
        var run = await PropsAsync(graph, MethodQn(A, "A", "run"));
        Assert.Equal("""[{"kind":"identifier","name":"password","line":17,"preview_masked":"Or*** (11 caracteres)"},{"kind":"call-argument","name":"getConnection","line":19,"preview_masked":"ar*** (10 caracteres)"}]""",
            run["hardcoded_secrets"]!.ToJsonString());
        var field = await PropsAsync(graph, Qn(A, "A.count"));
        Assert.Equal("connection-string", field["hardcoded_secrets"]![0]!["kind"]!.GetValue<string>());

        var other = await PropsAsync(graph, MethodQn(B, "B", "other"));
        Assert.Equal(Arr("p.Fixed", "p.export.*Impl"), other["reflection_targets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(other.ContainsKey("hardcoded_secrets"));

        Assert.Equal((3, 2, 2), (result.Stats.HardcodedSecrets, result.Stats.NodesWithSecrets, result.Stats.ReflectionTargets));
        // El secreto completo no está en ninguna propiedad del grafo.
        var all = string.Join('\n', await graph.ListAsync("SELECT properties FROM nodes", r => r.GetString(0)));
        Assert.DoesNotContain("userSecretsId", all);

        // Lo que escribió una pasada anterior y esta ya no encuentra se retira.
        var plain = await PropsAsync(graph, MethodQn(A, "A", "plain"));
        Assert.False(plain.ContainsKey("hardcoded_secrets"));
        Assert.False(plain.ContainsKey("reflection_targets"));
    }

    [Fact]
    public async Task The_file_node_gets_the_encoding_the_file_was_decoded_with_in_the_format_csharp_writes()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        Assert.Equal("utf-8", (await PropsAsync(graph, Qn(I, "__file__")))["encoding"]!.GetValue<string>());
        Assert.Equal("iso-8859-1", (await PropsAsync(graph, Qn(A, "__file__")))["encoding"]!.GetValue<string>());
        Assert.Equal("windows-1252", (await PropsAsync(graph, Qn(C, "__file__")))["encoding"]!.GetValue<string>());
        Assert.Equal(6, result.Stats.FilesWithEncoding);
    }

    [Fact]
    public async Task The_docstring_of_the_analyzer_replaces_the_one_from_cbm_even_when_it_is_empty()
    {
        using var temp = new TempDirectory();
        var (_, graph) = await ApplyAsync(temp);
        using var _g = graph;

        // Con texto: el de cbm, con U+FFFD, se sustituye y queda su origen.
        var run = await PropsAsync(graph, MethodQn(A, "A", "run"));
        Assert.Equal("Ejecuta A.", run["docstring"]!.GetValue<string>());
        Assert.Equal("comment", run["docstring_source"]!.GetValue<string>());
        // Sin texto (el comentario era código o una marca): el de cbm sale, no se queda con los U+FFFD.
        var helper = await PropsAsync(graph, MethodQn(A, "A", "helper"));
        Assert.False(helper.ContainsKey("docstring"));
        Assert.False(helper.ContainsKey("docstring_source"));
        foreach (var text in await graph.ListAsync("SELECT properties FROM nodes", r => r.GetString(0)))
        {
            Assert.DoesNotContain("\\ufffd", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(char.ConvertFromUtf32(0xFFFD), text);
        }

        var type = await PropsAsync(graph, Qn(A, "A"));
        Assert.Equal("Clase A con texto decodificado: áéí.", type["docstring"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_function_twin_of_an_interface_method_gets_the_same_docstring_without_counting_as_an_updated_node()
    {
        using var temp = new TempDirectory();
        var (result, graph) = await ApplyAsync(temp);
        using var _g = graph;

        var method = await PropsAsync(graph, MethodQn(I, "I", "run"));
        var twin = await PropsAsync(graph, Qn(I, "run"));
        Assert.Equal("Contrato de run.", method["docstring"]!.GetValue<string>());
        Assert.Equal("Contrato de run.", twin["docstring"]!.GetValue<string>());
        Assert.Equal("comment", twin["docstring_source"]!.GetValue<string>());
        // Solo el docstring: el gemelo no recibe la firma ni las propiedades del método.
        Assert.False(twin.ContainsKey("param_types"));
        Assert.False(twin.ContainsKey("owner_fqn"));
        Assert.Equal(1, result.Stats.TwinDocstrings);
    }

    [Fact]
    public async Task A_second_pass_leaves_the_same_graph_and_the_same_counts()
    {
        using var temp = new TempDirectory();
        var (first, graph) = await ApplyAsync(temp);
        using var _g = graph;

        async Task<string> SnapshotAsync()
        {
            // Las propiedades se comparan sin el orden de sus claves: una retirada y reescrita cambia de sitio, no de valor.
            static string Canonical(string json) => JsonNode.Parse(json) is JsonObject o
                ? string.Join(',', o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value?.ToJsonString()}"))
                : json;
            var nodes = await graph.ListAsync("SELECT qualified_name, properties FROM nodes ORDER BY qualified_name", r => $"{r.GetString(0)}={Canonical(r.GetString(1))}");
            var edges = await graph.ListAsync("""
                SELECT s.qualified_name || '>' || t.qualified_name || ':' || e.type, e.properties FROM edges e
                JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id ORDER BY 1
                """, r => r.GetString(0) + Canonical(r.GetString(1)));
            return string.Join('\n', nodes) + "\n--\n" + string.Join('\n', edges);
        }

        var before = await SnapshotAsync();
        var path = Path.Combine(temp.Path, "java-analysis.jsonl");
        var second = await JavaGraphWriter.ApplyAsync(new JavaGraphWriteRequest { DatabasePath = graph.DatabasePath, AnalyzedFiles = Files, ConfigFingerprint = "cfg", StartedAt = StartedAt },
            JavaAnalysisReader.Read(path), CancellationToken.None);

        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(first.Stats.OverridesEdges, second.Stats.OverridesEdges);
        Assert.Equal(first.Stats.MethodsWithMetrics, second.Stats.MethodsWithMetrics);
        Assert.Equal(first.Stats.TodoMarkers, second.Stats.TodoMarkers);
    }

    [Fact]
    public async Task A_schema_2_result_writes_no_issues_comments_or_schema_3_properties_but_still_the_metrics()
    {
        using var temp = new TempDirectory();
        var lines = ResultLines(schema: 2).Where(l => !l.Contains("\"k\":\"issue\"") && !l.Contains("\"k\":\"comment\"")).ToArray();
        var (_, graph) = await ApplyAsync(temp, schema: 2, lines: lines);
        using var _g = graph;

        var run = await PropsAsync(graph, MethodQn(A, "A", "run"));
        Assert.False(run.ContainsKey("is_override"));
        Assert.False(run.ContainsKey("owner_fqn"));
        Assert.False(run.ContainsKey("docstring_source"));
        Assert.False(run.ContainsKey("detected_issues"));
        Assert.False((await PropsAsync(graph, Qn(A, "__file__"))).ContainsKey("encoding"));
        Assert.True(run.ContainsKey("fan_in"));
    }
}
