using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AITaskAgent.Script.Protocol;

/// <summary>
/// One piece of JavaScript of a workflow: a step body (<c>function (input, context)</c>) or a library
/// (<c>function (module, exports, require)</c>). <paramref name="Id"/> is the name errors are reported under (the step
/// id, or <c>lib:name</c>); <paramref name="Key"/> is the name of its function in the bundle.
/// </summary>
internal sealed record BundlePiece(string Id, bool IsLibrary, string Key, string Source);

/// <summary>
/// All the JavaScript of a run joined in one text, compiled once (BRMS style): each step's script becomes a named
/// function of <c>__bundle.steps</c> and each library a function of <c>__bundle.libs</c>. Built the same way by the
/// host (to keep it), by the worker (to compile it and to name the lines of its errors) and by the syntax check, so
/// a run and the designer's validation compile exactly the same text.
/// </summary>
/// <remarks>
/// Each piece starts on a line of its own with its function header on that same line, so line N of a script is
/// line <c>start + N - 1</c> of the bundle; <see cref="MapLocations"/> turns bundle positions back into the piece's own
/// lines (and columns as the stand-alone wrappers <see cref="WrapStep"/>/<see cref="WrapLibrary"/> report them).
/// </remarks>
internal sealed partial class ScriptBundle
{
    /// <summary>Document name of the bundle in V8 messages; mapped away before anyone sees it.</summary>
    public const string DocumentName = "workflow-bundle";

    /// <summary>Header of a step body when it is compiled on its own (old per-execution form and the diagnostics).</summary>
    public const string StepWrapHeader = "(function (input, context) {";

    /// <summary>Header of a library when it is compiled on its own.</summary>
    public const string LibraryWrapHeader = "(function (module, exports, require) {";

    private const string StepFunction = "function (input, context) {";
    private const string LibraryFunction = "function (module, exports, require) {";
    private const string Preamble = "globalThis.__bundle = { steps: Object.create(null), libs: Object.create(null) };";

    private readonly List<Segment> _segments;

    private ScriptBundle(string text, List<Segment> segments, IReadOnlyList<BundlePiece> pieces)
    {
        Text = text;
        _segments = segments;
        Pieces = pieces;
    }

    /// <summary>The JavaScript to compile.</summary>
    public string Text { get; }

    /// <summary>The pieces, in bundle order.</summary>
    public IReadOnlyList<BundlePiece> Pieces { get; }

    /// <summary>Joins the pieces. Deterministic: the same pieces always give the same text.</summary>
    public static ScriptBundle Build(IReadOnlyList<BundlePiece> pieces)
    {
        StringBuilder text = new(Preamble);
        List<Segment> segments = [];
        int line = 1;
        foreach (BundlePiece piece in pieces)
        {
            string header = $"globalThis.__bundle.{(piece.IsLibrary ? "libs" : "steps")}[{JsonConvert.ToString(piece.Key)}] = " +
                (piece.IsLibrary ? LibraryFunction : StepFunction);
            text.Append('\n');
            line++;
            int lines = CountLines(piece.Source);
            segments.Add(new Segment(piece, line, lines, header.Length));
            text.Append(header).Append(piece.Source).Append("\n};");
            line += lines;
        }

        return new ScriptBundle(text.ToString(), segments, pieces);
    }

    /// <summary>The pieces as the wire carries them (JSON text).</summary>
    public static string ToJson(IReadOnlyList<BundlePiece> pieces)
        => new JArray(pieces.Select(p => new JObject
        {
            ["id"] = p.Id,
            ["lib"] = p.IsLibrary,
            ["key"] = p.Key,
            ["source"] = p.Source,
        })).ToString(Formatting.None);

    /// <summary>Reads the pieces of <see cref="ToJson"/>.</summary>
    public static List<BundlePiece> FromJson(string json)
        => [.. JArray.Parse(json).Select(t => new BundlePiece(
            t.Value<string>("id") ?? "",
            t.Value<bool?>("lib") ?? false,
            t.Value<string>("key") ?? "",
            t.Value<string>("source") ?? ""))];

    /// <summary>A piece on its own, as an expression (the stand-alone compile of the diagnostics).</summary>
    public static string Wrap(BundlePiece piece)
        => piece.IsLibrary ? WrapLibrary(piece.Source) : WrapStep(piece.Source);

    /// <summary>The header of a step script on line 1 of its text, so reported lines match the YAML block.</summary>
    public static string WrapStep(string source) => StepWrapHeader + source + "\n})";

    /// <summary>The header of a library on line 1 of its text, so reported lines match the library file.</summary>
    public static string WrapLibrary(string source) => LibraryWrapHeader + source + "\n})";

    /// <summary>The exact text <c>Function.prototype.toString</c> gives for the piece's function when it did not escape it.</summary>
    public static string FunctionText(BundlePiece piece)
        => (piece.IsLibrary ? LibraryFunction : StepFunction) + piece.Source + "\n}";

    /// <summary>The piece a bundle line belongs to (its closing line included), or null for the preamble.</summary>
    public BundlePiece? PieceAt(int bundleLine) => Find(bundleLine)?.Piece;

    /// <summary>
    /// Rewrites every <c>workflow-bundle:LINE:COL</c> of a V8 message as <c>id:line:col</c> of the piece it falls in,
    /// with the column a stand-alone compile of the piece would report, and drops what only the bundle has: the
    /// <c>globalThis.__bundle.steps.</c> prefix V8 infers for function names and the frames of the worker's own glue
    /// (<c>bootstrap</c>, <c>call</c>). Anything else is left as it is.
    /// </summary>
    public string MapLocations(string message)
    {
        string mapped = MapPositions(message).Replace("globalThis.__bundle.steps.", "").Replace("globalThis.__bundle.libs.", "");
        return string.Join('\n', mapped.Split('\n').Where(line => !GlueFrame().IsMatch(line)));
    }

    private string MapPositions(string message) => Location().Replace(message, match =>
    {
        int bundleLine = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
        if (Find(bundleLine) is not { } segment)
        {
            return match.Value;
        }

        int line = bundleLine - segment.StartLine + 1;
        string suffix = match.Groups["suffix"].Value;
        if (!match.Groups["col"].Success)
        {
            return $"{segment.Piece.Id}{suffix}:{line}";
        }

        int column = int.Parse(match.Groups["col"].Value, CultureInfo.InvariantCulture);
        if (line == 1)
        {
            string wrap = segment.Piece.IsLibrary ? LibraryWrapHeader : StepWrapHeader;
            column = Math.Max(1, column - segment.HeaderLength + wrap.Length);
        }

        return $"{segment.Piece.Id}{suffix}:{line}:{column}";
    });

    /// <summary>The first bundle line a V8 message points at, or null.</summary>
    public static int? FirstLine(string message)
    {
        Match match = Location().Match(message);
        return match.Success ? int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture) : null;
    }

    private Segment? Find(int bundleLine)
    {
        foreach (Segment segment in _segments)
        {
            // The source lines and the closing "};" line after them.
            if (bundleLine >= segment.StartLine && bundleLine <= segment.StartLine + segment.LineCount)
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>Lines as ECMAScript counts them: LF, CR, CRLF, U+2028 and U+2029 end a line.</summary>
    private static int CountLines(string text)
    {
        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                lines++;
            }
            else if (c is '\n' or '\u2028' or '\u2029')
            {
                lines++;
            }
        }

        return lines;
    }

    [GeneratedRegex(@"workflow-bundle(?<suffix> \[temp\])?:(?<line>\d+)(?::(?<col>\d+))?")]
    private static partial Regex Location();

    [GeneratedRegex(@"^\s+at (?:.*\()?(?:bootstrap|call)(?: \[\d+\])?:\d+:\d+\)?\s*(?:->.*)?$")]
    private static partial Regex GlueFrame();

    private sealed record Segment(BundlePiece Piece, int StartLine, int LineCount, int HeaderLength);
}
