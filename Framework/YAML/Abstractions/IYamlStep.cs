namespace AITaskAgent.YAML.Abstractions;

/// <summary>
/// Marker interface for steps that can be deserialized from YAML.
/// YAML steps have explicit StepId, InputSchema, and OutputSchema properties.
/// </summary>
public interface IYamlStep
{
    string StepId { get; init; }
    string InputSchema { get; init; }
    string OutputSchema { get; init; }
    string[]? DependsOn { get; init; }
}
