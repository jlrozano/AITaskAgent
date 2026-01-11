using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AITaskAgent.Support.Template;

/// <summary>
/// Template engine using JSON serialization and JPath for property navigation.
/// Optimized for interpolating typed step results into LLM prompts with token-efficient formatting.
/// </summary>
/// <remarks>
/// Supported syntax:
/// <list type="bullet">
/// <item>{{Property}} - Simple property access</item>
/// <item>{{Nested.Property}} - Nested navigation via JPath</item>
/// <item>{{Array[0]}} - Array indexing</item>
/// <item>{{Items:csv}} - Array to CSV (token-efficient for tabular data)</item>
/// <item>{{Data:json}} - Object/Array to compact JSON</item>
/// <item>{{Data:json:indent}} - Object/Array to indented JSON</item>
/// <item>{{Date:yyyy-MM-dd}} - Standard .NET formatting</item>
/// <item>{{Prop ?? default}} - Default value if null/missing</item>
/// </list>
/// </remarks>
public sealed partial class JsonTemplateEngine : ITemplateEngine
{
    /// <summary>
    /// Regex pattern matching:
    /// - {{PropertyPath}} - Simple property access
    /// - {{PropertyPath:format1:format2}} - With formatters (chained)
    /// - {{PropertyPath ?? default}} - With default value
    /// - {{PropertyPath:format ?? default}} - Both formatting and default
    /// </summary>
    [GeneratedRegex(@"\{\{([^}:?]+)(?::([^}?]+))?(?:\?\?\s*([^}]+))?\}\}", RegexOptions.Compiled)]
    private static partial Regex TemplatePattern();

    public static string Render(string template, object obj, bool strictMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentNullException.ThrowIfNull(obj);
        var jToken = JToken.FromObject(obj);
        return RenderInternal(template, jToken, strictMode);
    }
    /// <summary>
    /// Renders a template with parameters from an object's properties.
    /// Optimized for IStepResponse.Value interpolation.
    /// </summary>
    string ITemplateEngine.Render(string template, object obj)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentNullException.ThrowIfNull(obj);

        var jToken = JToken.FromObject(obj);
        return RenderInternal(template, jToken, strictMode: false);
    }

    private static string RenderInternal(string template, JToken rootToken, bool strictMode)
    {
        return TemplatePattern().Replace(template, match =>
        {
            var path = match.Groups[1].Value.Trim();
            var formatSpec = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            var defaultValue = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;

            try
            {
                // Use JPath to navigate nested properties and arrays
                var token = rootToken.SelectToken(path);

                if (token is null or { Type: JTokenType.Null })
                {
                    // In strict mode, throw if no default value is provided
                    if (strictMode && defaultValue == null)
                    {
                        throw new InvalidOperationException(
                            $"Template property '{path}' not found or is null in model of type '{rootToken.Type}'. " +
                            $"Provide a default value using '?? default' syntax or ensure the property exists.");
                    }
                    return defaultValue ?? string.Empty;
                }

                // Apply formatting if specified
                if (!string.IsNullOrEmpty(formatSpec))
                {
                    return ApplyFormatters(token, formatSpec) ?? token.ToString();
                }

                return token.ToString();
            }
            catch (InvalidOperationException)
            {
                // Re-throw strict mode exceptions
                throw;
            }
            catch (Exception ex)
            {
                // If path resolution fails
                if (strictMode && defaultValue == null)
                {
                    throw new InvalidOperationException(
                        $"Template property '{path}' failed to resolve in model of type '{rootToken.Type}': {ex.Message}. " +
                        $"Provide a default value using '?? default' syntax or fix the property path.", ex);
                }
                return defaultValue ?? match.Value;
            }
        });
    }

    private static string? ApplyFormatters(JToken token, string formatSpec)
    {
        // Split format spec by colon to support chained formatters (e.g., "json:indent")
        var formatters = formatSpec.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (formatters.Length == 0)
            return null;

        var primaryFormatter = formatters[0].ToLowerInvariant();

        return primaryFormatter switch
        {
            // Custom formatters for LLM token optimization
            "csv" => FormatAsCsv(token),
            "json" => FormatAsJson(token, indent: formatters.Length > 1 && formatters[1] == "indent"),

            // Standard .NET formatting for primitives
            _ => FormatPrimitive(token, formatSpec)
        };
    }

    /// <summary>
    /// Formats an array as CSV for token-efficient representation.
    /// Example: [{"Name":"John","Age":30},{"Name":"Jane","Age":25}]
    /// Output: "Name,Age\nJohn,30\nJane,25"
    /// </summary>
    private static string? FormatAsCsv(JToken token)
    {
        if (token is not JArray array || array.Count == 0)
            return token.ToString();

        var sb = new StringBuilder();

        // Get headers from first object
        if (array[0] is JObject firstObj)
        {
            var headers = firstObj.Properties().Select(p => p.Name).ToArray();
            sb.AppendLine(string.Join(",", headers));

            // Add rows
            foreach (var item in array.OfType<JObject>())
            {
                var values = headers.Select(h => EscapeCsvValue(item[h]?.ToString() ?? ""));
                sb.AppendLine(string.Join(",", values));
            }
        }
        else
        {
            // Simple array of primitives
            foreach (var item in array)
            {
                sb.AppendLine(EscapeCsvValue(item.ToString()));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string EscapeCsvValue(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    /// <summary>
    /// Formats token as JSON (compact or indented).
    /// </summary>
    private static string FormatAsJson(JToken token, bool indent)
    {
        var formatting = indent ? Formatting.Indented : Formatting.None;
        return token.ToString(formatting);
    }

    /// <summary>
    /// Formats primitive types using standard .NET formatting.
    /// </summary>
    private static string? FormatPrimitive(JToken token, string format)
    {
        return token.Type switch
        {
            JTokenType.Date => token.ToObject<DateTime>().ToString(format),
            JTokenType.Float => token.ToObject<double>().ToString(format),
            JTokenType.Integer => token.ToObject<long>().ToString(format),
            JTokenType.TimeSpan => token.ToObject<TimeSpan>().ToString(format),
            _ => null
        };
    }
}
