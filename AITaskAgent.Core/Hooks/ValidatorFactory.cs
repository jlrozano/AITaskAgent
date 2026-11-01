using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;

namespace AITaskAgent.Core.Hooks;

/// <summary>
/// Builds the validators whose YAML name starts with <see cref="Prefix"/> (<c>js:orders.checkTotal</c>): names no registry lists because they
/// are resolved against something only the extension knows (the JavaScript libraries of the run, for <c>js:</c>). Registered in DI; the YAML
/// binder asks the factory of a name's prefix before the hook registry. One instance is built per step and name.
/// </summary>
/// <remarks>A name the factory cannot read throws <see cref="InvalidOperationException"/> with the reason; the binder adds the step and the key.</remarks>
public interface IValidatorFactory
{
    /// <summary>The prefix of the names it builds, colon included (<c>js:</c>). Unique among the registered factories; never <c>schema:</c>.</summary>
    string Prefix { get; }

    /// <summary>A step validator (YAML <c>validators</c>) named <paramref name="name"/> (prefix included) for <paramref name="step"/>.</summary>
    IStepValidator CreateValidator(string name, StepBase step);

    /// <summary>A result validator (YAML <c>resultValidators</c>) named <paramref name="name"/> (prefix included) for <paramref name="step"/>.</summary>
    IResultValidator CreateResultValidator(string name, StepBase step);
}

/// <summary>
/// Builds the hooks whose YAML name (under <c>hooks:</c>) starts with <see cref="Prefix"/> (<c>js:orders.stamp</c>), the counterpart of
/// <see cref="IValidatorFactory"/> for slots (architecture §18.8). Registered in DI; the YAML binder asks it before the hook registry. One
/// delegate is built per step, slot and name. Additive (2026-10-23).
/// </summary>
/// <remarks>A name or a slot the factory cannot serve throws <see cref="InvalidOperationException"/> with the reason; the binder adds the step and the key.</remarks>
public interface IHookFactory
{
    /// <summary>The prefix of the names it builds, colon included (<c>js:</c>). Unique among the registered factories; never <c>schema:</c>.</summary>
    string Prefix { get; }

    /// <summary>
    /// A hook named <paramref name="name"/> (prefix included) for <paramref name="slot"/> of <paramref name="step"/>: a delegate of one of
    /// <see cref="HookSlot.AcceptedTypes"/> (the binder adapts the after form, as for a registered hook).
    /// </summary>
    Delegate CreateHook(string name, StepBase step, HookSlot slot);
}
