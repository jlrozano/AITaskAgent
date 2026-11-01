using AITaskAgent.Core.Steps;
using System.ComponentModel;
using System.Reflection;
using System.Text;

namespace AITaskAgent.Core.Hooks;

/// <summary>A <c>hooks:</c> slot of a step type.</summary>
/// <param name="Name">camelCase slot name (the YAML key under <c>hooks:</c>).</param>
/// <param name="DelegateType">Delegate type of the slot's property.</param>
/// <param name="Signature">
/// The signature a hook for the slot is written with, as text, for messages and the designer: the "after" form (<see cref="AfterType"/>) when
/// the slot has one, otherwise <paramref name="DelegateType"/>.
/// </param>
/// <param name="Description">Human text.</param>
/// <param name="VirtualMethod">Virtual method of the step that dispatches it, or null for call-site (notification) hooks.</param>
public sealed record HookSlot(string Name, Type DelegateType, string Signature, string? Description, string? VirtualMethod)
{
    /// <summary>
    /// The "after" form the slot also binds (<c>Func&lt;TArgs, Task&gt;</c>, <see cref="HookForms.AfterTypeOf"/>), or null when it binds only
    /// <see cref="DelegateType"/>. Additive (2026-10-23).
    /// </summary>
    public Type? AfterType { get; init; }

    /// <summary>Every delegate type a hook of the slot may have: the after form first (when there is one), then the property's.</summary>
    public IReadOnlyList<Type> AcceptedTypes => AfterType is { } after ? [after, DelegateType] : [DelegateType];

    /// <summary>The arguments type of the slot's delegate (its first parameter), or null for a delegate without parameters.</summary>
    public Type? ArgsType => DelegateType.GetMethod("Invoke")?.GetParameters() is [var first, ..] ? first.ParameterType : null;
}

/// <summary>Slot metadata and binding of <see cref="StepHooks"/> (and subtypes) from named delegates.</summary>
public static class HookSlots
{
    /// <summary>The <see cref="StepHooks"/> type a step type binds its hooks to.</summary>
    public static Type HooksTypeFor(Type stepType)
        => stepType.GetCustomAttribute<HooksTypeAttribute>(inherit: true)?.HooksType ?? typeof(StepHooks);

    /// <summary>Slots a step type accepts under <c>hooks:</c>. Empty for types that do not derive from <see cref="StepBase"/>.</summary>
    public static IReadOnlyList<HookSlot> For(Type stepType)
    {
        if (!typeof(StepBase).IsAssignableFrom(stepType))
        {
            return [];
        }

        return [.. HooksTypeFor(stepType).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(Delegate).IsAssignableFrom(p.PropertyType))
            .Select(p =>
            {
                Type? after = HookForms.AfterTypeOf(p.PropertyType);
                return new HookSlot(
                    char.ToLowerInvariant(p.Name[0]) + p.Name[1..],
                    p.PropertyType,
                    Describe(after ?? p.PropertyType),
                    p.GetCustomAttribute<DescriptionAttribute>()?.Description,
                    p.GetCustomAttribute<HookVirtualAttribute>()?.MethodName) { AfterType = after };
            })];
    }

    /// <summary>The hooks set in <paramref name="hooks"/>, by slot name (camelCase).</summary>
    public static IEnumerable<KeyValuePair<string, Delegate>> Bound(StepHooks? hooks)
    {
        if (hooks == null)
        {
            yield break;
        }

        foreach (PropertyInfo property in hooks.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (typeof(Delegate).IsAssignableFrom(property.PropertyType) && property.GetValue(hooks) is Delegate hook)
            {
                yield return KeyValuePair.Create(char.ToLowerInvariant(property.Name[0]) + property.Name[1..], hook);
            }
        }
    }

    /// <summary>Builds the hooks record of <paramref name="stepType"/> from slot name to delegate (already of the slot's type).</summary>
    public static StepHooks Create(Type stepType, IReadOnlyDictionary<string, Delegate> bound)
    {
        Type hooksType = HooksTypeFor(stepType);
        StepHooks hooks = (StepHooks)Activator.CreateInstance(hooksType)!;
        foreach ((string slot, Delegate hook) in bound)
        {
            PropertyInfo property = hooksType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Single(p => string.Equals(p.Name, slot, StringComparison.OrdinalIgnoreCase));
            property.SetValue(hooks, hook);
        }

        return hooks;
    }

    /// <summary>Delegate signature as text, e.g. <c>Func&lt;FinalizeArgs, Task&gt;</c>.</summary>
    public static string Describe(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        StringBuilder sb = new(type.Name[..type.Name.IndexOf('`')]);
        sb.Append('<').Append(string.Join(", ", type.GetGenericArguments().Select(Describe))).Append('>');
        return sb.ToString();
    }

    /// <summary>
    /// R3: for each bound slot that is dispatched by a virtual method, checks the most derived declaration
    /// of that method in <paramref name="stepType"/>. An override outside the framework assemblies may skip
    /// <c>base</c>, in which case the hook silently does not run; whether it calls base cannot be known
    /// without IL, so this only warns.
    /// </summary>
    public static IReadOnlyList<string> FindOverrideWarnings(Type stepType, IEnumerable<string> slotNames)
    {
        List<string> warnings = [];
        Dictionary<string, HookSlot> slots = For(stepType).ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (string slotName in slotNames)
        {
            if (!slots.TryGetValue(slotName, out HookSlot? slot) || slot.VirtualMethod is not { } method)
            {
                continue;
            }

            for (Type? t = stepType; t != null && t != typeof(object); t = t.BaseType)
            {
                MethodInfo? declared = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == method);
                if (declared == null)
                {
                    continue;
                }

                bool isOverride = declared.GetBaseDefinition().DeclaringType != t;
                if (isOverride && !IsFrameworkType(t))
                {
                    warnings.Add(
                        $"Hook '{slotName}' of a {stepType.Name} runs only if '{t.Name}.{method}' calls base: " +
                        "that override is not part of the framework.");
                }

                break;
            }
        }

        return warnings;
    }

    private static readonly HashSet<string> FrameworkAssemblies = new(StringComparer.Ordinal)
    {
        "AITaskAgent.Abstractions", "AITaskAgent.Core", "AITaskAgent.Extensions.Llm", "AITaskAgent.Yaml", "AITaskAgent.Extensions.Script",
    };

    /// <summary>True for the steps of the framework's own assemblies, whose overrides the R1 test reviews.</summary>
    internal static bool IsFrameworkType(Type type)
        => FrameworkAssemblies.Contains(type.Assembly.GetName().Name ?? "");
}
