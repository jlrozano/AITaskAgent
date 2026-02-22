using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using NJsonSchema;


namespace BRMS.Core.Models;

/// <summary>
/// Contexto común para validadores y normalizadores sobre JSON.
/// Todas las propiedades son readonly, pero el contenido de NewValue puede modificarse.
/// </summary>
public record BRMSExecutionContext
{
    public BRMSExecutionContext(
        JObject? oldValue, // Objeto JSON con los valores antiguos (opcional)
        JObject? newValue,  // Objeto JSON con los valores nuevos
        string? source,      // Origen o usuario que ha realizado el cambio
        JsonSchema? inputType // Esquema JSON para validación de tipos
        )
    {
        OldValue = oldValue;
        NewValue = newValue;
        Source = source;
        InputType = inputType;

    }
    [JsonProperty("operation")]
    public OperationType Operation => (OldValue == null && NewValue == null) ? OperationType.Error :
            (NewValue == null) ? OperationType.Delete :
            (OldValue == null) ? OperationType.Insert : OperationType.Update;

    [JsonProperty("oldValue")]
    public JObject? OldValue { get; init; }
    [JsonProperty("newValue")]
    public JObject? NewValue { get; set; }
    [JsonProperty("source")]
    public string? Source { get; init; }
    [JsonProperty("inputType")]
    public JsonSchema? InputType { get; init; }
};

[JsonConverter(typeof(StringEnumConverter))]
public enum OperationType
{
    Insert, Update, Delete, Error
}
