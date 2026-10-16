using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Lo que <see cref="ReportValidator"/> y el comparador de esperados tienen que leer de informes reales (fragmentos de los de
/// csharp-simple y java-simple del 2026-09-30): hallazgos en negrita de 02 y 08, citas con varios rangos, directorios y URLs
/// Struts declaradas, y las trampas <c>notExpected</c>.
/// </summary>
public sealed class ReportValidatorReadingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string Repo => _temp.Path;
    private string Reports => Path.Combine(Repo, "reports");

    public ReportValidatorReadingTests()
    {
        WriteLines("src/Orders.Core/Services/PricingService.cs", 100);
        WriteLines("src/Orders.Core/Services/InvoiceService.cs", 40);
        WriteLines("src/Orders.Core/Services/OrderService.cs", 200);
        WriteLines("src/Orders.Api/AdminEndpoints.cs", 30);
        WriteLines("src/com/acme/orders/export/CsvExporterImpl.java", 20);
        WriteLines("WebContent/WEB-INF/jsp/searchOrders.jsp", 10);
        Write("WebContent/WEB-INF/struts-config.xml", """
            <struts-config>
              <action-mappings>
                <action path="/listOrders" type="com.acme.ListOrdersAction"/>
                <action
                    path="/searchOrders" type="com.acme.SearchOrdersAction"/>
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

    private void WriteReport(string fileName, string content) => File.WriteAllText(Path.Combine(Reports, fileName), content);

    private ReportValidationResult Validate() => ReportValidator.Validate(new ReportValidationRequest(Repo, Reports));

    private static ReportFileValidation Report(ReportValidationResult result, string fileName) => result.Reports.Single(r => r.FileName == fileName);

    // Fragmentos de 02_code_smells.md de csharp-simple (dotnetenricher) y de java-simple (gpt-6-sol).
    private const string CodeSmells = """
        # Informe de Auditoría de Code Smells y Mantenibilidad

        ## Diagnóstico detallado de componentes

        ### Runtimes y motores especializados: complejidad esencial justificada

        **COH-001, `PricingService.CalculatePrice`.** El método reúne descuentos por volumen, cupones, impuestos y envío (`src/Orders.Core/Services/PricingService.cs:20-72`). Existe una distinción contractual (`:15-72`).

        | Riesgo operativo | Riesgo evolutivo | ROI |
        |---|---|---|
        | Bajo | Medio | Alto |

        **ORD-001, `OrderService` concentra límites de salida.** La clase orquesta creación y consulta (`src/Orders.Core/Services/OrderService.cs:137,217`).

        - **DUP-001, cálculo de descuento duplicado.** `PricingService.ApplyDiscount` (`src/Orders.Core/Services/PricingService.cs:85-95`) e `InvoiceService.ComputeDiscountedTotal` (`src/Orders.Core/Services/InvoiceService.cs:18-28`).

        **GOD-001 · P1, responsabilidades inconexas en `ReportManager`.** La clase calcula totales (`src/Orders.Core/Services/OrderService.cs:30-35`).
        Segunda línea del mismo párrafo con `src/Orders.Core/Services/InvoiceService.cs:1-5`.

        ## Plan

        | Fase | Hallazgos | Acción |
        |---|---|---|
        | 3 | `ORD-001`, `DUP-001` | Corregir. |

        Sin definir aquí: **Nivel 1, arquitectura:** conservar `COH-001` y `GOD-001`.
        """;

    [Fact]
    public void Findings_defined_by_a_bold_paragraph_are_read_with_their_own_citations()
    {
        WriteReport("02_code_smells.md", CodeSmells);

        var report = Report(Validate(), "02_code_smells.md");

        Assert.Equal(["COH-001", "ORD-001", "DUP-001", "GOD-001"], report.Findings.Select(f => f.Id));
        Assert.Equal(4, report.Scores.FindingsDefined);
        var coh = report.Findings.Single(f => f.Id == "COH-001");
        Assert.Equal(1, coh.Citations); // solo la de su párrafo: ni la tabla de debajo ni el plan
        var dup = report.Findings.Single(f => f.Id == "DUP-001");
        Assert.Equal((2, 2), (dup.Citations, dup.ValidCitations));
        Assert.Contains("ComputeDiscountedTotal", dup.Text);
        Assert.DoesNotContain("ComputeDiscountedTotal", report.Findings.Single(f => f.Id == "ORD-001").Text);
        // El párrafo llega hasta la línea en blanco: la segunda línea de GOD-001 cuenta, el «Nivel 1, arquitectura:» no define nada.
        var god = report.Findings.Single(f => f.Id == "GOD-001");
        Assert.Equal((1, 2), (god.Priority, god.Citations));
        Assert.DoesNotContain(report.Issues, i => i.Check == ReportValidationChecks.FindingIds && i.Level == ReportValidationLevels.Error);
    }

    [Fact]
    public void A_bold_definition_and_a_matrix_row_are_the_same_finding()
    {
        WriteReport("08_deuda_tecnica_hotspots.md", """
            # Matriz de Deuda Técnica

            ### Nivel 1. Arquitectura y fronteras modulares

            **DEU-001. Búsqueda de pedidos con SQL construido.** `OrderRepository.Search` concatena (`src/Orders.Core/Services/OrderService.cs:109-123`).

            **DEU-002. Credencial incrustada.** Ver `src/Orders.Core/Services/PricingService.cs:15`.

            ## Matriz esfuerzo, impacto y ROI

            | ID | Impacto | Esfuerzo | Riesgo operativo | Prioridad |
            |---|---|---:|---|---|
            | DEU-001 | Alto | 0,75 d/h | Bajo | Inmediata |
            | DEU-002 | Alto | 1,5 d/h | Medio | Inmediata |
            """);

        var report = Report(Validate(), "08_deuda_tecnica_hotspots.md");

        Assert.Equal(["DEU-001", "DEU-002"], report.Findings.Select(f => f.Id));
        Assert.All(report.Findings, f => Assert.Equal(1, f.Citations));
        Assert.Equal(0, report.Scores.DuplicateIds);
    }

    [Fact]
    public void Citations_with_several_lines_and_ranges_are_read_and_checked_range_by_range()
    {
        WriteReport("07_inventario_apis_seguridad.md", """
            # APIs

            ### SEC-001. Ruta administrativa sin autorización

            Devuelve datos personales (`src/Orders.Api/AdminEndpoints.cs:11,16-21`).

            ### SEC-002. Fuera de rango

            Dos tramos (`src/Orders.Api/AdminEndpoints.cs:11,99`) y una línea suelta (`AdminEndpoints.cs:3,7`).
            """);

        var report = Report(Validate(), "07_inventario_apis_seguridad.md");

        var ok = report.Citations.Single(c => c.Text == "src/Orders.Api/AdminEndpoints.cs:11,16-21");
        Assert.Equal(CitationStatuses.Ok, ok.Status);
        Assert.Equal((11, 21), (ok.StartLine, ok.EndLine));
        Assert.Equal([new CitationRange(11, 11), new CitationRange(16, 21)], ok.Ranges);
        Assert.Equal(CitationStatuses.OutOfRange, report.Citations.Single(c => c.Text == "src/Orders.Api/AdminEndpoints.cs:11,99").Status);
        var suffix = report.Citations.Single(c => c.Text == "AdminEndpoints.cs:3,7");
        Assert.Equal((CitationStatuses.Ok, true), (suffix.Status, suffix.BySuffix));

        var expected = new ExpectedFindingsDocument
        {
            Findings =
            [
                new ExpectedFinding { Id = "in-second-range", Report = "apis", File = "src/Orders.Api/AdminEndpoints.cs", StartLine = 20, EndLine = 21 },
                new ExpectedFinding { Id = "in-first-range", Report = "apis", File = "AdminEndpoints.cs", StartLine = 11 },
                new ExpectedFinding { Id = "in-the-gap", Report = "apis", File = "AdminEndpoints.cs", StartLine = 13, EndLine = 14, Keywords = ["personales"] }
            ]
        };

        var comparison = ExpectedFindingsComparer.Compare(Validate(), expected);

        Assert.Equal(["SEC-001"], comparison.Items[0].MatchedFindings);
        Assert.Equal(["SEC-001"], comparison.Items[1].MatchedFindings);
        Assert.False(comparison.Items[2].Found);
    }

    [Fact]
    public void An_existing_directory_and_a_struts_url_declared_in_the_config_are_not_broken_citations()
    {
        WriteReport("04_flujos_end_to_end.md", """
            # Flujos

            ### FLU-001. Listado

            Entra por `/listOrders.do` y `WebContent/WEB-INF/struts-config.xml:3`; también `/searchOrders.do`, en la
            acción de varias líneas. Código en `src/Orders.Core/` y `src/Orders.Core/Services/`.

            ### FLU-002. Rutas que no existen

            `/nope.do`, el directorio `src/Orders.Nope/` y `src/Orders.Core/Services/Nope.cs`.
            """);

        var report = Report(Validate(), "04_flujos_end_to_end.md");
        string Status(string text) => report.Citations.Single(c => c.Text == text).Status;

        Assert.Equal(CitationStatuses.Ok, Status("/listOrders.do"));
        Assert.Equal(CitationStatuses.Ok, Status("/searchOrders.do"));
        Assert.Equal("WebContent/WEB-INF/struts-config.xml", report.Citations.Single(c => c.Text == "/listOrders.do").Target);
        Assert.Equal(CitationStatuses.Ok, Status("src/Orders.Core/"));
        Assert.DoesNotContain(report.Citations, c => c.Text == "src/Orders.Core/Services/"); // sin punto en el último tramo no es una cita
        Assert.Equal(CitationStatuses.Missing, Status("/nope.do"));
        Assert.Equal(CitationStatuses.Missing, Status("src/Orders.Nope/"));
        Assert.Equal(CitationStatuses.Missing, Status("src/Orders.Core/Services/Nope.cs"));
    }

    [Fact]
    public void Not_expected_traps_report_the_findings_that_match_them_as_false_positives()
    {
        WriteReport("03_codigo_muerto.md", """
            # Código muerto

            ### DEAD-001. Exportador sin uso

            `CsvExporterImpl` no tiene llamadores (`src/com/acme/orders/export/CsvExporterImpl.java:1-20`).

            ### DEAD-002. Campo sin lecturas

            `_lastProcessedId` (`src/Orders.Core/Services/OrderService.cs:27-72`).
            """);
        WriteReport("02_code_smells.md", """
            # Smells

            **SMK-001. Sobrecargas de find.** Tres sobrecargas (`src/Orders.Core/Services/OrderService.cs:19-37`).
            """);
        var expected = ExpectedFindingsDocument.Load(WriteExpected("""
            {
              "findings": [
                { "id": "S15", "report": "deadcode", "file": "src/Orders.Core/Services/OrderService.cs", "startLine": 27, "endLine": 72 },
                { "id": "S16", "report": "smells", "keywords": ["nada"] }
              ],
              // las trampas: el comparador las lee
              "notExpected": [
                { "id": "T11-reflection", "report": "deadcode", "files": ["src/com/acme/orders/export/CsvExporterImpl.java", "src/com/acme/orders/export/XmlExporterImpl.java"] },
                { "id": "T16-overloads", "report": "smells", "file": "src/Orders.Core/Services/OrderService.cs", "startLine": 19, "endLine": 37 },
                { "id": "T-clean", "report": "deadcode", "file": "src/Orders.Api/AdminEndpoints.cs" },
                { "id": "T-bad", "report": "nope", "keywords": ["x"] },
                { "id": "T-empty", "report": "deadcode" }
              ]
            }
            """));

        var validation = Validate();
        var comparison = ExpectedFindingsComparer.Compare(validation, expected);
        validation.Expected = comparison;

        Assert.Equal((5, 2), (comparison.NotExpectedCount, comparison.FalsePositiveCount));
        Assert.Equal(["DEAD-001"], comparison.NotExpected[0].MatchedFindings);
        Assert.Equal(["SMK-001"], comparison.NotExpected[1].MatchedFindings);
        Assert.Empty(comparison.NotExpected[2].MatchedFindings);
        Assert.NotNull(comparison.NotExpected[3].Problem);
        Assert.NotNull(comparison.NotExpected[4].Problem);
        Assert.Equal(1, comparison.Found); // S15 casa con DEAD-002
        Assert.Contains(ReportValidationSummary.Describe(validation, null),
            l => l.Message.Contains("Falsos positivos") && l.Message.Contains("T11-reflection (DEAD-001)") && l.Message.Contains("T16-overloads (SMK-001)"));
    }

    private string WriteExpected(string json)
    {
        var path = Path.Combine(_temp.Path, "expected.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void The_di_prompt_receives_the_unused_di_registrations()
    {
        var templates = AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        var facets = new ReportFacetInput { UnusedDiRegistrations = "{\"marca\":\"DI-SIN-USO\"}" };

        Assert.Contains("DI-SIN-USO", templates.Render("report_di_prompt", facets)!);
        Assert.Contains("DI-SIN-USO", templates.Render("report_codigomuerto_prompt", facets)!);
    }
}
