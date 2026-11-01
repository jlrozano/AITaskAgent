using System.Collections.Concurrent;
using System.Reflection;

namespace AITaskAgent.Core.Hooks;

/// <summary>What a named hook is for; the YAML key that may reference it depends on it.</summary>
public enum HookKind
{
    /// <summary>A slot of <c>hooks:</c> (post-process, finalize, enrich, LLM hooks...).</summary>
    Hook,

    /// <summary>An entry of <c>validators:</c>: semantic validation of a step result (what <c>IStepValidator</c> is).</summary>
    StepValidator,

    /// <summary>An entry of <c>resultValidators:</c>: the response's own contract, run as part of <c>IStepResult.ValidateAsync</c>.</summary>
    ResultValidator,
}

/// <summary>Description of a registered hook, for diagnostics and the designer.</summary>
/// <param name="Name">Registry name (unique across kinds).</param>
/// <param name="Kind">What it is.</param>
/// <param name="DelegateType">Delegate type the hook was registered with.</param>
/// <param name="Description">Human text, optional.</param>
/// <param name="Source">Where it comes from: <c>unit:&lt;package&gt;</c>, <c>di</c> or <c>host</c>.</param>
public sealed record HookInfo(string Name, HookKind Kind, Type DelegateType, string? Description, string Source);

/// <summary>
/// Named delegates a YAML step can reference from <c>hooks:</c>, <c>validators:</c> and
/// <c>resultValidators:</c>. A name is unique across kinds and sources: a second registration of the
/// same name is an error, never a silent shadowing.
/// </summary>
public interface IHookRegistry
{
    /// <summary>
    /// Finds <paramref name="name"/> with the given kind and returns it as <paramref name="delegateType"/>
    /// (a compatible delegate is rebound with the usual delegate variance). False when the name does not
    /// exist, has another kind or its signature does not fit; <see cref="List"/> tells which.
    /// </summary>
    bool TryGet(string name, HookKind kind, Type delegateType, out Delegate? hook);

    /// <summary>Every registered hook.</summary>
    IReadOnlyCollection<HookInfo> List();
}

/// <summary>Default, thread-safe <see cref="IHookRegistry"/>: the host (or an extension) calls <see cref="Add"/>.</summary>
public sealed class HookRegistry : IHookRegistry
{
    private readonly ConcurrentDictionary<string, (HookInfo Info, Delegate Hook)> _entries = new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="hook"/> under <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException">The name is already registered.</exception>
    public HookRegistry Add(string name, HookKind kind, Delegate hook, string? description = null, string source = "host")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(hook);

        HookInfo info = new(name.Trim(), kind, hook.GetType(), description, source);
        if (!_entries.TryAdd(info.Name, (info, hook)))
        {
            HookInfo first = _entries[info.Name].Info;
            throw new InvalidOperationException(
                $"Hook name '{info.Name}' is already registered ({first.Kind} from '{first.Source}'); " +
                $"a name cannot be registered twice (second source: '{source}').");
        }

        return this;
    }

    /// <inheritdoc/>
    public bool TryGet(string name, HookKind kind, Type delegateType, out Delegate? hook)
    {
        hook = null;
        if (name is null || !_entries.TryGetValue(name, out var entry) || entry.Info.Kind != kind)
        {
            return false;
        }

        Delegate registered = entry.Hook;
        if (delegateType.IsInstanceOfType(registered))
        {
            hook = registered;
            return true;
        }

        // Variance-compatible rebinding (a method taking a base args type, for instance).
        MethodInfo method = registered.Method;
        try
        {
            hook = Delegate.CreateDelegate(delegateType, registered.Target, method, throwOnBindFailure: false);
        }
        catch (ArgumentException)
        {
            hook = null;
        }

        return hook != null;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<HookInfo> List() => [.. _entries.Values.Select(e => e.Info).OrderBy(i => i.Name, StringComparer.Ordinal)];
}
