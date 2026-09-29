using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Models;

public class EnrichmentBatchInput : StepResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string FilesBatch { get; set; } = string.Empty;
    public string ProjectLanguage { get; set; } = "C# / .NET";
    public string FrameworksDetected { get; set; } = "ASP.NET Core / Minimal APIs";
    public string CurrentTimestamp { get; set; } = DateTime.UtcNow.ToString("o");

    public EnrichmentBatchInput()
        : base(new EmptyStep(nameof(EnrichmentBatchInput)))
    {
    }

    public EnrichmentBatchInput(IStep step)
        : base(step)
    {
    }

    public EnrichmentBatchInput(IStep step, IStepError? error)
        : base(step)
    {
        Error = error;
    }
}

public class EnrichmentNodeDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("qualified_name")]
    public string QualifiedName { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = [];
}

public class EnrichmentEdgeDto
{
    [JsonPropertyName("source_qualified_name")]
    public string SourceQualifiedName { get; set; } = string.Empty;

    [JsonPropertyName("target_qualified_name")]
    public string TargetQualifiedName { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = [];
}

public class NodePropertyUpdateDto
{
    [JsonPropertyName("qualified_name")]
    public string QualifiedName { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = [];
}

public class EnrichmentBatchResultDto
{
    [JsonPropertyName("nodes")]
    public List<EnrichmentNodeDto> Nodes { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<EnrichmentEdgeDto> Edges { get; set; } = [];

    [JsonPropertyName("node_properties_updates")]
    public List<NodePropertyUpdateDto> NodePropertiesUpdates { get; set; } = [];
}
