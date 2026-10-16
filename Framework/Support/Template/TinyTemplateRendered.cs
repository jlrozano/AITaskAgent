using Microsoft.Extensions.Logging;
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
        },
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore
    });

    /// <param name="template">Template text.</param>
    /// <param name="obj">Model; properties are lowercased, dictionary keys stay literal.</param>
    /// <param name="strictMode">Throw on a missing include instead of leaving it in place.</param>
    /// <param name="baseDirectory">Base for relative includes; null means the working directory.</param>
    /// <param name="logger">
    /// Optional. When supplied, a <c>{{ placeholder }}</c> that resolves to nothing (and has no
    /// <c>??</c> default) leaves a warning with its name; the rendered text is unchanged.
    /// </param>
    public static string Render(string template, object obj, bool strictMode = false, string? baseDirectory = null, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        ArgumentNullException.ThrowIfNull(obj);

        // Propiedades C# → lowercase. Keys de diccionario → literales.
        var jToken = JToken.FromObject(obj, LowercaseSerializer);
        return RenderInternal(template, jToken, strictMode, baseDirectory, logger);
    }

    string ITemplateRendered.Render(string template, object obj) => Render(template, obj, false);

    private static string RenderInternal(string template, JToken rootToken, bool strictMode, string? baseDirectory = null, ILogger? logger = null)
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
            return ResolveFileInclude(rawPath, match.Value, rootToken, strictMode, baseDirectory, logger);
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
                var token = rootToken.SelectToken(jpath) ?? SelectTokenIgnoreCase(rootToken, jpath);

                if (token is null && defaultVal is null)
                {
                    logger?.LogWarning("Template placeholder '{Placeholder}' did not resolve to a value and was rendered empty.", expr);
                }

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
            return ResolveFileInclude(cleanPath, match.Value, rootToken, strictMode, baseDirectory, logger);
        });

        return result;
    }

    /// <summary>
    /// Resuelve un JPath descendiendo segmento a segmento, probando primero la coincidencia exacta
    /// y luego la insensible a mayúsculas.
    /// </summary>
    /// <remarks>
    /// Hace falta porque un mismo path puede mezclar dos convenciones: las propiedades C# se
    /// serializan en minúsculas (<c>Content</c> → <c>content</c>) mientras que las claves de
    /// diccionario conservan su literal. Ni <c>SelectToken(path)</c> ni
    /// <c>SelectToken(path.ToLower())</c> resuelven <c>{{Data.OriginalKey}}</c>: al primero le
    /// estorba <c>Data</c> y al segundo <c>OriginalKey</c>. Yendo por segmentos, cada tramo se
    /// resuelve con la convención que le toque.
    /// </remarks>
    private static JToken? SelectTokenIgnoreCase(JToken root, string jpath)
    {
        JToken? current = root;

        foreach (var segment in jpath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null)
            {
                return null;
            }

            // Un segmento puede traer índices de array pegados: items[0][1]
            var name = segment;
            var bracket = segment.IndexOf('[');
            var indices = "";
            if (bracket >= 0)
            {
                name = segment[..bracket];
                indices = segment[bracket..];
            }

            if (name.Length > 0)
            {
                if (current is not JObject obj)
                {
                    return null;
                }

                current = obj.GetValue(name, StringComparison.OrdinalIgnoreCase);
            }

            if (indices.Length > 0 && current is not null)
            {
                current = current.SelectToken("$" + indices);
            }
        }

        return current;
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
        JToken rootToken, bool strictMode, string? baseDirectory, ILogger? logger = null)
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
                ? RenderInternal(File.ReadAllText(filePath), rootToken, strictMode, baseDirectory, logger)
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