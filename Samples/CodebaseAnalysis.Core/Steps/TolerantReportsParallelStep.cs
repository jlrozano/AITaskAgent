using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using CodebaseAnalysis.Core.Progress;

namespace CodebaseAnalysis.Core.Steps;

/// <summary>
/// <see cref="ParallelStep"/> que no detiene el pipeline cuando falla un auditor. El framework marca
/// el resultado como error en cuanto falla una rama, y entonces la síntesis no se ejecutaba aunque
/// los demás informes estuvieran en disco. Aquí se quita ese error agregado; cada rama conserva el
/// suyo en <see cref="ParallelResult.Value"/>, y <see cref="ExecutiveSynthesizerStep"/> lo lee para
/// declarar el informe como no generado. Los reintentos de cada auditor no cambian, porque el error
/// se retira después de que la rama haya terminado.
/// </summary>
public sealed class TolerantReportsParallelStep : ParallelStep
{
    public TolerantReportsParallelStep(string name, params IStep[] branches)
        : base(name, branches)
    {
        InputType = typeof(StepResult);
    }

    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        var result = await base.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);

        if (result is ParallelResult { HasError: true } parallel)
        {
            var failed = parallel.Value.Count(kvp => kvp.Value.HasError);
            if (failed == parallel.Value.Count)
            {
                // Sin ningún informe no hay nada que sintetizar: se mantiene el error.
                return result;
            }

            context.Report(Name, AnalysisLogLevel.Warning, $"⚠️ {failed} auditor(es) fallaron; la síntesis sigue con los informes disponibles.");
            parallel.Error = null;
        }

        return result;
    }
}
