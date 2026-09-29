using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// Primer paso de la regeneración de informes (<see cref="AnalysisPipelineFactory.CreateRegenerationPipeline"/>): hace lo que
/// la detección deja listo para las facetas y los auditores, pero sobre el grafo que ya existe, sin indexar, ingerir ni
/// enriquecer. Pone en el contexto las mismas claves que <see cref="DetectAndIndexGraphStep"/>, comprueba que hay grafo,
/// asegura el esquema y retira del grafo los informes del catálogo, que vuelven en la síntesis o en el <c>finally</c>
/// del pipeline. Va en la fase <c>facets</c>.
/// </summary>
public sealed class PrepareRegenerationStep : IStep<AnalysisPipelineInput, GraphStatusResult>
{
    public const string StepName = "PrepareRegenerationStep";

    public string Name => StepName;
    public IResume? Resume => null;
    public int MaxRetries => 1;
    public int MiillisecondsBetweenRetries => 500;
    public TimeSpan? Timeout => TimeSpan.FromMinutes(5);
    public Type InputType => typeof(AnalysisPipelineInput);
    public Type OutputType => typeof(GraphStatusResult);

    public async Task<GraphStatusResult> ExecuteAsync(
        AnalysisPipelineInput input,
        PipelineContext context,
        int attempt,
        GraphStatusResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        context.Set(AnalysisContextKeys.PipelineInput, input);
        context.Set(AnalysisContextKeys.CbmDbPath, input.DatabasePath);
        context.Set(AnalysisContextKeys.KnowledgePaths, new Knowledge.KnowledgePaths(input.RepositoryPath, input.OutputReportsPath));

        var reports = string.Join(", ", input.RequestedReports);
        context.Report(StepName, AnalysisLogLevel.Info, $"♻️ Regenerando {input.RequestedReports.Count} informe(s) sobre el grafo existente: {reports}.");

        // Sin grafo no hay nada que auditar: la regeneración no indexa, así que se para aquí con un mensaje que diga qué hacer.
        if (string.IsNullOrWhiteSpace(input.DatabasePath) || !File.Exists(input.DatabasePath))
        {
            throw new InvalidOperationException(
                $"[{StepName}] No existe la base de datos del grafo '{input.DatabasePath}'. La regeneración trabaja sobre el grafo " +
                "de un análisis anterior: lanza un análisis completo.");
        }

        if (!await DetectAndIndexGraphStep.HasStructuralGraphAsync(input.DatabasePath, cancellationToken))
        {
            throw new InvalidOperationException(
                $"[{StepName}] La base de datos '{input.DatabasePath}' no contiene un grafo estructural (ni tipos ni funciones o " +
                "métodos). La regeneración trabaja sobre el grafo de un análisis anterior: lanza un análisis completo.");
        }

        await GraphSchema.EnsureAsync(input.DatabasePath, cancellationToken);

        var retired = await DetectAndIndexGraphStep.RetireCatalogReportsAsync(context, input, cancellationToken);
        if (retired > 0)
            context.Report(StepName, AnalysisLogLevel.Info, "🧩 Retirados del grafo los informes del análisis anterior hasta que se regeneren.");

        var (totalNodes, totalEdges) = await DetectAndIndexGraphStep.GetGraphCountsAsync(input.DatabasePath, cancellationToken);
        return new GraphStatusResult
        {
            ExistedBefore = true,
            IsEnriched = await DetectAndIndexGraphStep.CheckIfEnrichedAsync(input.DatabasePath, cancellationToken),
            TotalNodes = totalNodes,
            TotalEdges = totalEdges,
            DatabasePath = input.DatabasePath
        };
    }

    public Task FinalizeAsync(IStepResult input, PipelineContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((result is GraphStatusResult, (string?)null));
}
