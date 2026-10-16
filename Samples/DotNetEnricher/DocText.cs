using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DotNetEnricher;

/// <summary>
/// Texto de documentación de una declaración, sacado de la sintaxis (la trivia de Roslyn) y no de cbm: el texto ya viene
/// decodificado con la codificación real del fichero, así que no arrastra los U+FFFD de un fichero ANSI leído como UTF-8.
/// Prefiere la documentación XML completa (resumen, observaciones, parámetros, retorno y excepciones) y, si no la hay, el
/// comentario que precede a la declaración.
/// </summary>
internal static class DocText
{
    public const string FromXmlDoc = "xml";
    public const string FromComment = "comment";

    public static (string? Text, string? Source) For(ISymbol symbol)
    {
        var declaration = DeclarationOf(symbol);
        return declaration is null ? (null, null) : For(declaration);
    }

    public static (string? Text, string? Source) For(SyntaxNode declaration)
    {
        if (XmlDoc(declaration) is { } xml) return (xml, FromXmlDoc);
        if (PrecedingComment(declaration) is { } comment) return (comment, FromComment);
        return (null, null);
    }

    /// <summary>La declaración que lleva la documentación: para un campo, el <c>FieldDeclaration</c> y no el declarador.</summary>
    public static SyntaxNode? DeclarationOf(ISymbol symbol)
    {
        var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return syntax switch
        {
            VariableDeclaratorSyntax { Parent.Parent: { } field } => field,
            _ => syntax
        };
    }

    // ─── Documentación XML ──────────────────────────────────────────────────────

    private static string? XmlDoc(SyntaxNode declaration)
    {
        var doc = declaration.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .LastOrDefault();
        if (doc is null) return null;

        // Se quita el margen (/// o *) de cada línea y se lee como XML; un documento mal formado cae al texto sin etiquetas.
        var lines = doc.ToFullString().Split('\n').Select(l => Regex.Replace(l, @"^\s*(///|/\*\*|\*/|\*)\s?", "").TrimEnd('\r'));
        var xml = string.Join("\n", lines);
        string? text;
        try
        {
            text = Compose(XElement.Parse($"<doc>{xml}</doc>"));
        }
        catch (System.Xml.XmlException)
        {
            text = Normalize(Regex.Replace(xml, "<[^>]+>", " "));
        }
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? Compose(XElement doc)
    {
        var parts = new List<string>();
        Add(parts, "", doc.Elements("summary"));
        Add(parts, "Remarks: ", doc.Elements("remarks"));
        foreach (var p in doc.Elements("param"))
            if (Render(p) is { Length: > 0 } text)
                parts.Add($"Param {p.Attribute("name")?.Value}: {text}");
        Add(parts, "Returns: ", doc.Elements("returns"));
        foreach (var e in doc.Elements("exception"))
            if (Render(e) is { Length: > 0 } text)
                parts.Add($"Throws {CrefName(e.Attribute("cref")?.Value)}: {text}");
        // Un documento sin ninguna de esas etiquetas (texto suelto) también vale.
        if (parts.Count == 0 && !doc.Elements().Any()) parts.Add(Render(doc));
        return parts.Count == 0 ? null : string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private static void Add(List<string> parts, string label, IEnumerable<XElement> elements)
    {
        var text = string.Join(" ", elements.Select(Render).Where(t => t.Length > 0));
        if (text.Length > 0) parts.Add(label + text);
    }

    private static string Render(XElement element)
    {
        var sb = new StringBuilder();
        RenderInto(element, sb);
        return Normalize(sb.ToString());
    }

    private static void RenderInto(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "see" or "seealso":
                            sb.Append(' ').Append(child.Attribute("cref") is { } cref ? CrefName(cref.Value)
                                : child.Attribute("langword")?.Value ?? child.Attribute("href")?.Value ?? child.Value).Append(' ');
                            break;
                        case "paramref" or "typeparamref":
                            sb.Append(' ').Append(child.Attribute("name")?.Value).Append(' ');
                            break;
                        case "para" or "br" or "item" or "listheader" or "term" or "description":
                            sb.Append(' ');
                            RenderInto(child, sb);
                            sb.Append(' ');
                            break;
                        default:
                            RenderInto(child, sb);
                            break;
                    }
                    break;
            }
        }
    }

    /// <summary>De <c>T:Ns.Type</c> a <c>Type</c> y de <c>M:Ns.Type.Method(System.Int32)</c> a <c>Type.Method</c>; un cref sin prefijo se deja como lo escribió el autor.</summary>
    private static string CrefName(string? cref)
    {
        if (string.IsNullOrEmpty(cref)) return "";
        if (cref.Length < 2 || cref[1] != ':') return cref;
        var name = cref[2..];
        var paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren];
        var parts = name.Split('.');
        return cref[0] == 'T' || parts.Length < 2 ? parts[^1] : string.Join('.', parts[^2..]);
    }

    private static string Normalize(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        // <see/> y <paramref/> se rodean de espacios al aplanarlos: fuera los que quedan antes de la puntuación.
        return Regex.Replace(flat, @" ([.,;:!?)])", "$1");
    }

    // ─── Comentario anterior ────────────────────────────────────────────────────

    /// <summary>
    /// Los comentarios <c>//</c> o <c>/* */</c> pegados a la declaración, sin línea en blanco de por medio. No cuenta el
    /// código comentado ni los que son solo una marca TODO/FIXME/HACK (esos tienen su propia propiedad).
    /// </summary>
    private static string? PrecedingComment(SyntaxNode declaration)
    {
        var trivia = declaration.GetLeadingTrivia();
        var lines = new List<string>();
        var eols = 0;   // saltos de línea vistos, yendo hacia atrás, desde el último comentario (o la declaración)
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            var t = trivia[i];
            if (t.IsKind(SyntaxKind.WhitespaceTrivia)) continue;
            if (t.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                // Uno basta para pasar de un comentario a lo siguiente; dos son una línea en blanco: el comentario no está pegado.
                if (++eols >= 2) return lines.Count == 0 ? null : Finish(lines);
                continue;
            }
            if (!t.IsKind(SyntaxKind.SingleLineCommentTrivia) && !t.IsKind(SyntaxKind.MultiLineCommentTrivia))
                break;   // directivas, regiones, documentación: el comentario no les pertenece
            lines.InsertRange(0, CommentScanner.CleanLines(t.ToString()));
            eols = 0;
        }
        return Finish(lines);
    }

    private static string? Finish(List<string> lines)
    {
        if (lines.Count == 0) return null;
        if (CommentScanner.LooksLikeCode(lines)) return null;
        var text = Normalize(string.Join(" ", lines.Where(l => !Regex.IsMatch(l, @"^[\-=_*#/\s]*$"))));
        if (text.Length < 3 || CommentScanner.StartsWithMarker(text)) return null;
        return text;
    }
}

/// <summary>Un comentario de una o varias líneas seguidas: sus líneas ya sin marcas y la posición (1-based) de la primera.</summary>
internal sealed record CommentBlock(int StartLine, int EndLine, IReadOnlyList<string> Lines, SyntaxTrivia First, bool OwnLine);

/// <summary>Lectura de los comentarios de un árbol: bloques, marcas TODO/FIXME/HACK y código comentado.</summary>
internal static partial class CommentScanner
{
    [GeneratedRegex(@"\b(TODO|FIXME|HACK)\b[\s:(\-]*(?<text>[^\r\n]*)")]
    private static partial Regex MarkerRegex();

    public static bool StartsWithMarker(string text) => MarkerRegex().Match(text) is { Success: true, Index: 0 };

    /// <summary>Marcas en una línea de comentario. Solo en mayúsculas: «todo» es una palabra corriente en español.</summary>
    public static (string Kind, string Text)? Marker(string line)
    {
        var m = MarkerRegex().Match(line);
        return m.Success ? (m.Groups[1].Value, m.Groups["text"].Value.TrimEnd('*', '/', ' ').Trim()) : null;
    }

    /// <summary>Las líneas de un comentario sin <c>//</c>, <c>///</c>, <c>/*</c>, <c>*/</c> ni el asterisco de margen.</summary>
    public static IEnumerable<string> CleanLines(string comment)
    {
        foreach (var raw in comment.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("//")) line = line.TrimStart('/');
            else
            {
                if (line.StartsWith("/*")) line = line[2..];
                if (line.EndsWith("*/")) line = line[..^2];
                line = line.TrimStart('*');
            }
            yield return line.Trim();
        }
    }

    /// <summary>
    /// Comentarios del árbol, con los <c>//</c> de líneas consecutivas unidos en un bloque (si están solos en su línea) y
    /// sin los de documentación XML. Las líneas del bloque siguen numeradas desde <see cref="CommentBlock.StartLine"/>.
    /// </summary>
    public static IEnumerable<CommentBlock> Blocks(SyntaxNode root, SourceText text)
    {
        CommentBlock? open = null;
        List<string>? openLines = null;
        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                continue;

            var span = text.Lines.GetLinePositionSpan(trivia.Span);
            var start = span.Start.Line + 1;
            var end = span.End.Line + 1;
            var firstOnLine = text.Lines[span.Start.Line].ToString().TrimStart();
            var own = firstOnLine.StartsWith("//", StringComparison.Ordinal) || firstOnLine.StartsWith("/*", StringComparison.Ordinal);

            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && own && open is { OwnLine: true } && openLines is not null
                && open.First.IsKind(SyntaxKind.SingleLineCommentTrivia) && start == open.EndLine + 1)
            {
                openLines.AddRange(CleanLines(trivia.ToString()));
                open = open with { EndLine = end };
                continue;
            }

            if (open is not null) yield return open with { Lines = openLines! };
            openLines = [.. CleanLines(trivia.ToString())];
            open = new CommentBlock(start, end, openLines, trivia, own);
        }
        if (open is not null) yield return open with { Lines = openLines! };
    }

    /// <summary>
    /// Las rachas de código comentado de un bloque: el bloque entero si parsea como código, o, si mezcla código y prosa,
    /// cada tramo de líneas consecutivas que parsea por sí solo. Devuelve (primera línea, última línea, primera línea de texto).
    /// </summary>
    public static IEnumerable<(int Start, int End, string Preview)> CodeRuns(CommentBlock block)
    {
        if (LooksLikeCode(block.Lines))
        {
            yield return (block.StartLine, block.EndLine, block.Lines.FirstOrDefault(l => l.Length > 0) ?? "");
            yield break;
        }
        if (block.Lines.Count < 2) yield break;

        int? start = null;
        for (var i = 0; i <= block.Lines.Count; i++)
        {
            var isCode = i < block.Lines.Count && block.Lines[i].Length > 0 && LooksLikeCode([block.Lines[i]]);
            if (isCode) { start ??= i; continue; }
            if (start is { } first)
            {
                yield return (block.StartLine + first, block.StartLine + i - 1, block.Lines[first]);
                start = null;
            }
        }
    }

    /// <summary>
    /// Si el texto parsea como C# sin ningún error y contiene algo que hace cosas (una llamada, una asignación, un
    /// <c>return</c>, una declaración con valor, un miembro…), no es prosa. Hace falta un <c>;</c> o una llave: la prosa
    /// casi nunca las lleva y, sin ellas, cualquier palabra suelta parsearía como expresión.
    /// </summary>
    public static bool LooksLikeCode(IReadOnlyList<string> lines)
    {
        var code = string.Join("\n", lines).Trim();
        if (code.Length < 4 || (!code.Contains(';') && !code.Contains('{') && !code.Contains('}'))) return false;

        var tree = SyntaxFactory.ParseCompilationUnit(code, options: new CSharpParseOptions(LanguageVersion.Latest));
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        return tree.DescendantNodes().Any(IsCodeNode);
    }

    private static bool IsCodeNode(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax or AssignmentExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
            or ReturnStatementSyntax or ThrowStatementSyntax or IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
            or WhileStatementSyntax or DoStatementSyntax or UsingStatementSyntax or TryStatementSyntax or SwitchStatementSyntax
            or AwaitExpressionSyntax or PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax
            or MethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax or BaseTypeDeclarationSyntax
            or ConstructorDeclarationSyntax or UsingDirectiveSyntax => true,
        LocalDeclarationStatementSyntax local => local.Declaration.Variables.Any(v => v.Initializer is not null) || local.Declaration.Type.IsVar,
        _ => false
    };
}
