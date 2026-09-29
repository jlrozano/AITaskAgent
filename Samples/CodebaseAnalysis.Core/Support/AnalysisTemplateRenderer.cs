using AITaskAgent.Core.StepResults;
using AITaskAgent.Support.Template;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Support;

public class AnalysisTemplateRenderer : ITemplateRendered
{
    private readonly string? _baseDirectory;

    public AnalysisTemplateRenderer(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory;
    }

    public string Render(string template, object obj)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        var mergedDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["current_timestamp"] = DateTime.UtcNow.ToString("o")
        };

        // Extract input and context if present
        object? inputObj = null;
        object? contextObj = null;

        if (obj is not null)
        {
            var objType = obj.GetType();
            var inputProp = objType.GetProperty("Input", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var contextProp = objType.GetProperty("Context", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (inputProp != null)
            {
                inputObj = inputProp.GetValue(obj);
            }
            if (contextProp != null)
            {
                contextObj = contextProp.GetValue(obj);
            }

            if (inputObj == null)
            {
                inputObj = obj;
            }
        }

        // Add root input/context references
        if (inputObj != null)
        {
            mergedDict["input"] = inputObj;
            FlattenObject(inputObj, mergedDict);

            if (inputObj is StepResult sr && sr.Value != null && !ReferenceEquals(sr.Value, sr))
            {
                FlattenObject(sr.Value, mergedDict);
            }
        }

        if (contextObj != null)
        {
            mergedDict["context"] = contextObj;
            if (contextObj is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is string key)
                    {
                        mergedDict[key] = entry.Value;
                        mergedDict[ToSnakeCase(key)] = entry.Value;
                    }
                }
            }
            else
            {
                FlattenObject(contextObj, mergedDict);
            }
        }

        return TinyTemplateRendered.Render(template, mergedDict, strictMode: false, baseDirectory: _baseDirectory);
    }

    private static void FlattenObject(object target, Dictionary<string, object?> destination)
    {
        if (target is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is string key)
                {
                    destination[key] = entry.Value;
                    destination[ToSnakeCase(key)] = entry.Value;
                }
            }
            return;
        }

        var properties = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            if (prop.GetIndexParameters().Length > 0)
                continue;

            try
            {
                var val = prop.GetValue(target);
                destination[prop.Name] = val;
                destination[char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]] = val;
                destination[ToSnakeCase(prop.Name)] = val;
            }
            catch
            {
                // Ignore property getter errors
            }
        }
    }

    private static string ToSnakeCase(string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        return Regex.Replace(str, @"([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
    }
}
