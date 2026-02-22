using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using BRMS.Core.Attributes;
using BRMS.Core.Constants;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace BRMS.Core.Extensions;

/// <summary>
/// Métodos de extensión para JsonSchema que permiten generar esquemas JSON a partir de tipos C#
/// </summary>
public static class JSchemaExtensions
{


    private static readonly Lazy<JsonSchema> _metaSchema = new(() => JsonSchema.FromJsonAsync(@"
{
  ""$schema"": ""http://json-schema.org/draft-04/schema#"",
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""type""], 
  ""properties"": {
    ""type"": {
      ""anyOf"": [
        { ""enum"": [""array"", ""boolean"", ""integer"", ""null"", ""number"", ""object"", ""string""] },
        { 
          ""type"": ""array"", 
          ""items"": { ""enum"": [""array"", ""boolean"", ""integer"", ""null"", ""number"", ""object"", ""string""] },
          ""uniqueItems"": true 
        }
      ]
    },
    ""properties"": {
      ""type"": ""object"",
      ""additionalProperties"": { ""$ref"": ""#"" }
    },
    ""items"": { ""$ref"": ""#"" },
    ""required"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""format"": { ""type"": ""string"" },
    ""default"": {},
    ""enum"": { ""type"": ""array"", ""minItems"": 1 },
    ""const"": {},
    ""title"": { ""type"": ""string"" },
    ""description"": { ""type"": ""string"" },
    ""minLength"": { ""type"": ""integer"", ""minimum"": 0 },
    ""maxLength"": { ""type"": ""integer"", ""minimum"": 0 },
    ""pattern"": { ""type"": ""string"" },
    ""minimum"": { ""type"": ""number"" },
    ""maximum"": { ""type"": ""number"" }
  },
  ""patternProperties"": {
    ""^\\$"": { ""type"": ""string"" }
  }
}
").Result);

    public static IEnumerable<string> Validate(this JsonSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return _metaSchema.Value.Validate(schema.ToJson(Formatting.None)).Select(err => err.ToString());
    }

    public static IEnumerable<string> Validate(string jsonStringSchema)
    {
        ArgumentNullException.ThrowIfNull(jsonStringSchema);
        return _metaSchema.Value.Validate(jsonStringSchema).Select(err => err.ToString());
    }

    /// <summary>
    /// Genera un esquema JSON a partir de un tipo C#
    /// </summary>
    /// <param name="type">El tipo del cual generar el esquema</param>
    /// <returns>Un JsonSchema que representa el tipo especificado</returns>
    /// <exception cref="ArgumentNullException">Se lanza si el tipo es nulo</exception>

    public static JsonSchema ToJSchema(this Type type)
    {

        ArgumentNullException.ThrowIfNull(type, nameof(type));

        var schema = new JsonSchema()
        {
            Type = JsonObjectType.Object,

        };
        AddPropertiesFromType(type, schema.Properties, schema.RequiredProperties);

        CustomJsonSchemaAttribute? customSchemaAttribute = type.GetCustomAttribute<CustomJsonSchemaAttribute>();
        customSchemaAttribute?.CustomizeSchema(schema, type);
        return schema;
    }

    private static void AddPropertiesFromType(Type type, IDictionary<string, JsonSchemaProperty> properties, ICollection<string> requiredProperties)
    {

        // Initialize NullabilityInfoContext once
        var nullabilityContext = new NullabilityInfoContext();

        foreach (PropertyInfo? prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite &&
                       !Attribute.IsDefined(p, typeof(ExcludeFromSchemaAttribute)) &&
                       !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute))))
        {
            // Obtener el nombre de la propiedad para el JSON
            JsonPropertyAttribute? jsonPropertyAttr = prop.GetCustomAttribute<JsonPropertyAttribute>();
            string? propertyName = jsonPropertyAttr?.PropertyName;

            if (string.IsNullOrEmpty(propertyName))
            {
                // Si no hay JsonProperty, convertir a camelCase
                propertyName = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            }

            JsonSchemaProperty propSchema = CreateSchemaForType(prop.PropertyType);
            ApplyValidationAttributes(prop, propSchema);

            // Determinar si la propiedad es requerida
            NullabilityInfo nullabilityInfo = nullabilityContext.Create(prop);
            bool actualIsNullable = nullabilityInfo.ReadState == NullabilityState.Nullable;

            bool hasInitSetter = prop.SetMethod != null && prop.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));

            // Comprobar si la propiedad tiene un DefaultValueAttribute
            DefaultValueAttribute? defaultValueAttribute = prop.GetCustomAttribute<DefaultValueAttribute>();

            // Una propiedad es requerida si NO es nullable, tiene un init-setter Y NO tiene DefaultValueAttribute
            if (!actualIsNullable && hasInitSetter && defaultValueAttribute == null)
            {
                if (!requiredProperties.Contains(propertyName))
                {
                    requiredProperties.Add(propertyName);
                }
            }

            // Si la propiedad tiene un DefaultValueAttribute, configurar el valor por defecto en el esquema
            if (defaultValueAttribute != null)
            {
                // Si el tipo de la propiedad es un enum y el esquema es de tipo string (como lo es para enums),
                // el valor por defecto debe ser la representación en string del enum, no su valor entero subyacente.
                if (prop.PropertyType.IsEnum && propSchema.Type == JsonObjectType.String)
                {
#pragma warning disable CS8604 // Possible null reference argument.
                    propSchema.Default = JToken.FromObject(defaultValueAttribute.Value?.ToString());
#pragma warning restore CS8604
                }
                else
                {
#pragma warning disable CS8604 // Possible null reference argument.
                    propSchema.Default = JToken.FromObject(defaultValueAttribute.Value);
#pragma warning restore CS8604
                }

                // Si tiene un valor por defecto, ya no debe ser requerida
                _ = requiredProperties.Remove(propertyName);
            }

            properties.Add(propertyName, propSchema);
        }

        // Buscar y aplicar personalizaciones del esquema si existe el atributo CustomJsonSchema

    }

    /// <summary>
    /// Crea un esquema JSON para un tipo específico
    /// </summary>
    /// <param name="propertyType">El tipo para el cual crear el esquema</param>
    /// <returns>Un JsonSchema que representa el tipo</returns>
    private static JsonSchemaProperty CreateSchemaForType(Type propertyType)
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
        else if (underlyingType == typeof(DateTime))
        {
            propSchema.Type = JsonObjectType.String;
            propSchema.Format = ResourcesManager.GetLocalizedMessage("SCHEMA_DateTimeFormat");
            propSchema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_DateTimeDescription");
        }
        else if (underlyingType == typeof(DateOnly))
        {
            propSchema.Type = JsonObjectType.String;
            propSchema.Format = ResourcesManager.GetLocalizedMessage("SCHEMA_DateFormat");
            propSchema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_DateDescription");
        }
        else if (underlyingType.IsEnum)
        {
            propSchema.Type = JsonObjectType.String;
            string[] enumValues = Enum.GetNames(underlyingType);
            propSchema.Enumeration.Clear();
            foreach (string value in enumValues)
            {
                propSchema.Enumeration.Add(JToken.FromObject(value));
            }
            propSchema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_ValidValuesTemplate", (object)string.Join(ResourcesManager.GetLocalizedMessage("SCHEMA_ValueSeparator"), enumValues));
        }
        else if (underlyingType.IsGenericType && underlyingType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            propSchema.Type = JsonObjectType.Array;
            Type elementType = underlyingType.GetGenericArguments()[0];
            JsonSchemaProperty itemSchema = CreateSchemaForType(elementType);
            propSchema.Items.Add(itemSchema);
        }
        else
        {
            // Para tipos complejos, crear un esquema de objeto
            AddPropertiesFromType(underlyingType, propSchema.Properties, propSchema.RequiredProperties);
        }

        return propSchema;
    }

    /// <summary>
    /// Aplica atributos de validación estándar de .NET al esquema JsonSchema
    /// </summary>
    /// <param name="propertyInfo">Información de la propiedad</param>
    /// <param name="schema">Esquema JsonSchema a modificar</param>
    private static void ApplyValidationAttributes(PropertyInfo propertyInfo, JsonSchema schema)
    {
        object[] attributes = propertyInfo.GetCustomAttributes(true);

        foreach (object attribute in attributes)
        {
            switch (attribute)
            {
                case RangeAttribute rangeAttr:
                    // Aplicar restricciones de rango
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
                            // Para fechas, usar pattern en lugar de Minimum
                            _ = minDate.ToString("yyyy-MM-dd");
                            if (string.IsNullOrEmpty(schema.Pattern))
                            {
                                schema.Pattern = ".*";
                            }

                            schema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_MinimumDateTemplate", (object)minDate.ToString("yyyy-MM-dd"));
                        }
                    }

                    if (rangeAttr.Maximum != null)
                    {
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
                            // Para fechas, usar pattern en lugar de Maximum
                            _ = maxDate.ToString("yyyy-MM-dd");
                            if (string.IsNullOrEmpty(schema.Description))
                            {
                                schema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_MaximumDateTemplate", (object)maxDate.ToString("yyyy-MM-dd"));
                            }
                            else
                            {
                                schema.Description += ResourcesManager.GetLocalizedMessage("SCHEMA_ValueSeparator") + ResourcesManager.GetLocalizedMessage("SCHEMA_MaximumDateTemplate", (object)maxDate.ToString("yyyy-MM-dd"));
                            }
                        }
                    }
                    break;

                case StringLengthAttribute strLengthAttr:
                    // Aplicar restricciones de longitud de string usando las propiedades nativas de JSchema
                    if (strLengthAttr.MinimumLength > 0)
                    {
                        schema.MinLength = strLengthAttr.MinimumLength;
                    }

                    if (strLengthAttr.MaximumLength > 0)
                    {
                        schema.MaxLength = strLengthAttr.MaximumLength;
                    }

                    // Agregar descripción para claridad
                    var lengthDesc = new List<string>();
                    if (strLengthAttr.MinimumLength > 0)
                    {
                        lengthDesc.Add($"mínimo: {strLengthAttr.MinimumLength}");
                    }

                    if (strLengthAttr.MaximumLength > 0)
                    {
                        lengthDesc.Add($"máximo: {strLengthAttr.MaximumLength}");
                    }

                    if (lengthDesc.Count != 0)
                    {
                        if (string.IsNullOrEmpty(schema.Description))
                        {
                            schema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_LengthTemplate", (object)string.Join(", ", lengthDesc));
                        }
                        else
                        {
                            schema.Description += ResourcesManager.GetLocalizedMessage("SCHEMA_DescriptionSeparator") + ResourcesManager.GetLocalizedMessage("SCHEMA_LengthTemplate", (object)string.Join(ResourcesManager.GetLocalizedMessage("SCHEMA_ValueSeparator"), lengthDesc));
                        }
                    }
                    break;

                case MinLengthAttribute minLengthAttr:
                    // Aplicar longitud mínima usando las propiedades nativas de JSchema
                    if (schema.Type == JsonObjectType.String)
                    {
                        schema.MinLength = minLengthAttr.Length;
                    }
                    else if (schema.Type == JsonObjectType.Array)
                    {
                        schema.MinItems = minLengthAttr.Length;
                    }

                    // Agregar descripción para claridad
                    if (schema.Type == JsonObjectType.String)
                    {
                        if (string.IsNullOrEmpty(schema.Description))
                        {
                            schema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_MinimumLengthTemplate", minLengthAttr.Length);
                        }
                        else
                        {
                            schema.Description += ResourcesManager.GetLocalizedMessage("SCHEMA_DescriptionSeparator") + ResourcesManager.GetLocalizedMessage("SCHEMA_MinimumLengthTemplate", minLengthAttr.Length);
                        }
                    }
                    else if (schema.Type == JsonObjectType.Array)
                    {
                        if (string.IsNullOrEmpty(schema.Description))
                        {
                            schema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_MinimumItemsTemplate", minLengthAttr.Length);
                        }
                        else
                        {
                            schema.Description += ResourcesManager.GetLocalizedMessage("SCHEMA_DescriptionSeparator") + ResourcesManager.GetLocalizedMessage("SCHEMA_MinimumItemsTemplate", minLengthAttr.Length);
                        }
                    }
                    break;

                case MaxLengthAttribute maxLengthAttr:
                    // Aplicar longitud máxima usando las propiedades nativas de JSchema
                    if (schema.Type == JsonObjectType.String)
                    {
                        schema.MaxLength = maxLengthAttr.Length;
                    }
                    else if (schema.Type == JsonObjectType.Array)
                    {
                        schema.MaxItems = maxLengthAttr.Length;
                    }

                    // Agregar descripción para claridad
                    if (schema.Type == JsonObjectType.String)
                    {
                        if (string.IsNullOrEmpty(schema.Description))
                        {
                            schema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_MaximumLengthTemplate", maxLengthAttr.Length);
                        }
                        else
                        {
                            schema.Description += ResourcesManager.GetLocalizedMessage("SCHEMA_DescriptionSeparator") + ResourcesManager.GetLocalizedMessage("SCHEMA_MaximumLengthTemplate", maxLengthAttr.Length);
                        }
                    }
                    else if (schema.Type == JsonObjectType.Array)
                    {
                        if (string.IsNullOrEmpty(schema.Description))
                        {
                            schema.Description = ResourcesManager.GetLocalizedMessage("SCHEMA_MaximumItemsTemplate", maxLengthAttr.Length);
                        }
                        else
                        {
                            schema.Description += ResourcesManager.GetLocalizedMessage("SCHEMA_DescriptionSeparator") + ResourcesManager.GetLocalizedMessage("SCHEMA_MaximumItemsTemplate", maxLengthAttr.Length);
                        }
                    }
                    break;

                case RegularExpressionAttribute regexAttr:
                    // Aplicar patrón de expresión regular
                    schema.Pattern = regexAttr.Pattern;
                    break;

                case EmailAddressAttribute _:
                    // Marcar como formato de email
                    schema.Format = "email";
                    break;

                case PhoneAttribute _:
                    // Marcar como formato de teléfono
                    schema.Format = "phone";
                    break;

                case UrlAttribute _:
                    // Marcar como formato de URL
                    schema.Format = "uri";
                    break;
            }
        }
    }
}
