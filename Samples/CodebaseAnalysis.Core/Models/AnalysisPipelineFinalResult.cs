using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace CodebaseAnalysis.Core.Models;

public class AnalysisPipelineFinalResult : StepResult
{
    public string ExecutiveReportPath { get; set; } = string.Empty;
    public List<SingleReportResult> IndividualReports { get; set; } = [];

    /// <summary>Ficheros de los informes cuyo auditor falló en esta ejecución.</summary>
    public List<string> FailedReports { get; set; } = [];
    public TimeSpan Duration { get; set; }
    public bool Success => !HasError && !string.IsNullOrEmpty(ExecutiveReportPath);

    public AnalysisPipelineFinalResult()
        : base(new EmptyStep(nameof(AnalysisPipelineFinalResult)))
    {
    }

    public AnalysisPipelineFinalResult(IStep step)
        : base(step)
    {
    }

    public AnalysisPipelineFinalResult(IStep step, IStepError? error)
        : base(step)
    {
        Error = error;
    }
}
