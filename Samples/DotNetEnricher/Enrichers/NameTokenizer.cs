using System.Text.RegularExpressions;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Tokenizador de nombres de código para nombrar las comunidades de <see cref="GraphAnalyticsEnricher"/>. Es el de la
/// vectorización del enriquecedor original (compatible con nomic-embed-code), que aquí no se copia porque la app ya
/// tiene sus embeddings; se conserva solo para que los nombres de comunidad salgan igual.
/// Traduce el pipeline de tokenización de semantic.c al dominio C#:
///   1. Split camelCase
///   2. Split por delimitadores
///   3. Lowercase + filtro alfanumérico
///   4. Expansión de abreviaturas (añade tokens extra, no reemplaza)
/// </summary>
public static class NameTokenizer
{
    // Separadores: punto, barra, guión bajo, guión, espacio, paréntesis, coma, dos puntos
    private static readonly char[] Delimiters = ['.', '/', '_', '-', ' ', '(', ')', ',', ':'];

    // Regex que detecta el límite minúscula→mayúscula para split camelCase
    private static readonly Regex CamelCaseBoundary = new(@"(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);

    // Tabla de abreviaturas comunes en código C# empresarial.
    // Los valores se AÑADEN además del token original (no reemplazan).
    private static readonly Dictionary<string, string[]> Abbrevs = new(StringComparer.Ordinal)
    {
        ["impl"]  = ["implementation"],
        ["msg"]   = ["message"],
        ["req"]   = ["request"],
        ["resp"]  = ["response"],
        ["cfg"]   = ["configuration"],
        ["mgr"]   = ["manager"],
        ["svc"]   = ["service"],
        ["db"]    = ["database"],
        ["repo"]  = ["repository"],
        ["async"] = ["asynchronous"],
        ["auth"]  = ["authentication"],
        ["ctx"]   = ["context"],
        ["conn"]  = ["connection"],
        ["evt"]   = ["event"],
        ["ex"]    = ["exception"],
        ["err"]   = ["error"],
        ["val"]   = ["value"],
        ["cmd"]   = ["command"],
        ["idx"]   = ["index"],
        ["len"]   = ["length"],
        ["src"]   = ["source"],
        ["dst"]   = ["destination"],
        ["tmp"]   = ["temporary"],
        ["temp"]  = ["temporary"],
        ["tok"]   = ["token"],
        ["env"]   = ["environment"],
        ["prop"]  = ["property"],
        ["props"] = ["properties"],
        ["attr"]  = ["attribute"],
        ["ns"]    = ["namespace"],
        ["ctor"]  = ["constructor"],
        ["fmt"]   = ["format"],
        ["args"]  = ["arguments"],
        ["arg"]   = ["argument"],
        ["info"]  = ["information"],
        ["desc"]  = ["description"],
        ["prov"]  = ["provider"],
        ["pub"]   = ["publish"],
        ["sub"]   = ["subscribe"],
        ["di"]    = ["dependency", "injection"],
        ["ioc"]   = ["inversion", "control"],
        ["dto"]   = ["data", "transfer", "object"],
        ["api"]   = ["application", "programming", "interface"],
        ["http"]  = ["hypertext", "transfer", "protocol"],
        ["url"]   = ["uniform", "resource", "locator"],
        ["uri"]   = ["uniform", "resource", "identifier"],
        ["json"]  = ["javascript", "object", "notation"],
        ["sql"]   = ["structured", "query", "language"],
    };

    /// <summary>
    /// Tokeniza un texto según el pipeline de nomic-embed-code.
    /// Yield-return para ser LINQ-friendly.
    /// </summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        // 1. Split camelCase: insertar espacio en límite minúscula→mayúscula
        var afterCamel = CamelCaseBoundary.Replace(text, " ");

        // 2. Split por delimitadores
        var rawTokens = afterCamel.Split(Delimiters, StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in rawTokens)
        {
            // 3. Lowercase
            var lower = raw.ToLowerInvariant();

            // 4. Filtrar: al menos un carácter alfanumérico
            if (!lower.Any(char.IsAsciiLetterOrDigit))
                continue;

            // Emitir token base
            yield return lower;

            // 5. Expansión de abreviaturas: añadir expansión ADEMÁS del original
            if (Abbrevs.TryGetValue(lower, out var expansions))
            {
                foreach (var expansion in expansions)
                    yield return expansion;
            }
        }
    }
}
