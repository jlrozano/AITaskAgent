using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Support;

/// <summary>Una tabla que una sentencia SQL escrita en un literal de código nombra.</summary>
/// <param name="Name">La tabla, sin esquema y sin comillas (<c>invoice</c> en <c>FROM app."invoice" i</c>).</param>
/// <param name="Schema">El esquema escrito delante (<c>app</c>); null si no lo lleva o si era una variable.</param>
/// <param name="File">Ruta relativa al repositorio, con <c>/</c>.</param>
/// <param name="Line">Línea (desde 1) del primer fragmento de la sentencia.</param>
/// <param name="Statement">El verbo con que empieza la sentencia, en mayúsculas (<c>SELECT</c>, <c>INSERT</c>...).</param>
/// <param name="Writes">
/// La sentencia escribe en esta tabla: el destino de <c>INSERT</c>/<c>MERGE INTO</c>, la tabla de <c>UPDATE</c>, el primer <c>FROM</c> de
/// <c>DELETE</c> y la de una sentencia DDL. Las demás tablas de una escritura (las de sus <c>FROM</c>/<c>JOIN</c>) y todas las de una
/// consulta son lecturas.
/// </param>
public sealed record SqlTableReference(string Name, string? Schema, string File, int Line, string Statement, bool Writes = false);

/// <summary>Una tabla de <see cref="SqlTableScan.Tables"/>: sus referencias de todo el repositorio, sin distinguir mayúsculas.</summary>
public sealed record SqlTableSummary(string Name, string? Schema, int References, int Files);

/// <summary>El resultado de <see cref="SqlTableScanner.Scan"/>.</summary>
public sealed class SqlTableScan
{
    /// <summary>El escaneo se detuvo al llegar al máximo de ficheros: las tablas de los demás no están.</summary>
    public const string StoppedMaxFiles = "max_files";

    /// <summary>El escaneo se detuvo al agotar el tiempo: las tablas de los ficheros que faltaban no están.</summary>
    public const string StoppedTimeBudget = "time_budget";

    private IReadOnlyList<SqlTableSummary>? _tables;

    /// <summary>Cada tabla nombrada por cada sentencia, por fichero (orden ordinal) y línea.</summary>
    public IReadOnlyList<SqlTableReference> References { get; init; } = [];

    /// <summary>Ficheros <c>.java</c> o <c>.cs</c> que se leyeron.</summary>
    public int FilesScanned { get; init; }

    /// <summary>Ficheros que no se pudieron leer o que pasan de <see cref="SqlTableScanner.MaxFileBytes"/>.</summary>
    public int FilesSkipped { get; init; }

    /// <summary>Por qué se detuvo antes de leerlos todos (<see cref="StoppedMaxFiles"/>, <see cref="StoppedTimeBudget"/>); null si los leyó todos.</summary>
    public string? StoppedBecause { get; init; }

    /// <summary>
    /// Las tablas distintas (sin distinguir mayúsculas ni esquema), de más a menos referenciadas y por nombre. El nombre es el de la primera
    /// aparición, y el esquema el primero que alguna llevó.
    /// </summary>
    public IReadOnlyList<SqlTableSummary> Tables => _tables ??=
    [
        .. References.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SqlTableSummary(g.First().Name, g.Select(r => r.Schema).FirstOrDefault(s => s != null), g.Count(), g.Select(r => r.File).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(t => t.References).ThenBy(t => t.Name, StringComparer.Ordinal)
    ];

    /// <summary>Las tablas de cada fichero, sin repetir y por orden de aparición.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TablesByFile() =>
        References.GroupBy(r => r.File, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)[.. g.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase)],
            StringComparer.Ordinal);
}

/// <summary>
/// Escáner determinista de nombres de tabla en las sentencias SQL que el código escribe en cadenas (<c>.java</c> y <c>.cs</c>): con
/// <c>cbm</c> el grafo de un repositorio cuyos DAO consultan con SQL en literales no trae nodos <c>Table</c>, y sin ellos el inventario no
/// tiene tablas y el plan de cobertura no puede puntuar la persistencia de cada subsistema. Es el mismo estilo de heurística que
/// <see cref="DynamicInstantiationScanner"/>: expresiones regulares sobre el texto sin comentarios (<see cref="CFamilySource"/>), sin
/// nombres de dominio en el código, que lee los ficheros que se le dan y se deja acotar.
/// </summary>
/// <remarks>
/// <para><b>Qué reconoce</b>: un literal (o varios unidos por <c>+</c>, y los bloques de texto de Java y las cadenas verbatim, interpoladas y
/// crudas de C#) que empieza por un verbo SQL (<c>SELECT</c>, <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>MERGE</c>, <c>WITH</c>, o
/// <c>CREATE</c>, <c>ALTER</c>, <c>DROP</c> y <c>TRUNCATE</c> <c>TABLE</c>). De él saca la tabla de <c>FROM</c> (también una lista
/// <c>FROM a x, b y</c>), <c>JOIN</c>, <c>INSERT</c> y <c>MERGE INTO</c>, <c>UPDATE</c> y <c>DDL</c>, con prefijo de esquema, nombres entre
/// comillas dobles, invertidas o corchetes. Las constantes de texto del mismo fichero (<c>static final String</c>, <c>const string</c>,
/// <c>readonly string</c>) se sustituyen donde se usan, y una variable que no se conoce (<c>"FROM " + schema + ".ORDERS"</c>) deja la
/// tabla y pierde el esquema.</para>
/// <para><b>Lo que no hace</b>: no resuelve nada que no sea un literal o una constante del mismo fichero, no ve el SQL de ficheros
/// <c>.xml</c> o <c>.sql</c>, no entiende dialectos (un <c>FROM</c> de una función se salta solo para <c>EXTRACT</c>, <c>SUBSTRING</c>,
/// <c>TRIM</c>, <c>OVERLAY</c> y <c>POSITION</c>), y una frase de interfaz que empiece por un verbo SQL y lleve <c>from</c> puede dar una
/// tabla falsa si lo que sigue no es un artículo o un determinante. Un fichero que no se puede leer o analizar se salta y se cuenta; nunca lanza.</para>
/// </remarks>
public static partial class SqlTableScanner
{
    /// <summary>Tamaño máximo de un fichero que se lee; los mayores (código generado, datos) se saltan.</summary>
    public const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>Ficheros que se leen como mucho; el resto se deja sin leer y se dice (<see cref="SqlTableScan.StoppedBecause"/>).</summary>
    public const int DefaultMaxFiles = 25_000;

    /// <summary>Tiempo que se dedica a leer, como mucho.</summary>
    public static readonly TimeSpan DefaultTimeBudget = TimeSpan.FromSeconds(90);

    /// <summary>Lo que ocupa el lugar de una variable en una concatenación o interpolación: un identificador que nunca es una tabla ni un esquema.</summary>
    private const string Unknown = "__v__";

    private static readonly string[] CandidateWords = ["from", "join", "into", "update", "table"];

    private static readonly HashSet<string> NotTables = new(StringComparer.OrdinalIgnoreCase)
    {
        // Palabras reservadas que siguen a una tabla o a la palabra clave
        "select", "from", "where", "group", "order", "having", "union", "intersect", "except", "minus", "limit", "offset", "fetch", "for",
        "join", "inner", "left", "right", "full", "cross", "natural", "outer", "on", "using", "set", "values", "with", "window", "and", "or",
        "when", "then", "else", "end", "as", "lateral", "dual", "table", "only", "unnest", "if", "not", "exists", "null",
        // Determinantes: «Delete from the list» no es una sentencia
        "the", "a", "an", "this", "that", "these", "those", "my", "your", "our", "their", "its", "his", "her", "la", "el", "los", "las", "un", "una", "del"
    };

    private static readonly HashSet<string> FunctionsWithFrom = new(StringComparer.OrdinalIgnoreCase)
    {
        "extract", "substring", "substr", "trim", "overlay", "position"
    };

    /// <summary>
    /// Lee <paramref name="relativeFiles"/> (solo los <c>.java</c> y <c>.cs</c>, sin repetidos y en orden ordinal, para que el resultado no dependa
    /// del orden en que se den) y devuelve las tablas de sus sentencias SQL. Se detiene al leer <paramref name="maxFiles"/> ficheros o al
    /// pasar <paramref name="timeBudget"/> (<see cref="DefaultTimeBudget"/>; <see cref="Timeout.InfiniteTimeSpan"/> sin límite) y lo dice en
    /// <see cref="SqlTableScan.StoppedBecause"/>. Solo la cancelación lanza.
    /// </summary>
    public static SqlTableScan Scan(
        string root, IEnumerable<string> relativeFiles, CancellationToken ct, int maxFiles = DefaultMaxFiles, TimeSpan? timeBudget = null)
    {
        var budget = timeBudget ?? DefaultTimeBudget;
        var watch = Stopwatch.StartNew();
        var references = new List<SqlTableReference>();
        var scanned = 0;
        var skipped = 0;
        string? stopped = null;

        foreach (var file in relativeFiles.Where(IsSource).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (scanned + skipped >= maxFiles)
            {
                stopped = SqlTableScan.StoppedMaxFiles;
                break;
            }
            if (budget != Timeout.InfiniteTimeSpan && watch.Elapsed >= budget)
            {
                stopped = SqlTableScan.StoppedTimeBudget;
                break;
            }

            try
            {
                if (Read(Path.Combine(root, file)) is not { } text)
                {
                    skipped++;
                    continue;
                }
                references.AddRange(ScanText(file, text, file.EndsWith(".java", StringComparison.OrdinalIgnoreCase)));
                scanned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un fichero que no se puede analizar no debe parar el inventario.
                skipped++;
            }
        }

        return new SqlTableScan { References = references, FilesScanned = scanned, FilesSkipped = skipped, StoppedBecause = stopped };
    }

    private static bool IsSource(string file) =>
        file.EndsWith(".java", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static string? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
                return null;
            // La misma regla que las tools y DynamicInstantiationScanner: UTF-8 si es válido; si no, la codificación del repositorio.
            return AITaskAgent.FileTools.TextFileEncoding.Decode(File.ReadAllBytes(path), AITaskAgent.FileTools.FileToolsConfiguration.DefaultEncoding).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Las tablas de las sentencias SQL del texto de un fichero de código, en el orden en que aparecen.</summary>
    public static IReadOnlyList<SqlTableReference> ScanText(string file, string text, bool java)
    {
        _ = java; // la sintaxis de cadenas de Java y C# se reconoce a la vez: las formas de uno no aparecen en el otro.
        if (!CandidateWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return [];

        var tokens = Lex(CFamilySource.Mask(text, maskStrings: false));
        var constants = Constants(tokens);
        var references = new List<SqlTableReference>();

        var k = 0;
        while (k < tokens.Count)
        {
            var start = tokens[k];
            // Una cadena, o una constante que se continúa con `+` (`COLUMNS + " JOIN other ..."`). Una constante sola ya se leyó en su definición.
            var isStart = start.Kind == TokenKind.Literal
                || (start.Kind == TokenKind.Identifier && constants.ContainsKey(start.Text) && k + 1 < tokens.Count && tokens[k + 1].Kind == TokenKind.Plus);
            if (isStart && Chain(tokens, k, constants, out var sql, out var next))
            {
                Extract(sql, file, start.Line, references);
                k = next;
                continue;
            }
            k++;
        }
        return references;
    }

    // ───────────────────────────── Léxico ─────────────────────────────

    private enum TokenKind { Literal, Identifier, Plus, Other }

    /// <param name="Kind">Qué es.</param>
    /// <param name="Text">El valor de un literal (sin comillas y con los escapes resueltos) o el nombre de un identificador.</param>
    /// <param name="Line">Línea (desde 1) donde empieza.</param>
    /// <param name="Ch">El carácter de un token <see cref="TokenKind.Other"/>.</param>
    private readonly record struct Token(TokenKind Kind, string Text, int Line, char Ch = '\0');

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>
    /// Trocea el fuente (sin comentarios) en literales, identificadores, <c>+</c> y el resto. Las cadenas son las de Java (<c>"…"</c> con
    /// escapes y bloques <c>"""…"""</c>) y las de C# (<c>@"…"</c>, <c>$"…{x}…"</c>, <c>"""…"""</c>); un literal sin cerrar acaba en el fin de
    /// línea y un bloque sin cerrar se descarta. Los caracteres (<c>'"'</c>) no son cadenas.
    /// </summary>
    private static List<Token> Lex(string code)
    {
        var tokens = new List<Token>();
        var line = 1;
        var i = 0;
        var n = code.Length;
        while (i < n)
        {
            var c = code[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '"')
            {
                i = LexString(code, i, ref line, tokens);
            }
            else if (c == '\'')
            {
                i = SkipCharLiteral(code, i);
            }
            else if (IsIdentStart(c))
            {
                var j = i + 1;
                while (j < n && IsIdentPart(code[j]))
                    j++;
                tokens.Add(new Token(TokenKind.Identifier, code[i..j], line));
                i = j;
            }
            else if (c == '+' && !(i + 1 < n && (code[i + 1] == '+' || code[i + 1] == '=')) && !(i > 0 && code[i - 1] == '+'))
            {
                tokens.Add(new Token(TokenKind.Plus, "+", line));
                i++;
            }
            else
            {
                tokens.Add(new Token(TokenKind.Other, string.Empty, line, c));
                i++;
            }
        }
        return tokens;
    }

    /// <summary>Salta <c>'x'</c> o <c>'\''</c> (a lo sumo unos pocos caracteres y sin cruzar una línea); si no cierra, avanza uno.</summary>
    private static int SkipCharLiteral(string code, int open)
    {
        var limit = Math.Min(code.Length, open + 10);
        for (var j = open + (open + 1 < code.Length && code[open + 1] == '\\' ? 3 : 2); j < limit; j++)
        {
            if (code[j] == '\n')
                break;
            if (code[j] == '\'')
                return j + 1;
        }
        return open + 1;
    }

    /// <summary>Lee la cadena que empieza en <paramref name="open"/> (una comilla), añade su token y devuelve el índice que sigue.</summary>
    private static int LexString(string code, int open, ref int line, List<Token> tokens)
    {
        var n = code.Length;
        var startLine = line;
        var verbatim = (open > 0 && code[open - 1] == '@') || (open > 1 && code[open - 1] == '$' && code[open - 2] == '@');
        var interpolated = (open > 0 && code[open - 1] == '$') || (open > 1 && code[open - 1] == '@' && code[open - 2] == '$');

        // Bloque de texto de Java o cadena cruda de C#: """ … """
        if (open + 2 < n && code[open + 1] == '"' && code[open + 2] == '"')
        {
            var end = code.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
            if (end < 0)
                return n;
            var block = code[(open + 3)..end];
            tokens.Add(new Token(TokenKind.Literal, Interpolate(block, interpolated), startLine));
            line += block.Count(ch => ch == '\n');
            return end + 3;
        }

        var value = new StringBuilder();
        var j = open + 1;
        while (j < n)
        {
            var c = code[j];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (j + 1 < n && code[j + 1] == '"')
                    {
                        value.Append('"');
                        j += 2;
                        continue;
                    }
                    break;
                }
                if (c == '\n')
                    line++;
                value.Append(c);
                j++;
                continue;
            }

            if (c == '"' || c == '\n' || c == '\r')
                break;
            if (c == '\\' && j + 1 < n)
            {
                var escaped = code[j + 1];
                value.Append(escaped is 'n' or 't' or 'r' ? ' ' : escaped);
                j += 2;
                continue;
            }
            value.Append(c);
            j++;
        }

        tokens.Add(new Token(TokenKind.Literal, Interpolate(value.ToString(), interpolated), startLine));
        // Una cadena sin cerrar acaba en el fin de línea, que el bucle principal cuenta.
        return j < n && code[j] == '"' ? j + 1 : j;
    }

    /// <summary>En una cadena interpolada de C#, cada <c>{expr}</c> es una variable que no se conoce.</summary>
    private static string Interpolate(string value, bool interpolated) =>
        interpolated ? InterpolationPattern().Replace(value, Unknown) : value;

    // ───────────────────────────── Constantes y concatenaciones ─────────────────────────────

    /// <summary>
    /// Las constantes de texto del fichero (<c>final String X = "…"</c>, <c>const string X = "…"</c>, <c>readonly string X = "…"</c>), con su
    /// valor ya concatenado. Se recorre dos veces para que una constante pueda usar otra definida más abajo.
    /// </summary>
    private static Dictionary<string, string> Constants(List<Token> tokens)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var k = 0; k + 4 < tokens.Count; k++)
            {
                if (tokens[k].Kind == TokenKind.Identifier && tokens[k].Text is "final" or "const" or "readonly"
                    && tokens[k + 1].Kind == TokenKind.Identifier && tokens[k + 1].Text is "String" or "string"
                    && tokens[k + 2].Kind == TokenKind.Identifier
                    && tokens[k + 3] is { Kind: TokenKind.Other, Ch: '=' }
                    && Chain(tokens, k + 4, constants, out var value, out _))
                {
                    constants[tokens[k + 2].Text] = value;
                }
            }
        }
        return constants;
    }

    /// <summary>
    /// El texto de la expresión de cadenas que empieza en <paramref name="k"/>: un literal o una constante conocida, seguida de
    /// <c>+ literal</c>, <c>+ constante</c> o <c>+ variable</c> (una variable que no se conoce vale <see cref="Unknown"/>). Se detiene en lo que no
    /// es un operando simple (una llamada, un acceso a un miembro, un número). False si en <paramref name="k"/> no empieza ninguna.
    /// </summary>
    private static bool Chain(List<Token> tokens, int k, Dictionary<string, string> constants, out string value, out int next)
    {
        value = string.Empty;
        next = k;
        if (k >= tokens.Count)
            return false;

        var text = new StringBuilder();
        var first = tokens[k];
        if (first.Kind == TokenKind.Literal)
        {
            text.Append(first.Text);
        }
        else if (first.Kind == TokenKind.Identifier && constants.TryGetValue(first.Text, out var constant) && !IsMemberOrCall(tokens, k + 1))
        {
            text.Append(constant);
        }
        else
        {
            return false;
        }

        var j = k + 1;
        while (j + 1 < tokens.Count && tokens[j].Kind == TokenKind.Plus)
        {
            var operand = tokens[j + 1];
            if (operand.Kind == TokenKind.Literal)
            {
                text.Append(operand.Text);
            }
            else if (operand.Kind == TokenKind.Identifier && !IsMemberOrCall(tokens, j + 2))
            {
                text.Append(constants.TryGetValue(operand.Text, out var known) ? known : Unknown);
            }
            else
            {
                break;
            }
            j += 2;
        }

        value = text.ToString();
        next = j;
        return true;
    }

    private static bool IsMemberOrCall(List<Token> tokens, int index) =>
        index < tokens.Count && tokens[index] is { Kind: TokenKind.Other, Ch: '.' or '(' or '[' };

    // ───────────────────────────── Sentencias ─────────────────────────────

    /// <summary>Las tablas de la sentencia <paramref name="sql"/> (si es una), añadidas a <paramref name="into"/> sin repetir dentro de ella.</summary>
    private static void Extract(string sql, string file, int line, List<SqlTableReference> into)
    {
        var statement = WhitespacePattern().Replace(sql, " ").Trim();
        if (statement.Length < 10 || VerbPattern().Match(statement) is not { Success: true } verbMatch)
            return;

        var verb = verbMatch.Groups["verb"].Value.ToUpperInvariant();
        // Qué hace falta para tomarla por SQL y no por una frase que empieza igual («Update from remote server finished»).
        var isSql = verb switch
        {
            "UPDATE" => HasWord(statement, "SET"),
            "INSERT" or "MERGE" => HasWord(statement, "INTO"),
            "DELETE" => HasWord(statement, "FROM"),
            "SELECT" or "WITH" => true,
            _ => HasWord(statement, "TABLE")
        };
        if (!isSql)
            return;

        var found = new List<(string Name, string? Schema)>();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = verb == "WITH" ? CommonTableExpressions(statement) : [];

        // Lee las tablas desde «position»; con «writes» son el destino de la sentencia, y no sus fuentes.
        void Add(int position, bool list, bool writes)
        {
            var before = found.Count;
            AddTable(statement, position, list, found);
            if (writes)
                written.UnionWith(found.Skip(before).Select(t => t.Name));
        }

        if (verb == "UPDATE")
            Add(verbMatch.Length, list: false, writes: true);

        var firstFrom = true;
        foreach (Match m in KeywordPattern().Matches(statement))
        {
            var after = m.Index + m.Length;
            switch (m.Groups["kw"].Value.ToUpperInvariant())
            {
                case "FROM":
                    if (!InFunctionWithFrom(statement, m.Index))
                    {
                        Add(after, list: true, writes: verb == "DELETE" && firstFrom);
                        firstFrom = false;
                    }
                    break;
                case "JOIN":
                    Add(after, list: false, writes: false);
                    break;
                case "INTO" when verb is "INSERT" or "MERGE":
                    Add(after, list: false, writes: true);
                    break;
                case "TABLE" when verb is "CREATE" or "ALTER" or "DROP" or "TRUNCATE":
                    Add(SkipIfExists(statement, after), list: false, writes: true);
                    break;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, schema) in found)
        {
            if (!excluded.Contains(name) && seen.Add(name))
                into.Add(new SqlTableReference(name, schema, file, line, verb, written.Contains(name)));
        }
    }

    /// <summary>Los nombres de las expresiones de tabla comunes (<c>WITH x AS (…), y AS (…)</c>): se usan como tablas y no lo son.</summary>
    private static HashSet<string> CommonTableExpressions(string statement)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CteNamePattern().Matches(statement))
            names.Add(m.Groups["n"].Value);
        return names;
    }

    private static int SkipIfExists(string statement, int position)
    {
        while (position < statement.Length && statement[position] == ' ')
            position++;
        var match = IfExistsPattern().Match(statement, position);
        return match.Success && match.Index == position ? position + match.Length : position;
    }

    /// <summary>¿Está el <c>FROM</c> de <paramref name="index"/> dentro de <c>EXTRACT(… FROM …)</c> y parecidas, que no nombra una tabla?</summary>
    private static bool InFunctionWithFrom(string statement, int index)
    {
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            if (statement[i] == ')')
            {
                depth++;
            }
            else if (statement[i] == '(')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
                var end = i;
                while (end > 0 && statement[end - 1] == ' ')
                    end--;
                var begin = end;
                while (begin > 0 && IsIdentPart(statement[begin - 1]))
                    begin--;
                return FunctionsWithFrom.Contains(statement[begin..end]);
            }
        }
        return false;
    }

    /// <summary>
    /// Lee, desde <paramref name="position"/>, la tabla (con su esquema) y, con <paramref name="list"/>, las que siguen separadas por comas
    /// (<c>FROM a x, b AS y</c>), saltando el alias de cada una. No añade nada si lo que sigue es una subconsulta, una variable, un número
    /// o una palabra reservada.
    /// </summary>
    private static void AddTable(string s, int position, bool list, List<(string Name, string? Schema)> found)
    {
        var pos = position;
        while (true)
        {
            while (pos < s.Length && s[pos] == ' ')
                pos++;
            var segments = ReadQualifiedName(s, ref pos);
            if (segments.Count == 0)
                return;

            var name = segments[^1];
            if (name.Contains(Unknown, StringComparison.Ordinal) || NotTables.Contains(name))
                return;
            // Un punto colgando («INSERT INTO app.» + CONSTANTE) es un nombre que sigue fuera del literal: lo escrito es el esquema y no una tabla.
            if (pos < s.Length && s[pos] == '.')
                return;
            var schema = segments.Count >= 2 && !segments[^2].Contains(Unknown, StringComparison.Ordinal) ? segments[^2] : null;
            found.Add((name, schema));

            if (!list)
                return;

            // Alias («orders o», «orders AS o») y la coma de la lista de tablas.
            while (pos < s.Length && s[pos] == ' ')
                pos++;
            var wordStart = pos;
            var word = ReadWord(s, ref pos);
            if (word.Equals("AS", StringComparison.OrdinalIgnoreCase))
            {
                while (pos < s.Length && s[pos] == ' ')
                    pos++;
                ReadWord(s, ref pos);
            }
            else if (word.Length == 0 || NotTables.Contains(word))
            {
                pos = wordStart;
            }
            while (pos < s.Length && s[pos] == ' ')
                pos++;
            if (pos >= s.Length || s[pos] != ',')
                return;
            pos++;
        }
    }

    private static string ReadWord(string s, ref int pos)
    {
        var begin = pos;
        while (pos < s.Length && (IsIdentPart(s[pos]) || s[pos] == '#'))
            pos++;
        return s[begin..pos];
    }

    /// <summary>Un nombre con tramos separados por punto: palabras o nombres entre <c>"…"</c>, <c>`…`</c> o <c>[…]</c>.</summary>
    private static List<string> ReadQualifiedName(string s, ref int pos)
    {
        var segments = new List<string>();
        while (pos < s.Length)
        {
            string? segment = null;
            switch (s[pos])
            {
                case '"' or '`' or '[':
                    var close = s[pos] == '[' ? ']' : s[pos];
                    var end = s.IndexOf(close, pos + 1);
                    if (end > pos + 1)
                    {
                        segment = s[(pos + 1)..end];
                        pos = end + 1;
                    }
                    break;
                case var c when IsIdentStart(c) || c == '#':
                    segment = ReadWord(s, ref pos);
                    break;
            }

            if (segment == null)
                break;
            segments.Add(segment);
            // Otro tramo solo si tras el punto empieza un nombre.
            if (pos + 1 < s.Length && s[pos] == '.' && (IsIdentStart(s[pos + 1]) || s[pos + 1] is '"' or '`' or '[' or '#'))
                pos++;
            else
                break;
        }
        // Un enlace de base de datos de Oracle (`tabla@enlace`) no es parte del nombre.
        if (pos < s.Length && s[pos] == '@')
        {
            pos++;
            ReadWord(s, ref pos);
        }
        return segments;
    }

    /// <summary>¿Aparece <paramref name="word"/> como palabra entera, sin distinguir mayúsculas? (La caché de <see cref="Regex"/> evita compilar el patrón cada vez.)</summary>
    private static bool HasWord(string statement, string word) =>
        Regex.IsMatch(statement, $@"\b{word}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^\(?\s*(?<verb>SELECT|INSERT|UPDATE|DELETE|MERGE|WITH|TRUNCATE|DROP|ALTER|CREATE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerbPattern();

    [GeneratedRegex(@"\b(?<kw>FROM|JOIN|INTO|TABLE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeywordPattern();

    [GeneratedRegex(@"(?:\bWITH|,)\s*(?:RECURSIVE\s+)?(?<n>[A-Za-z_]\w*)\s*(?:\([^)]*\))?\s+AS\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CteNamePattern();

    [GeneratedRegex(@"IF\s+(?:NOT\s+)?EXISTS\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IfExistsPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\{[^{}\r\n]*\}")]
    private static partial Regex InterpolationPattern();
}
