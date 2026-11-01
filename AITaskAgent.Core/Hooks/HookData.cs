using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Reflection;

namespace AITaskAgent.Core.Hooks;

/// <summary>
/// The writable outcome of a hook's arguments: what a hook in the "after" form (<c>Func&lt;TArgs, Task&gt;</c>) changes instead of returning a
/// value. The step's virtual method computes its own value, stores it here, runs the hook and goes on with what the hook left
/// (<see cref="HookForms.After{TArgs, T}"/>). A hook that does not touch it keeps the default.
/// </summary>
/// <typeparam name="T">The value the virtual method returns (the result, the message...).</typeparam>
public interface IHookOutcome<T>
{
    /// <summary>The value the step goes on with.</summary>
    T Outcome { get; set; }
}

/// <summary>
/// Arguments a hook written in another language (JavaScript, run in the script worker) can receive as data: each member is a JSON value
/// read from the real arguments, and the writable ones are written back to them (architecture §18.8). The same list is the structure the
/// designer shows in the comment of a stub and completes after <c>args.</c>. Objects with behaviour (the step, the activity, the
/// cancellation token, the services) are never members: only data taken from them (the step's name).
/// </summary>
/// <typeparam name="TSelf">The arguments type itself.</typeparam>
public interface IHookData<TSelf> where TSelf : IHookData<TSelf>
{
    /// <summary>The members of the data view, in the order they are documented.</summary>
    static abstract IReadOnlyList<HookDataMember<TSelf>> DataMembers { get; }
}

/// <summary>A property of an object member of the data view (<c>args.step.name</c>), for the structure and the completion.</summary>
/// <param name="Name">The JavaScript name.</param>
/// <param name="Type">Its type as text (<c>string</c>, <c>number</c>, <c>boolean</c>, <c>JSON</c>; <c>| null</c> when it may be null).</param>
public sealed record HookDataProperty(string Name, string Type);

/// <summary>A member of the data view of a hook's arguments, untyped (see <see cref="HookDataMember{TArgs}"/>).</summary>
public abstract class HookDataMember
{
    private protected HookDataMember(string name, string type, IReadOnlyList<HookDataProperty>? properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Name = name;
        Type = type;
        Properties = properties ?? [];
    }

    /// <summary>The JavaScript name (camelCase, <c>args.&lt;name&gt;</c>).</summary>
    public string Name { get; }

    /// <summary>Its type as text: <c>string</c>, <c>number</c>, <c>boolean</c>, <c>JSON</c> (any JSON value) or <c>object</c>; <c>| null</c> when it may be null.</summary>
    public string Type { get; }

    /// <summary>The properties of an object member whose shape is fixed (<c>step</c>: <c>name</c>); empty otherwise.</summary>
    public IReadOnlyList<HookDataProperty> Properties { get; }

    /// <summary>True when a write is applied back to the arguments.</summary>
    public abstract bool Writable { get; }

    /// <summary>The member's value as JSON (null for a JSON null).</summary>
    public abstract JToken? Read(object args);

    /// <summary>Applies <paramref name="value"/> to the arguments.</summary>
    /// <exception cref="InvalidOperationException">The member is read-only, or the value does not fit it.</exception>
    public abstract void Write(object args, JToken? value);
}

/// <summary>A member of the data view of <typeparamref name="TArgs"/>: how to read it and, when writable, how to write it back.</summary>
public sealed class HookDataMember<TArgs> : HookDataMember
{
    private readonly Func<TArgs, JToken?> _read;
    private readonly Action<TArgs, JToken?>? _write;

    /// <summary>A member read with <paramref name="read"/>; writable when <paramref name="write"/> is given.</summary>
    public HookDataMember(string name, string type, Func<TArgs, JToken?> read, Action<TArgs, JToken?>? write = null, IReadOnlyList<HookDataProperty>? properties = null)
        : base(name, type, properties)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write;
    }

    /// <inheritdoc/>
    public override bool Writable => _write != null;

    /// <inheritdoc/>
    public override JToken? Read(object args) => _read((TArgs)args);

    /// <inheritdoc/>
    public override void Write(object args, JToken? value)
    {
        if (_write == null)
        {
            throw new InvalidOperationException($"args.{Name} is read-only.");
        }

        _write((TArgs)args, value);
    }
}

/// <summary>Reading and writing the data view of a hook's arguments (<see cref="IHookData{TSelf}"/>) without knowing their type.</summary>
public static class HookData
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<HookDataMember>?> Cache = new();

    /// <summary>The <c>step</c> member every data view starts with: <c>{ name }</c>, the step's id in the YAML.</summary>
    public static IReadOnlyList<HookDataProperty> StepProperties { get; } = [new("name", "string")];

    /// <summary>The members of the data view of <paramref name="argsType"/>, or null when the type is not <see cref="IHookData{TSelf}"/>.</summary>
    public static IReadOnlyList<HookDataMember>? MembersOf(Type argsType)
    {
        ArgumentNullException.ThrowIfNull(argsType);
        return Cache.GetOrAdd(argsType, static type =>
        {
            bool data = type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHookData<>) && i.GetGenericArguments()[0] == type);
            if (!data)
            {
                return null;
            }

            PropertyInfo? property = type.GetProperty("DataMembers", BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null) is IEnumerable<HookDataMember> members ? [.. members] : null;
        });
    }

    /// <summary>Every member of <paramref name="args"/> as one JSON object (<c>{ step: { name }, result: .., ... }</c>).</summary>
    /// <exception cref="InvalidOperationException">The arguments are not <see cref="IHookData{TSelf}"/>, or a value cannot be converted to JSON.</exception>
    public static JObject Read(object args)
    {
        ArgumentNullException.ThrowIfNull(args);
        IReadOnlyList<HookDataMember> members = MembersOf(args.GetType())
            ?? throw new InvalidOperationException($"{args.GetType().Name} has no data view (IHookData).");
        JObject data = [];
        foreach (HookDataMember member in members)
        {
            try
            {
                data[member.Name] = member.Read(args) ?? JValue.CreateNull();
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException($"args.{member.Name} could not be converted to JSON: {ex.Message}", ex);
            }
        }

        return data;
    }

    /// <summary>Writes <paramref name="value"/> to the member <paramref name="name"/> of <paramref name="args"/>.</summary>
    /// <exception cref="InvalidOperationException">No such member, the member is read-only or the value does not fit.</exception>
    public static void Write(object args, string name, JToken? value)
    {
        ArgumentNullException.ThrowIfNull(args);
        HookDataMember member = MembersOf(args.GetType())?.FirstOrDefault(m => m.Name == name)
            ?? throw new InvalidOperationException($"args has no member '{name}'.");
        member.Write(args, value);
    }

    /// <summary>A value as JSON: null stays null, a <see cref="JToken"/> as is, anything else through <see cref="JToken.FromObject(object)"/>.</summary>
    public static JToken? ToJson(object? value) => value switch
    {
        null => null,
        JToken token => token,
        _ => JToken.FromObject(value),
    };

    /// <summary>Text from JSON: a JSON string as its text, null as null, anything else as compact JSON.</summary>
    public static string? ToText(JToken? value) => value switch
    {
        null or { Type: JTokenType.Null or JTokenType.Undefined } => null,
        { Type: JTokenType.String } => value.Value<string>(),
        _ => value.ToString(Formatting.None),
    };

    /// <summary>
    /// Replaces the value of <paramref name="result"/> with <paramref name="value"/>, converted to the type of the result's <c>Value</c>
    /// (<see cref="JToken"/>, text, or any type JSON converts to). The same object is returned: its type, error, metrics and validators stay.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is not a <see cref="StepResult"/>, or the value does not convert.</exception>
    public static IStepResult WithValue(IStepResult result, JToken? value)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result is not StepResult stepResult)
        {
            throw new InvalidOperationException($"the value of a {result.GetType().Name} cannot be replaced.");
        }

        Type valueType = result.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name == nameof(IStepResult.Value) && p.GetIndexParameters().Length == 0)
            .OrderByDescending(p => Depth(p.DeclaringType))
            .Select(p => p.PropertyType)
            .FirstOrDefault() ?? typeof(object);
        stepResult.Value = Convert(value, valueType);
        return result;
    }

    /// <summary>JSON converted to <paramref name="type"/> (a <see cref="JToken"/> type, text, object or a type JSON deserializes to).</summary>
    public static object? Convert(JToken? value, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value == null || value.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return typeof(JToken).IsAssignableFrom(type) ? (type.IsInstanceOfType(JValue.CreateNull()) ? JValue.CreateNull() : null) : null;
        }

        if (typeof(JToken).IsAssignableFrom(type))
        {
            return type.IsInstanceOfType(value)
                ? value.DeepClone()
                : throw new InvalidOperationException($"a {value.Type} value cannot be a {type.Name}.");
        }

        if (type == typeof(string))
        {
            return ToText(value);
        }

        if (type == typeof(object))
        {
            return value is JValue scalar ? scalar.Value : value.DeepClone();
        }

        try
        {
            return value.ToObject(type);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidCastException)
        {
            throw new InvalidOperationException($"the value does not convert to {type.Name}: {ex.Message}", ex);
        }
    }

    private static int Depth(Type? type)
    {
        int depth = 0;
        for (Type? t = type; t != null; t = t.BaseType)
        {
            depth++;
        }

        return depth;
    }
}

/// <summary>The two forms a hook of an around slot can take (architecture §14.6).</summary>
public static class HookForms
{
    /// <summary>
    /// The "after" form as an around hook: the step's own behaviour runs first (<c>next</c>), its value is stored in
    /// <see cref="IHookOutcome{T}.Outcome"/>, the hook runs with the arguments and the step goes on with what the hook left there.
    /// </summary>
    public static HookAround<TArgs, T> After<TArgs, T>(Func<TArgs, Task> hook) where TArgs : IHookOutcome<T>
    {
        ArgumentNullException.ThrowIfNull(hook);
        return async (args, next) =>
        {
            args.Outcome = await next();
            await hook(args);
            return args.Outcome;
        };
    }

    /// <summary>
    /// The "after" delegate type of an around slot type (<c>HookAround&lt;TArgs, T&gt;</c> whose <c>TArgs</c> is <see cref="IHookOutcome{T}"/>):
    /// <c>Func&lt;TArgs, Task&gt;</c>; null for any other delegate type.
    /// </summary>
    public static Type? AfterTypeOf(Type delegateType)
    {
        ArgumentNullException.ThrowIfNull(delegateType);
        if (!delegateType.IsGenericType || delegateType.GetGenericTypeDefinition() != typeof(HookAround<,>))
        {
            return null;
        }

        Type[] arguments = delegateType.GetGenericArguments();
        return typeof(IHookOutcome<>).MakeGenericType(arguments[1]).IsAssignableFrom(arguments[0])
            ? typeof(Func<,>).MakeGenericType(arguments[0], typeof(Task))
            : null;
    }

    /// <summary>
    /// Adapts a delegate of the "after" type of <paramref name="aroundType"/> (<see cref="AfterTypeOf"/>) into <paramref name="aroundType"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="aroundType"/> has no after form, or <paramref name="hook"/> is not of it.</exception>
    public static Delegate AfterAsAround(Type aroundType, Delegate hook)
    {
        ArgumentNullException.ThrowIfNull(aroundType);
        ArgumentNullException.ThrowIfNull(hook);
        Type afterType = AfterTypeOf(aroundType) ?? throw new ArgumentException($"{aroundType.Name} has no after form.", nameof(aroundType));
        if (!afterType.IsInstanceOfType(hook))
        {
            throw new ArgumentException($"the hook is a {hook.GetType().Name}, not a {afterType.Name}.", nameof(hook));
        }

        Type[] arguments = aroundType.GetGenericArguments();
        MethodInfo after = typeof(HookForms).GetMethod(nameof(After))!.MakeGenericMethod(arguments[0], arguments[1]);
        return (Delegate)after.Invoke(null, [hook])!;
    }
}
