// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.DynamicArrayWrapper
using System.Dynamic;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Modules.Scripting;

public class DynamicArrayWrapper(JArray jArray, bool isImmutable = false) : DynamicObject
{
    private readonly JArray _jArray = jArray ?? throw new ArgumentNullException("jArray");

    private readonly bool _isImmutable = isImmutable;

    public bool IsImmutable => _isImmutable;

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        switch (binder.Name)
        {
            case "length":
                result = _jArray.Count;
                return true;
            case "toJSON":
                result = (Func<object?>)(() => ToJSON());
                return true;
            case "valueOf":
                result = (Func<object?>)(() => ToJSON());
                return true;
            case "push":
                result = (Func<object[], int>)(items => Push(items));
                return true;
            case "pop":
                result = (Func<object?>)(() => Pop());
                return true;
            case "shift":
                result = (Func<object?>)(() => Shift());
                return true;
            case "unshift":
                result = (Func<object[], int>)(items => Unshift(items));
                return true;
            case "slice":
                result = (Func<int, int?, DynamicArrayWrapper>)((start, end) => Slice(start, end));
                return true;
            case "indexOf":
                result = (Func<object?, int, int>)((item, fromIndex) => IndexOf(item, fromIndex));
                return true;
            case "includes":
                result = (Func<object?, bool>)(item => Includes(item));
                return true;
            case "join":
                result = (Func<string, string>)(separator => Join(separator));
                return true;
            default:
                result = null;
                return false;
        }
    }

    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        if (indexes.Length == 1 && indexes[0] is int index && index >= 0 && index < _jArray.Count)
        {
            result = ConvertJTokenToNative(_jArray[index]);
            return true;
        }
        result = null;
        return false;
    }

    public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object? value)
    {
        if (_isImmutable)
        {
            throw new InvalidOperationException("No se puede modificar un array inmutable");
        }
        if (indexes.Length == 1 && indexes[0] is int index && index >= 0)
        {
            while (_jArray.Count <= index)
            {
                _jArray.Add(JValue.CreateNull());
            }
            _jArray[index] = ConvertObjectToJToken(value);
            return true;
        }
        return false;
    }

    public object? ToJSON()
    {
        return _jArray.Select(ConvertToPlainObject).ToArray();
    }

    public int Push(params object[] items)
    {
        ThrowIfImmutable();
        foreach (object item in items)
        {
            _jArray.Add(ConvertObjectToJToken(item));
        }
        return _jArray.Count;
    }

    public object? Pop()
    {
        ThrowIfImmutable();
        if (_jArray.Count == 0)
        {
            return null;
        }
        object? result = ConvertJTokenToNative(_jArray.Last);
        _jArray.RemoveAt(_jArray.Count - 1);
        return result;
    }

    public object? Shift()
    {
        ThrowIfImmutable();
        if (_jArray.Count == 0)
        {
            return null;
        }
        object? result = ConvertJTokenToNative(_jArray.First);
        _jArray.RemoveAt(0);
        return result;
    }

    public int Unshift(params object[] items)
    {
        ThrowIfImmutable();
        for (int i = items.Length - 1; i >= 0; i--)
        {
            _jArray.Insert(0, ConvertObjectToJToken(items[i]));
        }
        return _jArray.Count;
    }

    public DynamicArrayWrapper Slice(int start, int? end = null)
    {
        int actualEnd = end ?? _jArray.Count;
        if (start < 0)
        {
            start = Math.Max(0, _jArray.Count + start);
        }
        if (actualEnd < 0)
        {
            actualEnd = Math.Max(0, _jArray.Count + actualEnd);
        }
        JArray newArray = [];
        for (int i = start; i < Math.Min(actualEnd, _jArray.Count); i++)
        {
            newArray.Add(_jArray[i].DeepClone());
        }
        return new DynamicArrayWrapper(newArray, _isImmutable);
    }

    public int IndexOf(object? searchElement, int fromIndex = 0)
    {
        for (int i = Math.Max(0, fromIndex); i < _jArray.Count; i++)
        {
            if (object.Equals(ConvertJTokenToNative(_jArray[i]), searchElement))
            {
                return i;
            }
        }
        return -1;
    }

    public bool Includes(object? searchElement)
    {
        return IndexOf(searchElement) >= 0;
    }

    public string Join(string separator = ",")
    {
        return string.Join(separator, _jArray.Select(t => ConvertJTokenToNative(t)?.ToString() ?? ""));
    }

    private void ThrowIfImmutable()
    {
        if (_isImmutable)
        {
            throw new InvalidOperationException("No se puede modificar un array inmutable");
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
                    {
                        foreach (JProperty prop in obj.Properties())
                        {
                            result[prop.Name] = ConvertToPlainObject(prop.Value);
                        }
                        return result;
                    }
                }
            case JTokenType.Array:
                return ((JArray)token).Select(ConvertToPlainObject).ToArray();
            default:
                return token.ToString();
        }
    }

    private static JToken ConvertObjectToJToken(object? value)
    {
        return value == null
            ? JValue.CreateNull()
            : value is DynamicObjectWrapper wrapper
            ? wrapper.GetJObject()
            : value is DynamicArrayWrapper arrayWrapper ? arrayWrapper._jArray : JToken.FromObject(value);
    }

    public JArray GetJArray()
    {
        return _jArray;
    }
}
