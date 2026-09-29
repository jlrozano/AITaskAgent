using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Results;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Steps;

public class SynthesisInput : StepResult
{
    public string GeneratedReportsDigest { get; set; } = string.Empty;
    public string DetectedLanguages { get; set; } = "No determinado";
    public int TotalNodes { get; set; }
    public int TotalEdges { get; set; }
    public int TotalLoc { get; set; }
    public int TotalRoutes { get; set; }
    public int TotalFlows { get; set; }
    public int DeadCodeCount { get; set; }

    /// <summary>
    /// Texto y no número: cuando el informe de errores no existe o no declara el recuento, el
    /// resumen debe decir "no disponible" y no un 0 que parezca un dato.
    /// </summary>
    public string CriticalBugsCount { get; set; } = "no disponible";

    public SynthesisInput()
        : base(new EmptyStep(nameof(SynthesisInput)))
    {
    }

    public SynthesisInput(IStep step)
        : base(step)
    {
    }

    public SynthesisInput(IStep step, IStepError? error)
        : base(step)
    {
        Error = error;
    }
}

public class ExecutiveSynthesizerStep : IStep<ParallelResult, AnalysisPipelineFinalResult>
{
    public const string PipelineStartedAtKey = AnalysisContextKeys.PipelineStartedAt;

    public const string StepName = "ExecutiveSynthesizerStep";

    private const int MaxOutlineHeadings = 30;

    /// <summary>Clave de <c>analysis-meta</c> del resumen generado por código porque el modelo no respondió.</summary>
    public const string FallbackKey = "fallback";

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 500;
    /// <summary><c>CodebaseAnalysis:Timeouts:SynthesisMinutes</c>; lo fija la factoría.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(15);
    public Type InputType => typeof(ParallelResult);
    public Type OutputType => typeof(AnalysisPipelineFinalResult);

    private readonly CustomStatelessTemplateLlmStep<SynthesisInput, LlmStringStepResult> _llmStep;

    public ExecutiveSynthesizerStep(CustomStatelessTemplateLlmStep<SynthesisInput, LlmStringStepResult> llmStep)
    {
        _llmStep = llmStep;
    }

    public async Task<AnalysisPipelineFinalResult> ExecuteAsync(
        ParallelResult input,
        PipelineContext context,
        int attempt,
        AnalysisPipelineFinalResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        context.Report(StepName, AnalysisLogLevel.Info, $"📑 Consolidando informes y generando {AnalysisPipelineInput.ExecutiveReportFileName}...");

        var config = context.Get<AnalysisPipelineInput>(AnalysisContextKeys.PipelineInput) ?? new AnalysisPipelineInput();
        var outputDir = !string.IsNullOrWhiteSpace(config.OutputReportsPath)
            ? config.OutputReportsPath
            : Path.Combine(Directory.GetCurrentDirectory(), "reports");

        Directory.CreateDirectory(outputDir);

        var reports = new List<(SingleReportResult Report, string Origin)>();
        var failures = new Dictionary<ReportType, string>();
        var otherResultsSb = new StringBuilder();

        // 1. Informes de esta ejecución (ya escritos a disco por cada auditor)
        if (input.Value != null)
        {
            foreach (var kvp in input.Value)
            {
                var stepName = kvp.Key;
                var stepResult = kvp.Value;

                // Un auditor fallido no es un informe: TolerantReportsParallelStep deja seguir al
                // pipeline, y aquí se declara como no generado en lugar de sintetizar un texto vacío.
                if (stepResult.HasError)
                {
                    if (AnalysisPipelineFactory.TryParseAuditorStepName(stepName, out var failedType))
                    {
                        failures[failedType] = Summarize(stepResult.Error?.Message);
                    }
                    continue;
                }

                if (stepResult is SingleReportResult report)
                {
                    report.PopulateDerivedMetadata();
                    reports.Add((report, "esta ejecución"));
                }
                else if (stepResult is StepResult genericResult && !string.IsNullOrEmpty(genericResult.Value?.ToString()))
                {
                    otherResultsSb.AppendLine($"### {stepName}");
                    otherResultsSb.AppendLine(genericResult.Value.ToString());
                    otherResultsSb.AppendLine();
                }
            }
        }

        // 2. Informes en disco de los tipos que no se han ejecutado ahora. Sin esto, una ejecución
        //    parcial (p. ej. solo el informe de DI) regeneraba el resumen con un único informe y lo
        //    dejaba contradiciendo a los demás, que seguían en la misma carpeta.
        // Antes de que el bucle siguiente retire de failures los que tienen versión anterior en disco:
        // siguen siendo auditores fallidos en esta ejecución.
        var failedReportFiles = failures.Keys.Order().Select(AnalysisPipelineInput.GetReportFileName).ToList();
        var executedTypes = reports.Select(r => r.Report.ReportType).ToHashSet();
        foreach (var type in Enum.GetValues<ReportType>().Where(t => !executedTypes.Contains(t)))
        {
            var fileName = AnalysisPipelineInput.GetReportFileName(type);
            var path = Path.Combine(outputDir, fileName);
            if (!File.Exists(path))
                continue;

            var previous = new SingleReportResult
            {
                ReportType = type,
                ReportFileName = fileName,
                MarkdownContent = await File.ReadAllTextAsync(path, cancellationToken)
            };
            previous.PopulateDerivedMetadata();
            var origin = $"ejecución anterior ({File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})";
            if (failures.TryGetValue(type, out var failure))
            {
                origin += $"; en esta ejecución el auditor falló: {failure}";
                failures.Remove(type);
            }
            reports.Add((previous, origin));
        }

        reports.Sort((a, b) => a.Report.ReportType.CompareTo(b.Report.ReportType));
        var individualReports = reports.Select(r => r.Report).ToList();

        var digestSb = new StringBuilder();
        foreach (var (report, origin) in reports)
        {
            AppendReportDigest(digestSb, report, origin);
        }
        foreach (var (type, failure) in failures.OrderBy(f => f.Key))
        {
            digestSb.AppendLine($"### {AnalysisPipelineInput.GetReportFileName(type)} ({type})");
            digestSb.AppendLine($"- **Origen:** no generado; el auditor falló en esta ejecución y no hay informe anterior en disco. Motivo: {failure}");
            digestSb.AppendLine();
        }
        digestSb.Append(otherResultsSb);

        // 3. Métricas globales del contexto (ReportFacetInput previo)
        int totalNodes = 0, totalEdges = 0, totalLoc = 0, totalRoutes = 0, totalFlows = 0, deadCode = 0;

        var storedFacet = context.Get<ReportFacetInput>("ReportFacetInput");
        if (storedFacet != null)
        {
            totalNodes = storedFacet.TotalNodes;
            totalEdges = storedFacet.TotalEdges;
            totalLoc = storedFacet.TotalLoc;
            totalRoutes = storedFacet.TotalRoutes;
            totalFlows = storedFacet.TotalFlows;
            deadCode = storedFacet.DeadCodeCount;
        }
        else
        {
            foreach (var key in context.StepResults.Keys)
            {
                if (context.StepResults[key] is ReportFacetInput facet)
                {
                    totalNodes = facet.TotalNodes;
                    totalEdges = facet.TotalEdges;
                    totalLoc = facet.TotalLoc;
                    totalRoutes = facet.TotalRoutes;
                    totalFlows = facet.TotalFlows;
                    deadCode = facet.DeadCodeCount;
                    break;
                }
            }
        }

        string detectedLanguages = storedFacet?.DetectedLanguages ?? "No determinado";
        if (detectedLanguages == "No determinado")
        {
            foreach (var key in context.StepResults.Keys)
            {
                if (context.StepResults[key] is ReportFacetInput facet && !string.IsNullOrWhiteSpace(facet.DetectedLanguages))
                {
                    detectedLanguages = facet.DetectedLanguages;
                    break;
                }
            }
        }

        // 4. Defectos críticos: los que declara el informe de errores, no una heurística del grafo.
        var errorsReport = individualReports.FirstOrDefault(r => r.ReportType == ReportType.ErroresYBugs);
        var criticalBugs = errorsReport switch
        {
            null => "no disponible (no hay informe de errores)",
            { CriticalFindings: int count } => count.ToString(CultureInfo.InvariantCulture),
            _ => "no disponible (el informe de errores no lo declaró)"
        };

        var synthesisInput = new SynthesisInput
        {
            GeneratedReportsDigest = digestSb.ToString(),
            DetectedLanguages = detectedLanguages,
            TotalNodes = totalNodes,
            TotalEdges = totalEdges,
            TotalLoc = totalLoc,
            TotalRoutes = totalRoutes,
            TotalFlows = totalFlows,
            DeadCodeCount = deadCode,
            CriticalBugsCount = criticalBugs
        };

        // 5. Invocar LLM para generar el resumen ejecutivo
        var llmResult = await _llmStep.ExecuteStepAsync(synthesisInput, context, attempt, cancellationToken);
        var executiveMarkdown = (llmResult as LlmStringStepResult)?.Content
            ?? (llmResult as LlmStepResult<string>)?.Value
            ?? llmResult?.Value?.ToString()
            ?? string.Empty;

        var fallback = string.IsNullOrWhiteSpace(executiveMarkdown);
        if (fallback)
        {
            executiveMarkdown = GenerateFallbackExecutiveSummary(individualReports, failures, synthesisInput);
        }
        executiveMarkdown = ReportMarkdown.RemoveFileNameHeading(executiveMarkdown);

        // Procedencia (recomendación 12), como en los informes; sin respuesta del modelo el resumen lo escribe el código.
        var provenance = _llmStep.Provenance(context, llmResult).ToList();
        if (fallback)
            provenance.Insert(1, new(FallbackKey, "true"));
        executiveMarkdown = ReportMarkdown.SetMetaValues(executiveMarkdown, provenance);

        // 6. Guardar archivo maestro
        var executivePath = Path.Combine(outputDir, AnalysisPipelineInput.ExecutiveReportFileName);
        await File.WriteAllTextAsync(executivePath, executiveMarkdown, cancellationToken);

        var executiveMeta = new SingleReportResult { MarkdownContent = executiveMarkdown };
        executiveMeta.PopulateDerivedMetadata();
        context.ReportWritten(StepName, executivePath, reportType: null, executiveMeta.QualityHealth, executiveMeta.CriticalFindings,
            truncated: llmResult is ILlmStepResult { FinishReason: FinishReason.Length });

        // Plan v2 §8.1.4: los informes del catálogo marcados para el grafo vuelven con el contenido nuevo.
        if (!string.IsNullOrWhiteSpace(config.DatabasePath) && !string.IsNullOrWhiteSpace(config.RepositoryPath))
        {
            var knowledge = new Knowledge.KnowledgePaths(config.RepositoryPath, outputDir);
            var restored = await Knowledge.Graph.KnowledgeGraphIngestor.ReincorporateAsync(config.DatabasePath, knowledge,
                new HashSet<Knowledge.SourceKind> { Knowledge.SourceKind.Report }, cancellationToken);
            context.SetMetadata(AnalysisContextKeys.CatalogReportsRetired, false);
            if (restored > 0)
                context.Report(StepName, AnalysisLogLevel.Info, $"🧩 Reincorporados al grafo {restored} informes del análisis.");
        }

        context.Report(StepName, AnalysisLogLevel.Success, $"🎉 ¡Pipeline finalizado con éxito! Dashboard guardado en: {executivePath}");

        // El cronómetro vivía en el step y arrancaba al construir el pipeline, no al ejecutarlo.
        var duration = context.Get<DateTimeOffset?>(PipelineStartedAtKey) is { } startedAt
            ? DateTimeOffset.UtcNow - startedAt
            : TimeSpan.Zero;

        return new AnalysisPipelineFinalResult
        {
            ExecutiveReportPath = executivePath,
            IndividualReports = individualReports,
            FailedReports = failedReportFiles,
            Duration = duration
        };
    }

    /// <summary>
    /// Error en una línea y acotado. Un HTTP 400 trae el JSON entero de la respuesta, cuya primera
    /// línea es solo "HTTP 400: {": se usa su campo "message" si lo tiene.
    /// </summary>
    private static string Summarize(string? message)
    {
        message ??= "error sin mensaje";
        var firstLine = message.Split('\n')[0].Trim();
        var apiMessage = Regex.Match(message, "\"message\"\\s*:\\s*\"(?<m>[^\"]+)\"");
        if (apiMessage.Success)
        {
            firstLine = $"{firstLine.TrimEnd('{', ' ')} {apiMessage.Groups["m"].Value}";
        }

        return firstLine.Length > 200 ? firstLine[..197] + "..." : firstLine;
    }

    private static void AppendReportDigest(StringBuilder sb, SingleReportResult report, string origin)
    {
        sb.AppendLine($"### {report.ReportFileName} ({report.ReportType})");
        sb.AppendLine($"- **Origen:** {origin}");
        sb.AppendLine($"- **Semáforo de Salud:** {GetHealthEmoji(report.QualityHealth)} {report.QualityHealth}");
        if (report.CriticalFindings is int critical)
        {
            sb.AppendLine($"- **Defectos críticos declarados (P0+P1):** {critical}");
        }
        sb.AppendLine($"- **Síntesis Ejecutiva:** {report.ExecutiveSummarySnippet}");

        // El fragmento de resumen no basta para que la síntesis vea los hallazgos concretos: el
        // índice de secciones del informe los expone sin mandar el documento entero.
        var outline = ExtractOutline(report.MarkdownContent);
        if (outline.Count > 0)
        {
            sb.AppendLine("- **Estructura del informe:**");
            foreach (var heading in outline)
            {
                sb.AppendLine(heading);
            }
        }

        sb.AppendLine();
    }

    private static List<string> ExtractOutline(string markdown)
    {
        var outline = new List<string>();
        var inFence = false;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
                continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                outline.Add($"  - {line[3..].Trim()}");
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                outline.Add($"    - {line[4..].Trim()}");
            }

            if (outline.Count >= MaxOutlineHeadings)
                break;
        }

        return outline;
    }

    private static string GetHealthEmoji(string health) => health?.ToLowerInvariant() switch
    {
        "red" => "🔴",
        "amber" => "🟡",
        "green" => "🟢",
        _ => "⚪"
    };

    private static string GenerateFallbackExecutiveSummary(List<SingleReportResult> reports, Dictionary<ReportType, string> failures, SynthesisInput metrics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 00. Resumen Ejecutivo y Dashboard Maestro de Arquitectura");
        sb.AppendLine();
        sb.AppendLine("> [!NOTE]");
        sb.AppendLine("> Generado automáticamente por AITaskFramework Codebase Analysis Pipeline.");
        sb.AppendLine();
        sb.AppendLine("## 1. Semáforo de Calidad y Salud Técnica");
        sb.AppendLine();
        sb.AppendLine("| # | Área de Auditoría | Archivo Entregable | Semáforo | Hallazgos Clave |");
        sb.AppendLine("|---|---|---|:---:|---|");

        foreach (var r in reports)
        {
            var emoji = GetHealthEmoji(r.QualityHealth);
            sb.AppendLine($"| {((int)r.ReportType + 1)} | **{r.ReportType}** | [`{r.ReportFileName}`](./{r.ReportFileName}) | {emoji} {r.QualityHealth} | {r.ExecutiveSummarySnippet.Replace("\n", " ")} |");
        }

        foreach (var (type, failure) in failures.OrderBy(f => f.Key))
        {
            var fileName = AnalysisPipelineInput.GetReportFileName(type);
            sb.AppendLine($"| {((int)type + 1)} | **{type}** | `{fileName}` (no generado) | ⚪ Unknown | El auditor falló: {failure.Replace("|", "/")} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 2. Métricas Consolidadas del Repositorio");
        sb.AppendLine($"- **Total Nodos en Grafo:** `{metrics.TotalNodes}`");
        sb.AppendLine($"- **Total Aristas / Relaciones:** `{metrics.TotalEdges}`");
        sb.AppendLine($"- **Líneas de Código Estimadas (LOC):** `{metrics.TotalLoc}`");
        sb.AppendLine($"- **Rutas HTTP Expuestas:** `{metrics.TotalRoutes}`");
        sb.AppendLine($"- **Flujos de Negocio Mapeados:** `{metrics.TotalFlows}`");
        sb.AppendLine($"- **Candidatos a Código Muerto:** `{metrics.DeadCodeCount}`");
        sb.AppendLine($"- **Defectos / Puntos Críticos (P0/P1):** `{metrics.CriticalBugsCount}`");
        sb.AppendLine();
        sb.AppendLine("## 3. Top 5 Acciones Prioritarias Recomendadas");
        sb.AppendLine("1. Revisar y mitigar las rutas y puntos de falla críticos identificados.");
        sb.AppendLine("2. Depurar elementos de código muerto confirmados para reducir la superficie de mantenimiento.");
        sb.AppendLine("3. Refactorizar los hotspots y métodos con complejidad ciclomática elevada.");
        sb.AppendLine("4. Saneamiento de dependencias circulares y aislamiento de capas arquitectónicas.");
        sb.AppendLine("5. Revisar la configuración de ciclo de vida (lifetimes) en la inyección de dependencias IoC.");

        return sb.ToString();
    }

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is AnalysisPipelineFinalResult, (string?)null));
}
