using CodebaseAnalysis.Core.Enrichment;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Support;

/// <summary>Un punto donde el código instancia o carga un tipo por nombre con un argumento que no es un literal.</summary>
/// <param name="File">Ruta relativa al repositorio, con <c>/</c>.</param>
/// <param name="Line">Línea de la llamada, desde 1.</param>
/// <param name="Kind">La llamada: <c>Class.forName</c>, <c>newInstance</c>, <c>Activator.CreateInstance</c>…</param>
/// <param name="Code">La línea, recortada.</param>
/// <param name="Context">La línea y las anteriores (hasta <see cref="DynamicInstantiationScanner.ContextLinesBefore"/>), donde suele componerse el nombre.</param>
public sealed record InstantiationSite(string File, int Line, string Kind, string Code, string Context);

/// <summary>Una clase nombrada fuera del código: en un XML de configuración o como literal de <c>Class.forName("…")</c>.</summary>
/// <param name="Name">Nombre completo tal como está escrito, con <c>$</c> cambiado por <c>.</c> y sin el ensamblado de .NET.</param>
/// <param name="File">Fichero donde se nombra, relativo al repositorio.</param>
/// <param name="Line">Línea donde se nombra, desde 1.</param>
/// <param name="Source"><c>xml</c> o <c>literal</c>.</param>
public sealed record NamedClass(string Name, string File, int Line, string Source);

/// <summary>Resultado de <see cref="DynamicInstantiationScanner.Scan"/>.</summary>
public sealed class DynamicInstantiationScan
{
    public const string SourceXml = "xml";
    public const string SourceLiteral = "literal";

    public IReadOnlyList<InstantiationSite> Sites { get; init; } = [];

    public IReadOnlyList<NamedClass> NamedClasses { get; init; } = [];

    /// <summary>Literales de cadena de cada fichero con algún sitio: el sufijo de un nombre compuesto suele ser una constante.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> LiteralsBySiteFile { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public int SourceFilesScanned { get; init; }

    public int ConfigurationFilesScanned { get; init; }

    /// <summary>Nombres de <see cref="NamedClasses"/> para buscar por nombre completo o por sufijo con puntos.</summary>
    public HashSet<string> NamedClassNames() => [.. NamedClasses.Select(n => n.Name)];
}

/// <summary>
/// Faceta determinista de instanciación dinámica (propuesta (d), capa 1 sin tocar el analizador): recorre con expresiones
/// regulares los <c>.java</c> y <c>.cs</c> del <see cref="RepositoryScan"/> (sin raíces de test ni carpetas excluidas),
/// sobre el texto sin comentarios (<see cref="CFamilySource"/>), y guarda cada instanciación o carga por nombre cuyo
/// argumento no es un literal: en Java <c>Class.forName(</c>, <c>.newInstance(</c> y <c>getDeclaredConstructor(</c> (no
/// sobre <c>X.class</c>) y <c>ServiceLoader.load(</c>; en C# <c>Activator.CreateInstance(</c> (no con <c>typeof</c> ni
/// genérico), <c>GetType(</c> con argumento y <c>Assembly.Load*(</c>. Un literal (<c>Class.forName("a.B")</c>) no es un
/// sitio, pero nombra una clase. Además, en los XML de configuración, las clases de los atributos <c>class="…"</c> y
/// <c>type="…"</c> con nombre completo y de los elementos <c>&lt;…-class&gt;</c> (<c>servlet-class</c>, <c>ejb-class</c>…).
/// Es una heurística: no resuelve el valor del argumento ni ve otras formas de reflexión.
/// </summary>
public static partial class DynamicInstantiationScanner
{
    public const int ContextLinesBefore = 5;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxCodeChars = 240;

    private static readonly string[] JavaTriggers = ["forName", "newInstance", "getDeclaredConstructor", "ServiceLoader"];
    private static readonly string[] CSharpTriggers = ["CreateInstance", "GetType", "Assembly"];

    public static DynamicInstantiationScan Scan(RepositoryScanResult scan, CancellationToken ct)
    {
        var sites = new List<InstantiationSite>();
        var named = new List<NamedClass>();
        var literals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var sourceFiles = 0;

        foreach (var (file, java) in scan.JavaFiles.Select(f => (f, true)).Concat(scan.CSharpFiles.Select(f => (f, false))))
        {
            ct.ThrowIfCancellationRequested();
            if (Read(scan.FullPath(file)) is not { } text)
                continue;
            sourceFiles++;
            if (!(java ? JavaTriggers : CSharpTriggers).Any(t => text.Contains(t, StringComparison.Ordinal)))
                continue;

            var before = sites.Count;
            ScanSource(file, text, java, sites, named);
            if (sites.Count > before)
                literals[file] = [.. StringLiteralPattern().Matches(CFamilySource.Mask(text, maskStrings: false))
                    .Select(m => m.Groups["v"].Value).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)];
        }

        var configurationFiles = 0;
        foreach (var file in scan.ConfigurationXmlFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (Read(scan.FullPath(file)) is not { } text)
                continue;
            configurationFiles++;
            ScanXml(file, text, named);
        }

        return new DynamicInstantiationScan
        {
            Sites = [.. sites.OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line)],
            NamedClasses = [.. named.OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Line).ThenBy(n => n.Name, StringComparer.Ordinal)],
            LiteralsBySiteFile = literals,
            SourceFilesScanned = sourceFiles,
            ConfigurationFilesScanned = configurationFiles
        };
    }

    private static string? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
                return null;
            // La misma regla que las tools: UTF-8 si es válido; si no, la codificación del repositorio.
            return AITaskAgent.FileTools.TextFileEncoding.Decode(File.ReadAllBytes(path), AITaskAgent.FileTools.FileToolsConfiguration.DefaultEncoding).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void ScanSource(string file, string text, bool java, List<InstantiationSite> sites, List<NamedClass> named)
    {
        var code = CFamilySource.Mask(text, maskStrings: false);
        var shape = CFamilySource.Mask(text, maskStrings: true);
        var lineStarts = CFamilySource.LineStarts(text);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var seenLines = new HashSet<int>();

        foreach (Match m in (java ? JavaCallPattern() : CSharpCallPattern()).Matches(code))
        {
            var kind = Kind(m.Groups["call"].Value, java);
            var openParen = m.Index + m.Length - 1;
            var closeParen = MatchingParen(shape, openParen);
            if (closeParen < 0)
                continue;
            var arguments = code[(openParen + 1)..closeParen];
            var first = FirstArgument(arguments).Trim();
            var receiver = code[Math.Max(0, m.Index - 300)..m.Index].TrimEnd();

            switch (kind)
            {
                case "newInstance" or "getDeclaredConstructor":
                    // X.class.newInstance(), X.class.getDeclaredConstructor(…).newInstance() o sobre Class.forName("a.B"): el
                    // tipo está escrito.
                    if (ClassLiteralReceiverPattern().IsMatch(receiver))
                        continue;
                    break;
                case "Activator.CreateInstance":
                    if (m.Groups["generic"].Success || first.Length == 0 || first.StartsWith("typeof", StringComparison.Ordinal))
                        continue;
                    break;
                case "GetType":
                    if (first.Length == 0)
                        continue;
                    break;
            }

            var line = CFamilySource.LineOf(lineStarts, m.Index);
            if (kind != "ServiceLoader.load" && SingleLiteralPattern().Match(first) is { Success: true } literal)
            {
                // Un literal nombra la clase: no es instanciación dinámica, pero es un uso que el grafo no ve.
                if (kind is "Class.forName" or "GetType" && DottedName(literal.Groups["v"].Value) is { } name)
                    named.Add(new NamedClass(name, file, line, DynamicInstantiationScan.SourceLiteral));
                continue;
            }

            if (!seenLines.Add(line))
                continue;
            var from = Math.Max(0, line - 1 - ContextLinesBefore);
            sites.Add(new InstantiationSite(file, line, kind, Clip(lines[line - 1].Trim()),
                string.Join("\n", lines[from..line].Select(l => l.Trim()).Where(l => l.Length > 0))));
        }
    }

    internal static void ScanXml(string file, string text, List<NamedClass> named)
    {
        var withoutComments = XmlCommentPattern().Replace(text, c => new string(c.Value.Select(ch => ch is '\n' or '\r' ? ch : ' ').ToArray()));
        var lineStarts = CFamilySource.LineStarts(withoutComments);
        foreach (var pattern in new[] { XmlClassAttributePattern(), XmlClassElementPattern() })
        {
            foreach (Match m in pattern.Matches(withoutComments))
            {
                if (DottedName(m.Groups["v"].Value) is { } name)
                    named.Add(new NamedClass(name, file, CFamilySource.LineOf(lineStarts, m.Groups["v"].Index), DynamicInstantiationScan.SourceXml));
            }
        }
    }

    private static string Kind(string call, bool java)
    {
        var compact = WhitespacePattern().Replace(call, "");
        if (java)
        {
            return compact switch
            {
                var c when c.StartsWith("Class.forName", StringComparison.Ordinal) => "Class.forName",
                var c when c.EndsWith("newInstance", StringComparison.Ordinal) => "newInstance",
                var c when c.EndsWith("getDeclaredConstructor", StringComparison.Ordinal) => "getDeclaredConstructor",
                _ => "ServiceLoader.load"
            };
        }
        return compact switch
        {
            var c when c.StartsWith("Activator", StringComparison.Ordinal) => "Activator.CreateInstance",
            var c when c.StartsWith("Assembly", StringComparison.Ordinal) => "Assembly.Load",
            _ => "GetType"
        };
    }

    /// <summary>Índice del paréntesis que cierra el de <paramref name="open"/>, sobre el texto sin cadenas; -1 si no lo hay.</summary>
    private static int MatchingParen(string shape, int open)
    {
        var depth = 0;
        for (var i = open; i < shape.Length; i++)
        {
            if (shape[i] == '(') depth++;
            else if (shape[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>El primer argumento: hasta la primera coma que no está dentro de paréntesis, genéricos ni una cadena.</summary>
    private static string FirstArgument(string arguments)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '"' && (i == 0 || arguments[i - 1] != '\\')) inString = !inString;
            if (inString) continue;
            if (c is '(' or '<' or '[') depth++;
            else if (c is ')' or '>' or ']') depth--;
            else if (c == ',' && depth == 0) return arguments[..i];
        }
        return arguments;
    }

    /// <summary>Nombre de clase con al menos un punto, sin el ensamblado (<c>A.B, Asm</c>) y con <c>$</c> como <c>.</c>; null si no lo es.</summary>
    private static string? DottedName(string value)
    {
        var name = value.Split(',')[0].Trim().Replace('$', '.');
        return DottedNamePattern().IsMatch(name) ? name : null;
    }

    private static string Clip(string text) => text.Length > MaxCodeChars ? text[..MaxCodeChars] + "…" : text;

    // ------------------------------------------------------------ faceta

    /// <summary>Sitios que salen en la faceta.</summary>
    public const int MaxSitesInFacet = 40;

    /// <summary>XML con clases nombradas que salen en la faceta, los de más clases primero.</summary>
    public const int MaxConfigurationFilesInFacet = 15;

    public const string HeuristicNote =
        "Heurística por expresiones regulares, no un análisis del código: puntos donde se instancia o carga un tipo por nombre " +
        "con un argumento que no es un literal (Class.forName, newInstance, getDeclaredConstructor y ServiceLoader.load en Java; " +
        "Activator.CreateInstance, GetType y Assembly.Load en C#), sin comentarios ni raíces de test, y clases nombradas en XML de " +
        "configuración (class=\"…\", type=\"…\" y <…-class>). No resuelve el valor del argumento ni ve otras formas de reflexión: " +
        "un sitio dice dónde mirar, no qué clases instancia.";

    /// <summary>La faceta <c>dynamic_instantiation_sites</c>: <c>[]</c> si no hay sitios ni clases nombradas.</summary>
    public static string ToFacetJson(DynamicInstantiationScan scan, System.Text.Json.JsonSerializerOptions options)
    {
        if (scan.Sites.Count == 0 && scan.NamedClasses.Count == 0)
            return "[]";

        var xml = scan.NamedClasses.Where(n => n.Source == DynamicInstantiationScan.SourceXml).ToList();
        var literal = scan.NamedClasses.Where(n => n.Source == DynamicInstantiationScan.SourceLiteral).ToList();
        var facet = new JsonObject
        {
            ["note"] = HeuristicNote,
            ["source_files_scanned"] = scan.SourceFilesScanned,
            ["configuration_files_scanned"] = scan.ConfigurationFilesScanned,
            ["sites_total"] = scan.Sites.Count,
            ["sites_shown"] = Math.Min(scan.Sites.Count, MaxSitesInFacet),
            ["sites"] = new JsonArray([.. scan.Sites.Take(MaxSitesInFacet).Select(s => (JsonNode)new JsonObject
            {
                ["file"] = s.File,
                ["line"] = s.Line,
                ["kind"] = s.Kind,
                ["code"] = s.Code
            })]),
            ["configuration_named_classes_total"] = xml.Select(n => n.Name).Distinct(StringComparer.Ordinal).Count(),
            ["configuration_files"] = new JsonArray([.. xml.GroupBy(n => n.File, StringComparer.Ordinal)
                .Select(g => (File: g.Key, Classes: g.Select(n => n.Name).Distinct(StringComparer.Ordinal).Count()))
                .OrderByDescending(g => g.Classes).ThenBy(g => g.File, StringComparer.Ordinal)
                .Take(MaxConfigurationFilesInFacet)
                .Select(g => (JsonNode)new JsonObject { ["file"] = g.File, ["classes"] = g.Classes })]),
            ["literal_named_classes"] = new JsonArray([.. literal.Take(MaxSitesInFacet).Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n.Name,
                ["file"] = n.File,
                ["line"] = n.Line
            })])
        };
        return facet.ToJsonString(options);
    }

    [GeneratedRegex(@"(?<call>\bClass\s*\.\s*forName|\.\s*newInstance|\.\s*getDeclaredConstructor|\bServiceLoader\s*\.\s*load)\s*\(")]
    private static partial Regex JavaCallPattern();

    [GeneratedRegex(@"(?<call>\bActivator\s*\.\s*CreateInstance|\bAssembly\s*\.\s*Load(?:From|File)?|\bGetType)\s*(?<generic><[^>()]*>)?\s*\(")]
    private static partial Regex CSharpCallPattern();

    [GeneratedRegex(@"(?:\.\s*class|\bClass\s*\.\s*forName\s*\(\s*""(?:[^""\\]|\\.)*""\s*\))\s*(?:\.\s*get(?:Declared)?Constructor\s*\([^()]*\)\s*)?$")]
    private static partial Regex ClassLiteralReceiverPattern();

    [GeneratedRegex(@"^@?""(?<v>(?:[^""\\]|\\.)*)""$")]
    private static partial Regex SingleLiteralPattern();

    [GeneratedRegex(@"""(?<v>(?:[^""\\\r\n]|\\.)*)""")]
    private static partial Regex StringLiteralPattern();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlCommentPattern();

    [GeneratedRegex(@"\b(?:class|type)\s*=\s*[""']\s*(?<v>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+(?:\s*,[^""']*)?)\s*[""']")]
    private static partial Regex XmlClassAttributePattern();

    [GeneratedRegex(@"<(?:[\w-]+:)?(?:[\w-]+-)?class\s*>\s*(?<v>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+)\s*</")]
    private static partial Regex XmlClassElementPattern();

    [GeneratedRegex(@"^[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+$")]
    private static partial Regex DottedNamePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
