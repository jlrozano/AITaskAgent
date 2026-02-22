using Newtonsoft.Json;
using Newtonsoft.Json.Converters;


namespace BRMS.StdRules.Modules.Scripting.Dynamic;

[JsonConverter(typeof(StringEnumConverter))]
public enum JsRuleType
{
    Validator,
    Normalizer,
    Transformation,
    Unknown
}
