using Newtonsoft.Json;
using NJsonSchema;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AITaskAgent.Core.Abstractions;

/// <summary>
/// Builds the JSON Schema of a configuration class from its properties: one property per public,
/// publicly writable (<c>set</c> or <c>init</c>), non-[JsonIgnore] property, named in camelCase or by
/// <c>[JsonProperty]</c>, with <c>[Description]</c> as description (and, in the configuration flavour, format <c>password</c> for a
/// string with <c>[PasswordPropertyText]</c> or <c>[DataType(DataType.Password)]</c>).
/// </summary>
/// <remarks>
/// Two flavours. The flat one (step configuration, <c>IStepRegistry.Describe</c>) types primitives, enums
/// and arrays of primitives and leaves everything else untyped; a property is required when it has
/// <c>[Required]</c> or is non-nullable. The configuration one (<c>IExtension.ConfigurationModel</c>)
/// also describes nested classes, lists and string-keyed dictionaries; a property is required only when it
/// has <c>[Required]</c> or is a C# <c>required</c> member, because configuration classes initialise most
/// of their non-nullable properties with a default. Interfaces, abstract classes and framework types stay
/// untyped, and a class that contains itself is described once.
/// </remarks>
public static class TypeSchemaBuilder
{
    /// <summary>Schema of <paramref name="type"/>'s properties.</summary>
    /// <param name="type">A class with a parameterless-constructible shape (never instantiated).</param>
    /// <param name="configuration">True for the configuration flavour; false for the flat one.</param>
    public static JsonSchema Build(Type type, bool configuration = false)
    {
        ArgumentNullException.ThrowIfNull(type);

        JsonSchema schema = new() { Type = JsonObjectType.Object };
        FillObject(schema, type, configuration, []);
        return schema;
    }

    private static void FillObject(JsonSchema target, Type type, bool configuration, HashSet<Type> path)
    {
        NullabilityInfoContext nullabilityCtx = new();
        path.Add(type);

        IEnumerable<PropertyInfo> props = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.SetMethod?.IsPublic == true
                     && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute)));

        foreach (PropertyInfo prop in props)
        {
            string propName = prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName
                ?? (char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]);

            JsonSchemaProperty propSchema = new();
            FillType(propSchema, prop.PropertyType, configuration, path);

            DescriptionAttribute? descAttr = prop.GetCustomAttribute<DescriptionAttribute>(inherit: true);
            if (descAttr != null)
            {
                propSchema.Description = descAttr.Description;
            }

            // A secret setting (configuration flavour): [PasswordPropertyText] or [DataType(DataType.Password)] on a string is format "password",
            // which a host masks and keeps out of what it shows.
            if (configuration && propSchema.Type == JsonObjectType.String
                && (prop.GetCustomAttribute<PasswordPropertyTextAttribute>(inherit: true)?.Password == true
                    || prop.GetCustomAttribute<DataTypeAttribute>(inherit: true)?.DataType == DataType.Password))
            {
                propSchema.Format = "password";
            }

            bool isRequired = Attribute.IsDefined(prop, typeof(RequiredAttribute), inherit: true)
                || (configuration
                    ? Attribute.IsDefined(prop, typeof(RequiredMemberAttribute), inherit: true)
                    : nullabilityCtx.Create(prop).ReadState != NullabilityState.Nullable);
            if (isRequired && !target.RequiredProperties.Contains(propName))
            {
                target.RequiredProperties.Add(propName);
            }

            target.Properties[propName] = propSchema;
        }

        path.Remove(type);
    }

    private static void FillType(JsonSchema target, Type type, bool configuration, HashSet<Type> path)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (TryGetPrimitiveType(underlying, out JsonObjectType primitive, out string? format))
        {
            target.Type = primitive;
            if (format != null)
            {
                target.Format = format;
            }
        }
        else if (underlying.IsEnum)
        {
            target.Type = JsonObjectType.String;
            foreach (object v in Enum.GetValues(underlying))
            {
                target.Enumeration.Add(v.ToString()!);
            }
        }
        else if (TryGetDictionaryValueType(underlying, out Type valueType))
        {
            // A string-keyed map is an object in both flavours (the flat one used to describe it as an array, which a YAML
            // mapping then failed to match). The flat flavour types the values only when they are primitives.
            target.Type = JsonObjectType.Object;
            JsonSchema valueSchema = new();
            Type valueUnderlying = Nullable.GetUnderlyingType(valueType) ?? valueType;
            if (configuration)
            {
                FillType(valueSchema, valueType, configuration, path);
            }
            else if (valueUnderlying != typeof(DateTime) && TryGetPrimitiveType(valueUnderlying, out JsonObjectType valueJsonType, out _))
            {
                valueSchema.Type = valueJsonType;
            }

            target.AdditionalPropertiesSchema = valueSchema;
        }
        else if (IsEnumerableType(underlying, out Type elementType))
        {
            target.Type = JsonObjectType.Array;
            JsonSchema itemSchema = new();
            if (configuration)
            {
                FillType(itemSchema, elementType, configuration, path);
            }
            else
            {
                Type elUnderlying = Nullable.GetUnderlyingType(elementType) ?? elementType;
                if (elUnderlying != typeof(DateTime)
                    && TryGetPrimitiveType(elUnderlying, out JsonObjectType itemType, out _))
                {
                    itemSchema.Type = itemType;
                }
            }

            target.Items.Add(itemSchema);
        }
        else if (configuration && IsDescribableClass(underlying) && !path.Contains(underlying))
        {
            target.Type = JsonObjectType.Object;
            FillObject(target, underlying, configuration, path);
        }
    }

    private static bool IsDescribableClass(Type type)
        => (type.IsClass || type.IsValueType)
        && !type.IsAbstract
        && type != typeof(object)
        && type.Namespace is { } ns
        && !ns.StartsWith("System", StringComparison.Ordinal)
        && !ns.StartsWith("Microsoft", StringComparison.Ordinal)
        && !ns.StartsWith("Newtonsoft", StringComparison.Ordinal);

    private static bool TryGetPrimitiveType(Type type, out JsonObjectType jsonType, out string? format)
    {
        format = null;
        if (type == typeof(string))
        {
            jsonType = JsonObjectType.String;
        }
        else if (type == typeof(int) || type == typeof(long))
        {
            jsonType = JsonObjectType.Integer;
        }
        else if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            jsonType = JsonObjectType.Number;
        }
        else if (type == typeof(bool))
        {
            jsonType = JsonObjectType.Boolean;
        }
        else if (type == typeof(DateTime))
        {
            jsonType = JsonObjectType.String;
            format = "date-time";
        }
        else
        {
            jsonType = JsonObjectType.None;
            return false;
        }

        return true;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        valueType = typeof(object);

        Type? dictionary = new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType
                && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                && i.GetGenericArguments()[0] == typeof(string));
        if (dictionary == null)
        {
            return false;
        }

        valueType = dictionary.GetGenericArguments()[1];
        return true;
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
        if (enumInterface == null && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
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
