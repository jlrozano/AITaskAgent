using AITaskAgent.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AITaskAgent.Script.Hosting;

/// <summary>
/// A method of a script object (<see cref="RegisteredScriptObject"/>, registered with <c>AddScriptObject</c>) as a host function: scripts call it
/// as <c>host.&lt;object&gt;.&lt;method&gt;(args)</c>, with the method's name in camelCase. Registering the object is the grant: unlike the other
/// host functions it needs no <see cref="ScriptHostOptions.GrantedFunctions"/> entry (the extension that registers it is code the host already
/// runs); a <c>ScriptStep</c> still lists it in <c>allow</c>, and a JavaScript validator may call every script object method.
/// </summary>
/// <remarks>
/// The call is a JSON round trip, like every host function: the argument is converted to the method's parameter type with
/// <c>JToken.ToObject</c> and the return value back with <c>JToken.FromObject</c>; the script never holds the .NET object. The instance is created
/// once per service provider (the run's), by the registration's factory or by <c>ActivatorUtilities.CreateInstance</c>.
/// </remarks>
public sealed class ScriptObjectFunction : IScriptHostFunction
{
    private static readonly ConditionalWeakTable<IServiceProvider, Dictionary<RegisteredScriptObject, object>> Instances = [];

    private readonly RegisteredScriptObject _registration;
    private readonly ParameterInfo? _data;
    private readonly ParameterInfo[] _parameters;

    internal ScriptObjectFunction(RegisteredScriptObject registration, MethodInfo method, string methodName)
    {
        _registration = registration;
        Method = method;
        MethodName = methodName;
        _parameters = method.GetParameters();
        _data = _parameters.FirstOrDefault(p => !ScriptObjects.IsInjected(p.ParameterType));
        Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? $"{registration.Type.Name}.{method.Name}{(_data == null ? "()" : $"({_data.ParameterType.Name} {_data.Name})")}"
            + (registration.Description is { Length: > 0 } d ? $" — {d}" : "");
    }

    /// <inheritdoc/>
    public string Name => ObjectName + "." + MethodName;

    /// <summary>The object's name (<c>host.&lt;ObjectName&gt;</c>).</summary>
    public string ObjectName => _registration.Name;

    /// <summary>The method's name in scripts (the .NET name in camelCase).</summary>
    public string MethodName { get; }

    /// <summary>The .NET method.</summary>
    public MethodInfo Method { get; }

    /// <summary>The registered class.</summary>
    public Type ObjectType => _registration.Type;

    /// <inheritdoc/>
    public string? Description { get; }

    /// <inheritdoc/>
    public JObject? ArgsSchema => null;

    /// <inheritdoc/>
    public async Task<JToken?> InvokeAsync(JToken args, ScriptCallContext context, CancellationToken cancellationToken)
    {
        object target = InstanceFor(context.Services);
        object?[] values = new object?[_parameters.Length];
        for (int i = 0; i < _parameters.Length; i++)
        {
            Type type = _parameters[i].ParameterType;
            values[i] = type == typeof(CancellationToken) ? cancellationToken
                : type == typeof(ScriptCallContext) ? context
                : args.Type is JTokenType.Null or JTokenType.Undefined ? (type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : null)
                : args.ToObject(type);
        }

        object? returned;
        try
        {
            returned = Method.Invoke(target, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        object? value = await Unwrap(returned);
        return value switch
        {
            null => null,
            JToken token => token,
            _ => JToken.FromObject(value),
        };
    }

    /// <summary>The value of a call, awaited when the declared return type is a task (<c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c>, <c>ValueTask&lt;T&gt;</c>).</summary>
    private async Task<object?> Unwrap(object? returned)
    {
        Type declared = Method.ReturnType;
        Type? definition = declared.IsGenericType ? declared.GetGenericTypeDefinition() : null;
        if (returned is Task task)
        {
            await task;
            return definition == typeof(Task<>) ? declared.GetProperty("Result")!.GetValue(task) : null;
        }

        if (returned is ValueTask valueTask)
        {
            await valueTask;
            return null;
        }

        if (definition == typeof(ValueTask<>) && returned != null)
        {
            Task asTask = (Task)declared.GetMethod("AsTask")!.Invoke(returned, null)!;
            await asTask;
            return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
        }

        return declared == typeof(void) ? null : returned;
    }

    private object InstanceFor(IServiceProvider services)
    {
        Dictionary<RegisteredScriptObject, object> instances = Instances.GetValue(services, _ => []);
        lock (instances)
        {
            if (!instances.TryGetValue(_registration, out object? instance))
            {
                instance = _registration.Factory?.Invoke(services) ?? ActivatorUtilities.CreateInstance(services, _registration.Type);
                if (!_registration.Type.IsInstanceOfType(instance))
                {
                    throw new InvalidOperationException(
                        $"the factory of script object '{_registration.Name}' returned a {instance.GetType().FullName}, not a {_registration.Type.FullName}.");
                }

                instances[_registration] = instance;
            }

            return instance;
        }
    }
}

/// <summary>
/// The methods a script object exposes (architecture §18.7): the public instance methods of its class, inherited ones included, except those
/// declared by <see cref="object"/>, <c>Dispose</c>/<c>DisposeAsync</c>, property and event accessors, operators, generic methods, and methods
/// with <c>ref</c>/<c>out</c>/pointer parameters, delegate parameters or a delegate return. At most one data parameter (the script's argument);
/// a <see cref="CancellationToken"/> and a <see cref="ScriptCallContext"/> parameter are filled by the host. No property navigation, no
/// delegates: the script reaches methods by name and exchanges JSON.
/// </summary>
public static class ScriptObjects
{
    /// <summary>
    /// The host functions of <paramref name="objects"/>, one per exposed method.
    /// </summary>
    /// <exception cref="InvalidOperationException">An overloaded method (scripts call by name) or a method with two data parameters.</exception>
    public static IReadOnlyList<ScriptObjectFunction> Functions(IEnumerable<RegisteredScriptObject> objects)
    {
        List<ScriptObjectFunction> functions = [];
        foreach (RegisteredScriptObject registration in objects)
        {
            Dictionary<string, MethodInfo> seen = new(StringComparer.Ordinal);
            foreach (MethodInfo method in Exposed(registration.Type))
            {
                string name = char.ToLowerInvariant(method.Name[0]) + method.Name[1..];
                if (seen.TryGetValue(name, out MethodInfo? other))
                {
                    throw new InvalidOperationException(
                        $"Script object '{registration.Name}' ({registration.Type.FullName}): '{method.Name}' and '{other.Name}' are both '{name}' in scripts " +
                        "(overloads are not allowed: scripts call methods by name).");
                }

                if (method.GetParameters().Count(p => !IsInjected(p.ParameterType)) > 1)
                {
                    throw new InvalidOperationException(
                        $"Script object '{registration.Name}' ({registration.Type.FullName}): '{method.Name}' takes more than one argument; " +
                        "a method scripts call takes at most one (an object with the fields it needs), plus an optional CancellationToken or ScriptCallContext.");
                }

                seen[name] = method;
                functions.Add(new ScriptObjectFunction(registration, method, name));
            }
        }

        return functions;
    }

    /// <summary>The methods of <paramref name="type"/> scripts may call, by name (see the type remarks).</summary>
    public static IEnumerable<MethodInfo> Exposed(Type type)
    {
        bool disposable = typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type);
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType != typeof(object)
                        && !m.IsSpecialName
                        && !m.IsGenericMethodDefinition
                        && !m.Name.Contains('<', StringComparison.Ordinal)
                        && m.Name != "Deconstruct"
                        && !(disposable && m.Name is "Dispose" or "DisposeAsync")
                        && !IsDelegate(m.ReturnType)
                        && !m.ReturnType.IsByRef && !m.ReturnType.IsPointer
                        && m.GetParameters().All(p => !p.ParameterType.IsByRef && !p.ParameterType.IsPointer && !p.IsOut && !IsDelegate(p.ParameterType)))
            .OrderBy(m => m.Name, StringComparer.Ordinal);
    }

    internal static bool IsInjected(Type type) => type == typeof(CancellationToken) || type == typeof(ScriptCallContext);

    private static bool IsDelegate(Type type) => typeof(Delegate).IsAssignableFrom(type);
}
