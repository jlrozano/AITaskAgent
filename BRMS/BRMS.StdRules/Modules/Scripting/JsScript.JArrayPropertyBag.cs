// BRMS.StdRules, Version=1.0.1.0, Culture=neutral, PublicKeyToken=null
// BRMS.StdRules.JsScript.JArrayPropertyBag
using System.Collections;
using Microsoft.ClearScript;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Modules.Scripting;

public class JArrayPropertyBag(JArray jarray, bool readOnly = false) : IPropertyBag, IDictionary<string, object>, ICollection<KeyValuePair<string, object>>, IEnumerable<KeyValuePair<string, object>>, IEnumerable
{
    internal readonly JArray _jarray = jarray ?? throw new ArgumentNullException(nameof(jarray));

    public int length => _jarray.Count;

    public object this[string name]
    {
        get
        {
            return name == "toJSON"
                ? new Func<object>(toJSON)
                : name == "valueOf"
                ? new Func<object>(valueOf)
                : int.TryParse(name, out int index)
                ? this[index]
                : name.ToLower(System.Globalization.CultureInfo.CurrentCulture) switch
                {
                    "length" => _jarray.Count,
                    "push" => (Func<object[], object>)(items => push(items)!),
                    "pop" => (Func<object>)(() => pop()!),
                    "shift" => (Func<object>)(() => shift()!),
                    "unshift" => (Func<object[], object>)(items => unshift(items)!),
                    "slice" => (Func<int, int?, JArrayPropertyBag>)((start, end) => slice(start, end)),
                    "splice" => (Func<int, int, object[], JArrayPropertyBag>)((start, deleteCount, items) => splice(start, deleteCount, items)),
                    "indexof" => (Func<object, int, int>)((searchElement, fromIndex) => indexOf(searchElement, fromIndex)),
                    "includes" => (Func<object, bool>)(searchElement => includes(searchElement)),
                    "join" => (Func<string, string>)(separator => join(separator)),
                    "reverse" => (Func<JArrayPropertyBag>)(() => reverse()),
                    "sort" => (Func<JArrayPropertyBag>)(() => sort()),
                    _ => null!,
                };
        }
        set
        {
            if (int.TryParse(name, out int index))
            {
                this[index] = value;
            }
        }
    }

    public object this[int index]
    {
        get
        {
            return index >= 0 && index < _jarray.Count ? ConvertJTokenToObject(_jarray[index])! : null!;
        }
        set
        {
            ThrowIfReadOnly();
            if (index >= 0)
            {
                while (_jarray.Count <= index)
                {
                    _jarray.Add(JValue.CreateNull());
                }
                _jarray[index] = ConvertObjectToJToken(value);
            }
        }
    }

    public string[] PropertyNames
    {
        get
        {
            List<string> names = [];
            for (int i = 0; i < _jarray.Count; i++)
            {
                names.Add(i.ToString());
            }
            names.AddRange(collection);
            return [.. names];
        }
    }

    public int PropertyCount => _jarray.Count;

    public ICollection<string> Keys => PropertyNames;

    public ICollection<object> Values
    {
        get
        {
            List<object> values = [];
            for (int i = 0; i < _jarray.Count; i++)
            {
                values.Add(ConvertJTokenToObject(_jarray[i])!);
            }
            values.AddRange(
            [
                length,
                new Func<object>(toJSON),
                new Func<object>(valueOf)
            ]);
            return values;
        }
    }

    public int Count => PropertyCount + PropertyNames.Length - PropertyCount;

    public bool IsReadOnly => readOnly;

    private static readonly string[] collection =
    [
        "length", "toJSON", "valueOf", "push", "pop", "shift", "unshift", "slice", "splice", "indexOf",
        "includes", "join", "reverse", "sort"
    ];

    public object toJSON()
    {
        return _jarray.Select(ConvertToPlainObject).ToArray();
    }

    public object valueOf()
    {
        return toJSON();
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

    public object? push(params object[] items)
    {
        ThrowIfReadOnly();
        foreach (object item in items)
        {
            _jarray.Add(ConvertObjectToJToken(item));
        }
        return _jarray.Count;
    }

    public object? pop()
    {
        ThrowIfReadOnly();
        if (_jarray.Count == 0)
        {
            return null;
        }
        object? result = ConvertJTokenToObject(_jarray.Last);
        _jarray.RemoveAt(_jarray.Count - 1);
        return result;
    }

    public object? shift()
    {
        ThrowIfReadOnly();
        if (_jarray.Count == 0)
        {
            return null;
        }
        object? result = ConvertJTokenToObject(_jarray.First);
        _jarray.RemoveAt(0);
        return result;
    }

    public object? unshift(params object[] items)
    {
        ThrowIfReadOnly();
        for (int i = items.Length - 1; i >= 0; i--)
        {
            _jarray.Insert(0, ConvertObjectToJToken(items[i]));
        }
        return _jarray.Count;
    }

    public JArrayPropertyBag slice(int start, int? end = null)
    {
        int actualEnd = end ?? _jarray.Count;
        if (start < 0)
        {
            start = Math.Max(0, _jarray.Count + start);
        }
        if (actualEnd < 0)
        {
            actualEnd = Math.Max(0, _jarray.Count + actualEnd);
        }
        JArray newArray = [];
        for (int i = start; i < Math.Min(actualEnd, _jarray.Count); i++)
        {
            newArray.Add(_jarray[i].DeepClone());
        }
        return new JArrayPropertyBag(newArray, readOnly);
    }

    public JArrayPropertyBag splice(int start, int deleteCount = 0, params object[] items)
    {
        ThrowIfReadOnly();
        if (start < 0)
        {
            start = Math.Max(0, _jarray.Count + start);
        }
        deleteCount = Math.Max(0, Math.Min(deleteCount, _jarray.Count - start));
        JArray deleted = [];
        for (int i = 0; i < deleteCount; i++)
        {
            if (start < _jarray.Count)
            {
                deleted.Add(_jarray[start].DeepClone());
                _jarray.RemoveAt(start);
            }
        }
        for (int j = 0; j < items.Length; j++)
        {
            _jarray.Insert(start + j, ConvertObjectToJToken(items[j]));
        }
        return new JArrayPropertyBag(deleted, readOnly);
    }

    public int indexOf(object searchElement, int fromIndex = 0)
    {
        for (int i = Math.Max(0, fromIndex); i < _jarray.Count; i++)
        {
            if (object.Equals(ConvertJTokenToObject(_jarray[i]), searchElement))
            {
                return i;
            }
        }
        return -1;
    }

    public bool includes(object searchElement)
    {
        return indexOf(searchElement) >= 0;
    }

    public string join(string separator = ",")
    {
        return string.Join(separator, _jarray.Select(t => ConvertJTokenToObject(t)?.ToString() ?? ""));
    }

    public JArrayPropertyBag reverse()
    {
        ThrowIfReadOnly();
        JToken[] items = [.. _jarray];
        _jarray.Clear();
        for (int i = items.Length - 1; i >= 0; i--)
        {
            _jarray.Add(items[i]);
        }
        return this;
    }

    public JArrayPropertyBag sort()
    {
        ThrowIfReadOnly();
        object[] array = [.. from x in _jarray.Select(ConvertJTokenToObject)
                              orderby x?.ToString()
                              select x];
        _jarray.Clear();
        object[] array2 = array;
        foreach (object item in array2)
        {
            _jarray.Add(ConvertObjectToJToken(item));
        }
        return this;
    }

    public bool Remove(string name)
    {
        ThrowIfReadOnly();
        if (int.TryParse(name, out int index) && index >= 0 && index < _jarray.Count)
        {
            _jarray.RemoveAt(index);
            return true;
        }
        return false;
    }

    public void Add(string key, object value)
    {
        ThrowIfReadOnly();
        if (int.TryParse(key, out int index))
        {
            if (index == _jarray.Count)
            {
                _jarray.Add(ConvertObjectToJToken(value));
                return;
            }
            throw new ArgumentException($"Index {index} is not at the end of the array");
        }
        throw new ArgumentException("Invalid array key: " + key);
    }

    public void Add(KeyValuePair<string, object> item)
    {
        Add(item.Key, item.Value);
    }

    public void Clear()
    {
        ThrowIfReadOnly();
        _jarray.Clear();
    }

    public bool Contains(KeyValuePair<string, object> item)
    {
        return int.TryParse(item.Key, out int index) && index >= 0 && index < _jarray.Count && object.Equals(ConvertJTokenToObject(_jarray[index]), item.Value);
    }

    public bool ContainsKey(string key)
    {
        return int.TryParse(key, out int index)
            ? index >= 0 && index < _jarray.Count
            : PropertyNames.Contains(key.ToLower(System.Globalization.CultureInfo.CurrentCulture));
    }

    public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        int i = arrayIndex;
        for (int idx = 0; idx < _jarray.Count; idx++)
        {
            if (i >= array.Length)
            {
                break;
            }
            array[i++] = new KeyValuePair<string, object>(idx.ToString(), ConvertJTokenToObject(_jarray[idx])!);
        }
    }

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        for (int i = 0; i < _jarray.Count; i++)
        {
            yield return new KeyValuePair<string, object>(i.ToString(), ConvertJTokenToObject(_jarray[i])!);
        }
    }

    public bool Remove(KeyValuePair<string, object> item)
    {
        ThrowIfReadOnly();
        return Contains(item) && Remove(item.Key);
    }

    public bool TryGetValue(string key, out object value)
    {
        value = this[key];
        return value != null || ContainsKey(key);
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
