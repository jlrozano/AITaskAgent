using System.Text;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// Un repositorio Java pequeño con un caso positivo y otro negativo por cada regla de <c>detected_issues</c>, la jerarquía para
/// <c>OVERRIDES</c>, las referencias <c>X::m</c> y los comentarios (un fuente en ISO-8859-1 con tildes), analizado una sola vez
/// con el JDK de las pruebas. Las aserciones leen el <c>java-analysis.jsonl</c> tal como lo escribe <c>JavaAnalyzer.java</c>.
/// </summary>
public sealed class JavaRulesFixture : IAsyncLifetime
{
    public const string HolderPath = "src/t/Holder.java";
    public const string OtherPath = "src/t/Other.java";
    public const string ShapesPath = "src/t/Shapes.java";
    public const string NotesPath = "src/t/Notes.java";
    public const string ManyPath = "src/t/Many.java";

    /// <summary>Un fuente con muchísimos comentarios con «;» que son prosa pero parecen asignaciones (más de los 100 errores que javac deja de contar) y uno de código.</summary>
    private static string ManySource()
    {
        var text = new StringBuilder("package t;\n\npublic class Many {\n");
        for (var i = 0; i < 250; i++)
            text.Append($"    // resultado{i} = suma de los valores; luego se ajusta el total\n    void m{i}() {{}}\n\n");
        text.Append("    void last() {\n        // finish(3);\n    }\n\n    void finish(int x) {}\n}\n");
        return text.ToString();
    }

    private TempDirectory? _temp;

    public IReadOnlyList<JavaAnalysisRecord> Records { get; private set; } = [];

    public string Source(string path) => Sources[path].Replace("\r\n", "\n");

    /// <summary>Línea (desde 1) de la primera aparición del fragmento en el fuente.</summary>
    public int LineOf(string path, string fragment)
    {
        var lines = Source(path).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(fragment, StringComparison.Ordinal))
                return i + 1;
        }
        throw new ArgumentException($"'{fragment}' no está en {path}.");
    }

    public IEnumerable<JavaIssueRecord> Issues(string method) =>
        Records.OfType<JavaIssueRecord>().Where(i => i.From != null && i.From.StartsWith($"t.Holder#{method}(", StringComparison.Ordinal));

    public static IReadOnlyDictionary<string, string> Sources { get; } = new Dictionary<string, string>
    {
        [HolderPath] = """
            package t;

            import java.io.File;
            import java.io.FileInputStream;
            import java.io.IOException;
            import java.io.InputStream;
            import java.util.ArrayList;
            import java.util.Date;
            import java.util.HashMap;
            import java.util.List;
            import java.util.Map;
            import java.util.function.Supplier;

            public class Holder {
                private InputStream kept;

                void work() throws Exception {}
                void io() throws IOException {}
                void unchecked() {}
                void log(Exception e) {}
                static String helper(String s) { return s; }
                void closeQuietly(InputStream s) {}
                InputStream getStream() { return null; }

                void emptyCatch() { try { work(); } catch (Exception e) { } }
                void swallowReturn() { try { unchecked(); } catch (RuntimeException e) { return; } }
                void emptySpecific() { try { io(); } catch (IOException e) { } }
                void catchIgnoredName() { try { io(); } catch (IOException ignored) { } }
                void catchHandled() { try { work(); } catch (Exception e) { log(e); } }
                void catchRethrow() { try { work(); } catch (Exception e) { throw new RuntimeException(e); } }
                void catchSpecificReturn() { try { io(); } catch (IOException e) { return; } }

                boolean stringEq(String a, String b) { return a == b; }
                boolean stringNe(String a) { return a != "x"; }
                boolean stringEquals(String a, String b) { return a.equals(b); }
                boolean stringNull(String a) { return a == null; }
                boolean intEq(int a) { return a == 3; }
                boolean bothLiterals() { return "a" == "a"; }
                boolean objects(Object a, Object b) { return a == b; }

                void resPositive(File f) throws IOException { InputStream in = new FileInputStream(f); in.read(); }
                void resCloseOutsideFinally(File f) throws IOException { InputStream in = new FileInputStream(f); in.read(); in.close(); }
                void resTryWithResources(File f) throws IOException { try (InputStream in = new FileInputStream(f)) { in.read(); } }
                void resFinally(File f) throws IOException {
                    InputStream in = null;
                    try { in = new FileInputStream(f); in.read(); } finally { if (in != null) in.close(); }
                }
                InputStream resReturned(File f) throws IOException { InputStream in = new FileInputStream(f); return in; }
                void resHelper(File f) throws IOException {
                    InputStream in = new FileInputStream(f);
                    try { in.read(); } finally { closeQuietly(in); }
                }
                void resField(File f) throws IOException { InputStream in = new FileInputStream(f); this.kept = in; }
                void resBytes() { java.io.ByteArrayOutputStream o = new java.io.ByteArrayOutputStream(); o.write(1); }
                void resGetter(Holder h) { InputStream s = h.getStream(); }

                static class Repo {
                    String name;
                    Map<String, Repo> map = new HashMap<String, Repo>();
                    Repo find(String k) { return map.get(k); }
                    Repo findNull() { return null; }
                    Repo safe() { return new Repo(); }
                }

                static class Cache {
                    Map<String, Repo> map = new HashMap<String, Repo>();
                    Repo lazy(String k) { if (!map.containsKey(k)) map.put(k, new Repo()); return map.get(k); }
                    String prop() { return System.getProperty("x"); }
                }

                int nullLazyCache(Cache c) { Repo x = c.lazy("k"); return x.name.length(); }
                int nullSystemProperty(Cache c) { String p = c.prop(); return p.length(); }
                int nullGuardedChain(Repo r) { return r.findNull() != null && r.findNull().name.length() > 0 ? 1 : 0; }
                int nullLocal(Repo r) { Repo x = r.findNull(); return x.name.length(); }
                int nullViaMap(Repo r) { Repo x = r.find("k"); return x.hashCode(); }
                int nullChain(Repo r) { return r.findNull().name.length(); }
                int nullChecked(Repo r) { Repo x = r.findNull(); if (x == null) return 0; return x.name.length(); }
                int nullTernary(Repo r) { Repo x = r.findNull(); return x != null ? x.name.length() : 0; }
                int nullSafe(Repo r) { Repo x = r.safe(); return x.name.length(); }

                @Deprecated void oldApi() {}
                int deprecatedJdk() { Date d = new Date(); return d.getYear(); }
                void deprecatedOwn() { oldApi(); }
                void deprecatedProject(Other o) { o.legacy(); }
                @SuppressWarnings("deprecation") int deprecatedSuppressed() { return new Date().getYear(); }
                @Deprecated int deprecatedInside() { return new Date().getYear(); }

                void references() {
                    List<String> l = new ArrayList<String>();
                    l.add("x");
                    l.forEach(System.out::println);
                    Supplier<Holder> s = Holder::new;
                    java.util.function.Function<String, String> f = Holder::helper;
                    java.util.function.Function<Object, String> v = String::valueOf;
                    java.util.function.IntFunction<int[]> arr = int[]::new;
                }
            }
            """,

        [OtherPath] = """
            package t;

            public class Other {
                @Deprecated
                public void legacy() {}
            }
            """,

        [ShapesPath] = """
            package t;

            interface Shape {
                double area();
                String name();
            }

            abstract class Base implements Shape {
                public String name() { return "base"; }
                @Override public String toString() { return name(); }
            }

            class Circle extends Base {
                public double area() { return 3.14; }
            }

            class Square extends Circle {
                @Override public double area() { return 1; }
                private void helper() {}
            }

            interface Named extends Shape {}

            class Plain implements Named {
                public double area() { return 0; }
                public String name() { return "plain"; }
            }
            """,

        // ISO-8859-1: las tildes se leen con la codificación del fichero, no como UTF-8.
        [ManyPath] = ManySource(),

        [NotesPath] = """
            package t;

            // Cabecera pegada al tipo: explica la clase
            public class Notes {
                // TODO(juan): limpiar esto: añadir validación
                // FIXME el año bisiesto
                private int a;

                // HACK temporal
                public void hack() {
                    // int x = compute(3);
                    // if (x > 2) {
                    //     log(x);
                    // }
                    int y = 1; // trailing foo();
                    // esto es prosa normal con punto y coma; pero no es código
                    // return;
                }

                /** Javadoc con acentos: áéí. */
                public void documented() {}

                // Comentario de un método: sin marca
                // segunda línea del comentario
                public void commented() {}

                // dead();
                public void afterCode() {}

                public void strings() {
                    String u = "http://x.org/*not a comment*/"; // HACK tras una cadena
                }

                void compute() {}
                void log(int x) {}
            }
            """
    };

    public async Task InitializeAsync()
    {
        if (TestJdk.Info == null)
            return;

        _temp = new TempDirectory();
        var root = Path.Combine(_temp.Path, "repo");
        foreach (var (path, text) in Sources)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, Encoding.Latin1.GetBytes(text.ReplaceLineEndings("\n")));
        }

        var scan = RepositoryScan.Scan(root, []);
        var run = await JavaAnalyzerRunner.RunAsync(new JavaAnalyzerRunRequest
        {
            Scan = scan,
            Options = new JavaAnalyzerOptions { JdkPath = TestJdk.Info.JdkPath, JvmOptions = ["-Xmx1g"] },
            Jdk = TestJdk.Info,
            Classpath = new JavaClasspath([], []),
            DefaultEncoding = "ISO-8859-1",
            WorkingDirectory = Path.Combine(_temp.Path, "work")
        }, null, null, CancellationToken.None);
        Records = [.. JavaAnalysisReader.Read(run.ResultPath)];
    }

    public Task DisposeAsync()
    {
        _temp?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Reglas de <c>issue</c>, <c>OVERRIDES</c>, <c>X::m</c> y comentarios de <c>JavaAnalyzer.java</c> (schema 3).</summary>
public class JavaAnalyzerRulesTests(JavaRulesFixture f) : IClassFixture<JavaRulesFixture>
{
    private static readonly string[] AllMethods =
    [
        "emptyCatch", "swallowReturn", "emptySpecific", "catchIgnoredName", "catchHandled", "catchRethrow", "catchSpecificReturn",
        "stringEq", "stringNe", "stringEquals", "stringNull", "intEq", "bothLiterals", "objects",
        "resPositive", "resCloseOutsideFinally", "resTryWithResources", "resFinally", "resReturned", "resHelper", "resField", "resBytes", "resGetter",
        "nullLocal", "nullViaMap", "nullChain", "nullChecked", "nullTernary", "nullSafe", "nullLazyCache", "nullSystemProperty", "nullGuardedChain",
        "oldApi", "deprecatedJdk", "deprecatedOwn", "deprecatedProject", "deprecatedSuppressed", "deprecatedInside", "references"
    ];

    private IEnumerable<string> RulesOf(string method) => f.Issues(method).Select(i => i.Rule).Distinct().Order(StringComparer.Ordinal);

    [JdkFact]
    public void The_fixture_compiles_without_errors_and_reports_the_schema_3_summary()
    {
        Assert.DoesNotContain(f.Records, r => r is JavaDiagnosticRecord);
        var summary = f.Records.OfType<JavaSummaryRecord>().Single();
        Assert.Equal(3, summary.Schema);
        Assert.Equal("4", summary.AnalyzerVersion);
        Assert.Equal(f.Records.OfType<JavaIssueRecord>().LongCount(), summary.Issues);
        Assert.All(f.Records.OfType<JavaFileRecord>(), file => Assert.False(string.IsNullOrEmpty(file.Encoding)));
    }

    [JdkFact]
    public void Empty_catch_flags_empty_blocks_and_general_catches_that_only_return_but_not_handled_ones()
    {
        Assert.Equal(["empty-catch"], RulesOf("emptyCatch"));
        Assert.Equal(["empty-catch"], RulesOf("swallowReturn"));
        Assert.Equal(["empty-catch"], RulesOf("emptySpecific"));
        Assert.Empty(f.Issues("catchIgnoredName"));
        Assert.Empty(f.Issues("catchHandled"));
        Assert.Empty(f.Issues("catchRethrow"));
        Assert.Empty(f.Issues("catchSpecificReturn"));

        var empty = Assert.Single(f.Issues("emptyCatch"));
        Assert.Equal("catch (Exception) vacío: la excepción se descarta sin registrarla ni relanzarla", empty.Text);
        Assert.Equal(f.LineOf(JavaRulesFixture.HolderPath, "void emptyCatch()"), empty.Line);
        Assert.Contains("solo devuelve o continúa", Assert.Single(f.Issues("swallowReturn")).Text);
    }

    [JdkFact]
    public void String_identity_flags_reference_comparisons_between_strings_only()
    {
        Assert.Equal(["string-identity"], RulesOf("stringEq"));
        Assert.Equal(["string-identity"], RulesOf("stringNe"));
        Assert.Contains("'!='", Assert.Single(f.Issues("stringNe")).Text);
        Assert.Empty(f.Issues("stringEquals"));
        Assert.Empty(f.Issues("stringNull"));
        Assert.Empty(f.Issues("intEq"));
        Assert.Empty(f.Issues("bothLiterals"));
        Assert.Empty(f.Issues("objects"));
    }

    [JdkFact]
    public void Undisposed_resource_needs_no_twr_no_finally_close_and_no_escape()
    {
        var positive = Assert.Single(f.Issues("resPositive"));
        Assert.Equal("undisposed-resource", positive.Rule);
        Assert.Contains("'in' (InputStream)", positive.Text);
        Assert.Equal(f.LineOf(JavaRulesFixture.HolderPath, "void resPositive"), positive.Line);

        var outside = Assert.Single(f.Issues("resCloseOutsideFinally"));
        Assert.Contains("fuera de un finally", outside.Text);

        foreach (var negative in new[] { "resTryWithResources", "resFinally", "resReturned", "resHelper", "resField", "resBytes", "resGetter" })
            Assert.Empty(f.Issues(negative));
    }

    [JdkFact]
    public void Null_return_deref_follows_a_method_that_can_return_null_to_the_unchecked_dereference()
    {
        foreach (var positive in new[] { "nullLocal", "nullViaMap", "nullChain" })
            Assert.Equal(["null-return-deref"], RulesOf(positive));
        Assert.Contains("'x' procede de 'Repo.findNull()'", Assert.Single(f.Issues("nullLocal")).Text);
        Assert.Contains("'Repo.find()'", Assert.Single(f.Issues("nullViaMap")).Text);
        Assert.Contains("el resultado de 'Repo.findNull()'", Assert.Single(f.Issues("nullChain")).Text);
        Assert.Equal(f.LineOf(JavaRulesFixture.HolderPath, "int nullLocal"), Assert.Single(f.Issues("nullLocal")).Line);

        // Una caché perezosa (containsKey/put antes del get) y un System.getProperty envuelto no son un null que se deba suponer.
        foreach (var negative in new[] { "nullChecked", "nullTernary", "nullSafe", "nullLazyCache", "nullSystemProperty", "nullGuardedChain" })
            Assert.Empty(f.Issues(negative));
    }

    [JdkFact]
    public void Obsolete_api_flags_deprecated_members_used_outside_their_class_and_respects_suppression()
    {
        Assert.Equal(["obsolete-api"], RulesOf("deprecatedJdk"));
        Assert.Contains("'Date.getYear()'", Assert.Single(f.Issues("deprecatedJdk")).Text);
        Assert.Equal(["obsolete-api"], RulesOf("deprecatedProject"));
        Assert.Contains("'Other.legacy()'", Assert.Single(f.Issues("deprecatedProject")).Text);
        Assert.Empty(f.Issues("deprecatedOwn"));
        Assert.Empty(f.Issues("deprecatedSuppressed"));
        Assert.Empty(f.Issues("deprecatedInside"));
    }

    [JdkFact]
    public void No_other_method_of_the_fixture_gets_a_finding()
    {
        var expected = new[]
        {
            "emptyCatch", "swallowReturn", "emptySpecific", "stringEq", "stringNe", "resPositive", "resCloseOutsideFinally",
            "nullLocal", "nullViaMap", "nullChain", "deprecatedJdk", "deprecatedProject"
        };
        var flagged = f.Records.OfType<JavaIssueRecord>().Select(i => i.From!.Split('#')[1].Split('(')[0]).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), flagged);
        Assert.All(AllMethods.Where(m => !expected.Contains(m)), m => Assert.Empty(f.Issues(m)));
    }

    [JdkFact]
    public void Overrides_go_to_the_nearest_project_method_and_is_override_covers_jdk_methods_too()
    {
        JavaMethodRecord Method(string key) => f.Records.OfType<JavaMethodRecord>().Single(m => m.Key == key);

        Assert.Equal(["t.Shape#area()"], Method("t.Circle#area()").Overrides);
        Assert.Equal(["t.Circle#area()"], Method("t.Square#area()").Overrides);
        Assert.Equal(["t.Shape#name()"], Method("t.Base#name()").Overrides);
        // toString solo sobrescribe el de Object (JDK): es override y no tiene arista del proyecto.
        var toString = Method("t.Base#toString()");
        Assert.True(toString.IsOverride);
        Assert.Empty(toString.Overrides);
        Assert.False(Method("t.Square#helper()").IsOverride);
        Assert.False(Method("t.Shape#area()").IsOverride);
    }

    [JdkFact]
    public void A_method_reference_is_a_call_of_kind_ref_with_the_line_of_its_name()
    {
        var refs = f.Records.OfType<JavaCallRecord>().Where(c => c.Kind == "ref").ToList();
        var line = f.LineOf(JavaRulesFixture.HolderPath, "Holder::new");
        Assert.Contains(refs, c => c.To == "t.Holder#Holder()" && c.Line == line && c.From!.StartsWith("t.Holder#references(", StringComparison.Ordinal));
        Assert.Contains(refs, c => c.To == "t.Holder#helper(java.lang.String)" && c.Origin == JavaOrigins.Project);
        Assert.Contains(refs, c => c.To == "java.lang.String#valueOf(java.lang.Object)" && c.Origin == JavaOrigins.Jdk);
        Assert.Contains(refs, c => c.To!.StartsWith("java.io.PrintStream#println(", StringComparison.Ordinal));
        // int[]::new no tiene destino: no cuenta como llamada sin resolver.
        Assert.DoesNotContain(refs, c => c.Reason != null);
        Assert.Equal(0, f.Records.OfType<JavaSummaryRecord>().Single().UnresolvedByReason.GetValueOrDefault("null-element"));
    }

    [JdkFact]
    public void Nested_types_carry_their_outer_type()
    {
        var types = f.Records.OfType<JavaTypeRecord>().ToDictionary(t => t.Fqn);
        Assert.Equal("t.Holder", types["t.Holder.Repo"].Outer);
        Assert.Null(types["t.Holder"].Outer);
    }

    // ---------------------------------------------------------------- comentarios

    private JavaFileRecord NotesFile => f.Records.OfType<JavaFileRecord>().Single(r => r.Path == JavaRulesFixture.NotesPath);

    private JavaCommentRecord? Comment(string owner, string? key) =>
        f.Records.OfType<JavaCommentRecord>().SingleOrDefault(c => c.Path == JavaRulesFixture.NotesPath && c.Owner == owner && c.Key == key);

    [JdkFact]
    public void Each_file_reports_the_encoding_it_was_decoded_with()
    {
        Assert.Equal("ISO-8859-1", NotesFile.Encoding);
    }

    [JdkFact]
    public void Todo_markers_go_to_the_method_that_contains_or_follows_them_or_else_to_the_type()
    {
        var type = Comment("type", "t.Notes")!;
        Assert.Equal(["TODO", "FIXME"], type.Todos.Select(t => t.Kind));
        Assert.Equal(f.LineOf(JavaRulesFixture.NotesPath, "// TODO(juan)"), type.Todos[0].Line);
        Assert.Contains("añadir validación", type.Todos[0].Text);
        Assert.Equal(f.LineOf(JavaRulesFixture.NotesPath, "// FIXME"), type.Todos[1].Line);
        Assert.Equal("el año bisiesto", type.Todos[1].Text);

        // «// HACK temporal» está justo encima de hack(): es de ese método.
        var hack = Comment("method", "t.Notes#hack()")!;
        Assert.Equal("HACK", Assert.Single(hack.Todos).Kind);
        Assert.Equal("temporal", hack.Todos[0].Text);

        // La cadena con // y /* no es un comentario; el HACK final es del método.
        var strings = Comment("method", "t.Notes#strings()")!;
        var marker = Assert.Single(strings.Todos);
        Assert.Equal(("HACK", "tras una cadena"), (marker.Kind, marker.Text));
        Assert.Empty(strings.Code);
    }

    [JdkFact]
    public void Commented_code_is_a_comment_that_parses_as_statements_and_prose_with_a_semicolon_is_not()
    {
        var hack = Comment("method", "t.Notes#hack()")!;
        var first = f.LineOf(JavaRulesFixture.NotesPath, "// int x = compute(3);");
        Assert.Equal(2, hack.Code.Count);
        Assert.Equal((first, first + 3, 4), (hack.Code[0].StartLine, hack.Code[0].EndLine, hack.Code[0].Lines));
        Assert.Equal("int x = compute(3);", hack.Code[0].Preview);
        // Del bloque de dos líneas («prosa; …» y «return;») solo la segunda parsea: una racha de una línea.
        var last = f.LineOf(JavaRulesFixture.NotesPath, "// return;");
        Assert.Equal((last, last, 1), (hack.Code[1].StartLine, hack.Code[1].EndLine, hack.Code[1].Lines));
        Assert.Equal("return;", hack.Code[1].Preview);

        // «// trailing foo();» comparte línea con código y «// dead();» es el comentario de afterCode: este sí es código comentado.
        var after = Comment("method", "t.Notes#afterCode()")!;
        Assert.Equal(f.LineOf(JavaRulesFixture.NotesPath, "// dead();"), Assert.Single(after.Code).StartLine);
        Assert.DoesNotContain(f.Records.OfType<JavaCommentRecord>().SelectMany(c => c.Code), c => c.Preview.Contains("foo()", StringComparison.Ordinal));
    }

    [JdkFact]
    public void Prose_with_a_semicolon_is_never_code_even_after_the_hundredth_candidate_of_a_parse_batch()
    {
        // javac deja de informar de errores pasados 100 por defecto: sin -Xmaxerrs, todo lo que va después parecería código.
        var code = f.Records.OfType<JavaCommentRecord>().Where(c => c.Path == JavaRulesFixture.ManyPath).SelectMany(c => c.Code).ToList();
        var only = Assert.Single(code);
        Assert.Equal("finish(3);", only.Preview);
        Assert.Equal(f.LineOf(JavaRulesFixture.ManyPath, "// finish(3);"), only.StartLine);
    }

    [JdkFact]
    public void Docstrings_come_from_javadoc_or_the_comment_glued_to_the_declaration_decoded_with_the_file_encoding()
    {
        var types = f.Records.OfType<JavaTypeRecord>().ToDictionary(t => t.Fqn);
        Assert.Equal(("Cabecera pegada al tipo: explica la clase", "comment"), (types["t.Notes"].Doc, types["t.Notes"].DocSource));

        var methods = f.Records.OfType<JavaMethodRecord>().Where(m => m.OwnerFqn == "t.Notes").ToDictionary(m => m.Name);
        Assert.Equal("Javadoc con acentos: áéí.", methods["documented"].Doc);
        Assert.Equal("javadoc", methods["documented"].DocSource);
        Assert.Equal(("Comentario de un método: sin marca segunda línea del comentario", "comment"), (methods["commented"].Doc, methods["commented"].DocSource));
        // Un comentario que es código comentado, o solo una marca, no documenta nada.
        Assert.Null(methods["afterCode"].Doc);
        Assert.Null(methods["hack"].Doc);
        Assert.Null(methods["afterCode"].DocSource);

        Assert.DoesNotContain(f.Records.OfType<JavaMethodRecord>(), m => m.Doc != null && m.Doc.Contains('\uFFFD'));
        Assert.DoesNotContain(f.Records.OfType<JavaCommentRecord>().SelectMany(c => c.Todos), t => t.Text.Contains('\uFFFD'));
    }
}
