using System.Globalization;
using System.Text;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Parte nombres de paquete, carpeta, clase y tabla en los términos con que se hace la fusión por vocabulario (T9): por
/// mayúsculas (<c>camelCase</c>, <c>PascalCase</c>, con siglas como <c>HTTPServer</c> → <c>http</c>, <c>server</c>), por
/// separadores (<c>_</c>, <c>.</c>, <c>-</c>, <c>/</c>) y por cifras; en minúsculas, sin tildes y con el plural quitado
/// (<c>parcels</c> y <c>parcel</c> son el mismo término). No sabe de ningún dominio: los términos genéricos
/// (<c>impl</c>, <c>dao</c>...) los filtra quien lo usa con la lista de configuración.
/// </summary>
public static class NameTokenizer
{
    /// <summary>Términos más cortos que esto (<c>of</c>, <c>id</c>, <c>to</c>) no distinguen nada.</summary>
    public const int MinTokenLength = 3;

    /// <summary>Los términos de <paramref name="text"/>, normalizados y en el orden en que aparecen (con repetidos).</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length == 0)
                return;
            tokens.Add(Stem(current.ToString().ToLowerInvariant()));
            current.Clear();
        }

        var chars = RemoveDiacritics(text);
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!char.IsLetter(c))
            {
                // Los separadores y las cifras cortan el término; las cifras no forman parte de ninguno.
                Flush();
                continue;
            }

            if (current.Length > 0 && char.IsUpper(c))
            {
                var previous = chars[i - 1];
                var next = i + 1 < chars.Length ? chars[i + 1] : '\0';
                // minúscula→Mayúscula (fooBar), o fin de una sigla seguida de una palabra (HTTPServer → HTTP | Server).
                if (char.IsLower(previous) || (char.IsUpper(previous) && char.IsLower(next)))
                    Flush();
            }
            current.Append(c);
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// Quita el plural de forma ingenua pero estable: <c>ies</c> → <c>y</c>, <c>sses</c>/<c>xes</c>/<c>ches</c>/<c>shes</c>/
    /// <c>ones</c>/<c>dades</c> → sin <c>es</c>, y una <c>s</c> final (menos <c>ss</c> y <c>us</c>/<c>is</c>). No es lingüística: basta con que
    /// <c>parcel</c> y <c>parcels</c> coincidan y que la misma palabra dé siempre el mismo término.
    /// </summary>
    public static string Stem(string token)
    {
        if (token.Length <= 3)
            return token;
        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 4)
            return token[..^3] + "y";
        // «es» de plural: inglés tras sibilante y castellano de -ón/-dad (razones, ubicaciones, entidades). No se toca
        // «ores» ni «ades» sueltos: stores o trades perderían su raíz.
        foreach (var suffix in new[] { "sses", "xes", "ches", "shes", "ones", "dades" })
        {
            if (token.EndsWith(suffix, StringComparison.Ordinal))
                return token[..^2];
        }
        if (token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal) && !token.EndsWith("us", StringComparison.Ordinal)
            && !token.EndsWith("is", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }

    /// <summary>Los términos de varios textos, sin repetidos y por orden de aparición.</summary>
    public static List<string> DistinctTokens(params string[] texts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var text in texts)
        {
            foreach (var token in Tokenize(text))
            {
                if (seen.Add(token))
                    result.Add(token);
            }
        }
        return result;
    }

    private static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
