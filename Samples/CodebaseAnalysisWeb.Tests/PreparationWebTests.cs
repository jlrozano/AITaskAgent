using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;
using CodebaseAnalysis.Core.Tests.Enrichment;
using CodebaseAnalysisWeb.Analysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>
/// La preparación y la investigación por subsistema a través de la web, con el pipeline real (<see cref="PipelineAnalysisWorkflow"/>)
/// y un LLM de mentira: un análisis lanzado por <c>POST /api/analysis</c> deja los cuatro <c>reports/_*.json</c> y los informes en
/// modo B con su apartado «Cobertura», y la regeneración de un informe en modo B por <c>POST /api/analysis/{runId}/regenerate</c>
/// reutiliza el mapa de dominios del disco. Mismo repositorio sintético y mismo guion que <c>PreparationPipelineTests</c> de Core
/// (aquí lo que se prueba es el cableado de la web: el host, el gestor de ejecuciones, el registro de eventos y los endpoints).
/// </summary>
public sealed class PreparationWebTests : IDisposable
{
    private static readonly string[] Capabilities = ["parcel", "ledger", "catalog"];
    private static readonly string[] Kinds = ["Service", "Policy", "Store", "Entry", "Record", "Action"];
    private static readonly string[] PreparationArtifactNames =
        [RepositoryInventory.FileName, DomainMap.FileName, CoveragePlan.FileName, TimeBudgetPlan.FileName];
    private static readonly ReportType[] ModeB = [.. CoveragePlanner.ModeBReports];

    private readonly TempDirectory _repo = new();
    private readonly PreparationLlm _llm = new();
    private readonly WebTestFactory _factory;

    public PreparationWebTests()
    {
        // Todos los roles del pipeline al proveedor falso (en la configuración de la web son los reales, con claves que aquí no hay).
        const string llm = LlmRoleFactory.SectionName;
        var roles = LlmRoleFactory.RequiredRoles.ToDictionary(r => $"{llm}:Roles:{r}:Provider", _ => (string?)"Fake");
        foreach (var role in LlmRoleFactory.RequiredRoles)
            roles[$"{llm}:Roles:{role}:Model"] = "fake-model";
        roles["CodebaseAnalysis:Timeouts:PipelineMinutes"] = "0";

        _factory = new WebTestFactory
        {
            CustomLlm = _llm,
            ExtraConfiguration = roles,
            ConfigureServices = services =>
            {
                services.RemoveAll<IAnalysisWorkflow>();
                services.AddSingleton<IAnalysisWorkflow, PipelineAnalysisWorkflow>();
            }
        };
    }

    public void Dispose()
    {
        _factory.Dispose();
        _repo.Dispose();
    }

    private string ReportsPath => AnalysisRunOptions.ResolveReportsPath(_factory.Services.GetRequiredService<IConfiguration>(), _repo.Path);

    private string ReportText(ReportType type) => File.ReadAllText(Path.Combine(ReportsPath, AnalysisPipelineInput.GetReportFileName(type)));

    private T Read<T>() where T : class, IPreparationArtifact
    {
        var artifact = PreparationArtifacts.TryRead<T>(ReportsPath, out var warning);
        Assert.Null(warning);
        return artifact!;
    }

    /// <summary>
    /// Tres capacidades de seis tipos cada una (ficheros C# reales en disco y su grafo ya enriquecido) en la BD que la web usará
    /// para este repositorio: el paso de detección reutiliza un grafo ya indexado, así que no hace falta cbm.
    /// </summary>
    private async Task SeedRepositoryAsync()
    {
        var dbPath = AnalysisRunOptions.ResolveDatabasePath(_factory.Services.GetRequiredService<IConfiguration>(), _repo.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var graph = await CbmTestGraph.CreateAsync(dbPath, "demo");
        var enrichedMark = true;
        foreach (var capability in Capabilities)
        {
            var methods = new List<long>();
            foreach (var kind in Kinds)
            {
                var name = capability + kind;
                var file = $"src/main/csharp/app/{capability}/{name}.cs";
                var path = Path.Combine(_repo.Path, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllLinesAsync(path, Enumerable.Range(1, 30).Select(i => $"// {name} {i}"));
                var type = await graph.NodeAsync("Class", name, $"Demo.{name}", file, 1, 30, enrichedMark ? "{\"cyclomatic\":3}" : "{}");
                enrichedMark = false;
                var method = await graph.NodeAsync("Method", "Run", $"Demo.{name}.Run", file, 3, 9);
                await graph.EdgeAsync(type, method, "DEFINES_METHOD");
                methods.Add(method);
            }
            for (var i = 0; i < methods.Count; i++)
            {
                for (var j = i + 1; j < methods.Count; j++)
                    await graph.EdgeAsync(methods[i], methods[j], "CALLS");
            }
        }
    }

    private async Task<(HttpClient Client, string RunId)> RunAnalysisAsync()
    {
        var client = _factory.CreateClient();
        await client.OpenWorkspaceAsync(_repo.Path);
        await SeedRepositoryAsync();
        var runId = await client.StartRunAsync();
        var final = await client.WaitForRunEndAsync(TimeSpan.FromSeconds(120));
        Assert.Equal("succeeded", final.GetProperty("status").GetString());
        Assert.Equal(0, final.GetProperty("failedReports").GetArrayLength());
        return (client, runId);
    }

    [Fact]
    public async Task An_analysis_through_the_web_leaves_the_four_artifacts_and_the_mode_b_reports_carry_the_coverage_section()
    {
        var (client, runId) = await RunAnalysisAsync();

        foreach (var name in PreparationArtifactNames)
            Assert.True(File.Exists(Path.Combine(ReportsPath, name)), $"falta {name}");
        var map = Read<DomainMap>();
        Assert.Equal(["catalog", "ledger", "parcel"], map.Subsystems.Select(s => s.Id));
        var plan = Read<CoveragePlan>();
        Assert.Equal(ModeB, plan.Reports.Select(r => r.Report));
        Assert.All(plan.Reports, r => Assert.Equal(3, r.Chosen.Count));
        Assert.Equal(9, Read<TimeBudgetPlan>().ReportTimeouts.Count);

        // Una llamada de nombrado, tres investigaciones por informe en modo B, cuatro consolidaciones y cinco auditores en modo A.
        Assert.Equal(1, _llm.Count(PreparationLlm.Kind.Naming));
        Assert.Equal(4 * 3, _llm.Count(PreparationLlm.Kind.Investigation));
        Assert.Equal(4, _llm.Count(PreparationLlm.Kind.ConsolidationB));
        Assert.Equal(5, _llm.Count(PreparationLlm.Kind.Auditor));
        foreach (var type in ModeB)
        {
            var text = ReportText(type);
            Assert.Contains("## Cobertura", text);
            Assert.All(["Capacidad parcel", "Capacidad ledger", "Capacidad catalog"], name => Assert.Contains(name, text));
        }
        foreach (var type in Enum.GetValues<ReportType>().Except(ModeB))
            Assert.DoesNotContain("## Cobertura", ReportText(type));

        // El registro de eventos de la ejecución lleva los pasos de preparación en la fase de facetas y cada investigación en el carril de su informe.
        using var stream = await client.GetSseAsync($"/api/analysis/{runId}/events?after=0");
        var events = await stream.ReadAllSseAsync();
        foreach (var step in new[] { "RepositoryInventoryStep", "DomainDiscoveryStep", "CoveragePlanningStep", "TimeBudgetStep" })
        {
            var started = events.Single(e => Type(e) == "step.started" && Source(e) == step);
            Assert.Equal("facets", started.Data.GetProperty("phase").GetString());
            Assert.Contains(events, e => Type(e) == "step.completed" && Source(e) == step);
        }
        // Las investigaciones no pasan por el middleware (las lanza el envoltorio con ExecuteStepAsync): su progreso es un log del
        // envoltorio «Auditor_<tipo>», en el carril de su informe, y sus eventos del LLM llevan el nombre «Investigation_<tipo>_<id>».
        var investigating = events.Where(e => Type(e) == "log" && Source(e).StartsWith("Auditor_", StringComparison.Ordinal)
            && e.Data.GetProperty("message").GetString()!.Contains("Investigando '", StringComparison.Ordinal)).ToList();
        Assert.Equal(4 * 3, investigating.Count);
        Assert.All(investigating, e =>
        {
            Assert.Equal("reports", e.Data.GetProperty("phase").GetString());
            Assert.Equal(Source(e)["Auditor_".Length..], e.Data.GetProperty("report").GetString());
            Assert.Contains(Enum.Parse<ReportType>(e.Data.GetProperty("report").GetString()!), ModeB);
        });
        var fromInvestigations = events.Where(e => Source(e).StartsWith("Investigation_", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(fromInvestigations);
        Assert.All(fromInvestigations, e =>
        {
            Assert.Equal("reports", e.Data.GetProperty("phase").GetString());
            Assert.StartsWith($"Investigation_{e.Data.GetProperty("report").GetString()}_", Source(e), StringComparison.Ordinal);
        });
        Assert.Equal(runId, events[0].Data.GetProperty("runId").GetString());
        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => e.Data.GetProperty("seq").GetInt32()));
    }

    [Fact]
    public async Task Regenerating_a_mode_b_report_through_the_endpoint_reuses_the_domain_map_from_disk()
    {
        var (client, runId) = await RunAnalysisAsync();
        var mapPath = Path.Combine(ReportsPath, DomainMap.FileName);
        var mapBefore = await File.ReadAllTextAsync(mapPath);
        Assert.Equal(1, _llm.Count(PreparationLlm.Kind.Naming));
        var investigationsBefore = _llm.Count(PreparationLlm.Kind.Investigation);
        var consolidationsBefore = _llm.Count(PreparationLlm.Kind.ConsolidationB);

        var response = await client.PostAsync($"/api/analysis/{runId}/regenerate", Http.JsonBody(new { reports = new[] { "funcional" } }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var final = await client.WaitForRunEndAsync(TimeSpan.FromSeconds(120));
        Assert.Equal("succeeded", final.GetProperty("status").GetString());
        // Sin volver a nombrar: el mapa es el del disco, intacto; el informe se investiga igual por cada subsistema y se consolida.
        Assert.Equal(1, _llm.Count(PreparationLlm.Kind.Naming));
        Assert.Equal(mapBefore, await File.ReadAllTextAsync(mapPath));
        Assert.Equal(investigationsBefore + 3, _llm.Count(PreparationLlm.Kind.Investigation));
        Assert.Equal(consolidationsBefore + 1, _llm.Count(PreparationLlm.Kind.ConsolidationB));
        var text = ReportText(ReportType.AnalisisFuncional);
        Assert.Contains("## Cobertura", text);
        Assert.All(["Capacidad parcel", "Capacidad ledger", "Capacidad catalog"], name => Assert.Contains(name, text));
        Assert.Equal(ModeB, Read<CoveragePlan>().Reports.Select(r => r.Report));
    }

    private static string Type(SseEvent e) => e.Data.GetProperty("type").GetString()!;

    private static string Source(SseEvent e) =>
        e.Data.TryGetProperty("source", out var source) && source.ValueKind == System.Text.Json.JsonValueKind.String ? source.GetString()! : "";

    /// <summary>
    /// Contesta según lo que se le pide, por el texto de las plantillas reales: nombra cada <c>### id</c> como «Capacidad id», entrega
    /// notas con una cita por investigación y, en la consolidación de un informe en modo B (la que trae el resumen de cobertura),
    /// escribe un informe con su apartado «Cobertura» con los subsistemas que le llegan. Es el guion de <c>PreparationPipelineTests</c>.
    /// </summary>
    private sealed class PreparationLlm : ILlmService
    {
        public enum Kind { Naming, Investigation, ConsolidationB, Auditor, Synthesis }

        private readonly Lock _lock = new();
        private readonly List<Kind> _calls = [];

        public string Name => "prep-web-fake";

        public int Count(Kind kind)
        {
            lock (_lock)
            {
                return _calls.Count(c => c == kind);
            }
        }

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var user = request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content ?? "";
            var system = request.SystemPrompt ?? request.Conversation.History.Messages.FirstOrDefault(m => m.Role == LlmConstants.MessageRoles.System)?.Content ?? "";
            var kind = Classify(system, user);
            lock (_lock)
            {
                _calls.Add(kind);
            }

            var answer = kind switch
            {
                Kind.Naming => Naming(user),
                Kind.Investigation => Investigation(user),
                Kind.ConsolidationB => Report(user),
                _ => "# Informe\n\n## Resumen\n\nContenido del servicio falso.\n\n<!-- analysis-meta: health=Green -->"
            };
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = answer, FinishReason = FinishReason.Stop }],
                tokensUsed: 10, promptTokens: 5, completionTokens: 5, model: "fake-model"));
        }

        private static Kind Classify(string system, string user) =>
            system.Contains("Analista de Dominios", StringComparison.Ordinal) ? Kind.Naming
            : user.StartsWith("Investiga un subsistema", StringComparison.Ordinal) ? Kind.Investigation
            : user.StartsWith("Genera el documento consolidado", StringComparison.Ordinal) ? Kind.Synthesis
            : user.Contains("Subsistemas investigados (", StringComparison.Ordinal) ? Kind.ConsolidationB
            : Kind.Auditor;

        private static string Naming(string user) => string.Concat(
            user.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)).Select(l => l[4..].Trim())
                .Where(id => id.Length > 0 && !id.Contains('<') && !id.Contains(' '))
                .Select(id => $"### {id}\nNombre: Capacidad {id}\nDescripción: Descripción de {id}.\nPrioridad: alta\n\n"));

        private static string Investigation(string user)
        {
            var name = Regex.Match(user, "- \\*\\*Nombre\\*\\*: `(?<n>[^`]+)`").Groups["n"].Value;
            var id = name.Replace("Capacidad ", "");
            return $"Subsistema: {name}\nQué hace: flujo completo en `src/main/csharp/app/{id}/{id}ServiceFile.cs:3`.";
        }

        private static string Report(string user)
        {
            var text = new StringBuilder("# Informe\n\n## Resumen\n\nContenido consolidado.\n\n## Cobertura\n\n");
            foreach (Match match in Regex.Matches(user, @"^- (?<n>.+?) \(id: [^)]+\), \d+ líneas", RegexOptions.Multiline))
                text.Append("- ").AppendLine(match.Groups["n"].Value);
            return text.Append("\n<!-- analysis-meta: health=Green -->").ToString();
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }
}
