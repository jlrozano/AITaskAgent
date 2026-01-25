namespace AITaskAgent.Designer.Scripting;

using System.ComponentModel;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Designer.Attributes;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

/// <summary>
/// A step that executes JavaScript code to transform or process data.
/// Equivalent to BRMS JsScriptRule/JsScriptNormalizer.
/// </summary>
[StepId("JsScript")]
[StepCategory("Script")]
[Description("Executes JavaScript code to transform pipeline data.")]
public class JsScriptStep : IStep, IDisposable
{
    private JsEngine? _engine;
    private readonly ILogger<JsScriptStep>? _logger;

    /// <summary>
    /// The JavaScript expression or function body to execute.
    /// Available variables: input (previous step result), context (pipeline context data).
    /// </summary>
    [Description("JavaScript expression to execute. Use 'input' for previous step result.")]
    public required string Expression { get; init; }

    /// <summary>
    /// Name of this step for observability.
    /// </summary>
    public string Name { get; init; } = "JsScriptStep";

    /// <summary>
    /// Timeout for script execution.
    /// </summary>
    public TimeSpan ScriptTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Input type - always IStepResult.
    /// </summary>
    public Type InputType => typeof(IStepResult);

    /// <summary>
    /// Output type - always StepResult.
    /// </summary>
    public Type OutputType => typeof(ScriptStepResult);

    public JsScriptStep() { }

    public JsScriptStep(ILogger<JsScriptStep> logger)
    {
        _logger = logger;
    }

    public async Task<IStepResult> ExecuteAsync(
    IStepResult input,
    PipelineContext context,
    int attempt,
    IStepResult? lastStepResult,
    CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                _engine ??= new JsEngine(ScriptTimeout);

                // Prepare input data
                var inputData = new JObject
                {
                    ["value"] = input.Value != null ? JToken.FromObject(input.Value) : JValue.CreateNull(),
                    ["hasError"] = input.HasError,
                    ["stepName"] = input.Step?.Name
                };

                // Prepare context data
                var contextData = new JObject
                {
                    ["correlationId"] = context.CorrelationId
                };

                // Execute script
                var result = _engine.Execute(Expression, inputData, new Dictionary<string, object?>
                {
                    ["pipelineContext"] = contextData.ToObject<dynamic>()
                });

                if (!result.Success)
                {
                    _logger?.LogError("JsScriptStep '{Name}' failed: {Error}", Name, result.ErrorMessage);
                    return ScriptStepResult.WithError(this, result.ErrorMessage ?? "Unknown script error", result.Exception);
                }

                _logger?.LogDebug("JsScriptStep '{Name}' completed. Console: {Console}",
        Name, string.Join(", ", result.ConsoleOutput));

                // Return result
                return ScriptStepResult.Success(this, result.Result);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "JsScriptStep '{Name}' threw exception", Name);
                return ScriptStepResult.WithError(this, $"Script execution failed: {ex.Message}", ex);
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Error from script execution.
/// </summary>
public record ScriptStepError(string Message, Exception? OriginalException = null) : IStepError;
