using BRMS.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;


namespace BRMS.StdRules.Modules.Scripting.Dynamic;

/// <summary>
/// Configuración para reglas dinámicas cargadas desde JSON
/// </summary>
public class DynamicRuleConfiguration
{
    /// <summary>
    /// Identificador único de la regla
    /// </summary>
    public string RuleId { get; set; } = string.Empty;
    /// <summary>
    /// Código JavaScript a ejecutar
    /// </summary>
    public string Description { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    /// <summary>
    /// Mensaje de error personalizado
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Nivel de severidad del error
    /// </summary>
    public ErrorSeverityLevelEnum ErrorSeverityLevel { get; set; } = ErrorSeverityLevelEnum.Error;

    /// <summary>
    /// Tipos de entrada soportados por esta regla
    /// </summary>
    public List<RuleInputType> InputTypes { get; set; } = [RuleInputType.Any];

    /// <summary>
    /// Tipo de regla: Validator, Normalizer, o Transformation
    /// </summary>
    public JsRuleType RuleType { get; set; } = JsRuleType.Unknown;
    /// <summary>
    /// Ejemplo de uso de la regla
    /// </summary>
    public JObject? Example { get; set; }
    /// <summary>
    /// Parámetros adicionales específicos de la regla
    /// </summary>
    public List<ParameterDescription> AdditionalParameters { get; set; } = [];

    [JsonIgnore]
    internal Type? Type { get; set; }
    [JsonIgnore]
    public string Key => $"{RuleType}_{RuleId}";

    /// <summary>
    /// Genera un JsonSchema basado en los parámetros adicionales definidos
    /// </summary>
    /// <returns>JsonSchema que representa la estructura de los parámetros adicionales</returns>
    public JsonSchema GenerateJsonSchema()
    {
        var schema = new JsonSchema
        {
            Type = JsonObjectType.Object,
            Title = $"Parámetros para {RuleId}",
            Description = !string.IsNullOrEmpty(Description) ? Description : $"Esquema de parámetros para la regla {RuleId}"
        };

        schema.Properties["ruleId"] = CreatePropertySchema(new ParameterDescription
        {
            Required = true,
            Type = RuleInputType.String,
            Name = "ruleId",
            Description = "Identificador de la regla."
        });

        schema.Properties["errorMessage"] = CreatePropertySchema(new ParameterDescription
        {
            Required = false,
            Type = RuleInputType.String,
            Name = "errorMessage",
            Description = "Mensaje en caso de error."
        });
        schema.Properties["errorSeverityLevel"] = CreatePropertySchema(new ParameterDescription
        {
            Required = false,
            Type = RuleInputType.String,
            Name = "errorSeverityLevel",
            Description = "**Nivel de Severidad del Error** - Determina si las fallas de validación se reportarán como errores o incidencias. Esto afecta cómo el sistema maneja y escala los problemas."
        });
        schema.Properties["errorSeverityLevel"].Enumeration.Add("Issue");
        schema.Properties["errorSeverityLevel"].Enumeration.Add("Error");

        //if (RuleType == JsRuleType.Normalizer)
        //{
        //    schema.Properties["mustNotifyChange"] = CreatePropertySchema(new ParameterDescription
        //    {
        //        Required = false,
        //        Type = RuleInputType.Boolean,
        //        Name = "mustNotifyChange",
        //        Description = "Indica si el cambio realizado en la normalización debe ser enviado de vuelta a la fuente que mandó el cambio."
        //    });
        //}

        if (AdditionalParameters != null && AdditionalParameters.Count > 0)
        {
            foreach (ParameterDescription parameter in AdditionalParameters)
            {
                JsonSchemaProperty propertySchema = CreatePropertySchema(parameter);
                schema.Properties[parameter.Name] = propertySchema;

                if (parameter.Required)
                {
                    schema.RequiredProperties.Add(parameter.Name);
                }
            }
        }

        return schema;
    }

    /// <summary>
    /// Crea un esquema de propiedad basado en un ParameterDescription
    /// </summary>
    /// <param name="parameter">Descripción del parámetro</param>
    /// <returns>JsonSchemaProperty configurado según el tipo y descripción del parámetro</returns>
    private static JsonSchemaProperty CreatePropertySchema(ParameterDescription parameter)
    {
        var propertySchema = new JsonSchemaProperty
        {
            Description = parameter.Description
        };

        // Mapear RuleInputType a JsonObjectType y formato
        switch (parameter.Type)
        {
            case RuleInputType.String:
                propertySchema.Type = JsonObjectType.String;
                break;
            case RuleInputType.Number:
                propertySchema.Type = JsonObjectType.Number;
                break;
            case RuleInputType.Integer:
                propertySchema.Type = JsonObjectType.Integer;
                break;
            case RuleInputType.Boolean:
                propertySchema.Type = JsonObjectType.Boolean;
                break;
            case RuleInputType.Object:
                propertySchema.Type = JsonObjectType.Object;
                break;
            case RuleInputType.Array:
                propertySchema.Type = JsonObjectType.Array;
                break;
            case RuleInputType.Null:
                propertySchema.Type = JsonObjectType.Null;
                break;
            case RuleInputType.String_Date:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "date";
                break;
            case RuleInputType.String_DateTime:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "date-time";
                break;
            case RuleInputType.String_Time:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "time";
                break;
            case RuleInputType.String_Duration:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "duration";
                break;
            case RuleInputType.String_Email:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "email";
                break;
            case RuleInputType.String_IdnEmail:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "idn-email";
                break;
            case RuleInputType.String_Hostname:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "hostname";
                break;
            case RuleInputType.String_IdnHostname:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "idn-hostname";
                break;
            case RuleInputType.String_Ipv4:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "ipv4";
                break;
            case RuleInputType.String_Ipv6:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "ipv6";
                break;
            case RuleInputType.String_Uuid:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "uuid";
                break;
            case RuleInputType.String_Uri:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "uri";
                break;
            case RuleInputType.String_UriReference:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "uri-reference";
                break;
            case RuleInputType.String_Iri:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "iri";
                break;
            case RuleInputType.String_IriReference:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "iri-reference";
                break;
            case RuleInputType.String_UriTemplate:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "uri-template";
                break;
            case RuleInputType.String_JsonPointer:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "json-pointer";
                break;
            case RuleInputType.String_RelativeJsonPointer:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "relative-json-pointer";
                break;
            case RuleInputType.String_Regex:
                propertySchema.Type = JsonObjectType.String;
                propertySchema.Format = "regex";
                break;
            case RuleInputType.Any:
            default:
                // Para 'Any' no especificamos tipo, permitiendo cualquier valor
                break;
        }

        return propertySchema;
    }

}

public record ParameterDescription
{
    public string Name { get; set; } = string.Empty;
    public RuleInputType Type { get; set; } = RuleInputType.Any;
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
}
