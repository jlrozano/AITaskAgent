using AITaskAgent.Core.Models;
using AITaskAgent.FileTools;
using AITaskAgent.FileTools.CodeItems;
using AITaskAgent.FileTools.Tools;
using AITaskAgent.LLM.Tools.Implementation;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Tests.Enrichment;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <c>view_code_item</c> en Java (tanda C): el rango sale del grafo (<see cref="GraphCodeItemLocator"/>) y, sin grafo o sin
/// nodo, de las llaves del fichero (<see cref="JavaCodeItemLocator"/>). Las sobrecargas se listan con la ruta que elige cada
/// una. La salida conserva la cabecera <c>=== … ===</c> / <c>Location:</c> que lee <c>ClaimAuditor</c>. En la colección de la
/// consola porque <see cref="FileToolsConfiguration"/> es estático.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class JavaCodeItemLocatorTests(ITestOutputHelper output) : IDisposable
{
    private const string RelativePath = "src/demo/Pedido.java";

    /// <summary>Sobrecargas, constructores, una clase anidada, una interfaz anidada, una clase anónima y llaves en cadenas y comentarios.</summary>
    private const string Source = """
        package demo;

        import java.util.List;

        /**
         * Pedido con { llaves } en el Javadoc.
         */
        public class Pedido {
            private int total = 0;

            public Pedido() {
                this(0);
            }

            public Pedido(int total) {
                this.total = total;
            }

            @Override
            public String toString() {
                String s = "}{ no cuenta";
                char c = '{';
                return s + c; // } tampoco
            }

            public int sumar(int a) {
                return total + a;
            }

            public int sumar(int a, List<String> extras) throws IllegalStateException {
                if (a > 0) {
                    for (int i = 0; i < a; i++) {
                        total++;
                    }
                }
                Runnable r = new Runnable() {
                    public void run() { total--; }
                };
                return total;
            }

            public static class Linea {
                private final String codigo;

                Linea(String codigo) {
                    this.codigo = codigo;
                }

                public String codigo() {
                    /* { */
                    return codigo;
                }
            }

            interface Calculo {
                int calcular(int base);
            }
        }

        """;

    private readonly TempDirectory _repo = new();
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        FileToolsConfiguration.SearchExcludedDirectories = null!;
        _repo.Dispose();
    }

    private static int Line(string fragment, int occurrence = 1)
    {
        var lines = Source.ReplaceLineEndings("\n").Split('\n');
        var seen = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(fragment, StringComparison.Ordinal) && ++seen == occurrence)
                return i + 1;
        }
        throw new InvalidOperationException($"'{fragment}' no está en el fuente");
    }

    private string WriteSource()
    {
        var path = Path.Combine(_repo.Path, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Source.ReplaceLineEndings("\n"));
        FileToolsConfiguration.RootDirectory = _repo.Path;
        return path;
    }

    private static CodeItemLookup Braces(string nodePath) =>
        JavaCodeItemLocator.Lookup(JavaDeclarationScanner.Scan(Source.ReplaceLineEndings("\n")), CodeItemPath.Parse(nodePath));

    private static (int, int) Range(CodeItemLookup lookup) => (Assert.Single(lookup.Matches).StartLine, lookup.Matches[0].EndLine);

    [Fact]
    public void The_brace_locator_finds_types_methods_constructors_and_nested_members_ignoring_braces_in_strings_and_comments()
    {
        // La clase entera, no sus constructores; el Javadoc queda fuera y la anotación dentro.
        Assert.Equal((Line("public class Pedido"), Source.ReplaceLineEndings("\n").Split('\n').ToList().FindLastIndex(l => l == "}") + 1), Range(Braces("Pedido")));
        Assert.Equal((Line("@Override"), Line("return s + c;") + 1), Range(Braces("Pedido.toString")));
        Assert.Equal((Line("public Pedido(int total)"), Line("this.total = total;") + 1), Range(Braces("Pedido.Pedido(int)")));
        Assert.Equal((Line("public Pedido()"), Line("this(0);") + 1), Range(Braces("Pedido()")));
        Assert.Equal((Line("public static class Linea"), Line("interface Calculo") - 2), Range(Braces("Pedido.Linea")));
        Assert.Equal((Line("public String codigo()"), Line("return codigo;") + 1), Range(Braces("Linea.codigo")));
        // Método sin cuerpo: acaba en su punto y coma.
        Assert.Equal((Line("int calcular(int base);"), Line("int calcular(int base);")), Range(Braces("Calculo.calcular")));
        // Los métodos de una clase anónima no son del tipo.
        Assert.Empty(Braces("run").Matches);
    }

    [Fact]
    public void The_brace_locator_lists_overloads_and_picks_one_by_its_parameter_types()
    {
        var both = Braces("Pedido.sumar");
        Assert.Equal(["Pedido.sumar(int)", "Pedido.sumar(int,List)"], both.Matches.Select(m => m.NodePath));
        Assert.Equal(["sumar(int a)", "sumar(int a, List<String> extras)"], both.Matches.Select(m => m.Detail));

        Assert.Equal((Line("public int sumar(int a, List<String>"), Line("return total;") + 1), Range(Braces("Pedido.sumar(int, java.util.List<String>)")));
        Assert.Equal((Line("public int sumar(int a)"), Line("return total + a;") + 1), Range(Braces("sumar(int)")));
        Assert.Empty(Braces("Pedido.sumar(String)").Matches);
    }

    [Fact]
    public async Task View_code_item_without_a_graph_shows_a_java_method_with_the_location_header()
    {
        var file = WriteSource();
        var tool = new ViewCodeItemTool([new GraphCodeItemLocator(Path.Combine(_repo.Path, "no-existe.db"), _repo.Path, new JavaCodeItemLocator())]);

        var result = await tool.ExecuteAsync($$"""{"File": "{{RelativePath}}", "NodePaths": ["Pedido.sumar(int,List)", "Pedido.sumar", "Pedido.falta"]}""",
            new PipelineContext(), "test", NullLogger.Instance);
        output.WriteLine(result);

        var start = Line("public int sumar(int a, List<String>");
        var end = Line("return total;") + 1;
        Assert.StartsWith($"=== Pedido.sumar(int,List) ===\nLocation: {RelativePath}:{start}-{end}\n\n    public int sumar(int a, List<String> extras) throws IllegalStateException {{",
            result.ReplaceLineEndings("\n"));
        Assert.Contains("        return total;\n    }\n\n⚠️ 'Pedido.sumar' matches 2 declarations.", result.ReplaceLineEndings("\n"));
        Assert.Contains($"- `Pedido.sumar(int)` (lines {Line("public int sumar(int a)")}-{Line("return total + a;") + 1}): sumar(int a)", result);
        Assert.EndsWith("⚠️ Not found: Pedido.falta", result);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task With_a_graph_the_range_comes_from_the_node_fields_included_and_what_the_graph_lacks_from_the_braces()
    {
        WriteSource();
        using var g = await CbmTestGraph.CreateAsync(Path.Combine(_repo.Path, "codebase.db"), "demo");
        const string prefix = "demo.src.demo.Pedido";
        // Rangos del analizador (javac); el de sumar(int) se desplaza a propósito para ver que manda el grafo.
        await g.NodeAsync("Class", "Pedido", prefix + ".Pedido", RelativePath, Line("public class Pedido"), Line("interface Calculo") + 3);
        await g.NodeAsync("Method", "sumar", prefix + ".Pedido.sumar", RelativePath, Line("public int sumar(int a)") - 1, Line("return total + a;") + 1,
            """{"enriched_by":["JavaAnalyzer"],"signature":"int sumar(int a)","param_types":["int"]}""");
        await g.NodeAsync("Method", "sumar", prefix + ".Pedido.sumar(int,List)", RelativePath, Line("public int sumar(int a, List<String>"), Line("return total;") + 1,
            """{"enriched_by":["JavaAnalyzer"],"signature":"int sumar(int a, List extras)","param_types":["int","java.util.List"],"overload_of":"demo.src.demo.Pedido.Pedido.sumar"}""");
        await g.NodeAsync("Field", "total = 0", prefix + ".Pedido.total = 0", RelativePath, Line("private int total = 0;"), Line("private int total = 0;"),
            """{"enriched_by":["JavaAnalyzer"],"field_name":"total"}""");
        // Un nodo sin líneas no sirve de rango.
        await g.NodeAsync("Method", "toString", prefix + ".Pedido.toString", RelativePath, 0, 0);
        var locator = new GraphCodeItemLocator(g.DatabasePath, _repo.Path, new JavaCodeItemLocator());

        var file = Path.Combine(_repo.Path, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var lookups = (await locator.LocateAsync(new CodeItemRequest(file, Source.ReplaceLineEndings("\n"),
            ["Pedido.sumar", "Pedido.sumar(int)", "Pedido.total", "Pedido.toString", "Calculo.calcular", "Pedido.nada"]), CancellationToken.None))!;

        Assert.Equal(["Pedido.sumar(int)", "Pedido.sumar(int,List)"], lookups[0].Matches.Select(m => m.NodePath));
        Assert.Equal(["int sumar(int a)", "int sumar(int a, List extras)"], lookups[0].Matches.Select(m => m.Detail));
        Assert.Equal((Line("public int sumar(int a)") - 1, Line("return total + a;") + 1), Range(lookups[1]));
        Assert.Equal((Line("private int total = 0;"), Line("private int total = 0;")), Range(lookups[2]));
        // Sin líneas en el grafo y sin nodo: las llaves.
        Assert.Equal((Line("@Override"), Line("return s + c;") + 1), Range(lookups[3]));
        Assert.Equal((Line("int calcular(int base);"), Line("int calcular(int base);")), Range(lookups[4]));
        Assert.Empty(lookups[5].Matches);
        Assert.Contains("Pedido.sumar", lookups[5].AvailableNodes);
    }

    [Fact]
    public async Task The_graph_locator_leaves_csharp_to_roslyn_and_the_registration_wires_it()
    {
        var file = Path.Combine(_repo.Path, "Pedido.cs");
        File.WriteAllText(file, "class Pedido\n{\n    int Total() => 1;\n}\n");
        Assert.Null(await new GraphCodeItemLocator(Path.Combine(_repo.Path, "x.db"), _repo.Path, new JavaCodeItemLocator())
            .LocateAsync(new CodeItemRequest(file, File.ReadAllText(file), ["Pedido.Total"]), CancellationToken.None));

        WriteSource();
        AnalysisToolRegistration.Register(new AnalysisPipelineInput
        {
            RepositoryPath = _repo.Path,
            DatabasePath = Path.Combine(_repo.Path, ".codebase", "codebase.db"),
            OutputReportsPath = Path.Combine(_repo.Path, "reports")
        });
        var tool = AvailableTools.Instance.GetTool("view_code_item")!;

        var java = await tool.ExecuteAsync($$"""{"File": "{{RelativePath}}", "NodePaths": ["Linea.codigo"]}""", new PipelineContext(), "test", NullLogger.Instance);
        var csharp = await tool.ExecuteAsync("""{"File": "Pedido.cs", "NodePaths": ["Pedido.Total"]}""", new PipelineContext(), "test", NullLogger.Instance);

        Assert.StartsWith($"=== Linea.codigo ===\nLocation: {RelativePath}:{Line("public String codigo()")}-{Line("return codigo;") + 1}", java.ReplaceLineEndings("\n"));
        Assert.Equal("=== Pedido.Total ===\nLocation: Pedido.cs:3-3\n\nint Total() => 1;", csharp.ReplaceLineEndings("\n"));
        // La carpeta de la BD y la de informes quedan fuera de grep_search.
        Assert.Equal([Path.Combine(_repo.Path, ".codebase"), Path.Combine(_repo.Path, "reports")], FileToolsConfiguration.SearchExcludedDirectories);
    }
}
