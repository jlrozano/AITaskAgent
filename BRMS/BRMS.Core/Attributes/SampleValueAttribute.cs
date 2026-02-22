using Newtonsoft.Json.Linq;

namespace BRMS.Core.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class SampleValueAttribute(object value) : Attribute
{

    public string Value => TokenValue.Type switch
    {
        JTokenType.Null => "null",
        JTokenType.String => $"\"{TokenValue.Value<string>()}\"",
        _ => value?.ToString() ?? "null"
    };

    public JToken TokenValue => JToken.FromObject(value);

}
