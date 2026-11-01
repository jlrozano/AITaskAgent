using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Support.Template;
using System.ComponentModel;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Configurable switch: evaluates <see cref="On"/> and runs the steps of the matching entry of
/// <see cref="Cases"/>, or <see cref="Default"/>. It is the declarative (YAML) face of
/// <see cref="SwitchStep{TIn}"/>, whose execution it reuses: the chosen steps are wrapped in a
/// <see cref="GroupStep"/> and handed to a <see cref="SwitchStep{TIn}"/> as its target, so routing,
/// the routing event and the <c>NextSteps</c> mechanics are the switch's own.
/// </summary>
/// <remarks>
/// Case keys are compared with the evaluated value ignoring case and surrounding whitespace. No
/// match and no <see cref="Default"/> is an error (the step throws and the pipeline reports it).
/// The input passes through unchanged to the chosen steps.
/// </remarks>
[StepTag("SwitchStep")]
[Description("Runs the steps of the case whose key equals the value of 'on' (a template expression over input and context), or 'default'.")]
public sealed class DeclarativeSwitchStep : StepBase
{
    private readonly Dictionary<string, GroupStep> _targets = new(StringComparer.Ordinal);
    private readonly Lock _targetsLock = new();

    public DeclarativeSwitchStep() : base("SwitchStep", typeof(IStepResult), typeof(IStepResult))
    {
        MaxRetries = 1;
    }

    /// <summary>
    /// Expression whose value selects the case: a <see cref="TemplateExpression"/> such as
    /// <c>input.category</c> or <c>{{ context.mode }}</c>.
    /// </summary>
    [Description("Template expression whose value selects the case, e.g. input.category.")]
    public string On { get; set; } = string.Empty;

    /// <summary>Steps to run for each value of <see cref="On"/>.</summary>
    public Dictionary<string, List<IStep>> Cases { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Steps to run when no case matches. Null makes an unmatched value an error.</summary>
    public List<IStep>? Default { get; init; }

    /// <inheritdoc/>
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt,
        IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        SwitchStep<IStepResult> router = new(Name, (value, ctx) => (SelectTarget(value, ctx), value));
        return await ((IStep)router).ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
    }

    private GroupStep SelectTarget(IStepResult input, PipelineContext context)
    {
        if (string.IsNullOrWhiteSpace(On))
        {
            throw new InvalidOperationException($"Switch '{Name}' has no 'on' expression.");
        }

        string value = TemplateExpression.Evaluate(On, input, context);

        string? key = Cases.Keys.FirstOrDefault(k => string.Equals(k.Trim(), value, StringComparison.OrdinalIgnoreCase));
        if (key != null)
        {
            return GetTarget($"{Name}[{key.Trim()}]", Cases[key]);
        }

        if (Default != null)
        {
            return GetTarget($"{Name}[default]", Default);
        }

        string available = Cases.Count == 0 ? "(none)" : string.Join(", ", Cases.Keys);
        throw new InvalidOperationException(
            $"Switch '{Name}': value '{value}' of '{On}' matches no case ({available}) and there is no default.");
    }

    private GroupStep GetTarget(string name, List<IStep> steps)
    {
        lock (_targetsLock)
        {
            if (!_targets.TryGetValue(name, out GroupStep? group))
            {
                group = new GroupStep(name, steps);
                _targets[name] = group;
            }

            return group;
        }
    }
}
