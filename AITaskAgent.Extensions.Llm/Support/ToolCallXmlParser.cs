using AITaskAgent.LLM.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AITaskAgent.LLM.Support;

/// <summary>
/// Parser que extrae tool calls en formato XML inline del contenido de respuesta LLM.
/// Compatible con modelos como GLM que devuelven tool calls como XML en lugar de formato OpenAI.
/// Soporta formatos:
/// 1. XML estandar: tool_name tags con contenido
/// 2. Formato GLM: pipe-delimited con arg_key/arg_value
/// </summary>
public static class ToolCallXmlParser
{
    private static readonly Regex TagRegex = new(
        @"<(\w[\w\-\.]*)([^>]*)>(.*?)</\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChildElementRegex = new(
        @"<(\w[\w\-\.]*)>(.*?)</\1>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AttributeRegex = new(
        @"(\w[\w\-\.]*)=""([^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex GlmToolCallRegex = new(
        @"<\|(\w[\w\-]*)>(.*?)<\|/\1\|>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GlmArgRegex = new(
        @"<arg_key>\s*([^<\n\r]+)\s*<arg_value>\s*([^<\n\r]+)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex GlmWrapperRegex = new(
        @"<tool_call>(\w[\w\-]*)\s*\n([\s\S]*?)</tool_call>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // JSON-in-<tool_call> wrapper: <tool_call>{"name":...}</tool_call> (Nemotron, Llama, etc.)
    private static readonly Regex JsonToolCallWrapperRegex = new(
        @"<tool_call>\s*(\{[\s\S]*?\}|\[[\s\S]*?\])\s*</tool_call>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ToolCall> ParseToolCalls(string content, HashSet<string> toolNames)
    {
        List<ToolCall> toolCalls = [];

        List<ToolCall> glmCalls = ParseGlmToolCalls(content, toolNames);
        toolCalls.AddRange(glmCalls);

        if (toolCalls.Count > 0)
        {
            return toolCalls;
        }

        List<ToolCall> wrapperCalls = ParseGlmWrapperToolCalls(content, toolNames);
        toolCalls.AddRange(wrapperCalls);

        if (toolCalls.Count > 0)
        {
            return toolCalls;
        }

        foreach (Match match in TagRegex.Matches(content))
        {
            var tagName = match.Groups[1].Value;

            if (toolNames.Contains(tagName))
            {
                var attrString = match.Groups[2].Value;
                var innerContent = match.Groups[3].Value;
                var arguments = BuildArgumentsJson(attrString, innerContent);

                toolCalls.Add(new ToolCall
                {
                    Id = System.Guid.NewGuid().ToString("N"),
                    Name = tagName,
                    Arguments = arguments
                });
                continue;
            }

            var matchedTool = FindBestToolMatch(tagName, toolNames);
            if (matchedTool != null)
            {
                var attrString = match.Groups[2].Value;
                var innerContent = match.Groups[3].Value;
                var arguments = BuildArgumentsJson(attrString, innerContent);

                toolCalls.Add(new ToolCall
                {
                    Id = System.Guid.NewGuid().ToString("N"),
                    Name = matchedTool,
                    Arguments = arguments
                });
            }
        }

        // JSON-in-content formats (Nemotron, Llama, etc.) — intentar si ningún formato anterior tuvo éxito
        if (toolCalls.Count == 0)
            toolCalls.AddRange(ParseJsonInContentToolCalls(content, toolNames));

        return toolCalls;
    }

    /// <summary>
    /// Parsea tool calls embebidas como JSON puro en content.
    /// Soporta: {"name":…,"arguments":{…}}, [{"name":…}], y &lt;tool_call&gt;{JSON}&lt;/tool_call&gt;.
    /// </summary>
    public static List<ToolCall> ParseJsonInContentToolCalls(string content, HashSet<string> toolNames)
    {
        if (string.IsNullOrWhiteSpace(content) || toolNames.Count == 0)
            return [];

        var trimmed = content.Trim();

        // 1. <tool_call>{JSON}</tool_call>
        var wrapperMatch = JsonToolCallWrapperRegex.Match(trimmed);
        if (wrapperMatch.Success)
        {
            var inner = wrapperMatch.Groups[1].Value.Trim();
            return TryParseJsonToolCallToken(inner, toolNames);
        }

        // 2. JSON puro (objeto o array)
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return TryParseJsonToolCallToken(trimmed, toolNames);

        return [];
    }

    private static List<ToolCall> TryParseJsonToolCallToken(string json, HashSet<string> toolNames)
    {
        try
        {
            var token = JToken.Parse(json);

            if (token is JArray arr)
            {
                var result = new List<ToolCall>();
                foreach (var item in arr.OfType<JObject>())
                {
                    var call = ParseSingleJsonToolCall(item, toolNames);
                    if (call != null) result.Add(call);
                }
                return result;
            }

            if (token is JObject obj)
            {
                var call = ParseSingleJsonToolCall(obj, toolNames);
                return call != null ? [call] : [];
            }
        }
        catch { }

        return [];
    }

    private static ToolCall? ParseSingleJsonToolCall(JObject obj, HashSet<string> toolNames)
    {
        // Formato: {"name":"tool","arguments":{...}}  (Nemotron/Llama)
        // Formato: {"type":"function","function":{"name":"tool","arguments":"..."}}  (OpenAI-style)
        string? name = obj["name"]?.Value<string>()
                    ?? obj["tool_name"]?.Value<string>();

        if (name == null && obj["function"] is JObject fn)
            name = fn["name"]?.Value<string>();

        if (name == null) return null;

        var matched = toolNames.Contains(name) ? name : FindBestToolMatch(name, toolNames);
        if (matched == null) return null;

        JToken? argsToken = obj["arguments"] ?? obj["parameters"] ?? obj["input"];
        if (argsToken == null && obj["function"] is JObject fnArgs)
            argsToken = fnArgs["arguments"];

        string arguments;
        if (argsToken == null)
            arguments = "{}";
        else if (argsToken.Type == JTokenType.String)
            arguments = argsToken.Value<string>() ?? "{}";
        else
            arguments = argsToken.ToString(Formatting.None);

        return new ToolCall
        {
            Id        = System.Guid.NewGuid().ToString("N"),
            Name      = matched,
            Arguments = arguments
        };
    }

    private static List<ToolCall> ParseGlmWrapperToolCalls(string content, HashSet<string> toolNames)
    {
        List<ToolCall> toolCalls = [];

        foreach (Match match in GlmWrapperRegex.Matches(content))
        {
            var tagName = match.Groups[1].Value.Trim();
            var innerContent = match.Groups[2].Value;

            var matchedTool = toolNames.Contains(tagName) ? tagName : FindBestToolMatch(tagName, toolNames);
            if (matchedTool == null)
            {
                continue;
            }

            var arguments = ParseGlmArguments(innerContent);

            toolCalls.Add(new ToolCall
            {
                Id = System.Guid.NewGuid().ToString("N"),
                Name = matchedTool,
                Arguments = arguments
            });
        }

        return toolCalls;
    }

    private static List<ToolCall> ParseGlmToolCalls(string content, HashSet<string> toolNames)
    {
        List<ToolCall> toolCalls = [];

        foreach (Match match in GlmToolCallRegex.Matches(content))
        {
            var tagName = match.Groups[1].Value;
            var innerContent = match.Groups[2].Value.Trim();

            var matchedTool = toolNames.Contains(tagName) ? tagName : FindBestToolMatch(tagName, toolNames);
            if (matchedTool == null)
            {
                continue;
            }

            var arguments = ParseGlmArguments(innerContent);

            toolCalls.Add(new ToolCall
            {
                Id = System.Guid.NewGuid().ToString("N"),
                Name = matchedTool,
                Arguments = arguments
            });
        }

        return toolCalls;
    }

    private static string ParseGlmArguments(string innerContent)
    {
        var trimmed = innerContent.Trim();

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                JsonConvert.DeserializeObject(trimmed);
                return trimmed;
            }
            catch { }
        }

        Dictionary<string, string> args = [];
        foreach (Match argMatch in GlmArgRegex.Matches(trimmed))
        {
            var key = argMatch.Groups[1].Value.Trim();
            var value = argMatch.Groups[2].Value.Trim();
            args[key] = value;
        }

        return args.Count > 0
            ? JsonConvert.SerializeObject(args)
            : JsonConvert.SerializeObject(new Dictionary<string, string> { ["content"] = trimmed });
    }

    /// <summary>
    /// Busca un tool que sea equivalente al tagName normalizando guiones y underscores.
    /// Solo aplica equivalencia guion-underscore, no substring matching genérico
    /// para evitar falsos positivos (ej: &lt;file&gt; matcheando view_file).
    /// </summary>
    private static string? FindBestToolMatch(string tagName, HashSet<string> toolNames)
    {
        // Normalizar: tratar guiones y underscores como equivalentes
        var tagNormalized = tagName.ToLowerInvariant().Replace('-', '_');

        foreach (var toolName in toolNames)
        {
            var toolNormalized = toolName.ToLowerInvariant().Replace('-', '_');
            if (tagNormalized == toolNormalized)
            {
                return toolName;
            }
        }

        return null;
    }

    public static string RemoveToolCallTags(string content, HashSet<string> toolNames)
    {
        content = GlmWrapperRegex.Replace(content, m =>
            toolNames.Contains(m.Groups[1].Value) || FindBestToolMatch(m.Groups[1].Value, toolNames) != null
                ? ""
                : m.Value);

        content = GlmToolCallRegex.Replace(content, m =>
            toolNames.Contains(m.Groups[1].Value) || FindBestToolMatch(m.Groups[1].Value, toolNames) != null
                ? ""
                : m.Value);

        content = TagRegex.Replace(content, m =>
            toolNames.Contains(m.Groups[1].Value) || FindBestToolMatch(m.Groups[1].Value, toolNames) != null
                ? ""
                : m.Value);

        return content;
    }

    private static string BuildArgumentsJson(string attrString, string innerContent)
    {
        Dictionary<string, string> attributes = ParseAttributes(attrString);
        var trimmedInner = innerContent.Trim();

        if (trimmedInner.StartsWith('{') || trimmedInner.StartsWith('['))
        {
            try
            {
                JsonConvert.DeserializeObject(trimmedInner);
                return trimmedInner;
            }
            catch { }
        }

        Dictionary<string, string> dict = new(attributes);

        var hasChildren = false;
        foreach (Match childMatch in ChildElementRegex.Matches(trimmedInner))
        {
            hasChildren = true;
            dict[childMatch.Groups[1].Value] = childMatch.Groups[2].Value.Trim();
        }

        if (!hasChildren && attributes.Count == 0 && !string.IsNullOrEmpty(trimmedInner))
        {
            dict["content"] = trimmedInner;
        }

        return dict.Count > 0 ? JsonConvert.SerializeObject(dict) : "{}";
    }

    private static Dictionary<string, string> ParseAttributes(string attrString)
    {
        Dictionary<string, string> attributes = [];

        foreach (Match match in AttributeRegex.Matches(attrString))
        {
            attributes[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return attributes;
    }
}
