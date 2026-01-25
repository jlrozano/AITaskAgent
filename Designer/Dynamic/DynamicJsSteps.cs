namespace AITaskAgent.Designer.Dynamic;

using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Designer.Scripting;
using Newtonsoft.Json.Linq;

/// <summary>
/// Base class for dynamically generated JavaScript action steps.
/// Equivalent to BRMS DynamicJsScriptValidator/DynamicJsScriptNormalizer.
/// </summary>
public abstract class DynamicJsActionStep : IStep, IDynamicStep, IDisposable
{
    private JsEngine? _engine;
    private DynamicStepConfiguration? _config;
    private JObject? _parameters;

    public string Name { get; private set; } = "DynamicStep";
    public string? Expression { get; private set; }
    public Type InputType => typeof(IStepResult);
    public Type OutputType => typeof(ScriptStepResult);

    protected DynamicJsActionStep() { }

    public void Configure(DynamicStepConfiguration config, JObject? values)
    {
        _config = config;
        _parameters = values;
        Name = config.DisplayName ?? config.StepId;
        Expression = config.Expression;
    }

    public async Task<IStepResult> ExecuteAsync(
    IStepResult input,
    PipelineContext context,
    int attempt,
    IStepResult? lastStepResult,
    CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Expression))
        {
            return ScriptStepResult.WithError(this, "No expression configured for dynamic step.");
        }

        return await Task.Run(() =>
        {
            try
            {
                _engine ??= new JsEngine();

                var inputData = PrepareInputData(input, context);
                var result = _engine.Execute(Expression!, inputData, PrepareParameters());

                if (!result.Success)
                {
                    return ScriptStepResult.WithError(this, result.ErrorMessage ?? "Script execution failed", result.Exception);
                }

                return ProcessResult(result);
            }
            catch (Exception ex)
            {
                return ScriptStepResult.WithError(this, $"Dynamic step failed: {ex.Message}", ex);
            }
        }, cancellationToken);
    }

    protected virtual JObject PrepareInputData(IStepResult input, PipelineContext context)
    {
        return new JObject
        {
            ["value"] = input.Value != null ? JToken.FromObject(input.Value) : JValue.CreateNull(),
            ["hasError"] = input.HasError,
            ["stepName"] = input.Step?.Name,
            ["correlationId"] = context.CorrelationId
        };
    }

    protected virtual Dictionary<string, object?> PrepareParameters()
    {
        var parameters = new Dictionary<string, object?>();

        if (_parameters != null)
        {
            foreach (var prop in _parameters.Properties())
            {
                parameters[prop.Name] = prop.Value.ToObject<dynamic>();
            }
        }

        return parameters;
    }

    protected virtual IStepResult ProcessResult(ScriptExecutionResult result)
    {
        return ScriptStepResult.Success(this, result.Result);
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Base class for dynamically generated JavaScript transform steps.
/// Transforms input data and returns modified output.
/// </summary>
public abstract class DynamicJsTransformStep : DynamicJsActionStep
{
    protected override IStepResult ProcessResult(ScriptExecutionResult result)
    {
        if (result.Result is JObject jObj)
        {
            return ScriptStepResult.Success(this, jObj);
        }

        return ScriptStepResult.Success(this, result.Result);
    }
}

/// <summary>
/// Base class for dynamically generated JavaScript validator steps.
/// Returns success/failure based on script boolean result.
/// </summary>
public abstract class DynamicJsValidatorStep : DynamicJsActionStep
{
    protected override IStepResult ProcessResult(ScriptExecutionResult result)
    {
        if (result.Result is bool isValid)
        {
            if (isValid)
                return ScriptStepResult.Success(this, true);
            else
                return ScriptStepResult.WithError(this, "Validation failed.");
        }

        var isTruthy = result.Result != null &&
            result.Result is not false &&
            result.Result is not 0 &&
            result.Result is not "";

        if (isTruthy)
            return ScriptStepResult.Success(this, true);

        return ScriptStepResult.WithError(this, "Validation returned falsy value.");
    }
}
