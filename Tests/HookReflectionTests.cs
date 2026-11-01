using AITaskAgent.Core.Hooks;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Steps;
using AITaskAgent.YAML.Steps;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AITaskAgent.Tests;

/// <summary>
/// R1: a framework step that overrides a virtual method a hook rides on must either call <c>base</c> (the
/// default implementation dispatches the hook) or call the dispatcher the base uses. Otherwise a YAML
/// <c>hooks:</c> entry would silently do nothing for that step. A new override fails this test until it is reviewed.
/// </summary>
public sealed class HookReflectionTests
{
    private static readonly Assembly[] FrameworkAssemblies =
    [
        typeof(StepBase).Assembly, typeof(BaseLlmStep).Assembly, typeof(YamlLlmStep).Assembly,
    ];

    private static HashSet<string> HookableVirtuals() =>
        [.. HookSlots.For(typeof(StepBase)).Concat(HookSlots.For(typeof(BaseLlmStep)))
            .Where(s => s.VirtualMethod != null).Select(s => s.VirtualMethod!)];

    private static List<MethodInfo> Overrides()
    {
        HashSet<string> names = HookableVirtuals();
        return [.. FrameworkAssemblies.Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(StepBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => names.Contains(m.Name) && m.IsVirtual && m.GetBaseDefinition().DeclaringType != m.DeclaringType)];
    }

    [Fact]
    public void Every_hookable_virtual_the_framework_overrides_dispatches_its_hook()
    {
        List<MethodInfo> overrides = Overrides();

        // The scan must see the overrides this design was reviewed against, or it proves nothing.
        string[] reviewed =
        [
            "YamlLlmStep.BuildUserMessageAsync", "YamlLlmStep.BuildSystemMessageAsync", "YamlLlmStep.ParseLlmResponseAsync",
            "TemplateLlmStep.BuildUserMessageAsync", "ExecStep.EnrichStartedEvent",
        ];
        string[] found = [.. overrides.Select(m => $"{m.DeclaringType!.Name.Split('`')[0]}.{m.Name}")];
        foreach (string expected in reviewed)
        {
            Assert.Contains(expected, found);
        }

        List<string> offenders = [];
        foreach (MethodInfo method in overrides)
        {
            bool dispatches = CalledMethods(method).Any(called =>
                called.Name == method.Name && called.DeclaringType != method.DeclaringType
                || called.Name.StartsWith("<>n__", StringComparison.Ordinal)   // the compiler's stub for `await base.X()`
                || called.Name is "DispatchUserMessageHook" or "DispatchSystemMessageHook");
            if (!dispatches)
            {
                offenders.Add($"{method.DeclaringType!.Name}.{method.Name}");
            }
        }

        Assert.True(offenders.Count == 0, "Overrides that neither call base nor a hook dispatcher: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_slots_name_virtuals_that_exist_on_their_step_type()
    {
        foreach (HookSlot slot in HookSlots.For(typeof(BaseLlmStep)).Where(s => s.VirtualMethod != null))
        {
            MethodInfo? method = typeof(BaseLlmStep).GetMethod(slot.VirtualMethod!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(method is { IsVirtual: true }, $"{slot.Name} -> {slot.VirtualMethod} is not a virtual of BaseLlmStep");
        }
    }

    /// <summary>Methods called from <paramref name="method"/> (or from its async state machine), read from the IL.</summary>
    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        List<MethodBase> bodies = [method];
        if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } machine
            && machine.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } moveNext)
        {
            bodies.Add(moveNext);
        }

        foreach (MethodBase body in bodies)
        {
            byte[]? il = body.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                continue;
            }

            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] is not (0x28 or 0x6F))   // call, callvirt
                {
                    continue;
                }

                MethodBase? target = null;
                try
                {
                    target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null, null);
                }
                catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
                {
                    // Not a token: the byte pair was part of another instruction.
                }

                if (target != null)
                {
                    yield return target;
                }
            }
        }
    }
}
