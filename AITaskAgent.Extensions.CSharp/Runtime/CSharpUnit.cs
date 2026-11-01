using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Hooks;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;

namespace AITaskAgent.CSharp;

/// <summary>
/// A compiled and loaded package unit: the methods of its <c>CSharpStep</c>s, its hooks and validators and its response types.
/// Each instance owns a collectible <see cref="AssemblyLoadContext"/> (static state of the author's code is per instance);
/// disposing it unloads the context. Nothing of the author's code runs while loading: methods are only bound, never called.
/// </summary>
public sealed class CSharpUnit : ICSharpUnit, IDisposable
{
    private readonly Dictionary<string, CSharpStepMethod> _steps;
    private AssemblyLoadContext? _context;

    private CSharpUnit(string hash, AssemblyLoadContext? context, Dictionary<string, CSharpStepMethod> steps, List<UnitHook> hooks, List<UnitResponseType> responseTypes)
    {
        Hash = hash;
        _context = context;
        _steps = steps;
        Hooks = hooks;
        ResponseTypes = responseTypes;
    }

    /// <summary>The hash of the sources this unit was compiled from.</summary>
    public string Hash { get; }

    /// <summary>Weak handle on the load context (tests check that disposing the unit lets it be collected).</summary>
    internal WeakReference ContextReference => new(_context);

    /// <summary>The hooks, validators and result validators of the unit, by <c>Class.Method</c>.</summary>
    public IReadOnlyList<UnitHook> Hooks { get; }

    /// <summary>The classes marked <see cref="ResponseTypeAttribute"/> with their schemas.</summary>
    public IReadOnlyList<UnitResponseType> ResponseTypes { get; }

    /// <summary>The ids of the steps the unit has a method for.</summary>
    public IReadOnlyCollection<string> StepIds => _steps.Keys;

    /// <summary>A unit with nothing in it (a package with no C#).</summary>
    public static CSharpUnit Empty { get; } = new(string.Empty, null, [], [], []);

    /// <inheritdoc/>
    public bool TryGetStep(string stepId, out CSharpStepMethod method) => _steps.TryGetValue(stepId, out method!);

    /// <summary>Unloads the context. Safe to call twice; a step still running keeps its code until it ends.</summary>
    public void Dispose()
    {
        AssemblyLoadContext? context = Interlocked.Exchange(ref _context, null);
        context?.Unload();
    }

    internal static CSharpUnit Load(string hash, byte[] assembly, byte[] symbols, IEnumerable<string>? runtimeAssemblies = null, IEnumerable<string>? nativeDirectories = null, Func<string, bool>? isShared = null)
    {
        AssemblyLoadContext context = new UnitLoadContext("csharp-unit-" + hash[..Math.Min(12, hash.Length)], runtimeAssemblies ?? [], nativeDirectories ?? [], isShared);
        try
        {
            Assembly loaded = context.LoadFromStream(new MemoryStream(assembly), new MemoryStream(symbols));
            Dictionary<string, CSharpStepMethod> steps = new(StringComparer.Ordinal);
            List<UnitHook> hooks = [];
            List<UnitResponseType> responseTypes = [];

            foreach (Type type in loaded.GetExportedTypes())
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<StepMethodAttribute>() is { } step)
                    {
                        steps[step.StepId] = (CSharpStepMethod)Delegate.CreateDelegate(typeof(CSharpStepMethod), method);
                        continue;
                    }

                    if (method.GetCustomAttribute<HookAttribute>() is { } hook)
                    {
                        hooks.Add(new UnitHook(string.IsNullOrWhiteSpace(hook.Name) ? NameOf(method) : hook.Name.Trim(), HookKind.Hook, Bind(method), hook.Description));
                    }
                    else if (method.GetCustomAttribute<ValidatorAttribute>() is { } validator)
                    {
                        hooks.Add(new UnitHook(NameOf(method), HookKind.StepValidator, Bind(method), validator.Description));
                    }
                    else if (method.GetCustomAttribute<ResultValidatorAttribute>() is { } result)
                    {
                        hooks.Add(new UnitHook(NameOf(method), HookKind.ResultValidator, Bind(method), result.Description));
                    }
                }

                if (type.GetCustomAttribute<ResponseTypeAttribute>() is { } response)
                {
                    responseTypes.Add(new UnitResponseType(response.Name ?? type.Name, type, ResponseSchemaBuilder.Build(type)));
                }
            }

            return new CSharpUnit(hash, context, steps, hooks, responseTypes);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    /// <summary><c>Class.Method</c> (nested classes as <c>Outer.Inner.Method</c>; namespaces are not part of the name).</summary>
    private static string NameOf(MethodInfo method)
    {
        List<string> parts = [method.Name];
        for (Type? type = method.DeclaringType; type != null; type = type.DeclaringType)
        {
            parts.Insert(0, type.Name);
        }

        return string.Join('.', parts);
    }

    /// <summary>The method as a delegate of its own shape (<c>Func</c>/<c>Action</c> when it fits): the registry rebinds it to the slot it is named under.</summary>
    private static Delegate Bind(MethodInfo method)
    {
        Type[] types = [.. method.GetParameters().Select(p => p.ParameterType), method.ReturnType];
        return method.CreateDelegate(Expression.GetDelegateType(types));
    }
}
