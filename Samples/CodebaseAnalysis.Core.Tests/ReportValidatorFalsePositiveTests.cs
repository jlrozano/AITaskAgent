using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Falsos positivos de <see cref="ReportValidator"/> medidos en los informes reales de la prueba del 2026-10-04 sobre un repositorio
/// Java con Struts (<c>java/off</c> y <c>java/subsystems</c>): una ruta JSP que <c>struts-config.xml</c> declara pero que no existe en el
/// repositorio no es una «cita rota» de código, y las viñetas <c>**04-PRO-001:**</c> de la sección de prioridad de ejecución no definen
/// otro hallazgo. Los casos de arriba van con su texto real; los de abajo comprueban que lo que de verdad está roto sigue estándolo.
/// </summary>
public sealed class ReportValidatorFalsePositiveTests : IDisposable
{
    private const string Meta = "<!-- analysis-meta: health=Amber; generated_at=2026-10-04T20:55:16Z; model=m; provider=fake; system_sha=aaaaaaaaaaaa; prompt_sha=bbbbbbbbbbbb; graph=cbm:1 -->";

    private readonly TempDirectory _temp = new();

    private string Repo => _temp.Path;
    private string Reports => Path.Combine(Repo, "reports");

    public ReportValidatorFalsePositiveTests()
    {
        WriteLines("src/main/java/com/acme/shop/web/actions/ProductListAction.java", 30);
        WriteLines("src/main/java/com/acme/shop/web/actions/InvoiceCreateAction.java", 30);
        WriteLines("WebContent/WEB-INF/jsp/present.jsp", 10);
        // El struts-config de la prueba real: declara JSP que el repositorio no trae.
        Write("WebContent/WEB-INF/struts-config.xml", """
            <struts-config>
              <action-mappings>
                <action path="/products" type="com.acme.shop.web.actions.ProductListAction" scope="request">
                  <forward name="success" path="/WEB-INF/jsp/products.jsp"/>
                </action>
                <action path="/createInvoice" type="com.acme.shop.web.actions.InvoiceCreateAction"
                        name="invoiceForm" scope="request" input="/WEB-INF/jsp/invoiceForm.jsp">
                  <forward name="success" path="/invoices.do"/>
                  <forward name="failure" path="/WEB-INF/jsp/invoiceForm.jsp"/>
                </action>
              </action-mappings>
            </struts-config>
            """);
        Directory.CreateDirectory(Reports);
    }

    public void Dispose() => _temp.Dispose();

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteLines(string relative, int count) =>
        Write(relative, string.Concat(Enumerable.Range(1, count).Select(i => $"line {i}\n")));

    private ReportFileValidation Validate(string fileName, string body, out ReportValidationResult result)
    {
        File.WriteAllText(Path.Combine(Reports, fileName), $"# Informe\n\n{body}\n\n{Meta}\n");
        result = ReportValidator.Validate(new ReportValidationRequest(Repo, Reports));
        return result.Reports.Single(r => r.FileName == fileName);
    }

    // ───────────────────────────── Rutas declaradas que no existen ─────────────────────────────

    /// <summary>La fila de la tabla de rutas de 07 en la prueba real (<c>java/off</c>, línea 15) y la de la 06 (<c>java/subsystems</c>, línea 64).</summary>
    private const string RealRouteTable = """
        | Ruta | Descripción | Destino | Auth |
        |---|---|---|---|
        | `/products` | Lista productos activos. `src/main/java/com/acme/shop/web/actions/ProductListAction.java:14-17` | `/WEB-INF/jsp/products.jsp`. `WebContent/WEB-INF/struts-config.xml:3-5` | Desconocida |
        | `/createInvoice` | Alta de factura. `src/main/java/com/acme/shop/web/actions/InvoiceCreateAction.java:15-20` | `/WEB-INF/jsp/invoiceForm.jsp`. `WebContent/WEB-INF/struts-config.xml:8-10` | Desconocida |
        """;

    [Fact]
    public void A_jsp_path_that_struts_config_declares_but_the_repository_lacks_is_not_a_broken_citation()
    {
        var report = Validate("07_inventario_apis_seguridad.md", RealRouteTable, out var result);

        // Las dos rutas JSP no son citas de código: ni válidas ni rotas.
        Assert.DoesNotContain(report.Citations, c => c.Text.EndsWith(".jsp", StringComparison.Ordinal));
        Assert.Equal(4, report.Citations.Count);
        Assert.All(report.Citations, c => Assert.True(c.IsValid, c.Text));
        Assert.Equal(0, result.Totals.BrokenCitations);
        Assert.Equal(100.0, result.Totals.CitationsValidPercent);
        Assert.Equal(0, report.Errors);

        // Quedan clasificadas aparte, con el fichero de configuración que las declara, y con un aviso (no un error).
        Assert.Equal(["/WEB-INF/jsp/products.jsp", "/WEB-INF/jsp/invoiceForm.jsp"], report.DeclaredPaths.Select(p => p.Text));
        Assert.All(report.DeclaredPaths, p => Assert.Equal("WebContent/WEB-INF/struts-config.xml", p.DeclaredIn));
        Assert.Equal(2, report.Scores.DeclaredPathsMissing);
        Assert.Equal(2, result.Totals.DeclaredPathsMissing);
        var warnings = report.Issues.Where(i => i.Check == ReportValidationChecks.DeclaredPaths).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(ReportValidationLevels.Warning, w.Level));
        Assert.Contains("declarada en `WebContent/WEB-INF/struts-config.xml`", warnings[0].Message);
        Assert.Contains("no existe en el repositorio", warnings[0].Message);
        Assert.Contains(report.Checks, c => c.Check == ReportValidationChecks.DeclaredPaths && c.Status == ReportCheckStatuses.Warn && c.Failed == 2);
    }

    [Fact]
    public void A_declared_resource_is_matched_with_the_context_prefix_or_without_it()
    {
        var report = Validate("07_inventario_apis_seguridad.md",
            "Forwards `jsp/products.jsp`, `/shop/WEB-INF/jsp/products.jsp` y `WEB-INF/jsp/invoiceForm.jsp`. Código `src/main/java/com/acme/shop/web/actions/ProductListAction.java:1`.",
            out _);

        Assert.Equal(["jsp/products.jsp", "/shop/WEB-INF/jsp/products.jsp", "WEB-INF/jsp/invoiceForm.jsp"], report.DeclaredPaths.Select(p => p.Text));
        Assert.Single(report.Citations);
    }

    [Fact]
    public void A_bare_jsp_name_declared_by_the_config_is_a_declared_path_and_not_a_graph_name_that_is_missing()
    {
        // En la prueba real, `products.jsp` en el 04 salía como «no está en el grafo» por parecer un nombre cualificado `Clase.método`.
        var report = Validate("04_flujos_end_to_end.md", "El forward `success` va a `products.jsp` y a `invoiceForm.jsp`; `Money.round` y `Nope.jsp` no.", out var result);

        Assert.Equal(["products.jsp", "invoiceForm.jsp"], report.DeclaredPaths.Select(p => p.Text));
        Assert.DoesNotContain(report.GraphNames, g => g.Text.EndsWith(".jsp", StringComparison.Ordinal));
        Assert.Equal(2, result.Totals.DeclaredPathsMissing);
    }

    [Fact]
    public void A_dotted_name_that_a_config_happens_to_mention_is_not_taken_for_a_resource_unless_it_has_a_resource_extension()
    {
        Write("WebContent/WEB-INF/other.xml", "<beans><bean id=\"Money.round\" type=\"Money.round\"/></beans>");

        var report = Validate("04_flujos_end_to_end.md", "Llama a `Money.round`.", out _);

        Assert.Empty(report.DeclaredPaths);
    }

    [Fact]
    public void A_missing_path_that_no_config_declares_is_still_a_broken_citation()
    {
        var report = Validate("07_inventario_apis_seguridad.md",
            "Pantalla `/WEB-INF/jsp/ghost.jsp` y fichero `src/main/java/com/acme/shop/web/actions/Nope.java`.", out var result);

        Assert.Empty(report.DeclaredPaths);
        Assert.Equal(["/WEB-INF/jsp/ghost.jsp", "src/main/java/com/acme/shop/web/actions/Nope.java"], report.Citations.Select(c => c.Text));
        Assert.All(report.Citations, c => Assert.Equal(CitationStatuses.Missing, c.Status));
        Assert.Equal(2, result.Totals.BrokenCitations);
        Assert.Equal(2, report.Errors);
    }

    [Fact]
    public void A_declared_path_cited_with_a_line_number_is_still_a_broken_citation()
    {
        // Con línea es una afirmación sobre el contenido de un fichero que no existe: eso sí está roto.
        var report = Validate("07_inventario_apis_seguridad.md", "Ver `/WEB-INF/jsp/products.jsp:12`.", out var result);

        Assert.Empty(report.DeclaredPaths);
        Assert.Equal(CitationStatuses.Missing, Assert.Single(report.Citations).Status);
        Assert.Equal(1, result.Totals.BrokenCitations);
    }

    [Fact]
    public void A_resource_that_exists_is_a_valid_citation_and_not_a_declared_path()
    {
        var report = Validate("07_inventario_apis_seguridad.md", "Pantalla `WebContent/WEB-INF/jsp/present.jsp`.", out _);

        Assert.Empty(report.DeclaredPaths);
        Assert.Equal(CitationStatuses.Ok, Assert.Single(report.Citations).Status);
    }

    [Fact]
    public void A_path_mentioned_only_inside_a_comment_or_text_of_a_config_is_not_a_declaration_of_another_file()
    {
        // `ghost.jsp` aparece en un comentario XML: no es un valor declarado.
        Write("WebContent/WEB-INF/web.xml", "<web-app><!-- /WEB-INF/jsp/ghost.jsp era la pantalla antigua --><display-name>x</display-name></web-app>");

        var report = Validate("07_inventario_apis_seguridad.md", "Pantalla `/WEB-INF/jsp/ghost.jsp`.", out _);

        Assert.Empty(report.DeclaredPaths);
        Assert.Equal(CitationStatuses.Missing, Assert.Single(report.Citations).Status);
    }

    [Fact]
    public void The_declared_path_summary_is_informational_and_does_not_count_as_a_broken_citation_in_the_log()
    {
        Validate("07_inventario_apis_seguridad.md", RealRouteTable, out var result);

        var lines = ReportValidationSummary.Describe(result, null);

        Assert.DoesNotContain(lines, l => l.Message.Contains("cita(s) de código rota(s)", StringComparison.Ordinal));
        var info = Assert.Single(lines, l => l.Message.Contains("declarada(s) en la configuración", StringComparison.Ordinal));
        Assert.Equal(AnalysisLogLevel.Info, info.Level);
        Assert.Contains("/WEB-INF/jsp/products.jsp", info.Message);
    }

    // ───────────────────────────── Identificadores de la prioridad de ejecución ─────────────────────────────

    /// <summary>
    /// La sección «Hallazgos y prioridades» y «Prioridad de ejecución» de <c>04_flujos_end_to_end.md</c> de <c>java/off</c>
    /// (líneas 112-126), recortada: los hallazgos son viñetas en negrita con su título y el plan los repite por número.
    /// </summary>
    private const string RealPrioritySections = """
        ## Hallazgos y prioridades

        - **04-PRO-001, alto, factura sin líneas persistidas:** `issue` añade `InvoiceLine` al objeto (`src/main/java/com/acme/shop/web/actions/InvoiceCreateAction.java:3-7`).

        - **04-PRO-002, alto, confirmación de envío sin entrega externa:** el scheduler interpreta `true` como entrega (`src/main/java/com/acme/shop/web/actions/ProductListAction.java:5-9`).

        - **04-PRO-003, medio, continuidad de la consulta tras emitir:** el éxito apunta a `/invoices.do`.

        - **04-PRO-004, medio, programación concurrente de intentos:** `scheduleFor` obtiene el intento mediante `COUNT(*)`.

        ## Prioridad de ejecución

        1. **04-PRO-001:** corregir persistencia y lectura de líneas antes de utilizar importes de facturas consultadas. Coste: esquema, DAO y pruebas de integración.
        2. **04-PRO-002:** vincular `sent` a una confirmación de entrega real. Coste: integración externa y tratamiento de fallos.
        3. **04-PRO-003 y 04-PRO-004:** cerrar el contrato de navegación y la atomicidad de los intentos. Coste: cambios acotados de controlador y persistencia.
        """;

    [Fact]
    public void The_bullets_of_the_execution_priority_section_do_not_define_a_second_finding()
    {
        var report = Validate("04_flujos_end_to_end.md", RealPrioritySections, out var result);

        Assert.Equal(["04-PRO-001", "04-PRO-002", "04-PRO-003", "04-PRO-004"], report.Findings.Select(f => f.Id));
        Assert.Equal(0, report.Scores.DuplicateIds);
        Assert.DoesNotContain(report.Issues, i => i.Check == ReportValidationChecks.FindingIds && i.Level == ReportValidationLevels.Error);
        Assert.Equal(0, result.Totals.DuplicateIds);
        // La definición es la viñeta con título: de ahí salen su línea y sus citas.
        var first = report.Findings[0];
        Assert.Equal(5, first.Line);
        Assert.Equal(1, first.Citations);
    }

    [Fact]
    public void A_plan_bullet_that_groups_ids_with_connectors_is_a_reference_too()
    {
        var report = Validate("04_flujos_end_to_end.md", """
            - **04-PRO-001, alto, uno:** texto `src/main/java/com/acme/shop/web/actions/InvoiceCreateAction.java:3`.

            - **04-PRO-002, alto, dos:** texto.

            ## Plan

            1. **04-PRO-001 y 04-PRO-002:** hacerlos juntos.
            2. **04-PRO-001, 04-PRO-002:** otra vez.
            """, out _);

        Assert.Equal(["04-PRO-001", "04-PRO-002"], report.Findings.Select(f => f.Id));
        Assert.Equal(0, report.Scores.DuplicateIds);
    }

    [Fact]
    public void Two_titled_bold_definitions_of_the_same_id_are_still_a_duplicate()
    {
        var report = Validate("04_flujos_end_to_end.md", """
            - **04-PRO-001, alto, uno:** texto `src/main/java/com/acme/shop/web/actions/InvoiceCreateAction.java:3`.

            - **04-PRO-001, alto, otro distinto:** texto.
            """, out _);

        Assert.Equal(1, report.Scores.DuplicateIds);
        Assert.Contains(report.Issues, i => i.Check == ReportValidationChecks.FindingIds && i.Message.Contains("encabeza 2 hallazgos", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bare_bold_id_with_no_other_definition_is_still_the_definition_of_its_finding()
    {
        var report = Validate("04_flujos_end_to_end.md", """
            - **04-PRO-001:** única definición, sin título en la negrita `src/main/java/com/acme/shop/web/actions/InvoiceCreateAction.java:3`.
            """, out _);

        Assert.Equal(["04-PRO-001"], report.Findings.Select(f => f.Id));
        Assert.Equal(0, report.Scores.DuplicateIds);
    }

    [Fact]
    public void Two_bare_bold_ids_with_no_titled_definition_are_still_a_duplicate()
    {
        var report = Validate("04_flujos_end_to_end.md", """
            - **04-PRO-001:** uno.

            - **04-PRO-001:** otro.
            """, out _);

        Assert.Equal(1, report.Scores.DuplicateIds);
    }
}
