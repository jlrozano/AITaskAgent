namespace AITaskAgent.Designer.Core;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Designer.Attributes;
using AITaskAgent.Designer.Models;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;

/// <summary>
/// Static registry for step factories. Equivalent to BRMS RuleManager.
/// Manages step registration, instantiation, and discovery.
/// </summary>
public static class StepManager
{
private static readonly ConcurrentDictionary<string, StepRegistration> _stepFactories = new();
private static readonly Dictionary<string, StepDescription> _stepDescriptionCache = [];
private static readonly Lock _lock = new();
private static IServiceProvider? _serviceProvider;

#region Service Provider

/// <summary>
/// Sets the service provider for dependency injection in step factories.
/// Call this once at application startup after building the DI container.
/// </summary>
public static void SetServiceProvider(IServiceProvider serviceProvider)
{
ArgumentNullException.ThrowIfNull(serviceProvider);
_serviceProvider = serviceProvider;
}

#endregion

#region Registration

/// <summary>
/// Registers a step type with an optional custom factory.
/// </summary>
/// <typeparam name="T">The step type to register.</typeparam>
/// <param name="factory">Optional factory function. If null, uses default factory.</param>
/// <param name="stepId">Optional step ID. If null, derives from type name.</param>
public static void AddStep<T>(
Func<JObject?, IServiceProvider?, T>? factory = null,
string? stepId = null) where T : class, IStep
{
AddStep(typeof(T), factory != null ? (j, sp) => factory(j, sp) : null, stepId);
}

/// <summary>
/// Registers a step type with an optional custom factory.
/// </summary>
/// <param name="stepType">The step type to register.</param>
/// <param name="factory">Optional factory function. If null, uses default factory.</param>
/// <param name="stepId">Optional step ID. If null, derives from type name.</param>
public static void AddStep(
Type stepType,
Func<JObject?, IServiceProvider?, IStep>? factory = null,
string? stepId = null)
{
ArgumentNullException.ThrowIfNull(stepType);

if (!typeof(IStep).IsAssignableFrom(stepType))
{
throw new ArgumentException($"Type {stepType.Name} does not implement IStep.", nameof(stepType));
}

stepId ??= GetStepId(stepType);
factory ??= FactoryFromType(stepType);

_ = _stepFactories.AddOrUpdate(
stepId,
new StepRegistration(stepType, factory),
(_, _) => new StepRegistration(stepType, factory));

// Update description cache if already populated
if (_stepDescriptionCache.Count > 0)
{
lock (_lock)
{
_stepDescriptionCache[stepId] = GetStepDescription(stepType);
}
}
}

/// <summary>
/// Removes a registered step. Used for hot-reload scenarios.
/// </summary>
/// <param name="stepId">The step ID to remove.</param>
/// <returns>True if removed, false if not found.</returns>
public static bool RemoveStep(string stepId)
{
var removed = _stepFactories.TryRemove(stepId, out _);

if (removed && _stepDescriptionCache.Count > 0)
{
lock (_lock)
{
_stepDescriptionCache.Remove(stepId);
}
}

return removed;
}

/// <summary>
/// Registers all step types from an assembly.
/// </summary>
/// <param name="assembly">The assembly to scan.</param>
public static void RegisterStepsFromAssembly(Assembly assembly)
{
ArgumentNullException.ThrowIfNull(assembly);

var stepTypes = assembly.GetTypes()
.Where(t => typeof(IStep).IsAssignableFrom(t)
&& t.IsClass
&& !t.IsAbstract
&& t.IsPublic);

foreach (var stepType in stepTypes)
{
AddStep(stepType);
}
}

#endregion

#region Step Creation

/// <summary>
/// Creates a step instance from a step ID and optional configuration.
/// Equivalent to BRMS RuleManager.GetRule.
/// </summary>
/// <typeparam name="T">Expected step type.</typeparam>
/// <param name="stepId">The registered step ID.</param>
/// <param name="config">Optional JSON configuration.</param>
/// <returns>Step instance or null if not found.</returns>
public static T? GetStep<T>(string stepId, JObject? config = null) where T : class, IStep
{
if (string.IsNullOrEmpty(stepId))
return null;

if (_stepFactories.TryGetValue(stepId, out var registration))
{
var step = registration.Factory(config, _serviceProvider);
if (step is T result)
{
return result;
}
}

return null;
}

/// <summary>
/// Creates a step instance from a step ID and optional configuration.
/// </summary>
/// <param name="stepId">The registered step ID.</param>
/// <param name="config">Optional JSON configuration.</param>
/// <returns>Step instance or null if not found.</returns>
public static IStep? GetStep(string stepId, JObject? config = null)
{
return GetStep<IStep>(stepId, config);
}

/// <summary>
/// Creates a step from a JSON object that contains "stepId" property.
/// </summary>
/// <param name="stepConfig">JSON with stepId and configuration.</param>
/// <returns>Step instance or null if not found.</returns>
public static IStep? CreateStepFromJson(JObject stepConfig)
{
var stepId = stepConfig["stepId"]?.ToString();
if (string.IsNullOrEmpty(stepId))
return null;

return GetStep(stepId, stepConfig);
}

#endregion

#region Discovery

/// <summary>
/// Gets all registered step descriptions.
/// </summary>
public static IEnumerable<StepDescription> GetAllStepDescriptions()
{
lock (_lock)
{
if (_stepDescriptionCache.Count == 0)
{
foreach (var kvp in _stepFactories)
{
_stepDescriptionCache[kvp.Key] = GetStepDescription(kvp.Value.StepType);
}
}
}

return _stepDescriptionCache.Values;
}

/// <summary>
/// Gets a specific step description by ID.
/// </summary>
public static StepDescription? GetStepDescription(string stepId)
{
if (_stepDescriptionCache.TryGetValue(stepId, out var cached))
return cached;

if (_stepFactories.TryGetValue(stepId, out var registration))
return GetStepDescription(registration.StepType);

return null;
}

/// <summary>
/// Gets step description from a type.
/// </summary>
public static StepDescription GetStepDescription(Type stepType)
{
var stepIdAttr = stepType.GetCustomAttribute<StepIdAttribute>();
var descAttr = stepType.GetCustomAttribute<DescriptionAttribute>();
var categoryAttr = stepType.GetCustomAttribute<StepCategoryAttribute>();

var stepId = stepIdAttr?.StepId ?? GetStepId(stepType);

return new StepDescription
{
StepId = stepId,
DisplayName = stepIdAttr?.StepId ?? stepType.Name,
Description = descAttr?.Description,
StepType = stepType,
ConfigurationSchema = GenerateConfigSchema(stepType),
Category = categoryAttr?.Category ?? "General"
};
}

/// <summary>
/// Checks if a step ID is registered.
/// </summary>
public static bool IsRegistered(string stepId) => _stepFactories.ContainsKey(stepId);

/// <summary>
/// Gets all registered step IDs.
/// </summary>
public static IEnumerable<string> GetRegisteredStepIds() => _stepFactories.Keys;

#endregion

#region Naming

/// <summary>
/// Gets the step ID for a type, removing common suffixes.
/// </summary>
public static string GetStepId(Type stepType)
{
// Check for attribute first
var attr = stepType.GetCustomAttribute<StepIdAttribute>();
if (attr != null)
return attr.StepId;

// Remove generic arity markers
var name = stepType.Name;
var genericIndex = name.IndexOf('`');
if (genericIndex > 0)
name = name[..genericIndex];

// Remove common suffixes
string[] suffixes = ["Step", "LlmStep", "Action"];
foreach (var suffix in suffixes)
{
if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
{
name = name[..^suffix.Length];
break;
}
}

return name;
}

#endregion

#region Internal

private static Func<JObject?, IServiceProvider?, IStep> FactoryFromType(Type stepType)
{
return (json, sp) =>
{
IStep instance;

if (sp != null)
{
instance = (IStep)ActivatorUtilities.CreateInstance(sp, stepType);
}
else
{
instance = (IStep)(Activator.CreateInstance(stepType)
?? throw new InvalidOperationException($"Cannot create instance of {stepType.Name}"));
}

if (json != null)
{
JsonConvert.PopulateObject(json.ToString(), instance);
}

return instance;
};
}

private static JsonSchema? GenerateConfigSchema(Type stepType)
{
try
{
return JsonSchema.FromType(stepType);
}
catch
{
return null;
}
}

#endregion

#region Testing Support

/// <summary>
/// Clears all registrations. For testing only.
/// </summary>
internal static void Clear()
{
_stepFactories.Clear();
lock (_lock)
{
_stepDescriptionCache.Clear();
}
}

#endregion
}
