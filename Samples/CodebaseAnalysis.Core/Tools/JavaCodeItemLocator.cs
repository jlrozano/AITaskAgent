using AITaskAgent.FileTools.CodeItems;
using CodebaseAnalysis.Core.Support;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// Ruta pedida a <c>view_code_item</c>: segmentos (<c>Outer.Inner.metodo</c>) y, si se dan, los tipos de los parámetros
/// (<c>metodo(int,String)</c>) para elegir una sobrecarga. Una declaración casa si su cadena de nombres acaba en esos
/// segmentos, igual que el localizador de C#, que busca en descendientes: <c>Inner.metodo</c> encuentra el método de la
/// clase anidada.
/// </summary>
internal sealed record CodeItemPath(IReadOnlyList<string> Segments, IReadOnlyList<string>? ParameterTypes)
{
    public static CodeItemPath Parse(string nodePath)
    {
        var text = nodePath.Trim();
        IReadOnlyList<string>? parameters = null;
        var open = text.IndexOf('(');
        if (open >= 0)
        {
            var close = text.LastIndexOf(')');
            var inner = close > open ? text[(open + 1)..close] : text[(open + 1)..];
            parameters = [.. JavaSignatures.SplitTopLevel(inner).Select(JavaSignatures.SimpleType).Where(t => t.Length > 0)];
            text = text[..open];
        }
        return new CodeItemPath([.. text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], parameters);
    }

    public bool Matches(IReadOnlyList<string> chain) =>
        Segments.Count > 0 && chain.Count >= Segments.Count
        && Segments.Select((s, i) => string.Equals(s, chain[chain.Count - Segments.Count + i], StringComparison.Ordinal)).All(x => x);

    public bool MatchesParameters(IReadOnlyList<string>? candidate) =>
        ParameterTypes == null || (candidate != null && candidate.SequenceEqual(ParameterTypes, StringComparer.Ordinal));
}

/// <summary>Normalización de tipos de parámetro Java, la misma que usa el analizador para <c>paramSimple</c>.</summary>
internal static partial class JavaSignatures
{
    /// <summary>
    /// Nombre simple del tipo: sin anotaciones, <c>final</c>, genéricos ni paquete, con los <c>[]</c> (un varargs
    /// <c>String...</c> es <c>String[]</c>, como lo borra <c>javac</c>). <c>java.util.Map.Entry&lt;K,V&gt;</c> es <c>Entry</c>.
    /// </summary>
    public static string SimpleType(string type)
    {
        var text = AnnotationPattern().Replace(type, " ");
        text = FinalPattern().Replace(text, " ");
        var sb = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var ch in text)
        {
            if (ch == '<') depth++;
            else if (ch == '>') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && !char.IsWhiteSpace(ch)) sb.Append(ch);
        }
        var erased = sb.ToString().Replace("...", "[]", StringComparison.Ordinal);
        var dims = "";
        while (erased.EndsWith("[]", StringComparison.Ordinal))
        {
            erased = erased[..^2];
            dims += "[]";
        }
        var dot = erased.LastIndexOf('.');
        return (dot >= 0 ? erased[(dot + 1)..] : erased) + dims;
    }

    /// <summary>Tipo de cada parámetro de una lista de declaración (<c>final int a, Map&lt;K, V&gt; m, String... r</c>).</summary>
    public static IReadOnlyList<string> ParameterTypes(string parameterList) =>
        [.. SplitTopLevel(parameterList).Select(p =>
        {
            var cleaned = FinalPattern().Replace(AnnotationPattern().Replace(p, " "), " ").Trim();
            var name = TrailingNamePattern().Match(cleaned);
            return SimpleType(name.Success ? cleaned[..name.Index] : cleaned);
        }).Where(t => t.Length > 0)];

    /// <summary>Partes separadas por comas que no están dentro de <c>&lt;&gt;</c>, <c>()</c> ni <c>[]</c>.</summary>
    public static IEnumerable<string> SplitTopLevel(string list)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            switch (list[i])
            {
                case '<' or '(' or '[': depth++; break;
                case '>' or ')' or ']': depth = Math.Max(0, depth - 1); break;
                case ',' when depth == 0:
                    if (list[start..i].Trim().Length > 0) yield return list[start..i];
                    start = i + 1;
                    break;
            }
        }
        if (list[start..].Trim().Length > 0)
            yield return list[start..];
    }

    [GeneratedRegex(@"@[\w.$]+(\s*\([^)]*\))?")]
    private static partial Regex AnnotationPattern();

    [GeneratedRegex(@"\bfinal\b")]
    private static partial Regex FinalPattern();

    [GeneratedRegex(@"\s*[A-Za-z_$][\w$]*\s*(\[\s*\])*\s*$")]
    private static partial Regex TrailingNamePattern();
}

/// <summary>Una declaración Java encontrada por llaves: tipo o método, con su cadena de nombres y su rango.</summary>
internal sealed record JavaDeclaration(IReadOnlyList<string> Chain, bool IsType, IReadOnlyList<string>? ParameterTypes, string Signature, int StartLine, int EndLine)
{
    public string Path => string.Join('.', Chain);

    /// <summary>La ruta que elige solo esta declaración: con los tipos de sus parámetros si es un método.</summary>
    public string SelectingPath => IsType ? Path : $"{Path}({string.Join(',', ParameterTypes ?? [])})";
}

/// <summary>
/// Declaraciones de un fuente Java sin compilarlo: recorre las llaves sobre el texto enmascarado (sin comentarios ni el
/// contenido de las cadenas, <see cref="CFamilySource"/>) y clasifica la cabecera de cada bloque, desde el último
/// <c>;</c>, <c>{</c> o <c>}</c>: tipo si lleva <c>class</c>/<c>interface</c>/<c>enum</c>/<c>record</c> y un nombre,
/// método si está en el cuerpo de un tipo y acaba en <c>nombre(parámetros)</c> con su <c>throws</c> opcional (sin
/// <c>new</c> delante ni una palabra reservada por nombre), y bloque sin nombre en cualquier otro caso (inicializadores,
/// lambdas, clases anónimas, sentencias). Los métodos sin cuerpo (interfaz, <c>abstract</c>, <c>native</c>) acaban en su
/// <c>;</c>. La declaración empieza en el primer carácter de su cabecera, anotaciones incluidas y Javadoc excluido. Los
/// campos y las constantes de enum no se reconocen.
/// </summary>
internal static partial class JavaDeclarationScanner
{
    private static readonly HashSet<string> NotMethodNames = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "synchronized", "try", "do", "else", "return", "new", "throw", "super", "this",
        "assert", "case", "default", "finally"
    };

    private sealed record Frame(bool IsType, bool IsMethod, IReadOnlyList<string> Chain, int StartLine, IReadOnlyList<string>? Parameters, string Signature);

    public static List<JavaDeclaration> Scan(string text)
    {
        var masked = CFamilySource.Mask(text, maskStrings: true);
        var lines = CFamilySource.LineStarts(text);
        var declarations = new List<JavaDeclaration>();
        var frames = new Stack<Frame>();
        var headerStart = 0;

        IReadOnlyList<string> EnclosingChain() => frames.FirstOrDefault(f => f.IsType || f.IsMethod)?.Chain ?? [];
        Frame? EnclosingType() => frames.Count > 0 && frames.Peek().IsType ? frames.Peek() : null;

        for (var i = 0; i < masked.Length; i++)
        {
            var c = masked[i];
            if (c != '{' && c != '}' && c != ';')
                continue;

            var header = masked[headerStart..i];
            var firstChar = headerStart + (header.Length - header.TrimStart().Length);
            var startLine = CFamilySource.LineOf(lines, firstChar);

            if (c == '{')
            {
                var type = TypeHeaderPattern().Match(header);
                if (type.Success)
                {
                    frames.Push(new Frame(true, false, [.. EnclosingChain(), type.Groups["name"].Value], startLine, null, type.Groups["name"].Value));
                }
                else if (EnclosingType() is { } owner && Method(header, owner, requirePrefix: false) is { } method)
                {
                    frames.Push(new Frame(false, true, [.. owner.Chain, method.Name], startLine, method.Parameters, method.Signature));
                }
                else
                {
                    frames.Push(new Frame(false, false, EnclosingChain(), startLine, null, ""));
                }
            }
            else if (c == '}')
            {
                if (frames.Count > 0)
                {
                    var frame = frames.Pop();
                    if (frame.IsType || frame.IsMethod)
                    {
                        declarations.Add(new JavaDeclaration(frame.Chain, frame.IsType, frame.Parameters, frame.Signature,
                            frame.StartLine, CFamilySource.LineOf(lines, i)));
                    }
                }
            }
            else if (EnclosingType() is { } owner && Method(header, owner, requirePrefix: true) is { } method)
            {
                // Método sin cuerpo: interfaz, abstract o native.
                declarations.Add(new JavaDeclaration([.. owner.Chain, method.Name], false, method.Parameters, method.Signature,
                    startLine, CFamilySource.LineOf(lines, i)));
            }

            headerStart = i + 1;
        }

        return [.. declarations.OrderBy(d => d.StartLine).ThenBy(d => d.Chain.Count)];
    }

    /// <summary>
    /// La cabecera como método del tipo <paramref name="owner"/>, o null. Delante del nombre tiene que haber un tipo o un
    /// modificador; sin nada delante solo vale si es un constructor (el nombre del tipo) y lleva cuerpo.
    /// </summary>
    private static (string Name, IReadOnlyList<string> Parameters, string Signature)? Method(string header, Frame owner, bool requirePrefix)
    {
        var match = MethodHeaderPattern().Match(header);
        if (!match.Success)
            return null;
        var name = match.Groups["name"].Value;
        if (NotMethodNames.Contains(name))
            return null;

        var before = header[..match.Groups["name"].Index].TrimEnd();
        if (before.Length == 0)
        {
            if (requirePrefix || name != owner.Chain[^1])
                return null;
        }
        else
        {
            var last = before[^1];
            if (!(char.IsLetterOrDigit(last) || last is '_' or '$' or '>' or ']' or ')'))
                return null;
            if (PrecedingWordPattern().Match(before) is { Success: true } word && NotMethodNames.Contains(word.Value))
                return null;
            // Un ')' delante solo puede cerrar una anotación con argumentos.
            if (last == ')' && !before.Contains('@'))
                return null;
        }

        var parameterList = match.Groups["params"].Value;
        var signature = $"{name}({CollapsePattern().Replace(parameterList.Trim(), " ")})";
        return (name, JavaSignatures.ParameterTypes(parameterList), signature);
    }

    [GeneratedRegex(@"\b(?:class|interface|enum|record)\s+(?<name>[A-Za-z_$][\w$]*)")]
    private static partial Regex TypeHeaderPattern();

    [GeneratedRegex(@"(?<name>[A-Za-z_$][\w$]*)\s*\((?<params>[^()]*(?:\([^()]*\)[^()]*)*)\)\s*(?:\[\s*\]\s*)*(?:throws\s+[\w$.,\s<>]+)?\s*(?:default\s+[^;{}]*)?$")]
    private static partial Regex MethodHeaderPattern();

    [GeneratedRegex(@"[A-Za-z_$][\w$]*$")]
    private static partial Regex PrecedingWordPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapsePattern();
}

/// <summary>
/// Localizador de <c>view_code_item</c> para ficheros <c>.java</c> sin grafo: <see cref="JavaDeclarationScanner"/> sobre el
/// texto del fichero. Tipos y métodos, también de clases anidadas; con varias sobrecargas, las lista para que se elija
/// con <c>Clase.metodo(tipos)</c>.
/// </summary>
public sealed class JavaCodeItemLocator : ICodeItemLocator
{
    /// <summary>Nodos que se ofrecen cuando no se encuentra el pedido.</summary>
    private const int MaxAvailable = 20;

    public ValueTask<IReadOnlyList<CodeItemLookup>?> LocateAsync(CodeItemRequest request, CancellationToken cancellationToken)
    {
        if (!request.FilePath.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult<IReadOnlyList<CodeItemLookup>?>(null);

        var declarations = JavaDeclarationScanner.Scan(request.SourceText);
        IReadOnlyList<string>? available = null;
        var lookups = request.NodePaths.Select(nodePath =>
        {
            var lookup = Lookup(declarations, CodeItemPath.Parse(nodePath));
            return lookup.Matches.Count > 0
                ? lookup
                : CodeItemLookup.NotFound(available ??= [.. declarations.Select(d => d.Path).Distinct(StringComparer.Ordinal).Take(MaxAvailable)]);
        }).ToList();
        return ValueTask.FromResult<IReadOnlyList<CodeItemLookup>?>(lookups);
    }

    internal static CodeItemLookup Lookup(IReadOnlyList<JavaDeclaration> declarations, CodeItemPath path)
    {
        var matches = declarations.Where(d => path.Matches(d.Chain)).ToList();
        // Un nombre que es de un tipo y de sus constructores es el tipo.
        if (matches.Any(d => d.IsType) && path.ParameterTypes == null)
            matches = [.. matches.Where(d => d.IsType)];
        else
            matches = [.. matches.Where(d => !d.IsType ? path.MatchesParameters(d.ParameterTypes) : path.ParameterTypes == null)];

        return new CodeItemLookup([.. matches.Select(d => new CodeItemMatch
        {
            StartLine = d.StartLine,
            EndLine = d.EndLine,
            NodePath = d.SelectingPath,
            Detail = d.Signature
        })], []);
    }
}
