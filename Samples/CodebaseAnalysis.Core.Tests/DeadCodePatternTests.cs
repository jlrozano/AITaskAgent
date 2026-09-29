using System.Text.Json;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tests.Enrichment;
using Xunit.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Capas 2 y 3 de la propuesta (d) en las facetas (tanda C): los candidatos de código muerto con un patrón común van
/// resumidos en grupos (<see cref="DeadCodeGrouping"/>), la faceta de instanciación dinámica recoge los sitios de
/// instanciación por nombre y las clases nombradas en XML (<see cref="DynamicInstantiationScanner"/>), y el cruce marca
/// <c>likely_dynamic</c> los grupos con evidencia. Sobre un grafo sintético y un repositorio sintético Java y C#.
/// </summary>
public sealed class DeadCodePatternTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Repo => Path.Combine(_temp.Path, "repo");

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"));
    }

    private static int LineOf(string text, string fragment) =>
        text.ReplaceLineEndings("\n").Split('\n').ToList().FindIndex(l => l.Contains(fragment, StringComparison.Ordinal)) + 1;

    private async Task<ReportFacetInput> FacetsAsync(string databasePath)
    {
        var context = new PipelineContext();
        context.Set(AnalysisContextKeys.PipelineInput, new AnalysisPipelineInput
        {
            RepositoryPath = Repo,
            DatabasePath = databasePath,
            OutputReportsPath = Path.Combine(Repo, "reports")
        });
        context.Set(AnalysisContextKeys.Reporter, new CapturingReporter());
        return await new SqlFacetExtractorStep().ExecuteAsync(new GraphStatusResult { DatabasePath = databasePath }, context, 1, null, CancellationToken.None);
    }

    private JsonElement Parse(string facet, string name)
    {
        output.WriteLine($"--- {name}\n{facet}");
        Assert.DoesNotContain("Faceta no disponible", facet);
        using var doc = JsonDocument.Parse(facet);
        return doc.RootElement.Clone();
    }

    /// <summary>La factoría compone el nombre con un sufijo que es una constante lejos de la llamada.</summary>
    private const string Factory = """
        package app.dao;

        public class FactoriaDAO {
            private static final String SUFIJO = "JdbcImpl";

            public static String prefijo() {
                return "DAO";
            }

            public static Object crear(String nombre) throws Exception {
                // Class.forName("comentado." + nombre) no cuenta
                String clase = "app.dao.jdbc." + prefijo() + nombre + SUFIJO;
                return Class.forName(clase).newInstance();
            }

            public static Object conocida() throws Exception {
                Object a = Class.forName("app.util.Cargada").getDeclaredConstructor().newInstance();
                return FactoriaDAO.class.newInstance();
            }
        }
        """;

    private const string Loader = """
        using System;
        using System.Reflection;

        public class Cargador
        {
            public object Cargar(string nombre, string ruta, object obj)
            {
                var t = Type.GetType(nombre + "Plugin");
                var o = Activator.CreateInstance(t);
                var s = Activator.CreateInstance(typeof(Fijo));
                var g = Activator.CreateInstance<Fijo>();
                var k = obj.GetType();
                var n = Type.GetType("Plugins.Nombrado, Plugins");
                var asm = Assembly.LoadFrom(ruta);
                return o;
            }
        }
        """;

    private const string WebXml = """
        <?xml version="1.0"?>
        <web-app>
          <!-- <servlet><servlet-class>app.web.Comentada</servlet-class></servlet> -->
          <servlet>
            <servlet-name>otra</servlet-name>
            <servlet-class>app.web.Otra</servlet-class>
          </servlet>
        </web-app>
        """;

    private void WriteRepository()
    {
        Write("src/app/dao/FactoriaDAO.java", Factory);
        Write("src/Plugins/Cargador.cs", Loader);
        Write("WebContent/WEB-INF/web.xml", WebXml);
        Write("WebContent/WEB-INF/applicationContext.xml", """
            <beans><bean id="alfa" class="app.dao.jdbc.DAOAlfaJdbcImpl"/><bean id="txt" type="text"/></beans>
            """);
        Write("WebContent/WEB-INF/struts-config.xml", """
            <struts-config><form-beans><form-bean name="cliente" type="app.forms.ClienteForm"/></form-beans></struts-config>
            """);
        // Ficheros de build y raíces de test no cuentan.
        Write("pom.xml", "<project><build><plugins><plugin type=\"app.build.NoCuenta\"/></plugins></build></project>");
        Write("src/test/java/app/PruebaFactoria.java", "class PruebaFactoria { Object x(String n) throws Exception { return Class.forName(n); } }");
    }

    private static readonly string[] DaoNames =
        ["Alfa", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa", "Lambda", "Mu"];

    private async Task<CbmTestGraph> GraphAsync()
    {
        Directory.CreateDirectory(_temp.Path);
        var g = await CbmTestGraph.CreateAsync(Path.Combine(_temp.Path, "codebase.db"), "demo");
        static string Type(string fqn) => $"{{\"fan_in\":0,\"fqn\":\"{fqn}\"}}";
        // Doce DAO con el mismo patrón, de tamaños distintos (la faceta ordena por LOC), y dos Impl de otro sufijo.
        for (var i = 0; i < DaoNames.Length; i++)
        {
            var name = $"DAO{DaoNames[i]}JdbcImpl";
            await g.NodeAsync("Class", name, $"demo.src.app.dao.jdbc.{name}", $"src/app/dao/jdbc/{name}.java", 1, 100 - i, Type($"app.dao.jdbc.{name}"));
        }
        await g.NodeAsync("Class", "PedidoServiceImpl", "demo.src.app.svc.PedidoServiceImpl", "src/app/svc/PedidoServiceImpl.java", 1, 300, Type("app.svc.PedidoServiceImpl"));
        await g.NodeAsync("Class", "ClienteServiceImpl", "demo.src.app.svc.ClienteServiceImpl", "src/app/svc/ClienteServiceImpl.java", 1, 250, Type("app.svc.ClienteServiceImpl"));
        // Sueltos; a Otra la nombra el web.xml.
        await g.NodeAsync("Class", "Suelta", "demo.src.app.Suelta", "src/app/Suelta.java", 1, 20, Type("app.Suelta"));
        await g.NodeAsync("Class", "Otra", "demo.src.app.web.Otra", "src/app/web/Otra.java", 1, 10, Type("app.web.Otra"));

        // Campos sin leer: cuatro por formulario en tres formularios, y dos sueltos.
        foreach (var form in new[] { "ClienteForm", "PedidoForm", "FacturaForm" })
        {
            for (var f = 0; f < 4; f++)
            {
                await g.NodeAsync("Field", $"campo{f}", $"demo.src.app.forms.{form}.campo{f}", $"src/app/forms/{form}.java", 10 + f, 10 + f,
                    $"{{\"field_name\":\"campo{f}\",\"accessibility\":\"private\",\"type_fqn\":\"java.lang.String\"}}");
            }
        }
        await g.NodeAsync("Field", "cache = null", "demo.src.app.Suelta.cache = null", "src/app/Suelta.java", 5, 5,
            "{\"field_name\":\"cache\",\"accessibility\":\"private\",\"type_fqn\":\"java.util.Map\"}");
        await g.NodeAsync("Field", "tmp", "demo.src.app.web.Otra.tmp", "src/app/web/Otra.java", 4, 4,
            "{\"field_name\":\"tmp\",\"accessibility\":\"private\",\"type_fqn\":\"int\"}");
        return g;
    }

    [Fact]
    public async Task A_large_group_with_a_common_pattern_is_summarised_and_marked_likely_dynamic_with_its_evidence()
    {
        WriteRepository();
        using var g = await GraphAsync();

        var facets = await FacetsAsync(g.DatabasePath);

        var groups = Parse(facets.ZeroFaninGroups, "zero_fanin_groups");
        Assert.Equal(16, groups.GetProperty("total_candidates").GetInt32());
        Assert.Equal(12, groups.GetProperty("grouped_candidates").GetInt32());
        Assert.Contains("Heurística", groups.GetProperty("note").GetString());
        var group = Assert.Single(groups.GetProperty("groups").EnumerateArray());
        Assert.Equal("src/app/dao/jdbc/**/DAO*JdbcImpl", group.GetProperty("pattern").GetString());
        Assert.Equal(("DAO", "JdbcImpl", "src/app/dao/jdbc", "app.dao.jdbc"),
            (group.GetProperty("name_prefix").GetString(), group.GetProperty("name_suffix").GetString(),
             group.GetProperty("directory").GetString(), group.GetProperty("package").GetString()));
        Assert.Equal(12, group.GetProperty("count").GetInt32());
        // Tres ejemplos, los más grandes primero, como la lista.
        Assert.Equal(["DAOAlfaJdbcImpl", "DAOBetaJdbcImpl", "DAOGammaJdbcImpl"],
            group.GetProperty("examples").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        Assert.True(group.GetProperty("likely_dynamic").GetBoolean());

        var evidence = group.GetProperty("evidence").EnumerateArray().ToList();
        var site = Assert.Single(evidence, e => e.GetProperty("type").GetString() == "instantiation_site");
        Assert.Equal(("src/app/dao/FactoriaDAO.java", LineOf(Factory, "return Class.forName(clase)"), "Class.forName"),
            (site.GetProperty("file").GetString(), site.GetProperty("line").GetInt32(), site.GetProperty("kind").GetString()));
        // El sufijo es una constante a más de cinco líneas: se encuentra entre los literales del fichero.
        Assert.Equal("sufijo «JdbcImpl» en un literal del mismo fichero", site.GetProperty("matched").GetString());
        var xml = Assert.Single(evidence, e => e.GetProperty("type").GetString() == "named_in_configuration");
        Assert.Equal((1, 12), (xml.GetProperty("members_named").GetInt32(), xml.GetProperty("of").GetInt32()));
        Assert.Equal("app.dao.jdbc.DAOAlfaJdbcImpl en WebContent/WEB-INF/applicationContext.xml:1", xml.GetProperty("example").GetString());

        // La lista ya no repite el grupo: los dos *ServiceImpl no llegan al 80 % del sufijo Impl y quedan sueltos.
        var loose = Parse(facets.ZeroFaninClasses, "zero_fanin_classes").EnumerateArray().ToList();
        Assert.Equal(["PedidoServiceImpl", "ClienteServiceImpl", "Suelta", "Otra"], loose.Select(r => r.GetProperty("name").GetString()));
        Assert.All(loose, r => Assert.False(r.TryGetProperty("_group_fqn", out _)));
        Assert.Equal($"app.web.Otra en WebContent/WEB-INF/web.xml:{LineOf(WebXml, "<servlet-class>app.web.Otra")}",
            loose.Single(r => r.GetProperty("name").GetString() == "Otra").GetProperty("named_in_configuration").GetString());
        Assert.False(loose[0].TryGetProperty("named_in_configuration", out _));
    }

    [Fact]
    public async Task Unread_fields_are_grouped_by_their_declaring_type_and_the_configuration_marks_the_group()
    {
        WriteRepository();
        using var g = await GraphAsync();

        var facets = await FacetsAsync(g.DatabasePath);

        var group = Assert.Single(Parse(facets.UnreadFieldGroups, "unread_field_groups").GetProperty("groups").EnumerateArray());
        Assert.Equal(("src/app/forms/**/*Form", 12), (group.GetProperty("pattern").GetString(), group.GetProperty("count").GetInt32()));
        Assert.Equal(["campo0", "campo1", "campo2"], group.GetProperty("examples").EnumerateArray().Select(e => e.GetProperty("field_name").GetString()));
        var xml = Assert.Single(group.GetProperty("evidence").EnumerateArray());
        Assert.Equal(("named_in_configuration", 4), (xml.GetProperty("type").GetString(), xml.GetProperty("members_named").GetInt32()));
        Assert.Equal(["cache", "tmp"], Parse(facets.UnreadFields, "unread_fields").EnumerateArray().Select(r => r.GetProperty("field_name").GetString()));
    }

    [Fact]
    public async Task The_dynamic_instantiation_facet_keeps_non_literal_sites_and_classes_named_in_configuration()
    {
        WriteRepository();
        using var g = await GraphAsync();

        var facets = await FacetsAsync(g.DatabasePath);

        var facet = Parse(facets.DynamicInstantiationSites, "dynamic_instantiation_sites");
        Assert.StartsWith("Heurística", facet.GetProperty("note").GetString());
        var sites = facet.GetProperty("sites").EnumerateArray()
            .Select(s => (s.GetProperty("file").GetString(), s.GetProperty("line").GetInt32(), s.GetProperty("kind").GetString())).ToList();
        Assert.Equal(
        [
            ("src/Plugins/Cargador.cs", LineOf(Loader, "Type.GetType(nombre"), "GetType"),
            ("src/Plugins/Cargador.cs", LineOf(Loader, "Activator.CreateInstance(t)"), "Activator.CreateInstance"),
            ("src/Plugins/Cargador.cs", LineOf(Loader, "Assembly.LoadFrom"), "Assembly.Load"),
            ("src/app/dao/FactoriaDAO.java", LineOf(Factory, "return Class.forName(clase)"), "Class.forName")
        ], sites);
        Assert.Equal("return Class.forName(clase).newInstance();", facet.GetProperty("sites")[3].GetProperty("code").GetString());
        // Sin el comentario, el test ni el pom: web.xml, applicationContext.xml y struts-config.
        Assert.Equal(3, facet.GetProperty("configuration_named_classes_total").GetInt32());
        Assert.Equal(["Plugins.Nombrado", "app.util.Cargada"],
            facet.GetProperty("literal_named_classes").EnumerateArray().Select(n => n.GetProperty("name").GetString()));
        Assert.DoesNotContain("Comentada", facets.DynamicInstantiationSites);
        Assert.DoesNotContain("NoCuenta", facets.DynamicInstantiationSites);
        Assert.DoesNotContain("PruebaFactoria", facets.DynamicInstantiationSites);
    }

    /// <summary>
    /// Una factoría que compone el nombre con piezas variables y un literal fijo al final (<c>paquete + clase + "Impl"</c>):
    /// el sufijo del grupo no aparece entero en ningún sitio, pero el literal es su final de tokens y cuenta como indicio.
    /// Un literal que no acaba en un límite de token (<c>"mpl"</c>) no cuenta.
    /// </summary>
    [Fact]
    public void A_literal_piece_of_a_composed_name_that_ends_the_group_suffix_marks_the_group()
    {
        GroupCandidate C(string name) => new(new Dictionary<string, object?> { ["name"] = name }, name, $"src/x/{name}.java", null, []);
        var candidates = DaoNames.Select(n => C($"Repo{n}JdbcImpl")).ToList();
        var groups = DeadCodeGrouping.Group(candidates, out _);
        var scan = new DynamicInstantiationScan
        {
            Sites =
            [
                new InstantiationSite("src/f/Factoria.java", 12, "Class.forName", "c = Class.forName(nombre);",
                    "nombre = paquete + \".\" + modulo + clase + \"Impl\";\nc = Class.forName(nombre);"),
                new InstantiationSite("src/f/Otra.java", 3, "Class.forName", "Class.forName(x + \"mpl\");", "Class.forName(x + \"mpl\");")
            ]
        };

        DeadCodeGrouping.CrossWith(groups, scan);

        var group = Assert.Single(groups);
        Assert.Equal("JdbcImpl", group.NameSuffix);
        var evidence = Assert.Single(group.Evidence);
        Assert.Equal("src/f/Factoria.java", evidence["file"]!.GetValue<string>());
        Assert.Equal("el literal «Impl» con que se compone el nombre, en la línea o las anteriores, es el final del sufijo «JdbcImpl»",
            evidence["matched"]!.GetValue<string>());
    }

    [Fact]
    public void Grouping_is_deterministic_takes_the_longest_common_affix_and_groups_the_rest_by_directory()
    {
        GroupCandidate C(string name, string file) => new(new Dictionary<string, object?> { ["name"] = name }, name, file, null, []);
        var candidates = new List<GroupCandidate>();
        for (var i = 0; i < 11; i++)
            candidates.Add(C($"Tipo{(char)('A' + i)}Jdbc Impl".Replace(" ", ""), $"src/a/b{i % 2}/X{i}.java"));
        candidates.Add(C("OtroServiceImpl", "src/c/OtroServiceImpl.java"));
        // Diez sin nombre común en el mismo directorio.
        foreach (var n in new[] { "Uno", "Dos", "Tres", "Cuatro", "Cinco", "Seis", "Siete", "Ocho", "Nueve", "Diez" })
            candidates.Add(C(n, "src/legado/" + n + ".java"));
        candidates.Add(C("Solo", "src/Solo.java"));

        var first = DeadCodeGrouping.Group(candidates, out var rest);
        var again = DeadCodeGrouping.Group([.. candidates.AsEnumerable().Reverse()], out _);

        Assert.Equal(["src/a/**/Tipo*JdbcImpl", "src/legado/**/*"], first.Select(g => g.Pattern));
        Assert.Equal([11, 10], first.Select(g => g.Members.Count));
        Assert.Equal(["name", "directory"], first.Select(g => g.Kind));
        Assert.Equal([("src/a/b0", 6), ("src/a/b1", 5)], first[0].TopDirectories);
        Assert.Equal(["OtroServiceImpl", "Solo"], rest.Select(c => c.Name));
        Assert.Equal(first.Select(g => (g.Pattern, g.Members.Count)), again.Select(g => (g.Pattern, g.Members.Count)));
        Assert.Equal(["DAO", "Customer", "Jdbc", "Impl"], DeadCodeGrouping.Tokenize("DAOCustomerJdbcImpl"));
        Assert.Equal(["XML", "Http", "Request", "2"], DeadCodeGrouping.Tokenize("XMLHttpRequest2"));
    }
}
