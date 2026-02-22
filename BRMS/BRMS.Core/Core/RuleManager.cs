using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Constants;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using CustomValidationAttribute = BRMS.Core.Attributes.CustomValidationAttribute;

namespace BRMS.Core.Core;

/// <summary>
/// Factory para registro y resolución de reglas, validadores y normalizadores.
/// Proporciona un registro centralizado thread-safe para todas las reglas del sistema.
/// </summary>
public static class RuleManager
{
    private static readonly ConcurrentDictionary<string, (Func<JObject?, IRule> Factory, Type RuleType)> _ruleFactories = new();
    private static readonly Dictionary<string, RuleDescription> _ruleDescriptionCache = [];
    private static readonly Lock _lock = new();
    private static IServiceProvider? _serviceProvider;

    public static void SetServiceProvider(IServiceProvider serviceProvider) { _serviceProvider = serviceProvider; }

    /// <summary>
    /// Registra una factory de reglas configurables en el sistema.
    /// Úsala para reglas que pueden ser configuradas desde JSON dinámicamente.
    /// Ejemplo: (json) => JsonConvert.DeserializeObject&lt;RangeValidator&gt;(json.ToString())
    /// </summary>
    /// <param name="ruleFactory">Factory function que crea una instancia de la regla desde configuración JSON</param>
    /// <param name="ruleId">Id de la regla, a la que se referiran para buscarla. Si no se suministra, se sa el nombre de la clase sin sufijo</param>
    /// <exception cref="ArgumentNullException">Cuando ruleFactory es null</exception>
    public static void AddRule<T>(Func<JObject?, T> ruleFactory, string? ruleId = null) where T : class, IRule
    {
        // Obtener el tipo de la regla a partir del parámetro genérico T.
        AddRule(typeof(T), ruleFactory, ruleId);
    }

    /// <summary>
    /// Registra una factory de reglas configurables en el sistema.
    /// Úsala para reglas que pueden ser configuradas desde JSON dinámicamente.
    /// Ejemplo: (json) => JsonConvert.DeserializeObject&lt;RangeValidator&gt;(json.ToString())
    /// </summary>
    /// <param name="ruleType">Tipo de la regla a registrar</param>
    /// <param name="ruleFactory">Factory function que crea una instancia de la regla desde configuración JSON</param>
    /// <param name="ruleId">Id de la regla, a la que se referiran para buscarla. Si no se suministra, se sa el nombre de la clase sin sufijo</param>
    /// <exception cref="ArgumentNullException">Cuando ruleFactory es null</exception>
    /// <exception cref="ArgumentException">Cuando ruleType no implementa IRule</exception>
    public static void AddRule(Type ruleType, Func<JObject?, IRule>? ruleFactory = null, string? ruleId = null)
    {
        ArgumentNullException.ThrowIfNull(ruleType, nameof(ruleType));
        // Comprueba que ruleType implementa IRule
        if (!typeof(IRule).IsAssignableFrom(ruleType))
        {
            throw new ArgumentException($"El tipo {ruleType.Name} no implementa la interfaz IRule.", nameof(ruleType));
        }

        if (string.IsNullOrWhiteSpace(ruleId))
        {
            var ruleNameAttribute = ruleType.GetCustomAttributes(typeof(RuleNameAttribute), false).FirstOrDefault() as RuleNameAttribute;
            ruleId = GetUniqueName(ruleNameAttribute?.Name);
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                ruleId = GetUniqueName(ruleType.GetNameWithoutSuffixes(
                    typeof(IValidator).Name[1..],
                    typeof(IRule).Name[1..],
                    typeof(INormalizer).Name[1..],
                    typeof(IDataTransform).Name[1..]))!;
            }
        }
        ruleFactory ??= FactoryFromType(ruleType);
        IRule factoryAdapter(JObject? json) => ruleFactory(json);

        _ = _ruleFactories.AddOrUpdate(
            ruleId,
            (factoryAdapter, ruleType),
            (key, oldValue) => (factoryAdapter, ruleType)
        );

        if (_ruleDescriptionCache.Count > 0)
        {
            _ruleDescriptionCache.Add(ruleId, GetRuleDescription(ruleType));
        }

    }
    /// <summary>
    /// Inspecciona todas las clases del proyecto en busca de implementaciones de IRule no abstractas
    /// y las registra automáticamente con RuleManager.AddRule()
    /// </summary>
    public static void RegisterRulesFromAssemby(Assembly assembly)
    {
        assembly ??= Assembly.GetExecutingAssembly();
        foreach (Type? ruleType in assembly.GetTypes()
            .Where(type => typeof(IRule).IsAssignableFrom(type) &&
                          !type.IsAbstract &&
                          !type.IsInterface))
        {

            AddRule(ruleType);
        }
    }

    /// <summary>
    /// Desregistra todas las reglas de un assembly específico del sistema.
    /// </summary>
    /// <param name="assembly">Assembly del cual desregistrar las reglas</param>
    public static void UnregisterRulesFromAssembly(Assembly assembly)
    {
        assembly ??= Assembly.GetExecutingAssembly();
        var rulesToRemove = new List<string>();

        foreach (Type? ruleType in assembly.GetTypes()
            .Where(type => typeof(IRule).IsAssignableFrom(type) &&
                          !type.IsAbstract &&
                          !type.IsInterface))
        {
            // Buscar la regla registrada por este tipo
            var ruleNameAttribute = ruleType.GetCustomAttributes(typeof(RuleNameAttribute), false).FirstOrDefault() as RuleNameAttribute;
            string? ruleId = GetUniqueName(ruleNameAttribute?.Name);
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                ruleId = GetUniqueName(ruleType.GetNameWithoutSuffixes(
                    typeof(IValidator).Name[1..],
                    typeof(IRule).Name[1..],
                    typeof(INormalizer).Name[1..],
                    typeof(IDataTransform).Name[1..]))!;
            }

            if (!string.IsNullOrWhiteSpace(ruleId) && _ruleFactories.ContainsKey(ruleId))
            {
                rulesToRemove.Add(ruleId);
            }
        }

        // Remover las reglas encontradas
        foreach (string ruleId in rulesToRemove)
        {
            if (_ruleFactories.TryRemove(ruleId, out _))
            {
                lock (_lock)
                {
                    _ = _ruleDescriptionCache.Remove(ruleId);
                }
            }
        }
    }
    /// <summary>
    /// Obtiene una regla del tipo especificado por su nombre.
    /// </summary>
    /// <typeparam name="RuleType">Tipo de regla a obtener</typeparam>
    /// <param name="name">Nombre único de la regla</param>
    /// <param name="ruleJson">Configuración JSON opcional para la regla</param>
    /// <returns>Instancia de la regla o null si no se encuentra</returns>
    public static RuleType? GetRule<RuleType>(string name, JObject? ruleJson = null) where RuleType : IRule
    {
        if (string.IsNullOrEmpty(name))
        {
            return default;
        }

        if (_ruleFactories.TryGetValue(name, out (Func<JObject?, IRule> Factory, Type RuleType) factoryTuple))
        {
            IRule rule = factoryTuple.Factory(ruleJson);
            if (rule is RuleType result)
            {
                return result;
            }
        }

        return default;
    }


    internal static Func<JObject?, IRule> FactoryFromType(Type ruleType)
    {
        return json =>
        {
            // Si hay IServiceProvider disponible, intentar crear con DI
            object? diInstance = null;
            if (_serviceProvider != null)
            {
                try
                {

                    IOrderedEnumerable<ConstructorInfo> ctors = ruleType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                        .OrderByDescending(c => c.GetParameters().Length);
                    foreach (ConstructorInfo? ctor in ctors)
                    {
                        ParameterInfo[] parameters = ctor.GetParameters();
                        object?[] args = new object?[parameters.Length];
                        bool canCreate = true;
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            ParameterInfo p = parameters[i];
                            object? service = _serviceProvider.GetService(p.ParameterType);
                            if (service == null)
                            {
                                if (p.HasDefaultValue)
                                {
                                    args[i] = p.DefaultValue;
                                }
                                else
                                {
                                    canCreate = false;
                                    break;
                                }
                            }
                            else
                            {
                                args[i] = service;
                            }
                        }
                        if (canCreate)
                        {
                            diInstance = ctor.Invoke(args);
                            break;
                        }
                    }
                }
                catch
                {
                    diInstance = null;
                }
            }

            if (json == null)
            {

                if (diInstance is IRule diRuleNoJson)
                {
                    return diRuleNoJson;
                }


                ConstructorInfo? defaultCtor = ruleType
                    .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(c => c.GetParameters().Length == 0);

                return defaultCtor != null ? (IRule)defaultCtor.Invoke(null)! : (IRule)RuntimeHelpers.GetUninitializedObject(ruleType);
            }

            // Cuando hay JSON de configuración, crear instancia (idealmente con DI) y poblarla
            var serializer = new JsonSerializer
            {
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
            };

            if (diInstance != null)
            {
                serializer.Populate(json.CreateReader(), diInstance);
                return (IRule)diInstance;
            }

            // Fallback: crear desde JSON directamente
            return (IRule)json.ToObject(ruleType, serializer)!;

        };
    }
    /// <summary>
    /// Obtiene el nombre de la regla por su tipo. Si no existe la registra.
    /// </summary>
    /// <param name="ruleType">Tipo de regla a obtener</param>
    /// <returns>Nombre de la regla o null si no se encuentra</returns>
    public static string GetRuleName(Type ruleType)
    {
        string name = _ruleFactories.FirstOrDefault(k => k.Value.RuleType == ruleType).Key;
        if (string.IsNullOrEmpty(name))
        {
            name = GetUniqueName(ruleType.GetNameWithoutSuffixes(
                typeof(IValidator).Name[1..],
                typeof(IRule).Name[1..],
                typeof(INormalizer).Name[1..],
                typeof(IDataTransform).Name[1..]))!;

            AddRule(
                ruleType,
                FactoryFromType(ruleType),
                name
            );
        }

        return name;
    }
    public static RuleType? GetRule<RuleType>(string name, string ruleJsonString) where RuleType : IRule
    {
        // Parsea el string a JObject de forma segura, manejando el caso de que sea nulo o vacío,
        // y reutiliza la lógica del método que ya acepta un JObject.
        JObject? ruleJson = string.IsNullOrEmpty(ruleJsonString) ? null : JObject.Parse(ruleJsonString);
        return GetRule<RuleType>(name, ruleJson);
    }

    public static IEnumerable<RuleDescription> GetAllRuleDescriptions()
    {
        return GetAllRuleDescriptions(null);
    }

    public static IEnumerable<RuleDescription> GetAllRuleDescriptions(CultureInfo? culture)
    {
        if (culture == null)
        {
            if (_ruleDescriptionCache.Count == 0)
            {
                _lock.Enter();
                try
                {
                    foreach (KeyValuePair<string, (Func<JObject?, IRule> Factory, Type RuleType)> v in _ruleFactories)
                    {
                        _ruleDescriptionCache.Add(v.Key, GetRuleDescription(v.Value.RuleType));
                    }
                }
                finally
                {
                    _lock.Exit();
                }
            }
            return _ruleDescriptionCache.Values;
        }

        // Return fresh descriptions for the specific culture
        var descriptions = new List<RuleDescription>();
        foreach (KeyValuePair<string, (Func<JObject?, IRule> Factory, Type RuleType)> v in _ruleFactories)
        {
            descriptions.Add(GetRuleDescription(v.Value.RuleType, culture));
        }
        return descriptions;
    }



    private static string GetFirstDescriptionFromClass(Type type)
    {
        while (type != null && type != typeof(object))
        {
            DescriptionAttribute? attr = type.GetCustomAttribute<DescriptionAttribute>(inherit: false);
            if (attr != null)
            {
                return attr.Description;
            }

            type = type.BaseType!;
        }

        return "";
    }

    public static RuleDescription? GetRuleDescription(string ruleId)
    {
        if (_ruleDescriptionCache.Count == 0)
        {
            _ = GetAllRuleDescriptions();
        }
        _ = _ruleDescriptionCache.TryGetValue(ruleId, out RuleDescription? description);
        return description;
    }

    private static MethodInfo? FindStaticMethodInHierarchy(Type initialType)
    {
        Type? currentType = initialType;

        while (currentType != null && currentType != typeof(object))
        {
            // 1. Usa BindingFlags.DeclaredOnly
            // Esto garantiza que solo miramos los métodos definidos en la CLASE ACTUAL.
            MethodInfo? staticMethod = currentType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.GetCustomAttribute<StaticRuleDescriptionAttribute>() != null &&
                                    m.ReturnType == typeof(RuleDescription) &&
                                    m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));

            if (staticMethod != null)
            {
                return staticMethod;
            }

            // 2. Sube un nivel en la jerarquía
            currentType = currentType.BaseType;
        }

        return null; // No se encontró el método en ninguna clase de la jerarquía
    }

    /// <summary>
    /// Obtiene la descripción de una regla a partir de su tipo.
    /// </summary>
    /// <param name="ruleType">El tipo de la regla.</param>
    /// <returns>Una instancia de RuleDescription con la metainformación de la regla.</returns>
    /// <exception cref="ArgumentNullException">Thrown if ruleType is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the rule type does not implement IRule or does not have a parameterless constructor.</exception>
    public static RuleDescription GetRuleDescription(Type ruleType)
    {
        return GetRuleDescription(ruleType, null);
    }

    public static RuleDescription GetRuleDescription(Type ruleType, CultureInfo? culture)
    {
        ArgumentNullException.ThrowIfNull(ruleType, nameof(ruleType));
        if (!typeof(IRule).IsAssignableFrom(ruleType))
        {
            throw new ArgumentException(ResourcesManager.GetLocalizedMessage("ERROR_RuleTypeDoesNotImplementIRule", culture?.Name, (object)ruleType.Name), nameof(ruleType));
        }

        // Buscar método estático marcado con StaticRuleDescriptionAttribute
        MethodInfo? staticMethod = FindStaticMethodInHierarchy(ruleType);

        if (staticMethod != null)
        {
            try
            {
                if (staticMethod.Invoke(null, [ruleType]) is RuleDescription result)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                // Log error but continue with normal processing
                System.Diagnostics.Debug.WriteLine($"Error executing static rule description method for {ruleType.Name}: {ex.Message}");
            }
        }

        // Continuar con el procesamiento normal si no hay método estático o falló
        string name = (ruleType.GetCustomAttributes(typeof(RuleNameAttribute), inherit: false).FirstOrDefault() as RuleNameAttribute)?.Name ??
                GetRuleName(ruleType);

        string description = GetFirstDescriptionFromClass(ruleType);
        //(ruleType.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false).FirstOrDefault() as DescriptionAttribute)?.Description ?? "";
        if (description != "")
        {
            description = ResourcesManager.GetLocalizedMessage(description, culture?.Name);
        }

        var schema = new JsonSchema
        {
            Type = JsonObjectType.Object
        };

        var nullabilityContext = new NullabilityInfoContext();
        foreach (PropertyInfo prop in from p in ruleType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                      where p.CanWrite && !Attribute.IsDefined(p, typeof(ExcludeFromSchemaAttribute)) && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute))
                                      select p)
        {
            string propertyName = prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ??
                (char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]);

            var propSchema = new JsonSchemaProperty();

            Type underlyingType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (underlyingType == typeof(string))
            {
                propSchema.Type = JsonObjectType.String;
            }
            else if (underlyingType == typeof(int) || underlyingType == typeof(long))
            {
                propSchema.Type = JsonObjectType.Integer;
            }
            else if (underlyingType == typeof(double) || underlyingType == typeof(decimal))
            {
                propSchema.Type = JsonObjectType.Number;
            }
            else if (underlyingType == typeof(bool))
            {
                propSchema.Type = JsonObjectType.Boolean;
            }
            else if (underlyingType == typeof(DateTime))
            {
                propSchema.Type = JsonObjectType.String;
                propSchema.Format = "date-time";
                propSchema.Description = "Formato: ISO 8601 (YYYY-MM-DDTHH:mm:ss.sssZ)";
            }
            else if (underlyingType == typeof(DateOnly))
            {
                propSchema.Type = JsonObjectType.String;
                propSchema.Format = "date";
                propSchema.Description = "Formato: YYYY-MM-DD";
            }
            else if (underlyingType.IsEnum)
            {
                propSchema.Type = JsonObjectType.String;
                IEnumerable<object> enumValues = Enum.GetValues(underlyingType).Cast<object>();
                foreach (object enumValue in enumValues)
                {
                    propSchema.Enumeration.Add(enumValue.ToString()!);
                }

                IEnumerable<string> enumNames = enumValues.Select(v => v.ToString()!);
                propSchema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_ValidValuesTemplate", culture?.Name, (object)string.Join(", ", enumNames));
            }
            else if (underlyingType.IsGenericType && underlyingType.GetInterfaces()
                    .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            {
                propSchema.Type = JsonObjectType.Array;
                Type elementType = underlyingType.GetGenericArguments()[0];
                Type elementUnderlyingType = Nullable.GetUnderlyingType(elementType) ?? elementType;
                var itemSchema = new JsonSchema();
                if (elementUnderlyingType == typeof(string))
                {
                    itemSchema.Type = JsonObjectType.String;
                }
                else if (elementUnderlyingType == typeof(int) || elementUnderlyingType == typeof(long))
                {
                    itemSchema.Type = JsonObjectType.Integer;
                }
                else if (elementUnderlyingType == typeof(double) || elementUnderlyingType == typeof(decimal))
                {
                    itemSchema.Type = JsonObjectType.Number;
                }
                else if (elementUnderlyingType == typeof(bool))
                {
                    itemSchema.Type = JsonObjectType.Boolean;
                }
                else if (elementUnderlyingType == typeof(DateTime))
                {
                    itemSchema.Type = JsonObjectType.String;
                    itemSchema.Format = "date-time";
                }
                else if (elementUnderlyingType == typeof(DateOnly))
                {
                    itemSchema.Type = JsonObjectType.String;
                    itemSchema.Format = "date";
                }
                else if (elementUnderlyingType.IsEnum)
                {
                    itemSchema.Type = JsonObjectType.String;
                    IEnumerable<object> enumValues2 = Enum.GetValues(elementUnderlyingType).Cast<object>();
                    foreach (object enumValue2 in enumValues2)
                    {
                        itemSchema.Enumeration.Add(enumValue2.ToString()!);
                    }
                    IEnumerable<string?> enumNames2 = enumValues2.Select(v => v.ToString());
                    itemSchema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_ValidValuesTemplate", culture?.Name, (object)string.Join(", ", enumNames2));
                }
                else
                {
                    itemSchema.Type = JsonObjectType.None;
                }

                propSchema.Items.Add(itemSchema);
            }
            ApplyValidationAttributes(prop, propSchema, culture);
            DefaultValueAttribute? defaultValueAttribute = prop.GetCustomAttribute<DefaultValueAttribute>(true);
            //bool hasInitSetter = prop.SetMethod != null && prop.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains<Type>(typeof(IsExternalInit));

            if ((prop.GetCustomAttribute<RequiredAttribute>(true) != null ||
                !(nullabilityContext.Create(prop).ReadState == NullabilityState.Nullable) //||
                /*hasInitSetter*/) && !schema.RequiredProperties.Contains(propertyName))
            {
                schema.RequiredProperties.Add(propertyName);
            }

            if (defaultValueAttribute != null)
            {

                propSchema.Default = defaultValueAttribute.Value;

            }
            schema.Properties.Add(propertyName, propSchema);
        }

        ruleType.GetCustomAttribute<CustomJsonSchemaAttribute>(true)?.CustomizeSchema(schema, ruleType);

        RuleInputType[] inputTypes = ruleType.GetCustomAttribute<SupportedTypesAttribute>(true)?.Types ?? [RuleInputType.Any];
        return new RuleDescription
        {
            Id = name,
            Description = description,
            Parameters = schema,
            Type = ruleType,
            InputTypes = inputTypes,
            Example = GenerateExampleFromSchema(ruleType, schema, name)
        };
    }

    /// <summary>
    /// Genera un ejemplo de uso basado en el esquema JsonSchema proporcionado.
    /// </summary>
    /// <param name="ruleType">Tipo de la regla</param>
    /// <param name="schema">El esquema JsonSchema del cual generar el ejemplo</param>
    /// <param name="ruleId">Identificador de la regla</param>
    /// <returns>Un JObject con valores de ejemplo o null si el esquema es null</returns>
    private static JObject? GenerateExampleFromSchema(Type ruleType, JsonSchema? schema, string ruleId)
    {
        if (schema?.Properties == null || schema.Properties.Count == 0)
        {
            return null;
        }

        if (ruleType.GetCustomAttribute<SampleValueAttribute>() is SampleValueAttribute sample && sample.TokenValue is JObject jObject)
        {
            return jObject;
        }

        var example = new JObject();

        foreach (KeyValuePair<string, JsonSchemaProperty> property in schema.Properties)
        {
            string propertyName = property.Key;
            JsonSchemaProperty propertySchema = property.Value;
            if (propertyName == "ruleId")
            {
                example[propertyName] = JToken.FromObject(ruleId);
                continue;
            }

            JToken? exampleValue;
            if (ruleType.GetProperty($"{propertyName[..1].ToUpper()}{propertyName[1..]}")?.GetCustomAttribute<SampleValueAttribute>(true) is SampleValueAttribute sampleProp)
            {
                exampleValue = sampleProp.TokenValue;
            }
            else
            {
                // Si hay un valor por defecto, usarlo
                if (propertySchema.Default != null)
                {
                    exampleValue = JToken.FromObject(propertySchema.Default);
                }
                else
                {
                    // Generar valor basado en el tipo
                    exampleValue = propertySchema.Type switch
                    {
                        JsonObjectType.String => propertySchema.Enumeration.Count > 0
                                                        ? JToken.FromObject(propertySchema?.Enumeration?.First()!)
                                                        : propertySchema.Format == "date-time"
                                                            ? (JToken)DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                                                            : propertySchema.Format == "date" ? (JToken)DateTime.Now.ToString("yyyy-MM-dd") : (JToken)"example_value",
                        JsonObjectType.Integer => (JToken)0,
                        JsonObjectType.Number => (JToken)0.0,
                        JsonObjectType.Boolean => (JToken)false,
                        JsonObjectType.Array => new JArray(),
                        JsonObjectType.Object => new JObject(),
                        _ => null,
                    };
                }
            }

            if (exampleValue != null)
            {
                example[propertyName] = exampleValue;
            }
        }

        return example.HasValues ? example : null;
    }

    /// <summary>
    /// Valida una lista de configuraciones de reglas contra sus esquemas JsonSchema correspondientes.
    /// Verifica que cada JObject tenga la propiedad 'name' y que las demás propiedades cumplan con el esquema de la regla.
    /// </summary>
    /// <param name="ruleConfigurations">Lista de configuraciones de reglas a validar</param>
    /// <param name="schema">Esquema contra el cual validar</param>
    /// <returns>Lista de errores de validación encontrados</returns>
    public static IEnumerable<string> ValidateRuleConfigurations(IEnumerable<JObject> ruleConfigurations, JsonSchema schema)
    {
        var errors = new List<string>();

        if (ruleConfigurations == null)
        {
            errors.Add("La lista de configuraciones de reglas no puede ser null");
            return errors;
        }

        var ruleDescriptions = GetAllRuleDescriptions().ToDictionary(rd => rd.Id, rd => rd);

        foreach ((JObject? configuration, int index) in ruleConfigurations.Select((config, idx) => (config, idx)))
        {
            if (configuration == null)
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_ConfigurationCannotBeNull", index, "Unknown"));
                continue;
            }

            // Verificar que tenga las propiedad 'name'
            string nameValue;
            if (!configuration.TryGetValue("name", out JToken? propertyToken) || propertyToken == null || string.IsNullOrWhiteSpace(nameValue = propertyToken.ToString()))
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_MissingName", (object)"name"));
                continue;
            }
            // verificar la propiedad propertyPath
            string propertyPathValue;
            if (!configuration.TryGetValue("propertyPath", out propertyToken) || propertyToken == null || string.IsNullOrWhiteSpace(propertyPathValue = propertyToken.ToString()))
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_MissingName", (object)"propertyValue"));
                continue;
            }
            if (!schema.PropertyExists(propertyPathValue))
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_PropertyPathNotExists", index, nameValue, propertyPathValue));
                continue;
            }
            if (!ruleDescriptions.TryGetValue(nameValue, out RuleDescription? ruleDescription))
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_RuleNotFound", (object)nameValue));
                continue;
            }

            if (ruleDescription.Parameters != null)
            {
                try
                {
                    //var errorHandler = new SchemaValidationErrorHandler();
                    bool isValid = configuration.IsValid(ruleDescription.Parameters, out IList<string> schemaErrors);
                    if (!isValid)
                    {
                        //foreach (var error in schemaErrors)
                        //{
                        //    errorHandler.AddError(error);
                        //}
                        foreach (string translatedError in schemaErrors)
                        {
                            errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_SchemaError", index, nameValue, translatedError));
                        }
                        continue; // Si falla la validación del esquema, pasamos a la siguiente configuración
                    }

                    // 2. Aplicar validaciones personalizadas si existen
                    IEnumerable<CustomValidationAttribute> customValidations = ruleDescription.Type.GetCustomAttributes<Attributes.CustomValidationAttribute>(false);
                    foreach (CustomValidationAttribute validation in customValidations)
                    {
                        IEnumerable<string> customErrors = validation.ValidateConfiguration(configuration);
                        foreach (string error in customErrors)
                        {
                            errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_CustomError", index, nameValue, error));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_ErrorDuringValidation", index, nameValue, ex.Message));
                }
            }
            else
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_NoParameterSchema", index, nameValue));
            }



        }

        return errors;
    }

    public static IEnumerable<string> ValidateRuleConfiguration(JObject configuration, Type ruleType)
    {
        ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));
        ArgumentNullException.ThrowIfNull(ruleType, nameof(ruleType));
        List<string> errors = [];
        try
        {
            RuleDescription ruleDescription = GetRuleDescription(ruleType);
            if (ruleDescription.Parameters == null)
            {
                errors.Add("La regla '" + ruleType.Name + "' no tiene esquema de parámetros definido.");
                return errors;
            }
            var errorHandler = new SchemaValidationErrorHandler();
            if (!configuration.IsValid(ruleDescription.Parameters, out IList<string> schemaErrors))
            {
                foreach (string error in schemaErrors)
                {
                    errorHandler.AddError(error);
                }
                foreach (string translatedError in errorHandler.Errors)
                {
                    errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_SchemaError", 0, ruleType.Name, translatedError));
                }
                return errors;
            }
            foreach (CustomValidationAttribute customAttribute in ruleType.GetCustomAttributes<CustomValidationAttribute>(inherit: true))
            {
                foreach (string error2 in customAttribute.ValidateConfiguration(configuration))
                {
                    errors.Add("Configuración inválida para regla '" + ruleType.Name + "': " + error2);
                }
            }
        }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message);
        }
        catch (Exception ex2)
        {
            errors.Add("Error durante la validación del esquema para regla '" + ruleType.Name + "': " + ex2.Message);
        }
        return errors;
    }

    public static IEnumerable<string> ValidateRuleConfigurations(IEnumerable<JObject> ruleConfigurations)
    {
        List<string> errors = [];
        if (ruleConfigurations == null)
        {
            errors.Add("La lista de configuraciones de reglas no puede ser null");
            return errors;
        }

        //Dictionary<string, RuleDescription> ruleDescriptions = GetAllRuleDescriptions().ToDictionary((RuleDescription rd) => rd.Id, (RuleDescription rd) => rd);

        foreach ((JObject? configuration, int index) in ruleConfigurations
            .Select((config, idx) => (config, idx)))
        {
            if (configuration == null)
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_ConfigurationCannotBeNull", index, "Unknown"));
                continue;
            }
            if (!configuration.TryGetValue("ruleId", out JToken? nameToken) || nameToken == null)
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_MissingName"));
                continue;
            }

            string ruleId = nameToken.ToString();
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_NameCannotBeEmpty", index, "Unknown"));
                continue;
            }

            RuleDescription? ruleDescription = GetRuleDescription(ruleId);

            if (ruleDescription == null)
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_RuleNotFound", (object)ruleId));
            }

            else if (ruleDescription.Parameters != null)
            {
                try
                {
                    SchemaValidationErrorHandler errorHandler = new();

                    // Revisar propiedades requeridas faltantes con valores por defecto

                    object rule = configuration.ToObject(ruleDescription.Type)!;
                    var obj = JObject.FromObject(rule, new JsonSerializer
                    {
                        NullValueHandling = NullValueHandling.Include
                    });
                    // sabemos que deserializa, pero no si cumple con las restricciones del JsonSchema
                    if (obj.IsValid(ruleDescription.Parameters, out IList<string> schemaErrors))
                    {
                        foreach (string error in schemaErrors)
                        {
                            errorHandler.AddError(error);
                        }
                        foreach (string translatedError in errorHandler.Errors)
                        {
                            errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_SchemaError", index, ruleId, translatedError));
                        }
                        continue;
                    }
                    foreach (CustomValidationAttribute customAttribute in ruleDescription.Type.GetCustomAttributes<CustomValidationAttribute>(inherit: true))
                    {
                        foreach (string error2 in customAttribute.ValidateConfiguration(configuration))
                        {
                            errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_CustomError", index, ruleId, error2));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_ErrorDuringValidation", index, ruleId, ex.Message));
                }
            }
            else
            {
                errors.Add(ResourcesManager.GetLocalizedMessage("VALIDATION_NoParameterSchema", index, ruleId));
            }
        }
        return errors;
    }

    public static void ApplyValidationAttributes(PropertyInfo propertyInfo, JsonSchema schema)
    {
        ApplyValidationAttributes(propertyInfo, schema, null);
    }

    public static void ApplyValidationAttributes(PropertyInfo propertyInfo, JsonSchema schema, CultureInfo? culture)
    {
        object[] customAttributes = propertyInfo.GetCustomAttributes(inherit: true);
        foreach (object attribute in customAttributes)
        {
            if (attribute is DescriptionAttribute descriptionAttribute)
            {
                schema.Description = ResourcesManager.GetLocalizedMessage(descriptionAttribute.Description, culture?.Name);
                continue;
            }
            if (attribute is not RangeAttribute rangeAttr)
            {
                if (attribute is not StringLengthAttribute strLengthAttr)
                {
                    if (attribute is not MinLengthAttribute minLengthAttr)
                    {
                        if (attribute is not MaxLengthAttribute maxLengthAttr)
                        {
                            if (attribute is not RegularExpressionAttribute regexAttr)
                            {
                                if (attribute is not EmailAddressAttribute)
                                {
                                    if (attribute is not PhoneAttribute)
                                    {
                                        if (attribute is UrlAttribute)
                                        {
                                            schema.Format = "uri";
                                        }
                                    }
                                    else
                                    {
                                        schema.Format = "phone";
                                    }
                                }
                                else
                                {
                                    schema.Format = "email";
                                }
                            }
                            else
                            {
                                schema.Pattern = regexAttr.Pattern;
                            }
                            continue;
                        }
                        if (schema.Type == JsonObjectType.String)
                        {
                            schema.MaxLength = maxLengthAttr.Length;
                        }
                        else if (schema.Type == JsonObjectType.Array)
                        {
                            schema.MaxItems = maxLengthAttr.Length;
                        }
                        string des = schema.Type == JsonObjectType.Array
                            ? ResourcesManager.GetLocalizedMessage("SchemaFormats_MaximumItemsTemplate", culture?.Name, maxLengthAttr.Length)
                            : ResourcesManager.GetLocalizedMessage("SchemaFormats_MaximumLengthTemplate", culture?.Name, maxLengthAttr.Length);

                        if (string.IsNullOrEmpty(schema.Description))
                        {
                            schema.Description = des;
                        }
                        else
                        {
                            schema.Description += ". " + des;
                        }
                        continue;
                    }
                    if (schema.Type == JsonObjectType.String)
                    {
                        schema.MinLength = minLengthAttr.Length;
                    }
                    else if (schema.Type == JsonObjectType.Array)
                    {
                        schema.MinItems = minLengthAttr.Length;
                    }
                    string desc = schema.Type == JsonObjectType.Array
                        ? ResourcesManager.GetLocalizedMessage("SchemaFormats_MinimumItemsTemplate", culture?.Name, minLengthAttr.Length)
                        : ResourcesManager.GetLocalizedMessage("SchemaFormats_MinimumLengthTemplate", culture?.Name, minLengthAttr.Length);

                    if (string.IsNullOrEmpty(schema.Description))
                    {
                        schema.Description = desc;
                    }
                    else
                    {
                        schema.Description += ". " + desc;
                    }
                    continue;
                }
                if (strLengthAttr.MinimumLength > 0)
                {
                    schema.MinLength = strLengthAttr.MinimumLength;
                }
                if (strLengthAttr.MaximumLength > 0)
                {
                    schema.MaxLength = strLengthAttr.MaximumLength;
                }
                var lengthDesc = new List<string>();
                if (strLengthAttr.MinimumLength > 0)
                {
                    lengthDesc.Add(ResourcesManager.GetLocalizedMessage("SchemaFormats_MinimumLengthTemplate", culture?.Name, strLengthAttr.MinimumLength));
                }
                if (strLengthAttr.MaximumLength > 0)
                {
                    lengthDesc.Add(ResourcesManager.GetLocalizedMessage("SchemaFormats_MaximumLengthTemplate", culture?.Name, strLengthAttr.MaximumLength));
                }

                if (lengthDesc.Count > 0)
                {
                    string joined = string.Join(", ", lengthDesc);
                    schema.Description = string.IsNullOrEmpty(schema.Description) ? joined : schema.Description + ". " + joined;
                }
                continue;
            }
            if (rangeAttr.Minimum != null)
            {
                if (schema.Type == JsonObjectType.Integer)
                {
                    schema.Minimum = Convert.ToInt64(rangeAttr.Minimum);
                }
                else if (schema.Type == JsonObjectType.Number)
                {
                    schema.Minimum = Convert.ToDecimal(rangeAttr.Minimum);
                }
                else if (schema.Type == JsonObjectType.String && rangeAttr.Minimum is DateTime minDate)
                {
                    string minDateStr = minDate.ToString("yyyy-MM-dd");
                    if (string.IsNullOrEmpty(schema.Pattern))
                    {
                        schema.Pattern = ".*";
                    }
                    string desc = ResourcesManager.GetLocalizedMessage("SchemaFormats_MinimumDateTemplate", culture?.Name, minDateStr);
                    if (string.IsNullOrEmpty(schema.Description))
                    {
                        schema.Description = desc;
                    }
                    else
                    {
                        schema.Description += ". " + desc;
                    }
                }
            }
            if (rangeAttr.Maximum == null)
            {
                continue;
            }
            if (schema.Type == JsonObjectType.Integer)
            {
                schema.Maximum = Convert.ToInt64(rangeAttr.Maximum);
            }
            else if (schema.Type == JsonObjectType.Number)
            {
                schema.Maximum = Convert.ToDecimal(rangeAttr.Maximum);
            }
            else if (schema.Type == JsonObjectType.String && rangeAttr.Maximum is DateTime maxDate)
            {
                string maxDateStr = maxDate.ToString("yyyy-MM-dd");
                string desc = ResourcesManager.GetLocalizedMessage("SchemaFormats_MaximumDateTemplate", culture?.Name, maxDateStr);
                schema.Description = string.IsNullOrEmpty(schema.Description) ? desc : schema.Description + ", " + desc;
            }

        }
    }

    private static string? GetUniqueName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        int index = 0;
        string safeName = name;
        while (_ruleFactories.ContainsKey(safeName))
        {
            safeName = $"{name}_{index++}";
        }
        return safeName;
    }

    private static bool PropertyExists(this JsonSchema schema, string jsonPath)
    {
        // Quitar el "$." inicial si existe
        if (jsonPath.StartsWith("$."))
        {
            jsonPath = jsonPath[2..];
        }

        string[] partes = jsonPath.Split('.');
        return HasProperty(schema, partes, 0);
    }

    private static bool HasProperty(JsonSchema schema, string[] partes, int indice)
    {
        if (indice >= partes.Length)
        {
            return true;
        }

        string parte = partes[indice];

        // Si es un objeto, buscar en sus propiedades
        if (schema != null && schema.Type == JsonObjectType.Object)
        {
            if (schema.Properties.TryGetValue(parte, out JsonSchemaProperty? subSchema))
            {
                return HasProperty(subSchema, partes, indice + 1);
            }
        }

        // Si es un array, buscar en sus items
        if (schema != null && schema.Type == JsonObjectType.Array)
        {
            foreach (JsonSchema itemSchema in schema.Items)
            {
                if (HasProperty(itemSchema, partes, indice))
                {
                    return true;
                }
            }
        }

        // Si tiene combinaciones, buscar en cada subesquema
        foreach (JsonSchema? combo in schema?.AllOf.Concat(schema?.AnyOf ?? []).Concat(schema?.OneOf ?? []) ?? [])
        {
            if (HasProperty(combo, partes, indice))
            {
                return true;
            }
        }

        return false;
    }
}
