using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Support.Template;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using System.ComponentModel;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Suspends the pipeline to ask for more information, and returns the answer as its result when the
/// run is resumed. Declarable in YAML as <c>type: SuspendStep</c>.
/// </summary>
/// <remarks>
/// <para>
/// Whoever answers may be a person or another system that calls the flow: the step only knows that
/// it needs a response. The first time it runs it returns an <see cref="AwaitingInputResult"/>
/// (<c>SuspendInfo</c> = <see cref="Marker"/>, the rendered <see cref="Message"/>, and
/// <see cref="DataToReview"/>) and the pipeline stops. Executing the pipeline again with the same
/// context and the response as input resumes it (see docs/guides/human-in-the-loop.md): the steps
/// that already finished are served from the cache, and this step returns the response as a
/// <see cref="JsonStepResult"/>, so the next step reads it with <c>{{input...}}</c>.
/// </para>
/// <para>
/// The response is JSON: a <see cref="JToken"/> (or an object) is kept as such, a string is text
/// (<c>{{input}}</c>). With <c>outputSchema</c> the response must match it; one that does not makes
/// the step suspend again with the errors in <see cref="AwaitingInputResult.RejectionReason"/>,
/// instead of failing the run.
/// </para>
/// <para>
/// It works at the root of a pipeline and inside <c>GroupStep</c>, <c>LoopStep</c> and
/// <c>SwitchStep</c>; its cache path (<see cref="PipelineResume.GetSuspendedStepPath"/>) is, for
/// instance, <c>"ask"</c>, <c>"group/ask"</c>, <c>"loop/Iter_2/ask"</c> or
/// <c>"route/route[a]/ask"</c>. Not inside a <c>ParallelStep</c> branch: a branch suspension is not
/// propagated by <c>ParallelStep</c>, so the run would carry on and the step would never be resumed
/// (known limitation, see <see cref="ParallelStep"/>).
/// </para>
/// <para>
/// The step keeps nothing in its fields between runs (the response reaches it through the context),
/// so one instance can serve concurrent runs.
/// </para>
/// </remarks>
[StepTag("SuspendStep")]
[Description("Suspends the pipeline to ask for more information (from a person or another system) and, when the run is resumed, returns the response as its result.")]
public sealed class SuspendStep : StepBase, IJsonSchemaStep
{
    /// <summary>Marker used when <see cref="Marker"/> is not set.</summary>
    public const string DefaultMarker = "awaiting-input";

    private string _marker = DefaultMarker;

    /// <summary>Parameterless ctor for YAML deserialization.</summary>
    public SuspendStep() : base("SuspendStep", typeof(IStepResult), typeof(JsonStepResult))
    {
        MaxRetries = 1;
    }

    /// <summary>Programmatic ctor.</summary>
    public SuspendStep(string name) : this()
    {
        Name = name;
    }

    /// <summary>
    /// Question or reason shown to whoever resumes the run: template text over <c>input</c> and
    /// <c>context</c> (<c>"Confirm the order {{input.id}}"</c>). Text without <c>{{</c> is literal.
    /// </summary>
    [Description("Question or reason shown to whoever resumes the run; template text over input and context.")]
    public string? Message { get; set; }

    /// <summary>
    /// What to review, as an expression over <c>input</c> and <c>context</c>: a path
    /// (<c>input.order</c>, <c>context.draft</c>) keeps the JSON value it reaches; other template text
    /// is rendered to a string. Null (the default) means the whole input value.
    /// </summary>
    [Description("What to review: a path over input and context (keeps its JSON value) or template text. Defaults to the input.")]
    public string? DataToReview { get; set; }

    /// <summary>
    /// The result's <c>SuspendInfo</c>, which tells the host what kind of wait this is. Defaults to
    /// <see cref="DefaultMarker"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    [Description("SuspendInfo of the suspended result, telling the host what kind of wait this is. Default: awaiting-input.")]
    public string Marker
    {
        get => _marker;
        set => _marker = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("'marker' is empty: leave it out to use 'awaiting-input'.", nameof(Marker))
            : value;
    }

    /// <inheritdoc/>
    /// <remarks>Not used: the step accepts any input.</remarks>
    [JsonIgnore]
    public JsonSchema? InputJsonSchema { get; set; }

    /// <inheritdoc/>
    /// <remarks>Schema the response must match. Null accepts any response.</remarks>
    [JsonIgnore]
    public JsonSchema? OutputJsonSchema { get; set; }

    /// <inheritdoc/>
    protected override JsonSchema? Schema => OutputJsonSchema;

    /// <inheritdoc/>
    protected override Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        if (!PipelineResume.TryGetResumeResponse(context, out IStepResult? response) || response is null)
        {
            return Task.FromResult(Suspend(input, context, rejectionReason: null));
        }

        JToken value = ToJson(response.Value);
        (bool isValid, string? error) = JsonStepResult.Validate(value, OutputJsonSchema);

        return Task.FromResult(isValid
            ? new JsonStepResult(this, value, OutputJsonSchema)
            : Suspend(input, context, rejectionReason: error));
    }

    private IStepResult Suspend(IStepResult input, PipelineContext context, string? rejectionReason)
    {
        string? message = Message is null ? null : TemplateExpression.Render(Message, input, context);
        object? review = DataToReview is null
            ? input.Value
            : TemplateExpression.Resolve(DataToReview, input, context);

        return new AwaitingInputResult(this, message)
        {
            SuspendInfo = Marker,
            DataToReview = review,
            RejectionReason = rejectionReason
        };
    }

    private static JToken ToJson(object? value) => value switch
    {
        null => JValue.CreateNull(),
        JToken token => token.DeepClone(),
        _ => JToken.FromObject(value)
    };
}
