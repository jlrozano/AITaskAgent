// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.JObjectPropertyBag
using System.Collections;
using Microsoft.ClearScript;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Modules.Scripting;

public class JObjectPropertyBag(JObject jobject, bool readOnly = false) : IPropertyBag, IDictionary<string, object>, ICollection<KeyValuePair<string, object>>, IEnumerable<KeyValuePair<string, object>>, IEnumerable
{
    internal readonly JObject _jobject = jobject ?? throw new ArgumentNullException(nameof(jobject));

    public object this[string name]
    {
        get
        {
            return name == "toJSON"
                ? new Func<object>(toJSON)
                : name == "valueOf"
                ? new Func<object>(valueOf)
                : _jobject.TryGetValue(name, out JToken? token) ? ConvertJTokenToObject(token)! : null!;
        }
        set
        {
            ThrowIfReadOnly();
            _jobject[name] = ConvertObjectToJToken(value);
        }
    }

    public object this[int index]
    {
        get
        {
            JProperty[] properties = [.. _jobject.Properties()];
            return index >= 0 && index < properties.Length ? ConvertJTokenToObject(properties[index].Value)! : null!;
        }
        set
        {
            ThrowIfReadOnly();
            JProperty[] properties = [.. _jobject.Properties()];
            if (index >= 0 && index < properties.Length)
            {
                properties[index].Value = ConvertObjectToJToken(value);
            }
        }
    }

    public string[] PropertyNames
    {
        get
        {
            var names = (from p in _jobject.Properties()
                         select p.Name).ToList();
            names.AddRange(collection);
            return [.. names];
        }
    }

    public int PropertyCount => _jobject.Count;

    public ICollection<string> Keys => [.. _jobject.Properties().Select(p => p.Name)];

    public ICollection<object> Values => [.. _jobject.Properties().Select(p => ConvertJTokenToObject(p.Value)!).Cast<object>()];

    public int Count => _jobject.Count;

    public bool IsReadOnly => readOnly;

    private static readonly string[] collection = ["toJSON", "valueOf"];

    public object toJSON()
    {
        return ConvertToPlainObject(_jobject)!;
    }

    public object valueOf()
    {
        return ConvertToPlainObject(_jobject)!;
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

    public bool Remove(string name)
    {
        ThrowIfReadOnly();
        return _jobject.Remove(name);
    }

    public void Add(string key, object value)
    {
        ThrowIfReadOnly();
        if (_jobject.ContainsKey(key))
        {
            throw new ArgumentException("An item with the same key has already been added. Key: " + key);
        }
        _jobject[key] = ConvertObjectToJToken(value);
    }

    public void Add(KeyValuePair<string, object> item)
    {
        Add(item.Key, item.Value);
    }

    public void Clear()
    {
        ThrowIfReadOnly();
        _jobject.RemoveAll();
    }

    public bool Contains(KeyValuePair<string, object> item)
    {
        return _jobject.TryGetValue(item.Key, out JToken? token) && object.Equals(ConvertJTokenToObject(token), item.Value);
    }

    public bool ContainsKey(string key)
    {
        return _jobject.ContainsKey(key);
    }

    public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (array.Length - arrayIndex < Count)
        {
            throw new ArgumentException("Not enough space in array");
        }
        int i = arrayIndex;
        foreach (JProperty property in _jobject.Properties())
        {
            array[i++] = new KeyValuePair<string, object>(property.Name, ConvertJTokenToObject(property.Value)!);
        }
    }

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        return (from p in _jobject.Properties()
                select new KeyValuePair<string, object>(p.Name, ConvertJTokenToObject(p.Value)!)).GetEnumerator();
    }

    public bool Remove(KeyValuePair<string, object> item)
    {
        ThrowIfReadOnly();
        return Contains(item) && _jobject.Remove(item.Key);
    }

    public bool TryGetValue(string key, out object value)
    {
        if (_jobject.TryGetValue(key, out JToken? token))
        {
            value = ConvertJTokenToObject(token)!;
            return true;
        }
        value = null!;
        return false;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private void ThrowIfReadOnly()
    {
        if (readOnly)
        {
            throw new InvalidOperationException("Collection is read-only");
        }
    }

    private object? ConvertJTokenToObject(JToken? token)
    {
        return token == null
            ? null
            : token.Type switch
            {
                JTokenType.Null => null,
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.Integer => token.Value<long>(),
                JTokenType.Float => token.Value<double>(),
                JTokenType.String => token.Value<string>(),
                JTokenType.Date => token.Value<DateTime>(),
                JTokenType.Object => new JObjectPropertyBag((JObject)token, readOnly),
                JTokenType.Array => new JArrayPropertyBag((JArray)token, readOnly),
                _ => token.ToString(),
            };
    }

    private static JToken ConvertObjectToJToken(object? value)
    {
        return value == null
            ? JValue.CreateNull()
            : value is JObjectPropertyBag bag
            ? bag._jobject
            : value is JArrayPropertyBag arrayBag ? arrayBag._jarray : JToken.FromObject(value);
    }
}
