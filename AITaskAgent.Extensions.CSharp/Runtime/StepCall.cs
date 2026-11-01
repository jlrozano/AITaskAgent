using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.CSharp;

/// <summary>
/// What the generated method of a <c>CSharpStep</c> receives: the same things a <c>DelegatedStep</c> gets (the step, the
/// previous result, the <see cref="PipelineContext"/>, the attempt, the last result) plus the services of the run, the
/// cancellation token and helpers to build the result. The body of the step must return an <see cref="IStepResult"/>.
/// </summary>
public sealed class StepCall
{
    internal StepCall(
        CSharpStep step, IStepResult input, PipelineContext context, int attempt, IStepResult? lastResult, IServiceProvider services, CancellationToken cancellationToken)
    {
        Step = step;
        Input = input;
        Context = context;
        Attempt = attempt;
        LastResult = lastResult;
        Services = services;
        CancellationToken = cancellationToken;
    }

    /// <summary>The step being run.</summary>
    public CSharpStep Step { get; }

    /// <summary>The id of the step.</summary>
    public string StepName => Step.Name;

    /// <summary>The previous step's result.</summary>
    public IStepResult Input { get; }

    /// <summary>The pipeline's context (variables, conversation, services).</summary>
    public PipelineContext Context { get; }

    /// <summary>The attempt (1 on the first).</summary>
    public int Attempt { get; }

    /// <summary>The result of the previous attempt of this step, when retried.</summary>
    public IStepResult? LastResult { get; }

    /// <summary>The services of the run.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Cancelled when the run is cancelled or the step times out.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The previous result's value as <typeparamref name="T"/> (JSON values are converted with <c>ToObject</c>); default when it is null.</summary>
    public T? InputAs<T>() => Convert<T>(Input.Value);

    /// <summary>A context variable as <typeparamref name="T"/>; default when it is missing.</summary>
    public T? Get<T>(string key) => Context.Metadata.TryGetValue(key, out object? value) ? Convert<T>(value) : default;

    /// <summary>Sets a context variable.</summary>
    public void Set(string key, object? value) => Context.Metadata[key] = value is null or JToken ? value : JToken.FromObject(value);

    /// <summary>A successful result holding <paramref name="value"/> as JSON.</summary>
    public IStepResult Ok(object? value = null) => new JsonStepResult(Step, ToToken(value));

    /// <summary>A successful result that also asks the pipeline to run <paramref name="nextSteps"/> after this step.</summary>
    public IStepResult Ok(object? value, IEnumerable<IStep> nextSteps)
    {
        ArgumentNullException.ThrowIfNull(nextSteps);
        IStepResult result = Ok(value);
        result.NextSteps.AddRange(nextSteps);
        return result;
    }

    /// <summary>An error result.</summary>
    public IStepResult Fail(string message, Exception? exception = null)
        => exception == null ? ErrorStepResult.FromMessage(Step, message) : ErrorStepResult.FromException(Step, new InvalidOperationException(message, exception));

    /// <summary>
    /// Builds a step of the registry (<paramref name="tag"/> as in the YAML <c>type</c>) with its YAML properties as
    /// <paramref name="config"/>, named <paramref name="id"/>: what <see cref="IStepResult.NextSteps"/> takes.
    /// </summary>
    public IStep CreateStep(string tag, string id, JObject? config = null)
    {
        IStep step = Services.GetRequiredService<IStepRegistry>().Create(tag, config, Services);
        if (step is StepBase)
        {
            // The setter is protected internal (the YAML factory names steps this way): reflection is how code outside the framework does it.
            typeof(StepBase).GetProperty(nameof(StepBase.Name))!.GetSetMethod(nonPublic: true)!.Invoke(step, [id]);
        }

        return step;
    }

    /// <summary>Writes to the step's log.</summary>
    public void Log(string message, LogLevel level = LogLevel.Information)
        => Services.GetService<ILoggerFactory>()?.CreateLogger($"CSharpStep.{StepName}").Log(level, "{Message}", message);

    private static JToken ToToken(object? value) => value switch
    {
        null => JValue.CreateNull(),
        JToken token => token,
        _ => JToken.FromObject(value),
    };

    private static T? Convert<T>(object? value) => value switch
    {
        null => default,
        T typed => typed,
        JToken token => token.ToObject<T>(),
        _ => JToken.FromObject(value).ToObject<T>(),
    };
}
