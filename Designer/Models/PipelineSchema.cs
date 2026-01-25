namespace AITaskAgent.Designer.Models;

using AITaskAgent.Core.Abstractions;
using AITaskAgent.Designer.Core;
using Newtonsoft.Json.Linq;
using NJsonSchema;

/// <summary>
/// Defines a pipeline configuration that can be built into executable steps.
/// Equivalent to BRMS ValidationSchema.
/// </summary>
public class PipelineSchema
{
private List<JObject> _steps = [];
private readonly Dictionary<string, IStep> _compiledSteps = [];

/// <summary>
/// Unique identifier for this pipeline.
/// </summary>
public required string PipelineId { get; init; }

/// <summary>
/// Display name for the pipeline.
/// </summary>
public string? Name { get; init; }

/// <summary>
/// JSON Schema for pipeline input data.
/// </summary>
public JsonSchema? InputSchema { get; init; }

/// <summary>
/// JSON Schema for pipeline output data.
/// </summary>
public JsonSchema? OutputSchema { get; init; }

/// <summary>
/// Step configurations to execute in order.
/// </summary>
public IReadOnlyList<JObject> Steps
{
get => _steps.AsReadOnly();
init
{
_steps = (value == null) ? [] : value.Select(x =>
{
x["_id"] = Guid.NewGuid().ToString();
return x;
}).ToList();
}
}

/// <summary>
/// Compiled steps ready for execution.
/// </summary>
internal IReadOnlyList<IStep> CompiledSteps => [.. _compiledSteps.Values];

/// <summary>
/// Builds the pipeline by compiling all step configurations into instances.
/// Equivalent to BRMS ValidationSchema.Build().
/// </summary>
/// <returns>List of errors encountered during build.</returns>
public List<string> Build()
{
var errors = new List<string>();
_compiledSteps.Clear();

foreach (JObject stepConfig in Steps)
{
var (error, step) = CompileStep(stepConfig);
if (error != null)
{
errors.Add(error);
}
else if (step != null)
{
_compiledSteps.Add(stepConfig["_id"]!.ToString(), step);
}
}

return errors;
}

/// <summary>
/// Gets the compiled steps as a list for pipeline execution.
/// </summary>
public List<IStep> GetCompiledSteps() => [.. _compiledSteps.Values];

private static (string? Error, IStep? Step) CompileStep(JObject stepConfig)
{
if (!stepConfig.TryGetValue("_id", out JToken? token) || token == null)
{
stepConfig["_id"] = Guid.NewGuid().ToString();
}

var stepId = stepConfig["stepId"]?.ToString()?.Trim();
var name = stepConfig["name"]?.ToString()?.Trim();

if (string.IsNullOrEmpty(stepId))
{
return ("All steps must have a 'stepId' property.", null);
}

if (!StepManager.IsRegistered(stepId))
{
return ($"Step '{stepId}' is not registered. Is it loaded?", null);
}

var step = StepManager.GetStep(stepId, stepConfig);
if (step == null)
{
return ($"Could not create step '{stepId}'.", null);
}

return (null, step);
}
}
