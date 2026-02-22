using Newtonsoft.Json.Linq;
using NJsonSchema;
using NJsonSchema.Validation;

namespace BRMS.Core.Extensions;

/// <summary>
/// Métodos de extensión para JObject para extracción y asignación tipada de valores usando path.
/// </summary>
public static class JsonObjectExtensions
{
    /// <summary>
    /// Extrae el valor de la propiedad indicada por path y lo convierte al tipo T.
    /// </summary>
    /// <param name="obj">Objeto JSON de origen</param>
    /// <param name="path">Ruta JSONPath de la propiedad</param>
    /// <returns>Valor convertido al tipo T o default si no existe</returns>
    /// <exception cref="ArgumentNullException">Cuando obj o path son null</exception>
    public static T? GetValueAs<T>(this JToken obj, string path)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JToken? token = obj.SelectToken(path);

        if (token == null)
        {
            // Intentar acceso directo si SelectToken falla
            if (obj[path] != null)
            {
                token = obj[path];
            }
            else
            {
                return default;
            }
        }

        if (token!.Type == JTokenType.Null)
        {
            return default;
        }

        try
        {
            T? result = token.ToObject<T>();
            return result;
        }
        catch
        {
            // Intento de conversión manual si falla ToObject
            string str = token.ToString();
            if (typeof(T) == typeof(int) && int.TryParse(str, out int i))
            {
                return (T)(object)i;
            }

            if (typeof(T) == typeof(decimal) && decimal.TryParse(str, out decimal d))
            {
                return (T)(object)d;
            }

            if (typeof(T) == typeof(DateTime) && DateTime.TryParse(str, out DateTime dt))
            {
                return (T)(object)dt;
            }

            if (typeof(T) == typeof(bool) && bool.TryParse(str, out bool b))
            {
                return (T)(object)b;
            }

            var strResult = (T)(object)str;
            return strResult;
        }
    }

    /// <summary>
    /// Asigna un valor a la propiedad indicada por path, creando la estructura necesaria si no existe.
    /// </summary>
    /// <param name="obj">Objeto JSON destino</param>
    /// <param name="path">Ruta JSONPath de la propiedad</param>
    /// <param name="value">Valor a asignar</param>
    /// <exception cref="ArgumentNullException">Cuando obj o path son null</exception>
    public static void SetValueWithType(this JToken obj, string path, object? value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Limpiar el path
        string cleanPath = path.Replace("$.", "");

        // Si el path es simple (sin puntos ni corchetes), asignar directamente
        if (!cleanPath.Contains('.') && !cleanPath.Contains('['))
        {
            obj[cleanPath] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            return;
        }

        // Para paths complejos, crear la estructura necesaria
        string[] pathParts = cleanPath.Split('.');
        JToken current = obj;

        for (int i = 0; i < pathParts.Length - 1; i++)
        {
            string part = pathParts[i];

            // Manejar arrays
            if (part.Contains('['))
            {
                string arrayName = part[..part.IndexOf('[')];
                string indexStr = part.Substring(part.IndexOf('[') + 1, part.IndexOf(']') - part.IndexOf('[') - 1);
                int index = int.Parse(indexStr);

                // Crear array si no existe
                if (current[arrayName] == null)
                {
                    current[arrayName] = new JArray();
                }

                if (current[arrayName] is JArray array)
                {
                    // Expandir array si es necesario
                    while (array.Count <= index)
                    {
                        array.Add(JValue.CreateNull());
                    }

                    // Crear objeto en la posición del array si es necesario
                    if (array[index] == null || array[index].Type == JTokenType.Null)
                    {
                        array[index] = new JObject();
                    }

                    current = array[index] as JObject ?? [];
                }
            }
            else
            {
                // Crear objeto anidado si no existe
                if (current[part] == null)
                {
                    current[part] = new JObject();
                }

                current = current[part] as JObject ?? [];
            }
        }

        // Asignar el valor final
        string finalPart = pathParts[^1];
        if (finalPart.Contains('['))
        {
            string arrayName = finalPart[..finalPart.IndexOf('[')];
            string indexStr = finalPart.Substring(finalPart.IndexOf('[') + 1, finalPart.IndexOf(']') - finalPart.IndexOf('[') - 1);
            int index = int.Parse(indexStr);

            // Crear array si no existe
            if (current[arrayName] == null)
            {
                current[arrayName] = new JArray();
            }

            if (current[arrayName] is JArray array)
            {
                // Expandir array si es necesario
                while (array.Count <= index)
                {
                    array.Add(JValue.CreateNull());
                }

                // Asignar valor
                array[index] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
        }
        else
        {
            // Asignar valor directo
            current[finalPart] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
        }
    }

    /// <summary>
    /// Valida un JObject contra un JsonSchema y devuelve si es válido junto con una lista de errores.
    /// Simula el comportamiento de JObject.IsValid de Newtonsoft.Json.Schema.
    /// </summary>
    /// <param name="jObject">El objeto JObject a validar</param>
    /// <param name="schema">El esquema JsonSchema contra el cual validar</param>
    /// <param name="errorMessages">Lista de mensajes de error de salida</param>
    /// <returns>True si el objeto es válido, false en caso contrario</returns>
    public static bool IsValid(this JObject jObject, JsonSchema schema, out IList<string> errorMessages)
    {
        ArgumentNullException.ThrowIfNull(jObject);

        ArgumentNullException.ThrowIfNull(schema, nameof(schema));

        try
        {
            ICollection<ValidationError> errors = schema.Validate(jObject);
            errorMessages = [.. errors.Select(error => FormatValidationError(error, jObject))];
            return errors.Count == 0;
        }
        catch (Exception ex)
        {
            errorMessages = [$"Error durante la validación: {ex.Message}"];
            return false;
        }
    }



    /// <summary>
    /// Formatea un error de validación en un mensaje legible, similar al formato de JSchema
    /// </summary>
    /// <param name="error">El error de validación</param>
    /// <param name="value">El objeto JSON que se validó</param>
    /// <returns>Mensaje de error formateado</returns>
    private static string FormatValidationError(ValidationError error, JObject value)
    {
        string path = string.IsNullOrEmpty(error.Path) ? "#" : $"#{error.Path}";
        object? actualValue = GetValueAtPath(value, error.Path);
        string actualType = GetActualType(actualValue);

        return error.Kind switch
        {
            ValidationErrorKind.NoAdditionalPropertiesAllowed =>
                $"Property '{GetLastPathSegment(error.Path)}' has not been defined and the schema does not allow additional properties. Path '{path}'.",

            ValidationErrorKind.PropertyRequired =>
                $"Required property '{error.Property ?? GetLastPathSegment(error.Path)}' is missing from object. Path '{path}'.",

            ValidationErrorKind.StringExpected =>
                $"Invalid type. Expected string but got {actualType}. Path '{path}'.",

            ValidationErrorKind.NumberExpected =>
                $"Invalid type. Expected number but got {actualType}. Path '{path}'.",

            ValidationErrorKind.IntegerExpected =>
                $"Invalid type. Expected integer but got {actualType}. Path '{path}'.",

            ValidationErrorKind.BooleanExpected =>
                $"Invalid type. Expected boolean but got {actualType}. Path '{path}'.",

            ValidationErrorKind.ObjectExpected =>
                $"Invalid type. Expected object but got {actualType}. Path '{path}'.",

            ValidationErrorKind.ArrayExpected =>
                $"Invalid type. Expected array but got {actualType}. Path '{path}'.",

            ValidationErrorKind.NullExpected =>
                $"Invalid type. Expected null but got {actualType}. Path '{path}'.",

            ValidationErrorKind.StringTooShort =>
                $"String is too short ({actualValue?.ToString()?.Length ?? 0} characters), minimum length required. Path '{path}'.",

            ValidationErrorKind.StringTooLong =>
                $"String is too long ({actualValue?.ToString()?.Length ?? 0} characters), maximum length exceeded. Path '{path}'.",

            ValidationErrorKind.NumberTooSmall =>
                $"Number {actualValue} is less than the minimum allowed value. Path '{path}'.",

            ValidationErrorKind.NumberTooBig =>
                $"Number {actualValue} is greater than the maximum allowed value. Path '{path}'.",

            ValidationErrorKind.IntegerTooBig =>
                $"Integer {actualValue} is greater than the maximum allowed value. Path '{path}'.",

            ValidationErrorKind.TooManyItems =>
                $"Array has too many items, maximum allowed exceeded. Path '{path}'.",

            ValidationErrorKind.TooFewItems =>
                $"Array has too few items, minimum required not met. Path '{path}'.",

            ValidationErrorKind.ItemsNotUnique =>
                $"Array items are not unique. Path '{path}'.",

            ValidationErrorKind.PatternMismatch =>
                $"String '{actualValue}' does not match the required pattern. Path '{path}'.",

            ValidationErrorKind.DateTimeExpected =>
                $"String '{actualValue}' is not a valid date-time format. Path '{path}'.",

            ValidationErrorKind.DateExpected =>
                $"String '{actualValue}' is not a valid date format. Path '{path}'.",

            ValidationErrorKind.TimeExpected =>
                $"String '{actualValue}' is not a valid time format. Path '{path}'.",

            ValidationErrorKind.TimeSpanExpected =>
                $"String '{actualValue}' is not a valid time-span format. Path '{path}'.",

            ValidationErrorKind.UriExpected =>
                $"String '{actualValue}' is not a valid URI format. Path '{path}'.",

            ValidationErrorKind.EmailExpected =>
                $"String '{actualValue}' is not a valid email format. Path '{path}'.",

            ValidationErrorKind.HostnameExpected =>
                $"String '{actualValue}' is not a valid hostname format. Path '{path}'.",

            ValidationErrorKind.IpV4Expected =>
                $"String '{actualValue}' is not a valid IPv4 address format. Path '{path}'.",

            ValidationErrorKind.IpV6Expected =>
                $"String '{actualValue}' is not a valid IPv6 address format. Path '{path}'.",

            ValidationErrorKind.GuidExpected =>
                $"String '{actualValue}' is not a valid GUID format. Path '{path}'.",

            ValidationErrorKind.UuidExpected =>
                $"String '{actualValue}' is not a valid UUID format. Path '{path}'.",

            ValidationErrorKind.Base64Expected =>
                $"String '{actualValue}' is not a valid Base64 format. Path '{path}'.",

            ValidationErrorKind.NotInEnumeration =>
                $"Value '{actualValue}' is not one of the allowed enumeration values. Path '{path}'.",

            ValidationErrorKind.NotAllOf =>
                $"Object does not validate against all of the required schemas (allOf). Path '{path}'.",

            ValidationErrorKind.NotAnyOf =>
                $"Object does not validate against any of the allowed schemas (anyOf). Path '{path}'.",

            ValidationErrorKind.NotOneOf =>
                $"Object does not validate against exactly one of the allowed schemas (oneOf). Path '{path}'.",

            ValidationErrorKind.ExcludedSchemaValidates =>
                $"Object validates against the excluded schema (not). Path '{path}'.",

            ValidationErrorKind.NumberNotMultipleOf =>
                $"Number {actualValue} is not a multiple of the required value. Path '{path}'.",

            ValidationErrorKind.IntegerNotMultipleOf =>
                $"Integer {actualValue} is not a multiple of the required value. Path '{path}'.",

            ValidationErrorKind.TooManyProperties =>
                $"Object has too many properties, maximum allowed exceeded. Path '{path}'.",

            ValidationErrorKind.TooFewProperties =>
                $"Object has too few properties, minimum required not met. Path '{path}'.",

            ValidationErrorKind.AdditionalPropertiesNotValid =>
                $"Additional properties are not valid according to the schema. Path '{path}'.",

            ValidationErrorKind.AdditionalItemNotValid =>
                $"Additional array item is not valid according to the schema. Path '{path}'.",

            ValidationErrorKind.ArrayItemNotValid =>
                $"Array item at index {GetLastPathSegment(error.Path)} is not valid. Path '{path}'.",

            ValidationErrorKind.TooManyItemsInTuple =>
                $"Tuple array has too many items. Path '{path}'.",

            ValidationErrorKind.NoTypeValidates =>
                $"Value does not match any of the expected types. Path '{path}'.",

            _ => $"Validation error: {error.Kind} - {error}. Path '{path}'."
        };
    }


    /// <summary>
    /// Obtiene el último segmento de una ruta JSON
    /// </summary>
    private static string GetLastPathSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        string[] segments = path.Split('/');
        return segments.LastOrDefault() ?? "";
    }

    /// <summary>
    /// Obtiene el valor en la ruta especificada del objeto JSON
    /// </summary>
    private static object? GetValueAtPath(JToken? token, string? path)
    {
        if (string.IsNullOrEmpty(path) || token == null)
        {
            return token;
        }

        try
        {
            IEnumerable<string> segments = path.Split('/').Where(s => !string.IsNullOrEmpty(s));
            JToken? current = token;

            foreach (string segment in segments)
            {
                if (current is JObject obj)
                {
                    current = obj[segment];
                }
                else if (current is JArray arr && int.TryParse(segment, out int index))
                {
                    current = index < arr.Count ? arr[index] : null;
                }
                else
                {
                    return null;
                }
            }

            return current?.Value<object>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Obtiene el tipo actual del valor para mensajes de error
    /// </summary>
    private static string GetActualType(object? value)
    {
        return value switch
        {
            null => "null",
            bool => "boolean",
            int or long or double or float or decimal => "number",
            string => "string",
            JObject => "object",
            JArray => "array",
            _ => value.GetType().Name.ToLower()
        };
    }

    public static bool DeepEquals(
        this JToken? a,
        JToken? b,
        Dictionary<string, Func<JToken, object?>> selectors,
        Dictionary<string, HashSet<string>> ignoreByPath,
        HashSet<string>? ignoreGlobal = null,
        string path = "")
    {
        if (a == null && b == null)
        {
            return true;
        }

        if (a == null || b == null)
        {
            return false;
        }

        if (a.Type != b.Type)
        {
            return false;
        }

        switch (a.Type)
        {
            case JTokenType.Object:
                var objA = (JObject)a;
                var objB = (JObject)b;

                HashSet<string> ignore = GetIgnoreSet(ignoreByPath, ignoreGlobal, path);

                var propsA = objA.Properties().Where(p => !ignore.Contains(p.Name)).ToList();
                var propsB = objB.Properties().Where(p => !ignore.Contains(p.Name)).ToList();

                if (propsA.Count != propsB.Count)
                {
                    return false;
                }

                var namesA = propsA.Select(p => p.Name).ToHashSet();
                var namesB = propsB.Select(p => p.Name).ToHashSet();
                if (!namesA.SetEquals(namesB))
                {
                    return false;
                }

                foreach (JProperty? prop in propsA)
                {
                    string childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                    if (!DeepEquals(prop.Value, objB[prop.Name], selectors, ignoreByPath, ignoreGlobal, childPath))
                    {
                        return false;
                    }
                }
                return true;

            case JTokenType.Array:
                var arrA = (JArray)a;
                var arrB = (JArray)b;
                if (arrA.Count != arrB.Count)
                {
                    return false;
                }

                string arrPath = $"{path}[]";
                IEnumerable<JToken> normA = arrA;
                IEnumerable<JToken> normB = arrB;

                if (selectors.TryGetValue(arrPath, out Func<JToken, object?>? selector))
                {
                    // Ordenamos por selector tras aplicar ignorados por ruta a cada objeto
                    normA = arrA.OrderBy(x => selector(ApplyIgnoresForPath(x, ignoreByPath, ignoreGlobal, arrPath)));
                    normB = arrB.OrderBy(x => selector(ApplyIgnoresForPath(x, ignoreByPath, ignoreGlobal, arrPath)));
                }
                else
                {
                    // Multiconjunto: ordena por clave canónica basada únicamente en JValue
                    normA = arrA.OrderBy(x => CanonicalLeafKey(x, ignoreByPath, ignoreGlobal, arrPath));
                    normB = arrB.OrderBy(x => CanonicalLeafKey(x, ignoreByPath, ignoreGlobal, arrPath));
                }

                using (IEnumerator<JToken> e1 = normA.GetEnumerator())
                using (IEnumerator<JToken> e2 = normB.GetEnumerator())
                {
                    while (e1.MoveNext() && e2.MoveNext())
                    {
                        if (!DeepEquals(e1.Current, e2.Current, selectors, ignoreByPath, ignoreGlobal, arrPath))
                        {
                            return false;
                        }
                    }
                }
                return true;

            default: // JValue
                return JToken.DeepEquals(a, b);
                ;
        }
    }

    private static HashSet<string> GetIgnoreSet(
        Dictionary<string, HashSet<string>> ignoreByPath,
        HashSet<string>? ignoreGlobal,
        string path)
    {
        return ignoreByPath.TryGetValue(path, out HashSet<string>? set) ? set : ignoreGlobal ?? [];
    }

    private static JToken ApplyIgnoresForPath(
        JToken token,
        Dictionary<string, HashSet<string>> ignoreByPath,
        HashSet<string>? ignoreGlobal,
        string path)
    {
        // Solo necesitamos eliminar propiedades ignoradas cuando el selector se evalúa en objetos
        if (token.Type != JTokenType.Object)
        {
            return token;
        }

        HashSet<string> ignore = GetIgnoreSet(ignoreByPath, ignoreGlobal, path);
        var obj = (JObject)token;
        var clone = new JObject();
        foreach (JProperty? p in obj.Properties().Where(p => !ignore.Contains(p.Name)))
        {
            clone[p.Name] = p.Value; // sin recursión: el selector apunta a campos de nivel actual
        }

        return clone;
    }

    private static string CanonicalLeafKey(
        JToken token,
        Dictionary<string, HashSet<string>> ignoreByPath,
        HashSet<string>? ignoreGlobal,
        string path)
    {
        // Construye una clave estable usando SOLO JValue hojas, con nombres totalmente calificados ordenados.
        var pairs = new List<(string Key, string Val)>();
        CollectLeaves(token, ignoreByPath, ignoreGlobal, path, "", pairs);

        return string.Join("|", pairs.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Val));
    }

    private static void CollectLeaves(
        JToken token,
        Dictionary<string, HashSet<string>> ignoreByPath,
        HashSet<string>? ignoreGlobal,
        string path,
        string localName,
        List<(string Key, string Val)> pairs)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
                HashSet<string> ignore = GetIgnoreSet(ignoreByPath, ignoreGlobal, path);
                foreach (JProperty? p in ((JObject)token).Properties().Where(p => !ignore.Contains(p.Name)))
                {
                    string childName = string.IsNullOrEmpty(localName) ? p.Name : $"{localName}.{p.Name}";
                    string childPath = string.IsNullOrEmpty(path) ? p.Name : $"{path}.{p.Name}";
                    CollectLeaves(p.Value, ignoreByPath, ignoreGlobal, childPath, childName, pairs);
                }
                break;

            case JTokenType.Array:

                foreach (JToken item in (JArray)token)
                {
                    string childName = string.IsNullOrEmpty(localName) ? "[]" : $"{localName}[]";
                    string childPath = $"{path}[]";
                    CollectLeaves(item, ignoreByPath, ignoreGlobal, childPath, childName, pairs);
                }
                break;

            default: // JValue
                pairs.Add((localName, token.ToString(Newtonsoft.Json.Formatting.None)));
                break;
        }
    }



}
