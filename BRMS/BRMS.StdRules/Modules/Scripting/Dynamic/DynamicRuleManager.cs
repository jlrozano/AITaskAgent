using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BRMS.Core.Abstractions;
using BRMS.Core.Core;
using BRMS.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Modules.Scripting.Dynamic;

/// <summary>
/// Gestor para cargar y crear reglas dinámicas desde configuraciones JSON
/// </summary>
internal class DynamicRuleManager
{

    private readonly StdRulesConfiguration _configuration;
    private static readonly ConcurrentDictionary<string, DynamicRuleConfiguration> _generatedTypes = [];
    private static readonly Lock _lock = new();
    private static ModuleBuilder? _moduleBuilder;

    static DynamicRuleManager()
    {
        InitializeModuleBuilder();
    }

    public DynamicRuleManager(StdRulesConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));
        _configuration = configuration;
    }

    private static void InitializeModuleBuilder()
    {
        var assemblyName = new AssemblyName("DynamicRulesAssembly");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        _moduleBuilder = assemblyBuilder.DefineDynamicModule("DynamicRulesModule");
    }

    /// <summary>
    /// Carga una configuración de regla dinámica desde un archivo JSON
    /// </summary>
    /// <param name="logger">Logger para registrar trazas</param>
    /// <returns>Lista de errores encontrados durante la carga (vacía si es exitosa)</returns>
    public IReadOnlyList<string> LoadRulesAsync(ILogger logger)
    {
        var errors = new List<string>();

        foreach (DynamicRuleConfiguration configuration in _configuration.JsRules)
        {

            if (ValidateConfiguration(configuration, errors))
            {
                logger.LogInformation("Configuración de regla {@Regla}\n correcta.", configuration);
                GenerateRuleType(configuration);

            }
            else
            {
                logger.LogError("Error validando configuración de regla {@Regla}\n {@Errores}", configuration, errors);
            }
        }

        return errors;
    }

    /// <summary>
    /// Genera un tipo dinámico para una configuración específica
    /// </summary>
    /// <param name="configuration">Configuración de la regla</param>
    /// <returns>Tipo generado</returns>
    public static void GenerateRuleType(DynamicRuleConfiguration configuration)
    {
        string typeName = $"Dynamic{SanitizeTypeName(configuration.Key)}";

        if (_generatedTypes.ContainsKey(typeName))
        {
            throw new Exception($"Ya existe una regla con ese nombre ({configuration.Key})");
        }

        lock (_lock)
        {
            Type baseType = GetBaseType(configuration.RuleType);

            TypeBuilder typeBuilder = _moduleBuilder!.DefineType(
                typeName,
                TypeAttributes.Public | TypeAttributes.Class,
                baseType);

            // Crear constructor vacío
            CreateEmptyConstructor(typeBuilder, baseType);
            configuration.Type = typeBuilder.CreateType();
            _ = _generatedTypes.TryAdd(typeName, configuration);

            RuleManager.AddRule(configuration.Type, (config) => CreateDynamicRule(configuration, config) ??
               throw new Exception($"No se pudo crear la regla {configuration.RuleId}"), configuration.RuleId);

        }
    }

    public static DynamicRuleConfiguration? GetConfigurationFromType(Type type)
    {
        return _generatedTypes.TryGetValue(type.Name, out DynamicRuleConfiguration? configuration) ? configuration : null;
    }

    public static RuleDescription? RuleDescriptionFromJsType(Type type)
    {
        DynamicRuleConfiguration? config = GetConfigurationFromType(type);
        return config == null
            ? null
            : new RuleDescription
            {
                InputTypes = config.InputTypes,
                Description = config.Description,
                Example = config.Example,
                Id = config.RuleId,
                Type = type,
                Parameters = config.GenerateJsonSchema()
            };
    }

    private static string SanitizeTypeName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "Unknown";
        }

        var result = new StringBuilder();

        foreach (char c in input)
        {
            _ = char.IsLetterOrDigit(c) ? result.Append(c) : result.Append('_');
        }

        return result.ToString();
    }

    private static Type GetBaseType(JsRuleType ruleType)
    {
        return ruleType switch
        {
            JsRuleType.Validator => typeof(DynamicJsScriptValidator),
            JsRuleType.Normalizer => typeof(DynamicJsScriptNormalizer),
            JsRuleType.Transformation => typeof(DynamicJsScriptTransformation),
            _ => throw new ArgumentException($"Unsupported rule type: {ruleType}")
        };
    }

    private static void CreateEmptyConstructor(TypeBuilder typeBuilder, Type baseType)
    {
        ConstructorInfo baseConstructor = baseType.GetConstructor(Type.EmptyTypes) ??
            throw new InvalidOperationException($"Base type {baseType.Name} must have a parameterless constructor");
        ConstructorBuilder constructorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);

        ILGenerator ilGenerator = constructorBuilder.GetILGenerator();

        // Llamar al constructor base
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Call, baseConstructor);
        ilGenerator.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Crea una instancia de regla dinámica basada en su configuración
    /// </summary>
    /// <param name="configuration">Configuración de la regla</param>
    /// <param name="values">Valores de configuración (JSON)</param>
    /// <returns>Instancia de la regla dinámica</returns>
    public static IRule? CreateDynamicRule(DynamicRuleConfiguration configuration, JObject? values)
    {
        // Si ya se generó un tipo dinámico, usarlo

        if (configuration.Type != null)
        {
            if (Activator.CreateInstance(configuration.Type) is JsDynamicScriptRule instance)
            {
                instance.Configure(configuration, values);
                return instance as IRule;
            }
        }

        return null; // CS8604 fixed by allowing values to be null
    }


    /// <summary>
    /// Valida que una configuración tenga los campos requeridos
    /// </summary>
    /// <param name="configuration">Configuración a validar</param>
    /// <param name="errors">Lista donde se añadirán los errores encontrados</param>
    private static bool ValidateConfiguration(DynamicRuleConfiguration configuration, List<string> errors)
    {
        int count = errors.Count;
        if (string.IsNullOrWhiteSpace(configuration.RuleId))
        {
            errors.Add("RuleId es requerido");
        }

        if (string.IsNullOrWhiteSpace(configuration.Expression))
        {
            errors.Add("Expression es requerido");
        }

        if (configuration.RuleType == JsRuleType.Unknown)
        {
            errors.Add("RuleType es requerido");
        }

        return count == errors.Count;
    }


}
