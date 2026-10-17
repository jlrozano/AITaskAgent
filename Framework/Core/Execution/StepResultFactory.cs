using AITaskAgent.Core.Abstractions;
using Newtonsoft.Json;
using NJsonSchema;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;


namespace AITaskAgent.Core.Execution;

internal static class StepResultFactory
{
    internal record StepActivatorInfo(
        Func<IStep, IStepError?, object>? CtorWithError,
        Func<IStep, object>? Ctor,
        PropertyInfo ValueProperty,
        JsonSchema? JsonSchema
    )
    {
        public Type ValueType { get; } = ValueProperty.PropertyType;

        public object CreateInstance(IStep step, IStepError? error = null)
        {
            if (CtorWithError is not null)
            {
                return CtorWithError(step, error);
            }
            else if (Ctor is not null)
            {
                object obj = Ctor(step);
                ((IStepResult)obj).Error = error;
                return obj;
            }
            throw new InvalidOperationException(
                $"No se pudo crear una instancia de {ValueType.Name}. No hay constructor válido.");
        }

        public object CreateInstance(IStep step, object? value, IStepError? error = null)
        {
            object obj = CreateInstance(step, error);

            if (value is not null && !ValueType.IsAssignableFrom(value.GetType()) &&
                !(value is null && !ValueType.IsValueType && Nullable.GetUnderlyingType(ValueType) != null))
            {
                throw new InvalidOperationException(
                    $"No se pudo asignar el valor de tipo {value?.GetType().Name ?? "null"} a la propiedad Value de tipo {ValueType.Name}.");
            }

            if (!ValueProperty.CanWrite)
            {
                throw new InvalidOperationException(
                    $"La propiedad Value en {ValueType.Name} es de solo lectura.");
            }

            ValueProperty.SetValue(obj, value);

            return obj;
        }

    }
    ;

    private static readonly ConcurrentDictionary<Type, StepActivatorInfo> _cache = new();

    public static T CreateStepResult<T>(IStep step, IStepError? error = null)
        where T : IStepResult
    {
        return (T)CreateStepResult(typeof(T), step, error);
    }
    public static T CreateStepResult<T>(IStep step, object? value, IStepError? error = null)
        where T : IStepResult
    {
        return (T)CreateStepResult(typeof(T), step, value, error);
    }

    public static IStepResult CreateStepResult(Type type, IStep step, IStepError? error = null)

    {
        StepActivatorInfo info = GetStepActivatorInfo(type);

        return (IStepResult)info.CreateInstance(step, error);
    }

    public static IStepResult CreateStepResult(Type type, IStep step, object? value, IStepError? error = null)
    {
        StepActivatorInfo info = GetStepActivatorInfo(type);

        return (IStepResult)info.CreateInstance(step, value, error);
    }
    internal static StepActivatorInfo GetStepActivatorInfo(Type type)
    {
        return _cache.GetOrAdd(type, BuildInfo);
    }

    private static StepActivatorInfo BuildInfo(Type type)
    {
        Func<IStep, IStepError?, object>? ctorWithError = null;
        Func<IStep, object>? ctor = null;

        if (!typeof(IStepResult).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"{type.Name} no implementa IStepResult.");
        }

        PropertyInfo valueProperty = GetValuePropertyInfo(type);
        JsonSchema? jsonSchema = BuildJsonSchema(valueProperty.PropertyType);

        ConstructorInfo? ctor2 = type.GetConstructors()
            .FirstOrDefault(c =>
            {
                ParameterInfo[] p = c.GetParameters();
                return p.Length == 2
                    && typeof(IStep).IsAssignableFrom(p[0].ParameterType)
                    && typeof(IStepError).IsAssignableFrom(p[1].ParameterType);
            });

        if (ctor2 is not null)
        {
            ctorWithError = CompileCtorWithError(ctor2);
            return new StepActivatorInfo(ctorWithError, null, valueProperty, jsonSchema);
        }


        ConstructorInfo? ctor1 = type.GetConstructors()
            .FirstOrDefault(c =>
            {
                ParameterInfo[] p = c.GetParameters();
                return p.Length == 1
                    && typeof(IStep).IsAssignableFrom(p[0].ParameterType);
            });

        if (ctor1 is not null)
        {
            ctor = CompileCtor(ctor1);
        }
        else
        {
            // Try constructor with (IStep, T?) where T is the value type
            ConstructorInfo? ctorWithOptionalValue = type.GetConstructors()
                .FirstOrDefault(c =>
                {
                    ParameterInfo[] p = c.GetParameters();
                    return p.Length == 2
                        && typeof(IStep).IsAssignableFrom(p[0].ParameterType)
                        && !typeof(IStepError).IsAssignableFrom(p[1].ParameterType)
                        && (p[1].IsOptional || p[1].HasDefaultValue);
                });

            if (ctorWithOptionalValue is not null)
            {
                ctor = CompileCtorWithOptionalValue(ctorWithOptionalValue);
            }
            else
            {
                // Try any constructor with (IStep, any) pattern - use default value for second param
                ConstructorInfo? ctorWithTwoParams = type.GetConstructors()
                    .FirstOrDefault(c =>
                    {
                        ParameterInfo[] p = c.GetParameters();
                        return p.Length == 2
                            && typeof(IStep).IsAssignableFrom(p[0].ParameterType);
                    });

                if (ctorWithTwoParams is not null)
                {
                    ctor = CompileCtorWithSecondParamDefault(ctorWithTwoParams, valueProperty.PropertyType);
                }
            }
        }

        return new StepActivatorInfo(ctorWithError, ctor, valueProperty, jsonSchema);
    }

    private static PropertyInfo GetValuePropertyInfo(Type type)
    {
        Type? currentType = type;
        while (currentType != null)
        {
            PropertyInfo? prop = currentType.GetProperty(nameof(IStepResult.Value),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            if (prop != null)
            {
                return prop;
            }

            currentType = currentType.BaseType;
        }

        throw new InvalidOperationException($"{type.Name} no tiene propiedad Value.");
    }

    private static JsonSchema? BuildJsonSchema(Type valueType)
    {
        Type actualType = Nullable.GetUnderlyingType(valueType) ?? valueType;

        // Tipos primitivos que no necesitan schema
        if (actualType == typeof(string) ||
            actualType == typeof(object) ||
            actualType.IsPrimitive)
        {
            return null;
        }

        try
        {
            var schema = new JsonSchema()
            {
                Type = JsonObjectType.Object,
                // Providers with strict JSON-schema mode (Groq, OpenAI) reject the schema
                // outright unless every object node sets this explicitly - it is not inferred
                // from the property list alone.
                AllowAdditionalProperties = false
            };

            var visitedTypes = new HashSet<Type> { actualType };
            AddPropertiesFromType(actualType, schema.Properties, schema.RequiredProperties, visitedTypes);
            return schema;
        }
        catch
        {
            return null;
        }
    }

    private static void AddPropertiesFromType(Type type, IDictionary<string, JsonSchemaProperty> properties, ICollection<string> requiredProperties, HashSet<Type> visitedTypes)
    {
        var nullabilityContext = new NullabilityInfoContext();

        foreach (PropertyInfo? prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite &&
                       !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute)) &&
                       !Attribute.IsDefined(p, typeof(ExcludeFromLlmSchemaAttribute))))
        {
            // Nombre de propiedad en camelCase
            JsonPropertyAttribute? jsonPropertyAttr = prop.GetCustomAttribute<JsonPropertyAttribute>();
            string propertyName = jsonPropertyAttr?.PropertyName
                ?? (char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]);

            JsonSchemaProperty propSchema = CreateSchemaForType(prop.PropertyType, visitedTypes);
            ApplyValidationAttributes(prop, propSchema);

            // Aplicar [Description] si existe
            DescriptionAttribute? descAttr = prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            if (descAttr != null)
            {
                propSchema.Description = descAttr.Description;
            }

            // Determinar si es requerida
            NullabilityInfo nullabilityInfo = nullabilityContext.Create(prop);
            bool isNullable = nullabilityInfo.ReadState == NullabilityState.Nullable;
            bool hasInitSetter = prop.SetMethod?.ReturnParameter.GetRequiredCustomModifiers()
                .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) ?? false;
            DefaultValueAttribute? defaultValueAttr = prop.GetCustomAttribute<DefaultValueAttribute>();

            // Required si: [Required] explícito, o no nullable + init setter + sin default value
            if (Attribute.IsDefined(prop, typeof(RequiredAttribute), inherit: true)
                || (!isNullable && hasInitSetter && defaultValueAttr == null))
            {
                if (!requiredProperties.Contains(propertyName))
                {
                    requiredProperties.Add(propertyName);
                }
            }

            // Aplicar valor por defecto si existe
            if (defaultValueAttr != null)
            {
                propSchema.Default = prop.PropertyType.IsEnum && propSchema.Type == JsonObjectType.String
                    ? (defaultValueAttr.Value?.ToString())
                    : defaultValueAttr.Value;
                requiredProperties.Remove(propertyName);
            }

            properties.Add(propertyName, propSchema);
        }
    }

    private static JsonSchemaProperty CreateSchemaForType(Type propertyType, HashSet<Type> visitedTypes)
    {
        var propSchema = new JsonSchemaProperty();
        Type underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (underlyingType == typeof(string))
        {
            propSchema.Type = JsonObjectType.String;
        }
        else if (underlyingType == typeof(int) || underlyingType == typeof(long))
        {
            propSchema.Type = JsonObjectType.Integer;
        }
        else if (underlyingType == typeof(double) || underlyingType == typeof(decimal) || underlyingType == typeof(float))
        {
            propSchema.Type = JsonObjectType.Number;
        }
        else if (underlyingType == typeof(bool))
        {
            propSchema.Type = JsonObjectType.Boolean;
        }
        else if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
        {
            propSchema.Type = JsonObjectType.String;
            propSchema.Format = "date-time";
        }
        else if (underlyingType.IsEnum)
        {
            propSchema.Type = JsonObjectType.String;
            string[] enumValues = Enum.GetNames(underlyingType);
            propSchema.Enumeration.Clear();
            foreach (string value in enumValues)
            {
                propSchema.Enumeration.Add(value);
            }

            // Add enum value descriptions if they exist
            List<string> enumDescriptions = [];
            foreach (object? enumValue in Enum.GetValues(underlyingType))
            {
                FieldInfo? fieldInfo = underlyingType.GetField(enumValue.ToString()!);
                DescriptionAttribute? descAttr = fieldInfo?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
                if (descAttr != null)
                {
                    enumDescriptions.Add($"{enumValue}: {descAttr.Description}");
                }
            }

            if (enumDescriptions.Count > 0)
            {
                propSchema.Description = string.Join("; ", enumDescriptions);
            }
        }
        else if (TryGetStringKeyDictionaryValueType(underlyingType, out Type? dictionaryValueType))
        {
            propSchema.Type = JsonObjectType.Object;
            propSchema.AdditionalPropertiesSchema = CreateAdditionalPropertiesSchema(dictionaryValueType, visitedTypes);
        }
        else if (underlyingType.IsArray && underlyingType.GetElementType() is Type elementType)
        {
            propSchema.Type = JsonObjectType.Array;
            propSchema.Item = CreateSchemaForType(elementType, visitedTypes);
        }
        else if (underlyingType.IsGenericType &&
                 (underlyingType.GetGenericTypeDefinition() == typeof(List<>) ||
                  underlyingType.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
                  underlyingType.GetGenericTypeDefinition() == typeof(ICollection<>)))
        {
            propSchema.Type = JsonObjectType.Array;
            Type genericElementType = underlyingType.GetGenericArguments()[0];
            JsonSchemaProperty itemSchema = CreateSchemaForType(genericElementType, visitedTypes);
            propSchema.Item = itemSchema;
        }
        else if (underlyingType.IsClass)
        {
            // Complex type - recursion, guarded against reference cycles
            propSchema.Type = JsonObjectType.Object;
            propSchema.AllowAdditionalProperties = false;

            if (visitedTypes.Add(underlyingType))
            {
                try
                {
                    AddPropertiesFromType(underlyingType, propSchema.Properties, propSchema.RequiredProperties, visitedTypes);
                }
                finally
                {
                    visitedTypes.Remove(underlyingType);
                }
            }
            // Cycle detected: leave as Object with no expanded properties.
        }

        return propSchema;
    }

    private static bool TryGetStringKeyDictionaryValueType(Type type, out Type valueType)
    {
        valueType = typeof(object);

        if (!type.IsGenericType)
        {
            return false;
        }

        if (type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
            type.GetGenericTypeDefinition() == typeof(IDictionary<,>))
        {
            Type[] args = type.GetGenericArguments();
            if (args.Length == 2 && args[0] == typeof(string))
            {
                valueType = args[1];
                return true;
            }
        }

        Type? dictInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (dictInterface == null)
        {
            return false;
        }

        Type[] interfaceArgs = dictInterface.GetGenericArguments();
        if (interfaceArgs.Length != 2 || interfaceArgs[0] != typeof(string))
        {
            return false;
        }

        valueType = interfaceArgs[1];
        return true;
    }

    private static JsonSchema CreateAdditionalPropertiesSchema(Type valueType, HashSet<Type> visitedTypes)
    {
        var schema = new JsonSchema();
        Type underlyingType = Nullable.GetUnderlyingType(valueType) ?? valueType;

        if (underlyingType == typeof(string))
        {
            schema.Type = JsonObjectType.String;
            return schema;
        }

        if (underlyingType == typeof(int) || underlyingType == typeof(long))
        {
            schema.Type = JsonObjectType.Integer;
            return schema;
        }

        if (underlyingType == typeof(double) || underlyingType == typeof(decimal) || underlyingType == typeof(float))
        {
            schema.Type = JsonObjectType.Number;
            return schema;
        }

        if (underlyingType == typeof(bool))
        {
            schema.Type = JsonObjectType.Boolean;
            return schema;
        }

        if (underlyingType == typeof(DateTime) || underlyingType == typeof(DateTimeOffset))
        {
            schema.Type = JsonObjectType.String;
            schema.Format = "date-time";
            return schema;
        }

        if (underlyingType.IsEnum)
        {
            schema.Type = JsonObjectType.String;
            foreach (string value in Enum.GetNames(underlyingType))
            {
                schema.Enumeration.Add(value);
            }
            return schema;
        }

        if (TryGetStringKeyDictionaryValueType(underlyingType, out Type? innerValueType))
        {
            schema.Type = JsonObjectType.Object;
            schema.AdditionalPropertiesSchema = CreateAdditionalPropertiesSchema(innerValueType, visitedTypes);
            return schema;
        }

        if (underlyingType.IsArray && underlyingType.GetElementType() is Type elementType)
        {
            schema.Type = JsonObjectType.Array;
            schema.Item = CreateAdditionalPropertiesSchema(elementType, visitedTypes);
            return schema;
        }

        if (underlyingType.IsGenericType &&
            (underlyingType.GetGenericTypeDefinition() == typeof(List<>) ||
             underlyingType.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
             underlyingType.GetGenericTypeDefinition() == typeof(ICollection<>)))
        {
            schema.Type = JsonObjectType.Array;
            schema.Item = CreateAdditionalPropertiesSchema(underlyingType.GetGenericArguments()[0], visitedTypes);
            return schema;
        }

        if (underlyingType.IsClass)
        {
            schema.Type = JsonObjectType.Object;
            schema.AllowAdditionalProperties = false;

            if (visitedTypes.Add(underlyingType))
            {
                try
                {
                    AddPropertiesFromType(underlyingType, schema.Properties, schema.RequiredProperties, visitedTypes);
                }
                finally
                {
                    visitedTypes.Remove(underlyingType);
                }
            }
            // Cycle detected: leave as Object with no expanded properties.

            return schema;
        }

        return schema;
    }

    private static void ApplyValidationAttributes(PropertyInfo propertyInfo, JsonSchema schema)
    {
        foreach (object attribute in propertyInfo.GetCustomAttributes(true))
        {
            switch (attribute)
            {
                case RangeAttribute rangeAttr:
                    // int and long are emitted as Integer, so Range must apply to both numeric kinds.
                    bool isNumeric = (schema.Type & (JsonObjectType.Number | JsonObjectType.Integer)) != 0;

                    if (rangeAttr.Minimum != null && isNumeric)
                    {
                        schema.Minimum = Convert.ToDecimal(rangeAttr.Minimum);
                    }

                    if (rangeAttr.Maximum != null && isNumeric)
                    {
                        schema.Maximum = Convert.ToDecimal(rangeAttr.Maximum);
                    }

                    break;

                case StringLengthAttribute strLengthAttr:
                    if (strLengthAttr.MinimumLength > 0)
                    {
                        schema.MinLength = strLengthAttr.MinimumLength;
                    }

                    if (strLengthAttr.MaximumLength > 0)
                    {
                        schema.MaxLength = strLengthAttr.MaximumLength;
                    }

                    break;

                case MinLengthAttribute minLengthAttr:
                    if (schema.Type == JsonObjectType.String)
                    {
                        schema.MinLength = minLengthAttr.Length;
                    }
                    else if (schema.Type == JsonObjectType.Array)
                    {
                        schema.MinItems = minLengthAttr.Length;
                    }

                    break;

                case MaxLengthAttribute maxLengthAttr:
                    if (schema.Type == JsonObjectType.String)
                    {
                        schema.MaxLength = maxLengthAttr.Length;
                    }
                    else if (schema.Type == JsonObjectType.Array)
                    {
                        schema.MaxItems = maxLengthAttr.Length;
                    }

                    break;

                case RegularExpressionAttribute regexAttr:
                    schema.Pattern = regexAttr.Pattern;
                    break;

                case EmailAddressAttribute:
                    schema.Format = "email";
                    break;

                case UrlAttribute:
                    schema.Format = "uri";
                    break;
            }
        }
    }

    private static Func<IStep, IStepError?, object> CompileCtorWithError(ConstructorInfo ctor)
    {
        ParameterExpression stepParam = Expression.Parameter(typeof(IStep));
        ParameterExpression errorParam = Expression.Parameter(typeof(IStepError));

        NewExpression newExp = Expression.New(ctor, stepParam, errorParam);
        UnaryExpression cast = Expression.Convert(newExp, typeof(object));

        return Expression.Lambda<Func<IStep, IStepError?, object>>(
            cast, stepParam, errorParam
        ).Compile();
    }

    private static Func<IStep, object> CompileCtor(ConstructorInfo ctor)
    {
        ParameterExpression stepParam = Expression.Parameter(typeof(IStep));

        NewExpression newExp = Expression.New(ctor, stepParam);
        UnaryExpression cast = Expression.Convert(newExp, typeof(object));

        return Expression.Lambda<Func<IStep, object>>(cast, stepParam).Compile();
    }

    private static Func<IStep, object> CompileCtorWithOptionalValue(ConstructorInfo ctor)
    {
        ParameterExpression stepParam = Expression.Parameter(typeof(IStep));
        ParameterInfo[] parameters = ctor.GetParameters();
        Type valueParamType = parameters[1].ParameterType;

        DefaultExpression defaultValueExpression = Expression.Default(valueParamType);
        NewExpression newExp = Expression.New(ctor, stepParam, defaultValueExpression);
        UnaryExpression cast = Expression.Convert(newExp, typeof(object));

        return Expression.Lambda<Func<IStep, object>>(cast, stepParam).Compile();
    }

    private static Func<IStep, object> CompileCtorWithSecondParamDefault(ConstructorInfo ctor, Type valueType)
    {
        ParameterExpression stepParam = Expression.Parameter(typeof(IStep));
        ParameterInfo[] parameters = ctor.GetParameters();
        Type secondParamType = parameters[1].ParameterType;

        // Use default value for the second parameter (e.g., null for string, default(T) for generic)
        DefaultExpression defaultValueExpression = Expression.Default(secondParamType);
        NewExpression newExp = Expression.New(ctor, stepParam, defaultValueExpression);
        UnaryExpression cast = Expression.Convert(newExp, typeof(object));

        return Expression.Lambda<Func<IStep, object>>(cast, stepParam).Compile();
    }

}
