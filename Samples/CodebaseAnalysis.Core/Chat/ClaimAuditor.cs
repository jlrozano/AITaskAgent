using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Support;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Chat;

/// <summary>
/// Contrasta las afirmaciones marcadas de una respuesta del chat con lo que leyeron sus tools en ese turno.
/// </summary>
/// <remarks>
/// <para>
/// El formato lo fija <c>chat_system.md</c>: <c>[V]</c> verificado en el código (con <c>`ruta:línea`</c>), <c>[D]</c>
/// sacado de un informe o documento (con <c>`informe › sección`</c>) e <c>[I]</c> inferido. Las propuestas no se marcan.
/// Una marca abarca hasta la siguiente marca o el final de su párrafo, elemento de lista, fila o encabezado; las que
/// caen dentro de un bloque o span de código no cuentan. La UI numera las marcas con la misma regla.
/// </para>
/// <para>
/// Nunca rechaza una respuesta: lo que no se puede respaldar queda <see cref="ChatClaimStatus.Unchecked"/> con el motivo.
/// La evidencia sale del resultado de las tools, no de sus argumentos: <c>view_file</c> dice qué líneas enseñó,
/// <c>grep_search</c> en qué líneas encontró coincidencias y <c>graph_search</c> con <c>include_code</c> qué tramo leyó.
/// Lo demás del grafo (la tabla y las propiedades de <c>graph_search</c>, <c>graph_neighbors</c>, <c>graph_domain</c>) no
/// es código leído aunque cite <c>ruta:línea</c>: esas tools no están en <see cref="TurnEvidence"/> a propósito.
/// </para>
/// </remarks>
public static partial class ClaimAuditor
{
    /// <summary>Ficheros más grandes no se cuentan: se da por buena cualquier línea citada.</summary>
    private const long MaxLineCountBytes = 20 * 1024 * 1024;

    public static ChatTurnAudit Audit(string answer, TurnEvidence evidence, ChatWorkspace workspace)
    {
        var resolver = new ReferenceResolver(workspace, evidence);
        var audit = new ChatTurnAudit();
        var seen = new Dictionary<string, ChatReference>(StringComparer.Ordinal);

        ChatReference Resolve(string text)
        {
            if (!seen.TryGetValue(text, out var reference))
            {
                reference = resolver.Resolve(text)!;
                seen[text] = reference;
                audit.References.Add(reference);
            }
            return reference;
        }

        foreach (var unit in Units(answer))
        {
            List<ChatReference>? current = null;
            ChatClaim? claim = null;
            foreach (Match token in TokenPattern().Matches(unit))
            {
                if (token.Groups["mark"].Success)
                {
                    Close(claim, current);
                    claim = new ChatClaim { Kind = KindOf(token.Groups["k"].Value) };
                    current = [];
                    audit.Claims.Add(claim);
                    continue;
                }

                var text = token.Groups["code"].Value;
                if (!ReferenceResolver.LooksLikeReference(text))
                    continue;
                var reference = Resolve(text);
                if (current != null && !current.Contains(reference))
                    current.Add(reference);
            }
            Close(claim, current);
        }

        return audit;
    }

    private static string KindOf(string mark) => mark switch
    {
        "V" => ChatClaimKinds.Verified,
        "D" => ChatClaimKinds.Documented,
        _ => ChatClaimKinds.Inferred
    };

    private static void Close(ChatClaim? claim, List<ChatReference>? references)
    {
        if (claim == null || references == null)
            return;

        claim.References = [.. references.Select(r => r.Text)];
        switch (claim.Kind)
        {
            case ChatClaimKinds.Inferred:
                claim.Status = ChatClaimStatus.Inferred;
                break;

            case ChatClaimKinds.Verified:
                Decide(claim, references.Where(r => r.Kind == ChatReferenceKinds.Code).ToList(),
                    ChatClaimStatus.Verified, "Sin referencia al código (`ruta:línea`).");
                break;

            default:
                Decide(claim, references.Where(r => r.Kind == ChatReferenceKinds.Source).ToList(),
                    ChatClaimStatus.Documented, "Sin referencia a un informe o documento (`nombre › sección`).");
                break;
        }
    }

    private static void Decide(ChatClaim claim, List<ChatReference> references, string okStatus, string noReference)
    {
        if (references.Count == 0)
        {
            claim.Status = ChatClaimStatus.Unchecked;
            claim.Reason = noReference;
            return;
        }

        var failed = references.FirstOrDefault(r => r.Status != ChatReferenceStatus.Ok);
        if (failed == null)
        {
            claim.Status = okStatus;
            return;
        }

        claim.Status = ChatClaimStatus.Unchecked;
        claim.Reason = failed.Status switch
        {
            ChatReferenceStatus.Partial => $"Solo se leyó una parte de `{failed.Text}` en este turno.",
            ChatReferenceStatus.NotRead => $"`{failed.Text}` no se leyó en este turno.",
            ChatReferenceStatus.OutOfRange => $"`{failed.Text}`: esas líneas no existen.",
            _ => $"`{failed.Text}` no existe."
        };
    }

    /// <summary>
    /// Párrafos, elementos de lista, filas de tabla, citas y encabezados fuera de los bloques de código cercados. Las
    /// líneas de continuación se unen a su unidad.
    /// </summary>
    internal static IEnumerable<string> Units(string answer)
    {
        var current = new StringBuilder();
        string? fence = null;
        foreach (var line in answer.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var opener = FencePattern().Match(trimmed);
            if (fence != null)
            {
                if (opener.Success && trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.Trim().Length == opener.Value.Length)
                    fence = null;
                continue;
            }
            if (opener.Success)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                fence = opener.Value;
                continue;
            }
            if (trimmed.Length == 0)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            if (BlockStartPattern().IsMatch(trimmed) && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(trimmed);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    [GeneratedRegex(@"^(`{3,}|~{3,})")]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"^([-*+]\s|\d+[.)]\s|#{1,6}\s|\||>)")]
    private static partial Regex BlockStartPattern();

    /// <summary>Un span de código (que se come las marcas de dentro) o una marca.</summary>
    [GeneratedRegex(@"`(?<code>[^`\n]+)`|(?<mark>\[(?<k>[VDI])\])")]
    private static partial Regex TokenPattern();

    /// <summary>Resuelve el texto de un span de código a una referencia auditada.</summary>
    private sealed partial class ReferenceResolver(ChatWorkspace workspace, TurnEvidence evidence)
    {
        private readonly string _root = Path.GetFullPath(workspace.RepositoryPath);
        private readonly Dictionary<string, int?> _lineCounts = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<(string Key, string Title)>? _reports;
        private IReadOnlyList<(string Id, string Title)>? _documents;

        public static bool LooksLikeReference(string text) =>
            text.Contains(SourceSeparator, StringComparison.Ordinal) || CodeReference(text) != null;

        public ChatReference? Resolve(string text) =>
            text.Contains(SourceSeparator, StringComparison.Ordinal) ? ResolveSource(text) : ResolveCode(text);

        private const string SourceSeparator = " › ";

        /// <summary>El destino de una referencia a un documento es su id en el visor: <c>doc/&lt;id&gt;</c>.</summary>
        private const string DocumentTargetPrefix = "doc/";

        private static (string Path, int? Start, int? End)? CodeReference(string text)
        {
            var m = CodeReferencePattern().Match(text.Trim());
            if (!m.Success)
                return null;
            var path = m.Groups["path"].Value;
            var hasLine = m.Groups["s"].Success;
            // Sin línea solo es referencia si parece una ruta: `Pedidos.MontarFiltro` es un identificador.
            if (!hasLine && path.IndexOfAny(['/', '\\']) < 0)
                return null;
            int? start = hasLine ? int.Parse(m.Groups["s"].Value) : null;
            int? end = m.Groups["e"].Success ? int.Parse(m.Groups["e"].Value) : start;
            return (path, start, end);
        }

        private ChatReference ResolveCode(string text)
        {
            var (raw, start, end) = CodeReference(text)!.Value;
            var reference = new ChatReference
            {
                Text = text,
                Kind = ChatReferenceKinds.Code,
                Target = raw.Replace('\\', '/'),
                StartLine = start,
                EndLine = end
            };

            var full = TurnEvidence.ToFullPath(_root, raw);
            if (full == null || !File.Exists(full))
            {
                // Una ruta incompleta (`Pedidos.bas:12`) vale si coincide con un único fichero leído en el turno.
                var suffix = "/" + raw.Replace('\\', '/').TrimStart('/');
                var matches = evidence.Files.Where(f => f.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
                full = matches.Count == 1 ? matches[0] : null;
            }
            if (full == null || !File.Exists(full) || !TurnEvidence.IsInside(_root, full))
            {
                reference.Status = ChatReferenceStatus.Missing;
                return reference;
            }

            reference.Target = Path.GetRelativePath(_root, full).Replace('\\', '/');
            if (start is { } s && end is { } e && LineCount(full) is { } total && (s < 1 || e < s || e > total))
            {
                reference.Status = ChatReferenceStatus.OutOfRange;
                return reference;
            }

            reference.Status = evidence.Coverage(full, start, end);
            return reference;
        }

        private ChatReference ResolveSource(string text)
        {
            var name = text[..text.IndexOf(SourceSeparator, StringComparison.Ordinal)].Trim();
            var reference = new ChatReference { Text = text, Kind = ChatReferenceKinds.Source, Target = name, Status = ChatReferenceStatus.Missing };

            _reports ??= ListReports();
            var report = _reports.FirstOrDefault(r => Same(r.Key, name) || Same(r.Title, name)
                || Same(Path.GetFileNameWithoutExtension(r.Key), name));
            if (report.Key != null)
            {
                reference.Target = report.Key;
                reference.Status = evidence.Reports.Contains(report.Key) ? ChatReferenceStatus.Ok : ChatReferenceStatus.NotRead;
                return reference;
            }

            _documents ??= new Documents.DocumentStore(workspace.Knowledge).List().Select(d => (d.Id, d.Title)).ToList();
            var document = _documents.FirstOrDefault(d => Same(d.Id, name) || Same(d.Title, name));
            if (document.Id != null)
            {
                reference.Target = DocumentTargetPrefix + document.Id;
                reference.Status = evidence.Documents.Contains(document.Id) ? ChatReferenceStatus.Ok : ChatReferenceStatus.NotRead;
            }
            return reference;
        }

        private IReadOnlyList<(string Key, string Title)> ListReports() =>
        [
            .. ReportFiles.List(workspace.ReportsPath).Select(r => (r.FileName, r.Title)),
            .. ReportLibrary.ListCustom(workspace.Knowledge).Select(r => (ReportLibrary.CustomIdPrefix + r.FileName, r.Title))
        ];

        private static bool Same(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

        private int? LineCount(string full)
        {
            if (_lineCounts.TryGetValue(full, out var cached))
                return cached;
            int? count = null;
            try
            {
                if (new FileInfo(full).Length <= MaxLineCountBytes)
                    count = File.ReadLines(full).Count();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _lineCounts[full] = count;
            return count;
        }

        [GeneratedRegex(@"^(?<path>(?:[A-Za-z]:[\\/])?[^\s:*?""<>|`]+?)(?::(?<s>\d+)(?:\s*[-–]\s*(?<e>\d+))?)?$")]
        private static partial Regex CodeReferencePattern();
    }
}

/// <summary>
/// Lo que las tools de un turno leyeron de verdad: tramos de ficheros del repositorio, informes y documentos. Se saca de
/// los mensajes del turno (cada resultado de tool con su llamada).
/// </summary>
public sealed partial class TurnEvidence
{
    private readonly Dictionary<string, List<(int Start, int End)>> _ranges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _wholeFiles = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Reports { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rutas completas de los ficheros leídos, en todo o en parte.</summary>
    public IEnumerable<string> Files => _ranges.Keys.Union(_wholeFiles, StringComparer.OrdinalIgnoreCase);

    public void AddRange(string fullPath, int start, int end)
    {
        if (end < start)
            return;
        if (!_ranges.TryGetValue(fullPath, out var list))
            _ranges[fullPath] = list = [];
        list.Add((start, end));
    }

    public void AddWholeFile(string fullPath) => _wholeFiles.Add(fullPath);

    /// <summary>Cuánto de <c>[start, end]</c> se leyó; sin líneas, basta con haber leído algo del fichero.</summary>
    public string Coverage(string fullPath, int? start, int? end)
    {
        if (_wholeFiles.Contains(fullPath))
            return ChatReferenceStatus.Ok;
        if (!_ranges.TryGetValue(fullPath, out var ranges))
            return ChatReferenceStatus.NotRead;
        if (start is not { } s || end is not { } e)
            return ChatReferenceStatus.Ok;

        var covered = 0;
        for (var line = s; line <= e; line++)
        {
            if (ranges.Any(r => line >= r.Start && line <= r.End))
                covered++;
        }
        return covered == e - s + 1 ? ChatReferenceStatus.Ok
            : covered > 0 ? ChatReferenceStatus.Partial
            : ChatReferenceStatus.NotRead;
    }

    /// <summary>Evidencia de los mensajes de un turno: cada mensaje de tool se empareja con su llamada por id.</summary>
    public static TurnEvidence FromMessages(IEnumerable<Message> messages, string repositoryPath)
    {
        var root = Path.GetFullPath(repositoryPath);
        var evidence = new TurnEvidence();
        var calls = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? [])
                calls[call.Id] = call;

            if (message.Role != LlmConstants.MessageRoles.Tool || message.ToolCallId == null
                || !calls.TryGetValue(message.ToolCallId, out var owner))
                continue;

            var content = message.Content ?? string.Empty;
            if (IsToolError(content))
                continue;

            try
            {
                evidence.Observe(root, owner.Name, owner.Arguments, content);
            }
            catch (JsonException)
            {
                // Argumentos o resultado que no se entienden: esa llamada no aporta evidencia.
            }
        }
        return evidence;
    }

    private void Observe(string root, string tool, string arguments, string content)
    {
        switch (tool)
        {
            case "view_file":
                {
                    var path = ViewFilePathPattern().Match(content);
                    var lines = ViewFileLinesPattern().Match(content);
                    if (path.Success && lines.Success && ToFullPath(root, path.Groups["p"].Value) is { } full)
                        AddRange(full, int.Parse(lines.Groups["s"].Value), int.Parse(lines.Groups["e"].Value));
                    break;
                }

            // Solo los nodos que devuelve, con el tramo de su Location, y no el fichero entero que se pidió.
            case "view_code_item":
                foreach (Match m in CodeItemLocationPattern().Matches(content))
                {
                    if (ToFullPath(root, m.Groups["p"].Value) is { } full)
                        AddRange(full, int.Parse(m.Groups["s"].Value), int.Parse(m.Groups["e"].Value));
                }
                break;

            case "grep_search":
                {
                    if (!content.TrimStart().StartsWith('[') || ToFullPath(root, Argument(arguments, "SearchPath") ?? string.Empty) is not { } searchPath)
                        break;
                    using var results = JsonDocument.Parse(content);
                    foreach (var hit in results.RootElement.EnumerateArray())
                    {
                        if (!hit.TryGetProperty("Filename", out var name) || !hit.TryGetProperty("LineNumber", out var number)
                            || !number.TryGetInt32(out var line))
                            continue;
                        var full = File.Exists(searchPath) ? searchPath : Path.GetFullPath(Path.Combine(searchPath, name.GetString() ?? string.Empty));
                        AddRange(full, line, line);
                    }
                    break;
                }

            // Solo los tramos de código que enseña; graph_neighbors y graph_domain no aportan evidencia.
            case "graph_search":
                foreach (Match m in GraphCodePattern().Matches(content))
                {
                    if (ToFullPath(root, m.Groups["p"].Value) is { } full)
                        AddRange(full, int.Parse(m.Groups["s"].Value), int.Parse(m.Groups["e"].Value));
                }
                break;

            case "read_report":
                if (ReportLibrary.TryParseId(Argument(arguments, "file_name"), out var source))
                    Reports.Add(ReportLibrary.ToId(source));
                break;

            case "read_document":
                if (Argument(arguments, "document_id") is { } id)
                    Documents.Add(id);
                break;
        }
    }

    /// <summary>
    /// Prefijos con los que las tools devuelven un error en lugar de un resultado: <c>Error</c> (<c>view_file</c> y el
    /// framework) y <c>TOOL_ERROR</c> / <c>TOOL_CALL_ERROR</c> (el resto de FileTools, p. ej. <c>view_code_item</c>).
    /// </summary>
    private static readonly string[] ToolErrorPrefixes = ["Error", "TOOL_ERROR", "TOOL_CALL_ERROR"];

    /// <summary>Un resultado de error no es evidencia: no se leyó nada.</summary>
    internal static bool IsToolError(string content)
    {
        var text = content.TrimStart();
        return ToolErrorPrefixes.Any(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Argument(string arguments, string name)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Ruta completa de una ruta citada o leída, con las mismas reglas que FileTools: relativa a la raíz, con o sin
    /// <c>/workspace/</c>, <c>file:///</c> o absoluta. Null si queda fuera del repositorio.
    /// </summary>
    public static string? ToFullPath(string root, string raw)
    {
        var path = raw.Trim();
        if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            path = path["file:///".Length..];
        if (path.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("\\workspace\\", StringComparison.OrdinalIgnoreCase))
            path = path["/workspace/".Length..];
        if (path.Length == 0)
            return null;

        string full;
        try
        {
            full = Path.IsPathRooted(path) && !path.StartsWith('/') && !path.StartsWith('\\')
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path.TrimStart('/', '\\')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return IsInside(root, full) ? full : null;
    }

    public static bool IsInside(string root, string full)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"File Path: `file:///(?<p>[^`]+)`")]
    private static partial Regex ViewFilePathPattern();

    [GeneratedRegex(@"Showing lines (?<s>\d+) to (?<e>\d+)")]
    private static partial Regex ViewFileLinesPattern();

    /// <summary>Cabecera de cada nodo que devuelve <c>view_code_item</c>: <c>=== nodo ===</c> y <c>Location: fichero:ini-fin</c>.</summary>
    [GeneratedRegex(@"^=== .+ ===\r?\nLocation: (?<p>.+):(?<s>\d+)-(?<e>\d+)\r?$", RegexOptions.Multiline)]
    private static partial Regex CodeItemLocationPattern();

    /// <summary>Cabecera de cada tramo que enseña <c>graph_search</c> con <c>include_code</c>.</summary>
    [GeneratedRegex(@"^#### Código · `(?<p>[^`]+):(?<s>\d+)-(?<e>\d+)`", RegexOptions.Multiline)]
    private static partial Regex GraphCodePattern();
}
