using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace AITaskAgent.Core.Steps;

/// <summary>
/// Centralised registry for step types. Equivalent to RuleManager in BRMS.
/// Provides step discovery, creation (DI-aware), and description generation.
/// </summary>
public static class StepManager
{
    private static readonly ConcurrentDictionary<string, Type> _stepTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, StepDescription> _descriptionCache = [];
    private static readonly Lock _cacheLock = new();
    private static IServiceProvider? _serviceProvider;
    private static readonly AsyncLocal<IServiceProvider?> _currentScopeProvider = new();
    private static readonly IStepActivator _defaultActivator = new DefaultStepActivator();

    /// <summary>
    /// Obtiene o establece el IServiceProvider para el flujo asíncrono actual.
    /// Permite aislar dependencias Scoped por petición/hilo en entornos concurrentes.
    /// </summary>
    public static IServiceProvider? CurrentScopeProvider
    {
        get => _currentScopeProvider.Value;
        set => _currentScopeProvider.Value = value;
    }

    public static void SetServiceProvider(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    // ── Registration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a step type. If tagName is null, reads [StepTag] from the type.
    /// Throws if the type does not implement IStep or carries no [StepTag].
    /// </summary>
    public static void AddStep(Type stepType, string? tagName = null)
    {
        ArgumentNullException.ThrowIfNull(stepType);

        if (!typeof(IStep).IsAssignableFrom(stepType))
        {
            throw new ArgumentException(
                $"Type '{stepType.Name}' does not implement IStep.", nameof(stepType));
        }

        if (string.IsNullOrWhiteSpace(tagName))
        {
            StepTagAttribute attr = stepType.GetCustomAttribute<StepTagAttribute>(inherit: false)
                ?? throw new ArgumentException(
                    $"Type '{stepType.Name}' has no [StepTag] attribute and no tagName was supplied.",
                    nameof(stepType));
            tagName = attr.TagName;
        }

        _stepTypes[tagName] = stepType;
    }

    /// <summary>
    /// Scans an assembly for concrete types decorated with [StepTag] that implement IStep
    /// and registers them automatically. Equivalent to RegisterRulesFromAssembly.
    /// </summary>
    public static void RegisterStepsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (Type? type in assembly.GetTypes()
            .Where(t => typeof(IStep).IsAssignableFrom(t)
                     && !t.IsAbstract
                     && !t.IsInterface
                     && t.GetCustomAttribute<StepTagAttribute>(inherit: false) != null))
        {
            AddStep(type);
        }
    }

    /// <summary>Returns a snapshot of all registered tag→type mappings.</summary>
    public static IReadOnlyDictionary<string, Type> GetRegisteredTypes()
        => _stepTypes;

    public static bool TryGetStepType(string tag, out Type stepType)
        => _stepTypes.TryGetValue(tag, out stepType!);

    // ── Creation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an instance of the step registered under <paramref name="tag"/>,
    /// optionally populating properties from <paramref name="config"/>.
    /// Uses DI when available (equivalent to RuleManager.FactoryFromType).
    /// </summary>
    public static IStep CreateStep(string tag, JObject? config = null, IServiceProvider? sp = null)
    {
        if (!_stepTypes.TryGetValue(tag, out Type? stepType))
        {
            throw new KeyNotFoundException($"No step registered with tag '{tag}'.");
        }

        IServiceProvider? provider = sp ?? _currentScopeProvider.Value ?? _serviceProvider;
        object? instance = _defaultActivator.Create(stepType, provider);

        if (config == null)
        {
            return instance is IStep diStep ? diStep : (IStep)_defaultActivator.Create(stepType, null);
        }

        // Populate properties from JSON config
        JsonSerializer serializer = new()
        {
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
        };

        if (instance != null)
        {
            serializer.Populate(config.CreateReader(), instance);
            return (IStep)instance;
        }

        return (IStep)config.ToObject(stepType, serializer)!;
    }

    // ── Description ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a description for the step type with a JSON Schema derived from
    /// its public writable properties. Equivalent to RuleManager.GetRuleDescription.
    /// </summary>
    public static StepDescription GetStepDescription(Type stepType)
    {
        ArgumentNullException.ThrowIfNull(stepType);

        StepTagAttribute? tagAttr = stepType.GetCustomAttribute<StepTagAttribute>(inherit: true);
        string tagName = tagAttr?.TagName ?? stepType.Name;

        // Check cache
        _cacheLock.Enter();
        try
        {
            if (_descriptionCache.TryGetValue(tagName, out StepDescription? cached))
            {
                return cached;
            }
        }
        finally
        {
            _cacheLock.Exit();
        }

        string description = GetDescriptionFromClass(stepType);
        JsonSchema schema = BuildSchema(stepType);

        StepDescription desc = new()
        {
            TagName = tagName,
            Description = description,
            StepType = stepType,
            Parameters = schema
        };

        _cacheLock.Enter();
        try { _descriptionCache[tagName] = desc; }
        finally { _cacheLock.Exit(); }

        return desc;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private static JsonSchema BuildSchema(Type stepType)
    {
        JsonSchema schema = new() { Type = JsonObjectType.Object };
        NullabilityInfoContext nullabilityCtx = new();

        IEnumerable<PropertyInfo> props = stepType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanWrite
                     && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute)));

        foreach (PropertyInfo? prop in props)
        {
            string propName = prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName
                ?? (char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]);

            JsonSchemaProperty propSchema = new();
            Type underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            if (underlying == typeof(string))
            {
                propSchema.Type = JsonObjectType.String;
            }
            else if (underlying == typeof(int) || underlying == typeof(long))
            {
                propSchema.Type = JsonObjectType.Integer;
            }
            else if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
            {
                propSchema.Type = JsonObjectType.Number;
            }
            else if (underlying == typeof(bool))
            {
                propSchema.Type = JsonObjectType.Boolean;
            }
            else if (underlying == typeof(DateTime))
            {
                propSchema.Type = JsonObjectType.String;
                propSchema.Format = "date-time";
            }
            else if (underlying.IsEnum)
            {
                propSchema.Type = JsonObjectType.String;
                foreach (var v in Enum.GetValues(underlying))
                {
                    propSchema.Enumeration.Add(v.ToString()!);
                }
            }
            else if (IsEnumerableType(underlying, out Type? elementType))
            {
                propSchema.Type = JsonObjectType.Array;
                JsonSchema itemSchema = new();
                Type elUnderlying = Nullable.GetUnderlyingType(elementType) ?? elementType;
                if (elUnderlying == typeof(string))
                {
                    itemSchema.Type = JsonObjectType.String;
                }
                else if (elUnderlying == typeof(int) || elUnderlying == typeof(long))
                {
                    itemSchema.Type = JsonObjectType.Integer;
                }
                else if (elUnderlying == typeof(double) || elUnderlying == typeof(float) || elUnderlying == typeof(decimal))
                {
                    itemSchema.Type = JsonObjectType.Number;
                }
                else if (elUnderlying == typeof(bool))
                {
                    itemSchema.Type = JsonObjectType.Boolean;
                }

                propSchema.Items.Add(itemSchema);
            }

            // Apply [Description]
            DescriptionAttribute? descAttr = prop.GetCustomAttribute<DescriptionAttribute>(inherit: true);
            if (descAttr != null)
            {
                propSchema.Description = descAttr.Description;
            }

            // Required: explicit [Required] or non-nullable reference type
            bool isRequired = Attribute.IsDefined(prop, typeof(RequiredAttribute), inherit: true)
                || nullabilityCtx.Create(prop).ReadState != NullabilityState.Nullable;
            if (isRequired && !schema.RequiredProperties.Contains(propName))
            {
                schema.RequiredProperties.Add(propName);
            }

            schema.Properties[propName] = propSchema;
        }

        return schema;
    }

    private static bool IsEnumerableType(Type type, out Type elementType)
    {
        elementType = typeof(object);
        if (!type.IsGenericType)
        {
            return false;
        }

        Type? enumInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumInterface == null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            enumInterface = type;
        }

        if (enumInterface == null)
        {
            return false;
        }

        elementType = enumInterface.GetGenericArguments()[0];
        return true;
    }
}
