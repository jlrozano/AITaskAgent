using System.Text;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Enrichment.Web;

/// <summary>
/// Referencia de una JSP a otra JSP (include) o a una acción (formulario), con la línea donde aparece. <c>Kind</c> es
/// <see cref="JspScanner.KindIncludeDirective"/>, <see cref="JspScanner.KindJspInclude"/>, <see cref="JspScanner.KindTilesInsert"/>
/// o <see cref="JspScanner.KindForm"/>; <c>Target</c>, el valor del atributo tal cual (ruta relativa, de contexto o
/// <c>path</c> de la acción).
/// </summary>
public sealed record JspReference(string Kind, string Target, int Line);

/// <summary>Una directiva <c>taglib</c>: prefijo y <c>uri</c> (o <c>tagdir</c>).</summary>
public sealed record JspTaglib(string Prefix, string Uri);

/// <summary>Lo que el plugin extrae de una JSP sin compilarla (P14).</summary>
public sealed record JspScanResult
{
    /// <summary>Número de líneas (la vacía tras el último salto no cuenta); al menos 1.</summary>
    public int LineCount { get; init; }

    /// <summary>Codificación con la que se ha leído (nombre web de .NET).</summary>
    public string Encoding { get; init; } = "utf-8";

    /// <summary>Scriptlets <c>&lt;% %&gt;</c>; las expresiones y declaraciones van aparte.</summary>
    public int ScriptletCount { get; init; }

    /// <summary>Línea de inicio de cada scriptlet.</summary>
    public IReadOnlyList<int> ScriptletLines { get; init; } = [];

    /// <summary>Expresiones <c>&lt;%= %&gt;</c>.</summary>
    public int ExpressionCount { get; init; }

    /// <summary>Declaraciones <c>&lt;%! %&gt;</c>.</summary>
    public int DeclarationCount { get; init; }

    /// <summary>Clases o paquetes de los <c>&lt;%@ page import %&gt;</c>, en orden y sin repetidos.</summary>
    public IReadOnlyList<string> PageImports { get; init; } = [];

    public IReadOnlyList<JspTaglib> Taglibs { get; init; } = [];

    /// <summary>Includes estáticos y dinámicos con destino fijo.</summary>
    public IReadOnlyList<JspReference> Includes { get; init; } = [];

    /// <summary>Formularios de Struts (<c>&lt;html:form action&gt;</c>) con destino fijo.</summary>
    public IReadOnlyList<JspReference> Forms { get; init; } = [];

    /// <summary>Includes y formularios cuyo destino se calcula en ejecución (<c>&lt;%= %&gt;</c> o EL): no generan arista.</summary>
    public int DynamicReferences { get; init; }

    /// <summary>Hay SQL en el código Java de la página (scriptlets, expresiones o declaraciones).</summary>
    public bool HasSql { get; init; }

    public IReadOnlyList<int> SqlLines { get; init; } = [];

    /// <summary>El código Java de la página usa la sesión HTTP.</summary>
    public bool SessionAccess { get; init; }

    /// <summary>Imports que casan con <c>StrutsJsp:BusinessImportPatterns</c>, se usen o no en los scriptlets.</summary>
    public IReadOnlyList<string> BusinessImports { get; init; } = [];

    public bool Suspicious { get; init; }

    /// <summary>
    /// <c>import:&lt;import&gt;</c> por cada import fuerte (<c>StrutsJsp:SuspiciousImportPatterns</c>),
    /// <c>business-code-in-scriptlet:&lt;import&gt;</c> por cada import de negocio que usa el código de un scriptlet o de una
    /// declaración, y <c>sql:&lt;línea&gt;</c> por cada SQL.
    /// </summary>
    public IReadOnlyList<string> SuspiciousReasons { get; init; } = [];
}

/// <summary>
/// Escáner léxico de JSP (P14): directivas <c>&lt;%@ %&gt;</c>, scriptlets <c>&lt;% %&gt;</c>, expresiones <c>&lt;%= %&gt;</c>,
/// declaraciones <c>&lt;%! %&gt;</c>, comentarios <c>&lt;%-- --%&gt;</c> (que se descartan enteros), formularios de Struts,
/// includes, taglibs, SQL y acceso a la sesión. No interpreta el Java: busca patrones en su texto. Los comentarios HTML
/// <c>&lt;!-- --&gt;</c> no se descartan, porque el contenedor JSP sí ejecuta lo que hay dentro.
/// </summary>
public sealed class JspScanner
{
    public const string KindIncludeDirective = "include";
    public const string KindJspInclude = "jsp:include";
    public const string KindTilesInsert = "tiles:insert";
    public const string KindForm = "html:form";

    /// <summary>Motivo de sospecha: un import de negocio usado en el código de un scriptlet o de una declaración.</summary>
    public const string ReasonBusinessCodeInScriptlet = "business-code-in-scriptlet";

    /// <summary>Carácter con el que se tapan los elementos Java al buscar etiquetas: marca un atributo como dinámico.</summary>
    private const char JavaMask = '\u0001';

    private static readonly Regex Sql = new(
        @"\bSELECT\b[\s\S]{1,300}?\bFROM\b|\bINSERT\s+INTO\b|\bUPDATE\b[\s\S]{1,120}?\bSET\b|\bDELETE\s+FROM\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Session = new(
        @"\bsession\s*\.\s*\w+\s*\(|\bgetSession\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DirectiveName = new(@"^\s*(?<name>[\w.]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Attributes = new(
        @"(?<name>[\w:.-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Tag = new(
        @"<(?<prefix>[A-Za-z_][\w-]*):(?<tag>[\w.]+)(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PageDirective = new(
        @"<%@\s*page\b(?<attrs>[\s\S]*?)%>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Charset = new(@"charset\s*=\s*(?<name>[\w.:-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Comentarios y literales de cadena o carácter de Java: se quitan antes de buscar identificadores.</summary>
    private static readonly Regex JavaNoise = new(
        @"/\*[\s\S]*?\*/|//[^\n]*|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Identificador con mayúscula inicial, no cualificado, seguido de <c>.</c> o <c>(</c>: uso de una clase.</summary>
    private static readonly Regex ClassUse = new(@"(?<![\w.$])(?<name>[A-Z][\w$]*)\s*[.(]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Clases de <c>java.lang</c> habituales en scriptlets: nunca vienen de un import con comodín.</summary>
    private static readonly HashSet<string> JavaLangTypes = new(StringComparer.Ordinal)
    {
        "Boolean", "Byte", "Character", "Class", "Double", "Enum", "Exception", "Float", "Integer", "Long", "Math", "Number",
        "Object", "Runtime", "RuntimeException", "Short", "String", "StringBuffer", "StringBuilder", "System", "Thread",
        "Throwable", "Error", "IllegalArgumentException", "IllegalStateException", "NullPointerException", "NumberFormatException"
    };

    private readonly IReadOnlyList<Regex> _suspiciousImports;
    private readonly IReadOnlyList<Regex> _businessImports;

    /// <param name="suspiciousImportPatterns">Imports fuertes: marcan la JSP por sí solos.</param>
    /// <param name="businessImportPatterns">Imports de negocio: marcan la JSP solo si los usa un scriptlet o una declaración.</param>
    /// <exception cref="InvalidOperationException">Si alguna expresión no es válida.</exception>
    public JspScanner(IEnumerable<string> suspiciousImportPatterns, IEnumerable<string>? businessImportPatterns = null)
    {
        _suspiciousImports = Compile(suspiciousImportPatterns, "StrutsJsp:SuspiciousImportPatterns");
        _businessImports = Compile(businessImportPatterns ?? [], "StrutsJsp:BusinessImportPatterns");
    }

    private static List<Regex> Compile(IEnumerable<string> source, string option)
    {
        var patterns = new List<Regex>();
        foreach (var pattern in source)
        {
            try
            {
                patterns.Add(new Regex(pattern, RegexOptions.CultureInvariant));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"{option}: expresión no válida '{pattern}': {ex.Message}", ex);
            }
        }
        return patterns;
    }

    /// <summary>
    /// Lee la JSP con la codificación que declara (<c>pageEncoding</c>, o el <c>charset</c> de <c>contentType</c>) y, si no
    /// declara ninguna, con la regla P6 (UTF-8 si es válido; si no, <paramref name="defaultEncoding"/>).
    /// </summary>
    /// <exception cref="IOException">Si no se puede leer.</exception>
    public JspScanResult ScanFile(string fullPath, Encoding defaultEncoding)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var (text, encoding) = Decode(bytes, defaultEncoding);
        return Scan(text, encoding.WebName);
    }

    public static (string Text, Encoding Encoding) Decode(byte[] bytes, Encoding defaultEncoding)
    {
        if (DeclaredEncoding(bytes) is not { } declared)
            return SourceEncoding.Decode(bytes, defaultEncoding);

        var span = bytes.AsSpan();
        if (declared.CodePage == Encoding.UTF8.CodePage && span.StartsWith(Encoding.UTF8.Preamble))
            span = span[Encoding.UTF8.Preamble.Length..];
        return (declared.GetString(span), declared);
    }

    /// <summary>
    /// Codificación que declaran las directivas <c>page</c> (<c>pageEncoding</c> manda sobre el <c>charset</c> de
    /// <c>contentType</c>), o null si no declaran ninguna reconocible. Las directivas son ASCII: se leen con Latin-1.
    /// </summary>
    public static Encoding? DeclaredEncoding(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        string? pageEncoding = null, charset = null;
        foreach (Match directive in PageDirective.Matches(text))
        {
            foreach (Match attribute in Attributes.Matches(directive.Groups["attrs"].Value))
            {
                var name = attribute.Groups["name"].Value;
                var value = attribute.Groups["value"].Value;
                if (pageEncoding == null && name.Equals("pageEncoding", StringComparison.OrdinalIgnoreCase) && value.Trim().Length > 0)
                    pageEncoding = value.Trim();
                else if (charset == null && name.Equals("contentType", StringComparison.OrdinalIgnoreCase)
                         && Charset.Match(value) is { Success: true } match)
                    charset = match.Groups["name"].Value;
            }
        }

        foreach (var candidate in new[] { pageEncoding, charset })
        {
            if (candidate != null && SourceEncoding.TryGetEncoding(candidate) is { } encoding)
                return encoding;
        }
        return null;
    }

    public JspScanResult Scan(string text, string encodingName = "utf-8")
    {
        var lines = new LineIndex(text);
        var markup = text.ToCharArray();
        var javaSegments = new List<(int Offset, string Code)>();
        // Código de scriptlets y declaraciones (sin expresiones): donde un import de negocio indica lógica en la pantalla.
        var statementCode = new StringBuilder();
        var scriptletLines = new List<int>();
        int scriptlets = 0, expressions = 0, declarations = 0;
        var imports = new List<string>();
        var taglibs = new List<JspTaglib>();
        var includes = new List<JspReference>();
        var forms = new List<JspReference>();
        var dynamic = 0;

        // Recorrido de los elementos JSP. En `markup` se tapan los comentarios (con espacios) y el Java (con JavaMask),
        // conservando los saltos de línea para que los offsets y las líneas sigan valiendo.
        var i = 0;
        while ((i = text.IndexOf("<%", i, StringComparison.Ordinal)) >= 0)
        {
            if (string.CompareOrdinal(text, i, "<%--", 0, 4) == 0)
            {
                var close = text.IndexOf("--%>", i + 4, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 4;
                Mask(markup, i, end, ' ');
                i = end;
                continue;
            }

            var bodyStart = i + 2;
            var marker = bodyStart < text.Length ? text[bodyStart] : '\0';
            if (marker is '@' or '=' or '!')
                bodyStart++;
            var closeTag = text.IndexOf("%>", bodyStart, StringComparison.Ordinal);
            var bodyEnd = closeTag < 0 ? text.Length : closeTag;
            var elementEnd = closeTag < 0 ? text.Length : closeTag + 2;
            var body = text[bodyStart..bodyEnd];

            switch (marker)
            {
                case '@':
                    ReadDirective(body, lines.LineOf(i), imports, taglibs, includes, ref dynamic);
                    Mask(markup, i, elementEnd, ' ');
                    break;
                case '=':
                    expressions++;
                    javaSegments.Add((bodyStart, body));
                    Mask(markup, i, elementEnd, JavaMask);
                    break;
                case '!':
                    declarations++;
                    javaSegments.Add((bodyStart, body));
                    statementCode.Append(body).Append('\n');
                    Mask(markup, i, elementEnd, JavaMask);
                    break;
                default:
                    scriptlets++;
                    scriptletLines.Add(lines.LineOf(i));
                    javaSegments.Add((bodyStart, body));
                    statementCode.Append(body).Append('\n');
                    Mask(markup, i, elementEnd, JavaMask);
                    break;
            }
            i = elementEnd;
        }

        // Etiquetas sobre el texto sin comentarios ni Java: un atributo que contenía Java queda marcado como dinámico.
        var formPrefixes = new HashSet<string>(StringComparer.Ordinal) { "html" };
        var tilesPrefixes = new HashSet<string>(StringComparer.Ordinal) { "tiles" };
        foreach (var taglib in taglibs)
        {
            if (taglib.Uri.Contains("struts-html", StringComparison.OrdinalIgnoreCase)
                || taglib.Uri.Contains("tags-html", StringComparison.OrdinalIgnoreCase)
                || taglib.Uri.Contains("struts-nested", StringComparison.OrdinalIgnoreCase))
                formPrefixes.Add(taglib.Prefix);
            if (taglib.Uri.Contains("tiles", StringComparison.OrdinalIgnoreCase))
                tilesPrefixes.Add(taglib.Prefix);
        }

        var masked = new string(markup);
        foreach (Match tag in Tag.Matches(masked))
        {
            var prefix = tag.Groups["prefix"].Value;
            var name = tag.Groups["tag"].Value;
            string kind, attribute;
            if (prefix == "jsp" && name == "include")
                (kind, attribute) = (KindJspInclude, "page");
            else if (prefix == "jsp" && name == "directive.include")
                (kind, attribute) = (KindIncludeDirective, "file");
            else if (name == "form" && formPrefixes.Contains(prefix))
                (kind, attribute) = (KindForm, "action");
            else if (name == "insert" && tilesPrefixes.Contains(prefix))
                (kind, attribute) = (KindTilesInsert, "page");
            else
                continue;

            var attributes = ReadAttributes(tag.Groups["attrs"].Value);
            if (kind == KindTilesInsert && !attributes.ContainsKey("page"))
                attribute = "template";
            if (!attributes.TryGetValue(attribute, out var value) || value.Length == 0)
                continue;
            if (IsDynamic(value))
            {
                dynamic++;
                continue;
            }
            // tiles:insert solo cuenta como include de una JSP; con attribute/definition inserta otra cosa.
            if (kind == KindTilesInsert && !IsJspPath(value))
                continue;

            var reference = new JspReference(kind, value, lines.LineOf(tag.Index));
            (kind == KindForm ? forms : includes).Add(reference);
        }

        var sqlLines = new SortedSet<int>();
        var session = false;
        foreach (var (offset, code) in javaSegments)
        {
            foreach (Match match in Sql.Matches(code))
                sqlLines.Add(lines.LineOf(offset + match.Index));
            session |= Session.IsMatch(code);
        }

        var distinctImports = imports.Distinct(StringComparer.Ordinal).ToList();
        var reasons = new List<string>();
        var businessImports = new List<string>();
        var statementText = JavaNoise.Replace(statementCode.ToString(), " ");
        var usedClasses = ClassUse.Matches(statementText).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var import in distinctImports)
        {
            if (_suspiciousImports.Any(p => p.IsMatch(import)))
            {
                reasons.Add($"import:{import}");
                continue;
            }
            if (!_businessImports.Any(p => p.IsMatch(import)))
                continue;
            businessImports.Add(import);
            if (UsesImport(import, distinctImports, statementText, usedClasses))
                reasons.Add($"{ReasonBusinessCodeInScriptlet}:{import}");
        }
        reasons.AddRange(sqlLines.Select(l => $"sql:{l}"));

        return new JspScanResult
        {
            LineCount = lines.LineCount,
            Encoding = encodingName,
            ScriptletCount = scriptlets,
            ScriptletLines = scriptletLines,
            ExpressionCount = expressions,
            DeclarationCount = declarations,
            PageImports = distinctImports,
            BusinessImports = businessImports,
            Taglibs = taglibs,
            Includes = includes,
            Forms = forms,
            DynamicReferences = dynamic,
            HasSql = sqlLines.Count > 0,
            SqlLines = sqlLines.ToList(),
            SessionAccess = session,
            Suspicious = reasons.Count > 0,
            SuspiciousReasons = reasons
        };
    }

    /// <summary>
    /// True si el código de los scriptlets y declaraciones (<paramref name="code"/>, ya sin comentarios ni literales) usa
    /// lo importado: aparece el nombre simple de un import de clase, o, en un import con comodín (<c>pkg.*</c>), se usa
    /// alguna clase (<paramref name="usedClasses"/>) que no viene de otro import de clase ni es de <c>java.lang</c>. Lo
    /// segundo es una aproximación: sin compilar no se sabe de qué paquete sale cada clase.
    /// </summary>
    private static bool UsesImport(string import, IReadOnlyList<string> allImports, string code, HashSet<string> usedClasses)
    {
        if (!import.EndsWith(".*", StringComparison.Ordinal))
            return Regex.IsMatch(code, $@"(?<![\w$]){Regex.Escape(SimpleName(import))}(?![\w$])", RegexOptions.CultureInvariant);

        var explicitNames = allImports
            .Where(i => !i.EndsWith(".*", StringComparison.Ordinal))
            .Select(SimpleName)
            .ToHashSet(StringComparer.Ordinal);
        return usedClasses.Any(c => !explicitNames.Contains(c) && !JavaLangTypes.Contains(c));
    }

    private static string SimpleName(string import)
    {
        var dot = import.LastIndexOf('.');
        return dot < 0 ? import : import[(dot + 1)..];
    }

    /// <summary>Destino con extensión de JSP (<c>.jsp</c>, <c>.jspf</c>), ignorando la query.</summary>
    public static bool IsJspPath(string path)
    {
        var clean = StripQuery(path);
        return clean.EndsWith(".jsp", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".jspf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Quita la query (<c>?…</c>) y el fragmento (<c>#…</c>) de un destino.</summary>
    public static string StripQuery(string path)
    {
        var cut = path.IndexOfAny(['?', '#']);
        return (cut < 0 ? path : path[..cut]).Trim();
    }

    private static bool IsDynamic(string value) =>
        value.Contains(JavaMask) || value.Contains("${", StringComparison.Ordinal) || value.Contains("#{", StringComparison.Ordinal);

    private static void ReadDirective(string body, int line, List<string> imports, List<JspTaglib> taglibs, List<JspReference> includes, ref int dynamic)
    {
        var name = DirectiveName.Match(body).Groups["name"].Value;
        var attributes = ReadAttributes(body);
        switch (name.ToLowerInvariant())
        {
            case "page" when attributes.TryGetValue("import", out var import):
                imports.AddRange(import.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
            case "taglib" when attributes.TryGetValue("prefix", out var prefix):
                var uri = attributes.GetValueOrDefault("uri") ?? attributes.GetValueOrDefault("tagdir") ?? "";
                taglibs.Add(new JspTaglib(prefix, uri));
                break;
            case "include" when attributes.TryGetValue("file", out var file) && file.Length > 0:
                if (IsDynamic(file))
                    dynamic++;
                else
                    includes.Add(new JspReference(KindIncludeDirective, file, line));
                break;
        }
    }

    private static Dictionary<string, string> ReadAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Attributes.Matches(text))
            attributes.TryAdd(match.Groups["name"].Value, match.Groups["value"].Value.Trim());
        return attributes;
    }

    private static void Mask(char[] buffer, int start, int end, char with)
    {
        for (var k = start; k < end && k < buffer.Length; k++)
        {
            if (buffer[k] is not ('\n' or '\r'))
                buffer[k] = with;
        }
    }

    /// <summary>Offset → línea (desde 1) por búsqueda binaria sobre los inicios de línea.</summary>
    private sealed class LineIndex
    {
        private readonly List<int> _starts = [0];

        public LineIndex(string text)
        {
            for (var k = 0; k < text.Length; k++)
            {
                if (text[k] == '\n')
                    _starts.Add(k + 1);
            }
            // La línea vacía que deja el último salto no cuenta.
            var count = _starts.Count;
            if (count > 1 && _starts[^1] == text.Length)
                count--;
            LineCount = Math.Max(1, count);
        }

        public int LineCount { get; }

        public int LineOf(int offset)
        {
            var index = _starts.BinarySearch(offset);
            return (index >= 0 ? index : ~index - 1) + 1;
        }
    }
}
