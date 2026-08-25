namespace AITaskAgent.YAML.Abstractions;

/// <summary>
/// Marker interface for steps that can be deserialized from YAML.
/// YAML steps have explicit StepId, InputSchema, and OutputSchema properties.
/// </summary>
public interface IYamlStep
{
    string StepId { get; init; }

    /// <summary>
    /// Schema name for the input type. Nullable because <c>StepBase</c> already declares it for
    /// every step; <c>YamlPipelineFactory</c> rejects a YAML step that leaves it unset.
    /// </summary>
    string? InputSchema { get; init; }

    /// <summary>
    /// Schema name for the output type. Same contract as <see cref="InputSchema"/>.
    /// </summary>
    string? OutputSchema { get; init; }

    string[]? DependsOn { get; init; }
}
