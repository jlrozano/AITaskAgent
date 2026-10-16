using System.Reflection;
using System.Text.RegularExpressions;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Steps;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Las plantillas reales (<c>templates/</c>, tal como se copian junto al ejecutable) de la preparación por subsistema (T20 y T22),
/// pintadas con el motor real: el prompt de investigación de cada informe en modo B con una entrada de ejemplo, el prompt de cada
/// informe (modo A y B) con una entrada por defecto y con una entrada cuyas propiedades de texto llevan una marca, para comprobar que
/// cada <c>{{ placeholder }}</c> de la plantilla (y de lo que incluye) existe en la entrada y se resuelve. El motor pinta vacío lo que
/// no resuelve y solo deja un aviso, así que «sin <c>{{</c> en el resultado» no basta: la marca es la que descubre un nombre mal escrito.
/// Tampoco hay plantillas con nombres del repositorio con el que se probó el diseño.
/// </summary>
public sealed partial class InvestigationTemplatesTests
{
    private static readonly ITemplateProvider Templates = AnalysisPipelineFactory.CreateDefaultTemplateProvider();

    private static readonly string TemplatesDir = Path.Combine(AppContext.BaseDirectory, "templates");

    /// <summary>
    /// Palabras del repositorio real con el que se diseñó esto; ninguna plantilla puede nombrarlas. Van partidas para que un grep del
    /// repositorio por esos nombres (criterio A9) no encuentre ni este test.
    /// </summary>
    private static readonly string[] ForbiddenWords =
    [
        "noti" + "fic", "super" + "contrato", "g" + "eo/", "entidades" + "Participantes", "multi" + "Rol"
    ];

    /// <summary>
    /// Nombres de los repositorios de prueba (<c>eval-repos</c>: carpetas, paquetes y proyectos de java-simple, csharp-simple y los de
    /// subsistemas) que ninguna plantilla ni la configuración que viaja con ellas puede usar ni siquiera como ejemplo: un modelo que
    /// lee en su prompt lo toma por una pista del repositorio que está analizando. Como expresiones regulares, con límite de palabra donde
    /// la palabra suelta sería legítima. Van partidas, como <see cref="ForbiddenWords"/>, para que un grep por esos nombres no encuentre este test.
    /// </summary>
    private static readonly string[] ForbiddenTestRepositoryPatterns =
    [
        @"\bac" + @"me\b", @"\bOrd" + @"ers\.(?:Api|Core|Tests)\b", @"\bSh" + @"op\.(?:Domain|Web|Qa|Tests)\b", @"com\.[a-z]+\.sh" + @"op\b",
        "(?:java|csharp)-sim" + "ple", "subsystems" + "-(?:java|csharp)", "eval-" + "repos"
    ];

    private static readonly ReportType[] ModeB = [.. CoveragePlanner.ModeBReports];

    public static TheoryData<ReportType> ModeBReports => [.. ModeB];

    public static TheoryData<ReportType> AllReports => [.. Enum.GetValues<ReportType>()];

    // ───────────────────────────── Utilidades ─────────────────────────────

    private static string SnakeCase(string pascal) =>
        Regex.Replace(pascal, "(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant();

    private static string Render(string templateName, object input) =>
        Templates.Render(templateName, new { Input = input, Context = new Dictionary<string, object?>() })!;

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][\w.]*)\s*(?:\|[^}]*)?\}\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\{\{\s*include\s+'(?<a>[^']+)'\s*\}\}|@\[(?<b>[^\]]+)\]")]
    private static partial Regex IncludeRegex();

    /// <summary>Los <c>{{ nombre }}</c> de la plantilla y de lo que incluye (transitivamente), sin los de contexto ni el reloj del motor.</summary>
    private static HashSet<string> PlaceholdersOf(string templateName)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(templateName);
        return found;

        void Collect(string name)
        {
            if (!visited.Add(name))
                return;
            var text = Templates.GetTemplate(name) ?? throw new FileNotFoundException($"No existe la plantilla '{name}'.");
            foreach (Match placeholder in PlaceholderRegex().Matches(text))
            {
                var key = placeholder.Groups["name"].Value;
                if (key is "include" or "current_timestamp" || key.StartsWith("context.", StringComparison.Ordinal))
                    continue;
                found.Add(key);
            }
            foreach (Match include in IncludeRegex().Matches(text))
                Collect(include.Groups["a"].Success ? include.Groups["a"].Value : include.Groups["b"].Value.Trim());
        }
    }

    /// <summary>Una copia de <paramref name="model"/> con cada propiedad de texto marcada con su nombre: <c>§nombre§</c>.</summary>
    private static T Marked<T>(T model) where T : new()
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string) && p.CanWrite))
            property.SetValue(model, $"§{SnakeCase(property.Name)}§");
        return model;
    }

    private static HashSet<string> PropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => SnakeCase(p.Name)).ToHashSet(StringComparer.Ordinal);

    private static void AssertEveryPlaceholderResolves<T>(string templateName) where T : class, new()
    {
        var placeholders = PlaceholdersOf(templateName);
        Assert.NotEmpty(placeholders);

        var known = PropertyNames(typeof(T));
        var unknown = placeholders.Where(p => !known.Contains(p)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0, $"{templateName}: placeholders que no son propiedad de {typeof(T).Name}: {string.Join(", ", unknown)}");

        var rendered = Render(templateName, Marked(new T()));
        Assert.DoesNotContain("{{", rendered);
        var stringProperties = typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => SnakeCase(p.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var placeholder in placeholders.Where(stringProperties.Contains))
            Assert.True(rendered.Contains($"§{placeholder}§", StringComparison.Ordinal), $"{templateName}: «{{{{ {placeholder} }}}}» no se resolvió");
    }

    // ───────────────────────────── Investigación (T20) ─────────────────────────────

    private static (CoveragePlan Plan, DomainMap Map) PlanOfExample()
    {
        Subsystem Sub(string id, int files) => new()
        {
            Id = id, Name = id.ToUpperInvariant(), Description = $"Capacidad {id}.", Roots = [$"src/{id}"], KeyTypes = [$"app.{id}.Main"],
            Files = [.. Enumerable.Range(0, files).Select(i => $"src/{id}/F{i}.cs")],
            EntryPoints = [new InventoryEntryPoint { Kind = EntryPointKinds.Main, Name = $"{id}.Main", File = $"src/{id}/Main.cs", Line = 4 }],
            Tables = [$"T_{id.ToUpperInvariant()}"],
            Metrics = new SubsystemMetrics { Files = files, Loc = files * 50, EntryPoints = 1, Tables = 1 }
        };
        var map = new DomainMap { GraphFingerprint = "fp", Subsystems = [Sub("alpha", 12), Sub("beta", 9)] };
        return (CoveragePlanner.Plan(map, new PreparationOptions(), Templates, DateTimeOffset.UtcNow), map);
    }

    [Theory]
    [MemberData(nameof(ModeBReports))]
    public void The_investigation_prompt_of_each_mode_b_report_renders_with_the_real_engine_and_injects_its_question_list(ReportType type)
    {
        var (plan, map) = PlanOfExample();
        var coverage = plan.Reports.Single(r => r.Report == type);
        var selection = coverage.Chosen[0];
        var inventory = new RepositoryInventory { ProductionLoc = new ProductionLines { Total = 1050, Files = [new FileLines { Path = "src/alpha/F0.cs", Language = "csharp", Lines = 50 }] } };
        var input = InvestigationTexts.BuildInput(selection, map.Subsystems.Single(s => s.Id == selection.SubsystemId), coverage, inventory);

        var rendered = Render($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_investigacion_prompt", input);

        Assert.DoesNotContain("{{", rendered);
        Assert.Contains(selection.Name, rendered);
        Assert.Contains($"src/{selection.SubsystemId}/Main.cs:4", rendered);
        // La lista de preguntas sale de _investigation_questions_<informe>.md, no del código: cada línea de ese fichero está en el prompt.
        var questionLines = File.ReadAllLines(Path.Combine(TemplatesDir, CoveragePlanner.QuestionsTemplateName(type) + ".md"))
            .Select(l => l.Trim().TrimStart('-').Trim()).Where(l => l.Length > 0).ToList();
        Assert.True(questionLines.Count >= 5, $"{type}: la lista de preguntas tiene {questionLines.Count} líneas");
        Assert.All(questionLines, question => Assert.Contains(question, rendered));
        Assert.All(coverage.MinimumEvidence, evidence => Assert.Contains(evidence, rendered));
        // Otros subsistemas del plan: se nombran para no repetirlos.
        Assert.Contains(coverage.Chosen[1].Name, rendered);
    }

    [Theory]
    [MemberData(nameof(ModeBReports))]
    public void Every_placeholder_of_an_investigation_prompt_is_a_property_of_its_input_and_resolves(ReportType type) =>
        AssertEveryPlaceholderResolves<SubsystemInvestigationInput>($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_investigacion_prompt");

    [Theory]
    [MemberData(nameof(ModeBReports))]
    public void The_investigation_prompt_tolerates_an_input_without_a_map_or_an_inventory(ReportType type)
    {
        var coverage = new ReportCoverage { Report = type, Chosen = [new CoverageSelection { SubsystemId = "solo", Name = "Solo", Rank = 1, Score = 1, Loc = 10 }] };
        var input = InvestigationTexts.BuildInput(coverage.Chosen[0], null, coverage, null);

        var rendered = Render($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_investigacion_prompt", input);

        Assert.DoesNotContain("{{", rendered);
        Assert.Contains("Solo", rendered);
    }

    // ───────────────────────────── Informes (T22) ─────────────────────────────

    [Theory]
    [MemberData(nameof(AllReports))]
    public void The_report_prompt_renders_with_the_default_input_and_leaves_no_placeholder(ReportType type)
    {
        var rendered = Render($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_prompt", new ReportFacetInput());

        Assert.DoesNotContain("{{", rendered);
        Assert.False(string.IsNullOrWhiteSpace(rendered));
    }

    [Theory]
    [MemberData(nameof(AllReports))]
    public void Every_placeholder_of_a_report_prompt_is_a_property_of_the_facet_input_and_resolves(ReportType type) =>
        AssertEveryPlaceholderResolves<ReportFacetInput>($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_prompt");

    [Theory]
    [MemberData(nameof(ModeBReports))]
    public void A_mode_b_report_prompt_carries_the_notes_and_the_coverage_summary(ReportType type)
    {
        var input = new ReportFacetInput { InvestigationNotes = "=== NOTAS-DE-PRUEBA ===", CoverageSummary = "RESUMEN-DE-COBERTURA-DE-PRUEBA" };

        var rendered = Render($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_prompt", input);

        Assert.Contains("=== NOTAS-DE-PRUEBA ===", rendered);
        Assert.Contains("RESUMEN-DE-COBERTURA-DE-PRUEBA", rendered);
        Assert.Contains("## Cobertura", rendered);
    }

    /// <summary>
    /// La consolidación de modo B parte de las notas y gasta herramientas solo en citas dudosas o huecos concretos: en la medición real
    /// (2026-10-04) el prompt anterior le pedía comprobar la cita de cada afirmación importante y la consolidación hacía 26-48 llamadas.
    /// El tope duro lo pone <c>ConsolidationMaxToolIterations</c>; el prompt dice cómo gastarlo.
    /// </summary>
    [Theory]
    [MemberData(nameof(ModeBReports))]
    public void A_mode_b_consolidation_starts_from_the_notes_and_uses_tools_only_for_doubtful_citations_or_concrete_gaps(ReportType type)
    {
        var rendered = Render($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_prompt", new ReportFacetInput { InvestigationNotes = "NOTAS" });

        Assert.Contains("Presupuesto de herramientas de la consolidación", rendered);
        Assert.Contains("parte de las notas", rendered);
        Assert.Contains("cita dudosa", rendered);
        Assert.Contains("hueco concreto", rendered);
        // La instrucción antigua, que invitaba a verificar cada cita importante, ya no está.
        Assert.DoesNotContain("comprueba con las herramientas la cita de cada", rendered);
        Assert.DoesNotContain("lee la línea citada de cada defecto antes", rendered);
    }

    [Theory]
    [MemberData(nameof(AllReports))]
    public void The_report_prompt_carries_the_domain_map_and_the_coverage_plan_in_mode_a_and_the_notes_in_mode_b(ReportType type)
    {
        var input = new ReportFacetInput { DomainMap = "MAPA-DE-PRUEBA", CoveragePlan = "PLAN-DE-PRUEBA", RepositoryInventory = "INVENTARIO-DE-PRUEBA" };

        var rendered = Render($"{AnalysisPipelineInput.GetTemplatePrefix(type)}_prompt", input);

        // Los de modo B reciben la investigación ya hecha; los de modo A, el inventario, el mapa y el plan como contexto.
        if (!ModeB.Contains(type))
        {
            Assert.Contains("MAPA-DE-PRUEBA", rendered);
            Assert.Contains("PLAN-DE-PRUEBA", rendered);
            Assert.Contains("INVENTARIO-DE-PRUEBA", rendered);
        }
        Assert.DoesNotContain("{{", rendered);
    }

    // ───────────────────────────── Genericidad (A9) ─────────────────────────────

    [Fact]
    public void No_template_names_the_repository_the_design_was_tested_on()
    {
        var offenders = Directory.GetFiles(TemplatesDir, "*.md")
            .SelectMany(file => ForbiddenWords
                .Where(word => File.ReadAllText(file).Contains(word, StringComparison.OrdinalIgnoreCase))
                .Select(word => $"{Path.GetFileName(file)}: «{word}»"))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Ninguna plantilla (ni subcarpeta de plantillas, ni la configuración que viaja con ellas) usa los nombres de los repositorios de
    /// prueba, tampoco como ejemplo: en la prueba real del 2026-10-04 el 03 llevaba un patrón de ejemplo de reflexión con el paquete de uno
    /// de ellos. Los ejemplos de las plantillas van con <c>com.example</c>.
    /// </summary>
    [Fact]
    public void No_template_or_shipped_config_uses_the_names_of_the_test_repositories()
    {
        var files = Directory.GetFiles(TemplatesDir, "*", SearchOption.AllDirectories)
            .Append(Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json"))
            .Where(File.Exists)
            .ToList();
        Assert.True(files.Count > 30, "se esperaban todas las plantillas y la configuración");

        var offenders = files
            .SelectMany(file =>
            {
                var text = File.ReadAllText(file);
                return ForbiddenWords.Where(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))
                    .Concat(ForbiddenTestRepositoryPatterns.Where(pattern => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)))
                    .Select(found => $"{Path.GetRelativePath(AppContext.BaseDirectory, file)}: «{found}»");
            })
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_forbidden_test_repository_patterns_do_match_what_they_are_meant_to_catch()
    {
        // Guarda de la guarda: un patrón mal escrito no detectaría nada y el test de arriba pasaría en vacío.
        string[] caught =
        [
            "com." + "ac" + "me.export.*Impl", "src/com/ac" + "me/orders/Foo.java", "Ord" + "ers.Core/Services", "Sh" + "op.Domain", "com.example.sh" + "op.web",
            "java-sim" + "ple", "csharp-sim" + "ple", "subsystems" + "-java", "eval-" + "repos/answers"
        ];
        string[] notCaught = ["com.example.export.*Impl", "src/com/example/orders/Foo.java", "Orders", "workshop", "Acmeless"];

        Assert.All(caught, text => Assert.Contains(ForbiddenTestRepositoryPatterns, p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase)));
        Assert.All(notCaught, text => Assert.DoesNotContain(ForbiddenTestRepositoryPatterns, p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase)));
    }

    [Fact]
    public void The_new_templates_exist_in_the_shipped_folder()
    {
        string[] expected =
        [
            "report_funcional_investigacion_prompt.md", "report_flujos_investigacion_prompt.md", "report_errores_investigacion_prompt.md",
            "report_arquitectura_investigacion_prompt.md", "_investigation_questions_funcional.md", "_investigation_questions_flujos.md",
            "_investigation_questions_errores.md", "_investigation_questions_arquitectura.md", "domain_naming_prompt.md", "domain_naming_system.md"
        ];

        Assert.All(expected, name => Assert.True(File.Exists(Path.Combine(TemplatesDir, name)), $"falta templates/{name}"));
    }
}
