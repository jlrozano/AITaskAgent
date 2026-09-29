using AITaskAgent.FileTools.CodeItems;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tools;

/// <summary>
/// Localizador de <c>view_code_item</c> que saca el rango de líneas del grafo: los nodos de tipo, método y campo del fichero
/// (por <c>file_path</c>) cuyo Qualified Name acaba en la ruta pedida, con su <c>start_line</c>/<c>end_line</c>, que en Java
/// escribe el analizador con el rango de <c>javac</c>. Con varios (sobrecargas, que el analizador crea como
/// <c>metodo(int,String)</c>), los lista con la ruta que elige cada uno, o elige por los tipos de <c>metodo(tipos)</c>
/// contra su <c>param_types</c>. No atiende los <c>.cs</c>: Roslyn, que va detrás, da el rango exacto. Lo que el grafo no
/// sabe (no hay BD, el fichero no tiene nodos, el nombre no casa o el nodo no tiene líneas) lo pregunta a
/// <paramref name="fallback"/>, que en la app es <see cref="JavaCodeItemLocator"/>.
/// </summary>
/// <param name="databasePath">La copia del grafo del análisis.</param>
/// <param name="repositoryPath">Raíz del repositorio, contra la que el grafo guarda las rutas.</param>
/// <param name="fallback">Localizador para lo que el grafo no sabe; null, ninguno.</param>
public sealed class GraphCodeItemLocator(string databasePath, string repositoryPath, ICodeItemLocator? fallback = null) : ICodeItemLocator
{
    private const int MaxAvailable = 20;

    private static readonly string[] Labels = ["Class", "Interface", "Enum", "Record", "Struct", "Trait", "Method", "Function", "Constructor", "Field"];

    private static readonly HashSet<string> TypeLabels = new(StringComparer.Ordinal) { "Class", "Interface", "Enum", "Record", "Struct", "Trait" };

    private sealed record Node(IReadOnlyList<string> Chain, bool IsType, IReadOnlyList<string>? ParameterTypes, string? Signature, int StartLine, int EndLine);

    public async ValueTask<IReadOnlyList<CodeItemLookup>?> LocateAsync(CodeItemRequest request, CancellationToken cancellationToken)
    {
        if (request.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        var nodes = await NodesAsync(request.FilePath, cancellationToken);
        var lookups = new CodeItemLookup?[request.NodePaths.Count];
        var missing = new List<int>();
        for (var i = 0; i < request.NodePaths.Count; i++)
        {
            var lookup = Lookup(nodes, CodeItemPath.Parse(request.NodePaths[i]));
            if (lookup.Matches.Count > 0)
                lookups[i] = lookup;
            else
                missing.Add(i);
        }

        if (missing.Count > 0 && fallback != null)
        {
            var asked = await fallback.LocateAsync(request with { NodePaths = [.. missing.Select(i => request.NodePaths[i])] }, cancellationToken);
            if (asked != null)
            {
                for (var k = 0; k < missing.Count && k < asked.Count; k++)
                    lookups[missing[k]] = asked[k];
            }
            else if (nodes.Count == 0)
            {
                return null;
            }
        }
        else if (missing.Count == request.NodePaths.Count && nodes.Count == 0)
        {
            return null;
        }

        IReadOnlyList<string> available = [.. nodes.Select(n => string.Join('.', n.Chain.TakeLast(n.IsType ? 1 : 2))).Distinct(StringComparer.Ordinal).Take(MaxAvailable)];
        return [.. lookups.Select(l => l is { Matches.Count: > 0 } ? l : CodeItemLookup.NotFound(l?.AvailableNodes is { Count: > 0 } a ? a : available))];
    }

    private static CodeItemLookup Lookup(IReadOnlyList<Node> nodes, CodeItemPath path)
    {
        var matches = nodes.Where(n => path.Matches(n.Chain)).ToList();
        if (matches.Any(n => n.IsType) && path.ParameterTypes == null)
            matches = [.. matches.Where(n => n.IsType)];
        else
            matches = [.. matches.Where(n => !n.IsType ? path.MatchesParameters(n.ParameterTypes) : path.ParameterTypes == null)];

        // cbm puede tener dos nodos para la misma declaración (el Method y el Function gemelo): cuenta el rango.
        var distinct = matches.GroupBy(n => (n.StartLine, n.EndLine)).Select(g => g.First()).ToList();
        return new CodeItemLookup([.. distinct.Select(n => new CodeItemMatch
        {
            StartLine = n.StartLine,
            EndLine = n.EndLine,
            NodePath = n.IsType || n.ParameterTypes == null
                ? string.Join('.', path.Segments.Count > 0 ? n.Chain.TakeLast(path.Segments.Count) : n.Chain)
                : $"{string.Join('.', n.Chain.TakeLast(path.Segments.Count))}({string.Join(',', n.ParameterTypes)})",
            Detail = n.Signature
        })], []);
    }

    /// <summary>Nodos del fichero con rango de líneas; vacío sin BD, fuera del repositorio o si la consulta falla.</summary>
    private async Task<IReadOnlyList<Node>> NodesAsync(string filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath) || string.IsNullOrWhiteSpace(repositoryPath))
            return [];

        var relative = Path.GetRelativePath(Path.GetFullPath(repositoryPath), Path.GetFullPath(filePath)).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return [];

        var db = new CbmGraphDb(databasePath);
        var rows = await db.SelectAsync(
            $"SELECT label, name, qualified_name AS qn, coalesce(nullif(start_line, 0), line_start, 0) AS s, " +
            $"coalesce(nullif(end_line, 0), line_end, 0) AS e, {CbmGraphDb.SafeProperties} AS props " +
            $"FROM nodes WHERE coalesce(nullif(file_path, ''), file) = @file AND label IN ({string.Join(", ", Labels.Select(l => $"'{l}'"))}) " +
            "ORDER BY s, qualified_name",
            p => p.AddWithValue("@file", relative), ct: ct);

        var nodes = new List<Node>();
        foreach (var row in rows)
        {
            var start = Convert.ToInt32(row["s"] ?? 0);
            var end = Convert.ToInt32(row["e"] ?? 0);
            var qn = row["qn"]?.ToString() ?? "";
            var label = row["label"]?.ToString() ?? "";
            if (start <= 0 || end < start || qn.Length == 0)
                continue;

            string? signature = null;
            IReadOnlyList<string>? parameters = null;
            string? fieldName = null;
            if (row["props"] is string props)
            {
                using var doc = JsonDocument.Parse(props);
                var root = doc.RootElement;
                if (root.TryGetProperty("signature", out var sig) && sig.ValueKind == JsonValueKind.String)
                    signature = sig.GetString();
                if (root.TryGetProperty("param_types", out var types) && types.ValueKind == JsonValueKind.Array)
                    parameters = [.. types.EnumerateArray().Select(t => JavaSignatures.SimpleType(t.GetString() ?? ""))];
                if (root.TryGetProperty("field_name", out var field) && field.ValueKind == JsonValueKind.String)
                    fieldName = field.GetString();
            }

            // El QN de una sobrecarga lleva sus tipos: metodo(int,String).
            var open = qn.IndexOf('(');
            var baseQn = open >= 0 ? qn[..open] : qn;
            if (open >= 0 && parameters == null)
                parameters = [.. JavaSignatures.SplitTopLevel(qn[(open + 1)..].TrimEnd(')')).Select(JavaSignatures.SimpleType)];

            List<string> chain;
            var name = row["name"]?.ToString() ?? "";
            if (label == "Field" && name.Length > 0 && qn.EndsWith("." + name, StringComparison.Ordinal))
            {
                // cbm guarda el campo con su inicializador en el nombre ("total = a.b"); el analizador deja field_name.
                chain = [.. qn[..^(name.Length + 1)].Split('.'), fieldName ?? name.Split('=', ' ', '\t')[0]];
            }
            else
            {
                chain = [.. baseQn.Split('.')];
            }

            // Sin param_types ni tipos en el QN, los parámetros son desconocidos (null), no una lista vacía.
            var isType = TypeLabels.Contains(label);
            nodes.Add(new Node(chain, isType, isType || label == "Field" ? null : parameters, signature, start, end));
        }
        return nodes;
    }
}
