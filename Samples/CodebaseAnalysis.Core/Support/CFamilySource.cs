namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Máscara de un fuente de la familia C (Java, C#): devuelve un texto de la misma longitud, con los saltos de línea en su
/// sitio, en el que los comentarios pasan a espacios y, si se pide, también el contenido de las cadenas y los caracteres
/// (sus comillas se conservan). Así una llave, un punto y coma o un <c>Class.forName(</c> dentro de un comentario o de una
/// cadena no cuentan, y las posiciones siguen valiendo para el texto original. Reconoce <c>//</c>, <c>/* */</c>,
/// <c>"…"</c> con escapes, <c>'…'</c>, los bloques de texto <c>"""…"""</c> y las cadenas literales <c>@"…"</c> de C#.
/// </summary>
public static class CFamilySource
{
    public static string Mask(string text, bool maskStrings)
    {
        var output = text.ToCharArray();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                    output[i++] = ' ';
                continue;
            }

            if (c == '/' && next == '*')
            {
                output[i] = output[i + 1] = ' ';
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    Blank(output, i++);
                if (i < text.Length)
                {
                    output[i] = output[i + 1] = ' ';
                    i += 2;
                }
                continue;
            }

            if (c == '"' && next == '"' && i + 2 < text.Length && text[i + 2] == '"')
            {
                i += 3;
                while (i < text.Length && !(text[i] == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"'))
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        if (maskStrings) { Blank(output, i); Blank(output, i + 1); }
                        i += 2;
                        continue;
                    }
                    if (maskStrings) Blank(output, i);
                    i++;
                }
                i = Math.Min(text.Length, i + 3);
                continue;
            }

            if (c == '@' && next == '"')
            {
                i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        if (maskStrings) { Blank(output, i); Blank(output, i + 1); }
                        i += 2;
                        continue;
                    }
                    if (text[i] == '"')
                        break;
                    if (maskStrings) Blank(output, i);
                    i++;
                }
                i++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length && text[i] != quote && text[i] != '\n' && text[i] != '\r')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        if (maskStrings) { Blank(output, i); Blank(output, i + 1); }
                        i += 2;
                        continue;
                    }
                    if (maskStrings) Blank(output, i);
                    i++;
                }
                if (i < text.Length && text[i] == quote)
                    i++;
                continue;
            }

            i++;
        }
        return new string(output);
    }

    /// <summary>Espacio en lugar del carácter, salvo los saltos de línea, para que las líneas no se muevan.</summary>
    private static void Blank(char[] output, int index)
    {
        if (output[index] is not ('\n' or '\r'))
            output[index] = ' ';
    }

    /// <summary>Inicio de cada línea (índice del primer carácter), para pasar de posición a número de línea.</summary>
    public static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
                starts.Add(i + 1);
        }
        return [.. starts];
    }

    /// <summary>Número de línea (desde 1) de <paramref name="position"/>.</summary>
    public static int LineOf(int[] lineStarts, int position)
    {
        var index = Array.BinarySearch(lineStarts, position);
        return (index >= 0 ? index : ~index - 1) + 1;
    }
}
