using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// A placeholder step used for error results and similar cases.
/// </summary>
public sealed class EmptyStep : IStep
{
    public string Name { get; }

    public Type InputType => typeof(void);

    public Type OutputType => typeof(void);

    public TimeSpan? Timeout => null;

    public int MaxRetries => 1;

    /// <summary>
    /// Creates an EmptyStep with default name.
    /// </summary>
    public EmptyStep() => Name = "EmptyStep";

    /// <summary>
    /// Creates an EmptyStep with custom name.
    /// </summary>
    public EmptyStep(string name) => Name = name;

    public Task FinalizeAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int Attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
        => Task.FromResult(input);

    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
        => Task.FromResult((true, (string?)null));

}
