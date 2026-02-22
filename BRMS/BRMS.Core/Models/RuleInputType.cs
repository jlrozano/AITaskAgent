using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;


namespace BRMS.Core.Models;

/// <summary>
/// Define los tipos de entrada soportados por las reglas del sistema BRMS
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum RuleInputType
{
    [EnumMember(Value = "string")]
    String,

    [EnumMember(Value = "number")]
    Number,

    [EnumMember(Value = "integer")]
    Integer,

    [EnumMember(Value = "boolean")]
    Boolean,

    [EnumMember(Value = "object")]
    Object,

    [EnumMember(Value = "array")]
    Array,

    [EnumMember(Value = "null")]
    Null,

    [EnumMember(Value = "string:date")]
    String_Date,

    [EnumMember(Value = "string:date-time")]
    String_DateTime,

    [EnumMember(Value = "string:time")]
    String_Time,

    [EnumMember(Value = "string:duration")]
    String_Duration,

    [EnumMember(Value = "string:email")]
    String_Email,

    [EnumMember(Value = "string:idn-email")]
    String_IdnEmail,

    [EnumMember(Value = "string:hostname")]
    String_Hostname,

    [EnumMember(Value = "string:idn-hostname")]
    String_IdnHostname,

    [EnumMember(Value = "string:ipv4")]
    String_Ipv4,

    [EnumMember(Value = "string:ipv6")]
    String_Ipv6,

    [EnumMember(Value = "string:uuid")]
    String_Uuid,

    [EnumMember(Value = "string:uri")]
    String_Uri,

    [EnumMember(Value = "string:uri-reference")]
    String_UriReference,

    [EnumMember(Value = "string:iri")]
    String_Iri,

    [EnumMember(Value = "string:iri-reference")]
    String_IriReference,

    [EnumMember(Value = "string:uri-template")]
    String_UriTemplate,

    [EnumMember(Value = "string:json-pointer")]
    String_JsonPointer,

    [EnumMember(Value = "string:relative-json-pointer")]
    String_RelativeJsonPointer,

    [EnumMember(Value = "string:regex")]
    String_Regex,

    [EnumMember(Value = "any")]
    Any
}

/// <summary>
/// Métodos de extensión para trabajar con tipos de entrada de reglas
/// </summary>
public static class RuleInputTypeExtensions
{
    /// <summary>
    /// Converts a JToken to the corresponding RuleInputType based on its type and content.
    /// </summary>
    /// <param name="token">The JToken to analyze.</param>
    /// <returns>The corresponding RuleInputType.</returns>
    public static RuleInputType GetRuleInputType(this JToken token)
    {
        return token == null
            ? RuleInputType.Null
            : token.Type switch
            {
                JTokenType.String => AnalyzeStringContent(token.Value<string>() ?? string.Empty),
                JTokenType.Integer => RuleInputType.Integer,
                JTokenType.Float => RuleInputType.Number,
                JTokenType.Boolean => RuleInputType.Boolean,
                JTokenType.Object => RuleInputType.Object,
                JTokenType.Array => RuleInputType.Array,
                JTokenType.Null => RuleInputType.Null,
                _ => RuleInputType.Any,
            };
    }

    /// <summary>
    /// Analyzes string content to determine the most specific RuleInputType.
    /// </summary>
    /// <param name="value">The string value to analyze.</param>
    /// <returns>The most specific RuleInputType for the string content.</returns>
    private static RuleInputType AnalyzeStringContent(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return RuleInputType.String;
        }

        // Check for date patterns
        if (DateTime.TryParse(value, out _))
        {
            return RuleInputType.String_Date;
        }

        // Check for email pattern
        if (IsEmail(value))
        {
            return RuleInputType.String_Email;
        }

        // Check for URI pattern
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return RuleInputType.String_Uri;
        }

        // Default to string
        return RuleInputType.String;
    }

    /// <summary>
    /// Checks if a string matches an email pattern.
    /// </summary>
    /// <param name="value">The string to check.</param>
    /// <returns>True if the string appears to be an email address.</returns>
    private static bool IsEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var addr = new System.Net.Mail.MailAddress(value);
            return addr.Address == value;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Comprueba si la colección contiene el tipo+formato indicado por un JToken de JSON Schema.
    /// El token debe tener al menos la propiedad "type" y opcionalmente "format".
    /// </summary>
    public static bool ContainsType(this IEnumerable<RuleInputType> source, JToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(token);

        if (source.Contains(RuleInputType.Any))
        {
            return true;
        }
        // Extraer "type" y "format" del token
        string? type = token["type"]?.ToString();

        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        string? format = token["format"]?.ToString();

        // Construir clave "tipo[:formato]"
        string key = string.IsNullOrWhiteSpace(format) ? type : $"{type}:{format}";

        // Comparar con los valores de EnumMember
        return source.Any(v =>
            string.Equals(v.GetEnumMemberValue(), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Obtiene el valor de EnumMemberAttribute o el nombre del enum si no existe.
    /// </summary>
    public static string GetEnumMemberValue(this Enum enumValue)
    {
        Type type = enumValue.GetType();
        string? name = Enum.GetName(type, enumValue);
        if (name == null)
        {
            return enumValue.ToString();
        }

        FieldInfo? field = type.GetField(name);
        var attr = (EnumMemberAttribute?)Attribute.GetCustomAttribute(field!, typeof(EnumMemberAttribute));

        return attr?.Value ?? name;
    }
}

