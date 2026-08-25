using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace AITaskAgent.Support.Template;

public sealed partial class TinyTemplateRendered : ITemplateRendered
{
    // {{ property }} / {{ object.property }} / {{ dict['Key'] }}
    // Soporta pipe: {{ value | filter }} / {{ value | filter "arg" }}
    // Soporta default legacy: {{ value ?? fallback }}
    private static readonly Regex TemplatePattern = TemplateRegex();

    private static readonly Regex FileIncludePattern = FileIncludeRegex();

    private static readonly Regex ScribanIncludePattern = ScribanIncludeRegex();

    private static readonly Regex ScribanCommentPattern = ScribanCommentRegex();

    // Serializer que convierte propiedades C# a lowercase (SomeProperty → someproperty)
    // Los diccionarios mantienen sus keys literales — se acceden con ['Key']
    private static readonly JsonSerializer LowercaseSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new LowercaseNamingStrategy()
        }
    });

    public static string Render(string template, object obj, bool strictMode = false, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        ArgumentNullException.ThrowIfNull(obj);

        // Propiedades C# → lowercase. Keys de diccionario → literales.
        var jToken = JToken.FromObject(obj, LowercaseSerializer);
        return RenderInternal(template, jToken, strictMode, baseDirectory);
    }

    string ITemplateRendered.Render(string template, object obj) => Render(template, obj, false);

    private static string RenderInternal(string template, JToken rootToken, bool strictMode, string? baseDirectory = null)
    {
        if (string.IsNullOrEmpty(template) || template.Length > 2_000_000)
        {
            return template;
        }

        // Pass 0: Eliminar comentarios {{# ... #}}
        var result = ScribanCommentPattern.Replace(template, string.Empty);

        // Pass 1: {{ include 'path' }}
        result = ScribanIncludePattern.Replace(result, match =>
        {
            var rawPath = match.Groups[1].Value.Trim();
            return ResolveFileInclude(rawPath, match.Value, rootToken, strictMode, baseDirectory);
        });

        // Pass 2: Interpolación {{ property }} / {{ property | filter }} / {{ dict['Key'] }}
        result = TemplatePattern.Replace(result, match =>
        {
            var expr = match.Groups[1].Value.Trim();
            var pipeExpr = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            var defaultVal = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;

            if (IsScribanDirective(expr))
            {
                return match.Value;
            }

            try
            {
                // Convertir sintaxis Scriban dict['Key'] → JPath dict.Key
                var jpath = ToJPath(expr);
                var token = rootToken.SelectToken(jpath);

                return token is null or { Type: JTokenType.Null }
                    ? defaultVal ?? string.Empty
                    : pipeExpr is not null ? ApplyPipeFilter(token, pipeExpr) ?? token.ToString() : token.ToString();
            }
            catch
            {
                return defaultVal ?? match.Value;
            }
        });

        // Pass 3: @[path] includes (legacy)
        result = FileIncludePattern.Replace(result, match =>
        {
            var rawPath = match.Groups[1].Value.Trim();
            var cleanPath = rawPath.Trim('[', ']', '@', ' ', '\t', '\r', '\n');
            return ResolveFileInclude(cleanPath, match.Value, rootToken, strictMode, baseDirectory);
        });

        return result;
    }

    /// <summary>
    /// Convierte expresión Scriban a JPath compatible con SelectToken.
    /// dict['Key']   → dict.Key
    /// dict["Key"]   → dict.Key
    /// input.arr[0]  → input.arr[0]  (índice numérico, ya es JPath válido)
    /// context.mykey → context.mykey (sin cambios)
    /// </summary>
    private static string ToJPath(string expr)
    {
        var result = PipeFilterRegex().Replace(expr, ".$1");
        result = result.Replace("..", ".");
        return result.TrimStart('.');
    }

    private static bool IsScribanDirective(string expr)
    {
        var keyword = expr.Split(' ')[0].ToLowerInvariant();
        return keyword is "for" or "end" or "if" or "else" or "elsif"
                       or "func" or "ret" or "break" or "continue"
                       or "capture" or "wrap" or "with";
    }

    private static string? ApplyPipeFilter(JToken token, string pipeExpr)
    {
        var match = Regex.Match(pipeExpr.Trim(), @"^(\w[\w.]*)\s*(?:[""']([^""']*)[""'])?");
        if (!match.Success)
        {
            return null;
        }

        var filterName = match.Groups[1].Value.ToLowerInvariant();
        var arg = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;

        return filterName switch
        {
            "json" or "object.json" => token.ToString(
                                          arg is "indent" or "indented"
                                              ? Formatting.Indented
                                              : Formatting.None),
            "yaml" or "object.yaml" => SerializeToYaml(token),
            "upcase" => token.ToString().ToUpperInvariant(),
            "downcase" => token.ToString().ToLowerInvariant(),
            "capitalize" => Capitalize(token.ToString()),
            "strip" or "strip_newlines" => token.ToString().Trim(),
            "date" => token.Type == JTokenType.Date
                                          ? token.ToObject<DateTime>().ToString(arg)
                                          : token.ToString(),
            "default" => string.IsNullOrEmpty(token.ToString()) ? arg : token.ToString(),
            "csv" => FormatAsCsv(token),
            "size" => token switch
            {
                JArray a => a.Count.ToString(),
                JObject o => o.Count.ToString(),
                _ => token.ToString().Length.ToString()
            },
            _ => null
        };
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string ResolveFileInclude(
        string rawPath, string originalMatch,
        JToken rootToken, bool strictMode, string? baseDirectory)
    {
        var filePath = rawPath;
        try
        {
            filePath = filePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            var resolvedBase = baseDirectory ?? Directory.GetCurrentDirectory();
            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.Combine(resolvedBase, filePath);
            }

            filePath = Path.GetFullPath(filePath);

            if (baseDirectory is not null)
            {
                var safeBase = Path.GetFullPath(baseDirectory);
                if (!safeBase.EndsWith(Path.DirectorySeparatorChar))
                {
                    safeBase += Path.DirectorySeparatorChar;
                }

                if (!filePath.StartsWith(safeBase, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        $"File include path '{rawPath}' resolves outside the allowed base directory '{baseDirectory}'.");
                }
            }

            return File.Exists(filePath)
                ? RenderInternal(File.ReadAllText(filePath), rootToken, strictMode, baseDirectory)
                : strictMode ? throw new FileNotFoundException($"Included template file not found: {filePath}", filePath) : originalMatch;
        }
        catch (Exception ex)
        {
            if (strictMode)
            {
                throw;
            }

            Console.WriteLine($"[TemplateEngine Error] Path={filePath} | Error={ex.Message}");
            return $"[Error reading {filePath}]";
        }
    }

    private static string FormatAsCsv(JToken token)
    {
        if (token is not JArray array || array.Count == 0)
        {
            return token.ToString();
        }

        StringBuilder sb = new();
        if (array[0] is JObject firstObj)
        {
            var headers = firstObj.Properties().Select(p => p.Name).ToArray();
            sb.AppendLine(string.Join(",", headers));
            foreach (var item in array.OfType<JObject>())
            {
                sb.AppendLine(string.Join(",", headers.Select(h => EscapeCsvValue(item[h]?.ToString() ?? ""))));
            }
        }
        else
        {
            foreach (var item in array)
            {
                sb.AppendLine(EscapeCsvValue(item.ToString()));
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string EscapeCsvValue(string v) =>
        v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\""
            : v;

    private static string SerializeToYaml(JToken token)
    {
        var serializer = new SerializerBuilder()
            .DisableAliases()
            .Build();
        return serializer.Serialize(token);
    }

    [GeneratedRegex(@"\[['""](.*?)['""]\]")]
    private static partial Regex PipeFilterRegex();
    [GeneratedRegex(@"\{\{-?\s*([^}|?{]+?)\s*(?:\|\s*([^}?""']+(?:""[^""]*""|'[^']*')?[^}?]*))?(?:\?\?\s*([^}]+?))?\s*-?\}\}", RegexOptions.Compiled)]
    private static partial Regex TemplateRegex();
    [GeneratedRegex(@"@\[\s*([^\]\s]+)\s*\]", RegexOptions.Compiled)]
    private static partial Regex FileIncludeRegex();
    [GeneratedRegex(@"\{\{-?\s*include\s+['""]([^'""]+)['""]\s*-?\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled, "es-ES")]
    private static partial Regex ScribanIncludeRegex();
    [GeneratedRegex(@"\{\{-?\s*#[^#]*#\s*-?\}\}", RegexOptions.Compiled)]
    private static partial Regex ScribanCommentRegex();
}

/// <summary>
/// NamingStrategy que convierte propiedades C# a lowercase puro.
/// SomeProperty → someproperty
/// Los diccionarios NO pasan por aquí — sus keys se usan literalmente.
/// </summary>
internal sealed class LowercaseNamingStrategy : NamingStrategy
{
    protected override string ResolvePropertyName(string name) => name.ToLowerInvariant();
}