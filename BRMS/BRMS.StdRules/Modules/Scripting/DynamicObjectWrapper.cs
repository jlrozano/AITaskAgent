// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.DynamicObjectWrapper
using System.Dynamic;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Modules.Scripting;

public class DynamicObjectWrapper(JObject jObject, bool isImmutable = false) : DynamicObject
{
    private readonly JObject _jObject = jObject ?? throw new ArgumentNullException(nameof(jObject));
    private readonly bool _isImmutable = isImmutable;

    public bool IsImmutable => _isImmutable;

    private static readonly string[] second = ["toJSON", "valueOf", "toString"];

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        switch (binder.Name)
        {
            case "toJSON":
                Console.WriteLine("JavaScript está pidiendo toJSON");
                result = ToJSON();
                return true;
            case "valueOf":
                result = (Func<object?>)(() => ToJSON());
                return true;
            case "toString":
                result = (Func<string>)(() => _jObject.ToString());
                return true;
            default:
                {
                    if (_jObject.TryGetValue(binder.Name, out JToken? token))
                    {
                        result = ConvertJTokenToNative(token);
                        return true;
                    }
                    result = null;
                    return false;
                }
        }
    }

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        if (binder.Name is "toJSON" or "valueOf" or "toString")
        {
            return false;
        }
        if (_isImmutable)
        {
            throw new InvalidOperationException("No se puede modificar la propiedad '" + binder.Name + "' en un objeto inmutable");
        }
        _jObject[binder.Name] = ConvertObjectToJToken(value);
        return true;
    }

    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        if (indexes.Length == 1 && indexes[0] is string propertyName && _jObject.TryGetValue(propertyName, out JToken? token))
        {
            result = ConvertJTokenToNative(token);
            return true;
        }
        result = null;
        return false;
    }

    public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object? value)
    {
        if (_isImmutable)
        {
            throw new InvalidOperationException($"No se puede modificar la propiedad '{indexes[0]}' en un objeto inmutable");
        }
        if (indexes.Length == 1 && indexes[0] is string propertyName)
        {
            _jObject[propertyName] = ConvertObjectToJToken(value);
            return true;
        }
        return false;
    }

    public override IEnumerable<string> GetDynamicMemberNames()
    {
        return (from p in _jObject.Properties()
                select p.Name).Concat(second);
    }

    public object? ToJSON()
    {
        return ConvertToPlainObject(_jObject);
    }

    private object? ConvertToPlainObject(JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Null:
                return null;
            case JTokenType.Boolean:
                return token.Value<bool>();
            case JTokenType.Integer:
                return token.Value<long>();
            case JTokenType.Float:
                return token.Value<double>();
            case JTokenType.String:
                return token.Value<string>();
            case JTokenType.Date:
                return token.Value<DateTime>();
            case JTokenType.Object:
                {
                    var obj = (JObject)token;
                    Dictionary<string, object?> result = [];
                    foreach (JProperty prop in obj.Properties())
                    {
                        result[prop.Name] = ConvertToPlainObject(prop.Value);
                    }
                    return result;
                }
            case JTokenType.Array:
                return ((JArray)token).Select(ConvertToPlainObject).ToArray();
            default:
                return token.ToString();
        }
    }

    private object? ConvertJTokenToNative(JToken? token)
    {
        return token == null
            ? null
            : token.Type switch
            {
                JTokenType.Integer => token.Value<long>(),
                JTokenType.Float => token.Value<double>(),
                JTokenType.String => token.Value<string>(),
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.Null => null,
                JTokenType.Date => token.Value<DateTime>(),
                JTokenType.Object => new DynamicObjectWrapper((token as JObject)!, _isImmutable),
                JTokenType.Array => new DynamicArrayWrapper((token as JArray)!, _isImmutable),
                _ => token.ToString(),
            };
    }

    private static JToken ConvertObjectToJToken(object? value)
    {
        return value == null
            ? JValue.CreateNull()
            : value is DynamicObjectWrapper wrapper
            ? wrapper._jObject
            : value is DynamicArrayWrapper arrayWrapper ? arrayWrapper.GetJArray() : JToken.FromObject(value);
    }

    public JObject GetJObject()
    {
        return _jObject;
    }

    public override string ToString()
    {
        return _jObject.ToString();
    }

    [Obsolete("Usar ToJSON() en su lugar")]
    public object? ToSerializableObject()
    {
        return ToJSON();
    }
}
