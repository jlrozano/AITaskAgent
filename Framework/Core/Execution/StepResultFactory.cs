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
                var obj = Ctor(step);
                ((IStepResult)obj).Error = error;
                return obj;
            }
            throw new InvalidOperationException(
                $"No se pudo crear una instancia de {ValueType.Name}. No hay constructor válido.");
        }

        public object CreateInstance(IStep step, object? value, IStepError? error = null)
        {
            var obj = CreateInstance(step, error);

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
        var info = GetStepActivatorInfo(type);

        return (IStepResult)info.CreateInstance(step, error);
    }

    public static IStepResult CreateStepResult(Type type, IStep step, object? value, IStepError? error = null)
    {
        var info = GetStepActivatorInfo(type);

        return (IStepResult)info.CreateInstance(step, value, error);
    }
    public static StepActivatorInfo GetStepActivatorInfo(Type type)
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

        var valueProperty = GetValuePropertyInfo(type);
        var jsonSchema = BuildJsonSchema(valueProperty.PropertyType);

        var ctor2 = type.GetConstructors()
            .FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 2
                    && typeof(IStep).IsAssignableFrom(p[0].ParameterType)
                    && typeof(IStepError).IsAssignableFrom(p[1].ParameterType);
            });

        if (ctor2 is not null)
        {
            ctorWithError = CompileCtorWithError(ctor2);
            return new StepActivatorInfo(ctorWithError, null, valueProperty, jsonSchema);
        }


        var ctor1 = type.GetConstructors()
            .FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 1
                    && typeof(IStep).IsAssignableFrom(p[0].ParameterType);
            });

        if (ctor1 is not null)
        {
            ctor = CompileCtor(ctor1);
        }

        return new StepActivatorInfo(ctorWithError, ctor, valueProperty, jsonSchema);
    }

    private static PropertyInfo GetValuePropertyInfo(Type type)
    {
        var currentType = type;
        while (currentType != null)
        {
            var prop = currentType.GetProperty(nameof(IStepResult.Value),
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
        var actualType = Nullable.GetUnderlyingType(valueType) ?? valueType;

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
                Type = JsonObjectType.Object
            };

            AddPropertiesFromType(actualType, schema.Properties, schema.RequiredProperties);
            return schema;
        }
        catch
        {
            return null;
        }
    }

    private static void AddPropertiesFromType(Type type, IDictionary<string, JsonSchemaProperty> properties, ICollection<string> requiredProperties)
    {
        var nullabilityContext = new NullabilityInfoContext();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite &&
                       !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute))))
        {
            // Nombre de propiedad en camelCase
            var jsonPropertyAttr = prop.GetCustomAttribute<JsonPropertyAttribute>();
            var propertyName = jsonPropertyAttr?.PropertyName
                ?? (char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]);

            var propSchema = CreateSchemaForType(prop.PropertyType);
            ApplyValidationAttributes(prop, propSchema);

            // Aplicar [Description] si existe
            var descAttr = prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            if (descAttr != null)
            {
                propSchema.Description = descAttr.Description;
            }

            // Determinar si es requerida
            var nullabilityInfo = nullabilityContext.Create(prop);
            var isNullable = nullabilityInfo.ReadState == NullabilityState.Nullable;
            var hasInitSetter = prop.SetMethod?.ReturnParameter.GetRequiredCustomModifiers()
                .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) ?? false;
            var defaultValueAttr = prop.GetCustomAttribute<DefaultValueAttribute>();

            // Required si: no nullable + init setter + sin default value
            if (!isNullable && hasInitSetter && defaultValueAttr == null)
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

    private static JsonSchemaProperty CreateSchemaForType(Type propertyType)
    {
        var propSchema = new JsonSchemaProperty();
        var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

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
            var enumValues = Enum.GetNames(underlyingType);
            propSchema.Enumeration.Clear();
            foreach (var value in enumValues)
            {
                propSchema.Enumeration.Add(value);
            }

            // Add enum value descriptions if they exist
            List<string> enumDescriptions = [];
            foreach (var enumValue in Enum.GetValues(underlyingType))
            {
                var fieldInfo = underlyingType.GetField(enumValue.ToString()!);
                var descAttr = fieldInfo?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
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
        else if (underlyingType.IsGenericType &&
                 (underlyingType.GetGenericTypeDefinition() == typeof(List<>) ||
                  underlyingType.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
                  underlyingType.GetGenericTypeDefinition() == typeof(ICollection<>)))
        {
            propSchema.Type = JsonObjectType.Array;
            var elementType = underlyingType.GetGenericArguments()[0];
            var itemSchema = CreateSchemaForType(elementType);
            propSchema.Item = itemSchema;
        }
        else if (underlyingType.IsClass)
        {
            // Complex type - recursion
            propSchema.Type = JsonObjectType.Object;
            AddPropertiesFromType(underlyingType, propSchema.Properties, propSchema.RequiredProperties);
        }

        return propSchema;
    }

    private static void ApplyValidationAttributes(PropertyInfo propertyInfo, JsonSchema schema)
    {
        foreach (var attribute in propertyInfo.GetCustomAttributes(true))
        {
            switch (attribute)
            {
                case RangeAttribute rangeAttr:
                    if (rangeAttr.Minimum != null && schema.Type == JsonObjectType.Number)
                    {
                        schema.Minimum = Convert.ToDecimal(rangeAttr.Minimum);
                    }

                    if (rangeAttr.Maximum != null && schema.Type == JsonObjectType.Number)
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
        var stepParam = Expression.Parameter(typeof(IStep));
        var errorParam = Expression.Parameter(typeof(IStepError));

        var newExp = Expression.New(ctor, stepParam, errorParam);
        var cast = Expression.Convert(newExp, typeof(object));

        return Expression.Lambda<Func<IStep, IStepError?, object>>(
            cast, stepParam, errorParam
        ).Compile();
    }

    private static Func<IStep, object> CompileCtor(ConstructorInfo ctor)
    {
        var stepParam = Expression.Parameter(typeof(IStep));

        var newExp = Expression.New(ctor, stepParam);
        var cast = Expression.Convert(newExp, typeof(object));

        return Expression.Lambda<Func<IStep, object>>(cast, stepParam).Compile();
    }

}



