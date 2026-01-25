namespace AITaskAgent.Designer.Dynamic;

using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Designer.Core;
using AITaskAgent.Designer.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

/// <summary>
/// Configuration for a dynamically generated step type.
/// </summary>
public record DynamicStepConfiguration
{
    /// <summary>
    /// Unique identifier for this step type.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Base step type to inherit from.
    /// </summary>
    public required DynamicStepType StepType { get; init; }

    /// <summary>
    /// JavaScript expression for script-based steps.
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>
    /// Additional parameters for the step.
    /// </summary>
    public JObject? Parameters { get; init; }

    /// <summary>
    /// The generated CLR type (set after generation).
    /// </summary>
    internal Type? GeneratedType { get; set; }
}

/// <summary>
/// Types of dynamic steps that can be generated.
/// </summary>
public enum DynamicStepType
{
    /// <summary>JavaScript-based action step.</summary>
    JsAction,

    /// <summary>JavaScript-based transformation step.</summary>
    JsTransform,

    /// <summary>JavaScript-based validation step.</summary>
    JsValidator
}

/// <summary>
/// Manages dynamic step type generation using TypeBuilder.
/// Equivalent to BRMS DynamicRuleManager.
/// </summary>
public class DynamicStepManager
{
    private static readonly ConcurrentDictionary<string, DynamicStepConfiguration> _generatedTypes = new();
    private static readonly Lock _lock = new();
    private static ModuleBuilder? _moduleBuilder;
    private readonly ILogger<DynamicStepManager>? _logger;

    public DynamicStepManager(ILogger<DynamicStepManager>? logger = null)
    {
        _logger = logger;
        InitializeModuleBuilder();
    }

    private static void InitializeModuleBuilder()
    {
        if (_moduleBuilder != null) return;

        lock (_lock)
        {
            if (_moduleBuilder != null) return;

            var assemblyName = new AssemblyName("AITaskAgent.Designer.DynamicSteps");
            var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.Run);

            _moduleBuilder = assemblyBuilder.DefineDynamicModule("DynamicStepsModule");
        }
    }

    /// <summary>
    /// Generates a dynamic step type from configuration.
    /// </summary>
    /// <param name="config">Step configuration.</param>
    public void GenerateStepType(DynamicStepConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.StepId))
            throw new ArgumentException("StepId is required.", nameof(config));

        var typeName = $"Dynamic{SanitizeTypeName(config.StepId)}Step";

        if (_generatedTypes.ContainsKey(typeName))
            throw new InvalidOperationException($"A step with ID '{config.StepId}' already exists.");

        lock (_lock)
        {
            var baseType = GetBaseType(config.StepType);

            var typeBuilder = _moduleBuilder!.DefineType(
            typeName,
            TypeAttributes.Public | TypeAttributes.Class,
            baseType);

            // Create empty constructor
            CreateEmptyConstructor(typeBuilder, baseType);

            // Create type
            config.GeneratedType = typeBuilder.CreateType();
            _generatedTypes[typeName] = config;

            // Register with StepManager
            StepManager.AddStep(
            config.GeneratedType,
            (json, sp) => CreateDynamicStep(config, json, sp),
            config.StepId);

            _logger?.LogInformation("Generated dynamic step type '{TypeName}' for StepId '{StepId}'",
            typeName, config.StepId);
        }
    }

    /// <summary>
    /// Removes a dynamically generated step type.
    /// </summary>
    public bool RemoveStepType(string stepId)
    {
        var typeName = $"Dynamic{SanitizeTypeName(stepId)}Step";

        if (_generatedTypes.TryRemove(typeName, out _))
        {
            StepManager.RemoveStep(stepId);
            _logger?.LogInformation("Removed dynamic step type '{StepId}'", stepId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets all dynamically generated step configurations.
    /// </summary>
    public IEnumerable<DynamicStepConfiguration> GetGeneratedTypes()
    {
        return _generatedTypes.Values;
    }

    /// <summary>
    /// Checks if a step ID is dynamically generated.
    /// </summary>
    public bool IsDynamicStep(string stepId)
    {
        var typeName = $"Dynamic{SanitizeTypeName(stepId)}Step";
        return _generatedTypes.ContainsKey(typeName);
    }

    private static Type GetBaseType(DynamicStepType stepType) => stepType switch
    {
        DynamicStepType.JsAction => typeof(DynamicJsActionStep),
        DynamicStepType.JsTransform => typeof(DynamicJsTransformStep),
        DynamicStepType.JsValidator => typeof(DynamicJsValidatorStep),
        _ => throw new ArgumentException($"Unknown step type: {stepType}")
    };

    private static void CreateEmptyConstructor(TypeBuilder typeBuilder, Type baseType)
    {
        var baseCtor = baseType.GetConstructor(
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
        null, Type.EmptyTypes, null);

        if (baseCtor == null)
        {
            // Try parameterless
            baseCtor = baseType.GetConstructor(Type.EmptyTypes);
        }

        var ctorBuilder = typeBuilder.DefineConstructor(
        MethodAttributes.Public,
        CallingConventions.Standard,
        Type.EmptyTypes);

        var il = ctorBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);

        if (baseCtor != null)
        {
            il.Emit(OpCodes.Call, baseCtor);
        }

        il.Emit(OpCodes.Ret);
    }

    private static IStep CreateDynamicStep(
    DynamicStepConfiguration config,
    JObject? json,
    IServiceProvider? sp)
    {
        if (config.GeneratedType == null)
            throw new InvalidOperationException($"Step type for '{config.StepId}' not yet generated.");

        var instance = Activator.CreateInstance(config.GeneratedType)
        ?? throw new InvalidOperationException($"Cannot create instance of {config.GeneratedType.Name}");

        // Configure the step
        if (instance is IDynamicStep dynamicStep)
        {
            dynamicStep.Configure(config, json);
        }

        return (IStep)instance;
    }

    private static string SanitizeTypeName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";

        return new string(input
        .Where(c => char.IsLetterOrDigit(c) || c == '_')
        .ToArray());
    }
}

/// <summary>
/// Interface for dynamically configured steps.
/// </summary>
public interface IDynamicStep
{
    void Configure(DynamicStepConfiguration config, JObject? values);
}
