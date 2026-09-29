using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace CodebaseAnalysis.Core.Models;

public class GraphStatusResult : StepResult
{
    public bool ExistedBefore { get; set; }
    public bool IsEnriched { get; set; }
    public int TotalNodes { get; set; }
    public int TotalEdges { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public Dictionary<string, object?> Facets { get; set; } = [];

    public GraphStatusResult()
        : base(new EmptyStep(nameof(GraphStatusResult)))
    {
    }

    public GraphStatusResult(IStep step)
        : base(step)
    {
    }

    public GraphStatusResult(IStep step, IStepError? error)
        : base(step)
    {
        Error = error;
    }
}
