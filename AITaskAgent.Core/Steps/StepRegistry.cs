using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Default <see cref="IStepRegistry"/>: an instance registry of step types by tag (the equivalent
/// of BRMS's RuleManager, without static state or an ambient service provider).
/// </summary>
public sealed class StepRegistry(IStepActivator? activator = null) : IStepRegistry
{
    private readonly ConcurrentDictionary<string, Type> _stepTypes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, (string Description, JsonSchema Schema)> _descriptionCache = new();
    private readonly IStepActivator _activator = activator ?? new DefaultStepActivator();
    private readonly Lock _addLock = new();

    // ── Registration ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Add(Type stepType, string? tag = null)
    {
        ArgumentNullException.ThrowIfNull(stepType);

        if (!typeof(IStep).IsAssignableFrom(stepType))
        {
            throw new ArgumentException($"Type '{stepType.FullName}' does not implement IStep.", nameof(stepType));
        }

        if (stepType.IsAbstract || stepType.IsInterface || stepType.ContainsGenericParameters)
        {
            throw new ArgumentException(
                $"Type '{stepType.FullName}' cannot be registered: it is abstract, an interface or an open generic.",
                nameof(stepType));
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            tag = stepType.GetCustomAttribute<StepTagAttribute>(inherit: false)?.TagName ?? stepType.Name;
        }

        lock (_addLock)
        {
            if (_stepTypes.TryGetValue(tag, out Type? existing))
            {
                if (existing == stepType)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Step tag '{tag}' is already registered with type '{existing.FullName}'; " +
                    $"cannot register it again with type '{stepType.FullName}'.");
            }

            _stepTypes[tag] = stepType;
        }
    }

    /// <inheritdoc/>
    public int AddFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<Type> types = [.. assembly.GetTypes()
            .Where(t => typeof(IStep).IsAssignableFrom(t)
                     && !t.IsAbstract
                     && !t.IsInterface
                     && !t.ContainsGenericParameters
                     && t.GetCustomAttribute<StepTagAttribute>(inherit: false) != null)];

        foreach (Type type in types)
        {
            Add(type);
        }

        return types.Count;
    }

    /// <inheritdoc/>
    public bool TryGetType(string tag, [NotNullWhen(true)] out Type? stepType)
        => _stepTypes.TryGetValue(tag, out stepType);

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, Type> GetRegisteredTypes()
        => new Dictionary<string, Type>(_stepTypes, StringComparer.Ordinal);

    // ── Creation ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IStep Create(string tag, JObject? config = null, IServiceProvider? services = null)
    {
        Type stepType = Resolve(tag);
        object instance = _activator.Create(stepType, services);

        if (config != null)
        {
            JsonSerializer serializer = new()
            {
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
            };
            serializer.Populate(config.CreateReader(), instance);
        }

        return (IStep)instance;
    }

    // ── Description ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public StepDescription Describe(string tag)
        => BuildDescription(tag, Resolve(tag));

    /// <inheritdoc/>
    public StepDescription Describe(Type stepType)
    {
        ArgumentNullException.ThrowIfNull(stepType);

        string tag = _stepTypes.FirstOrDefault(kv => kv.Value == stepType).Key
            ?? stepType.GetCustomAttribute<StepTagAttribute>(inherit: true)?.TagName
            ?? stepType.Name;

        return BuildDescription(tag, stepType);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Type Resolve(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        if (_stepTypes.TryGetValue(tag, out Type? stepType))
        {
            return stepType;
        }

        string available = _stepTypes.IsEmpty
            ? "(none registered)"
            : string.Join(", ", _stepTypes.Keys.Order(StringComparer.Ordinal));
        throw new KeyNotFoundException($"No step registered with tag '{tag}'. Registered tags: {available}.");
    }

    private StepDescription BuildDescription(string tag, Type stepType)
    {
        (string description, JsonSchema schema) = _descriptionCache.GetOrAdd(
            stepType, t => (GetDescriptionFromClass(t), TypeSchemaBuilder.Build(t)));

        return new StepDescription
        {
            TagName = tag,
            Description = description,
            StepType = stepType,
            Parameters = schema
        };
    }

    private static string GetDescriptionFromClass(Type type)
    {
        Type? current = type;
        while (current != null && current != typeof(object))
        {
            DescriptionAttribute? attr = current.GetCustomAttribute<DescriptionAttribute>(inherit: false);
            if (attr != null)
            {
                return attr.Description;
            }

            current = current.BaseType;
        }
        return "";
    }
}
