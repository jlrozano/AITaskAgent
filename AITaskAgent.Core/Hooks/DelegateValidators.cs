using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace AITaskAgent.Core.Hooks;

/// <summary>An <see cref="IStepValidator"/> backed by a delegate (a method of a package unit, a lambda).</summary>
/// <remarks>An exception thrown by the delegate is wrapped in <see cref="StepHookException"/> so the retry middleware ends the step instead of retrying.</remarks>
public sealed class DelegateValidator(string id, StepBase step, Func<ValidateArgs, Task<(bool IsValid, string? Error)>> validate) : IStepValidator
{
    public string Id { get; } = id;

    public async Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await validate(new ValidateArgs(step, result, context, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StepHookException)
        {
            throw new StepHookException(id, step.Name, ex);
        }
    }
}

/// <summary>An <see cref="IResultValidator"/> backed by a delegate. Exceptions are wrapped like <see cref="DelegateValidator"/>'s.</summary>
public sealed class DelegateResultValidator(string id, string stepName, Func<ResultValidateArgs, Task<(bool IsValid, string? Error)>> validate) : IResultValidator
{
    public string Id { get; } = id;

    public async Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await validate(new ResultValidateArgs(result, context, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not StepHookException)
        {
            throw new StepHookException(id, stepName, ex);
        }
    }
}

/// <summary>Built-in result validator <c>schema:name</c>: the value must satisfy a registered JSON schema.</summary>
public sealed class SchemaResultValidator(string id, JsonSchema schema) : IResultValidator
{
    public string Id { get; } = id;

    public Task<(bool IsValid, string? Error)> ValidateAsync(IStepResult result, PipelineContext context, CancellationToken cancellationToken)
    {
        JToken? value = result.Value switch
        {
            null => null,
            JToken token => token,
            string text => TryParse(text),
            var other => JToken.FromObject(other),
        };
        return Task.FromResult(JsonStepResult.Validate(value, schema));
    }

    private static JToken TryParse(string text)
    {
        try
        {
            return JToken.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new JValue(text);
        }
    }
}
