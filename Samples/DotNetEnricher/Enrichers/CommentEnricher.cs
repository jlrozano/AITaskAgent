using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Lo que dicen los comentarios, con la trivia de Roslyn en lugar de los <c>//</c> que extrae cbm:
///
///   - <c>docstring</c> de tipos y métodos, desde la documentación XML completa (resumen, observaciones, parámetros,
///     retorno y excepciones, con <c>see cref</c> y <c>paramref</c> resueltos) o, si no la hay, el comentario pegado a la
///     declaración; <c>docstring_source</c> dice cuál. El texto sale decodificado con la codificación real del fichero.
///   - <c>todo_markers</c>: <c>[{kind, line, text}]</c> con las marcas TODO/FIXME/HACK (en mayúsculas) y <c>todo_count</c>.
///   - <c>commented_code</c>: <c>[{start_line, end_line, lines, preview}]</c> con los comentarios que parsean como C# sin
///     errores y no son prosa, y <c>commented_code_lines</c>.
///   - <c>encoding</c> en el nodo File: la codificación con la que Roslyn leyó el fichero.
///
/// Las marcas y el código comentado van al método que contiene el comentario (o al que sigue, si está justo encima); si
/// no hay método con nodo, al tipo, y si no, al nodo File del fichero.
/// </summary>
public class CommentEnricher : IGraphEnricher
{
    public string Name  => "CommentEnricher";
    public int    Order => 9;

    private const int MaxMarkerText = 200;
    private const int MaxPreview = 120;

    private sealed class Findings
    {
        public List<Dictionary<string, object?>> Todos { get; } = [];
        public List<Dictionary<string, object?>> Code { get; } = [];
    }

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph = ctx.Repository;
        var project = ctx.Project;

        // Idempotencia: el parche solo suma, así que se retira lo de la pasada anterior antes de recalcular.
        await graph.ExecuteAsync($"""
            UPDATE nodes SET properties = json_remove(properties,
                '$.todo_markers','$.todo_count','$.commented_code','$.commented_code_lines')
            WHERE project = @project AND {LanguageScope.NotJava()}
              AND (json_type(properties,'$.todo_count') IS NOT NULL OR json_type(properties,'$.commented_code_lines') IS NOT NULL)
            """, new { project });

        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        int docstrings = 0, todos = 0, codeBlocks = 0, files = 0;
        // Acumulado de todo el repositorio: un tipo parcial puede tener comentarios en varios ficheros.
        var findings = new Dictionary<long, Findings>();

        foreach (var compilation in ctx.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = compilation.GetSemanticModel(tree);
                var text = tree.GetText();
                var root = await tree.GetRootAsync();
                files++;

                var fileQn = SymbolQn.ForFile(tree.FilePath, project, ctx.RepoRoot);
                if (fileQn is not null && text.Encoding is { } encoding)
                    await graph.PatchNodePropertiesAsync(project, fileQn, new { encoding = encoding.WebName });

                foreach (var block in CommentScanner.Blocks(root, text))
                {
                    var markers = Markers(block).ToList();
                    // Un comentario al final de una línea de código («x = 1; // foo()») no es código comentado.
                    var runs = block.OwnLine ? CommentScanner.CodeRuns(block).ToList() : [];
                    if (markers.Count == 0 && runs.Count == 0) continue;

                    var ownerId = await OwnerAsync(block.First, model, fileQn, ctx);
                    if (ownerId is null) continue;
                    if (!findings.TryGetValue(ownerId.Value, out var f)) findings[ownerId.Value] = f = new Findings();
                    f.Todos.AddRange(markers);
                    foreach (var (start, end, preview) in runs)
                    {
                        f.Code.Add(new Dictionary<string, object?>
                        {
                            ["start_line"] = start,
                            ["end_line"]   = end,
                            ["lines"]      = end - start + 1,
                            ["preview"]    = Truncate(preview, MaxPreview)
                        });
                    }
                }

                docstrings += await WriteDocstringsAsync(root, model, ctx);
            }
        }

        foreach (var (id, f) in findings)
        {
            var patch = new Dictionary<string, object?>();
            if (f.Todos.Count > 0)
            {
                patch["todo_markers"] = f.Todos;
                patch["todo_count"]   = f.Todos.Count;
            }
            if (f.Code.Count > 0)
            {
                patch["commented_code"]       = f.Code;
                patch["commented_code_lines"] = f.Code.Sum(c => (int)c["lines"]!);
            }
            await graph.PatchNodePropertiesByIdAsync(id, patch);
            todos += f.Todos.Count;
            codeBlocks += f.Code.Count;
        }


        progress?.Report($"Comments: {docstrings} docstrings, {todos} TODO/FIXME/HACK, {codeBlocks} commented-code block(s) in {files} file(s).");
    }

    private static IEnumerable<Dictionary<string, object?>> Markers(CommentBlock block)
    {
        for (var i = 0; i < block.Lines.Count; i++)
        {
            if (CommentScanner.Marker(block.Lines[i]) is not var (kind, text)) continue;
            yield return new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["line"] = block.StartLine + i,
                ["text"] = Truncate(text, MaxMarkerText)
            };
        }
    }

    /// <summary>Nodo que recibe el hallazgo: el método o constructor que contiene la trivia, si no su tipo, si no el fichero.</summary>
    private static async Task<long?> OwnerAsync(SyntaxTrivia trivia, SemanticModel model, string? fileQn, EnrichmentContext ctx)
    {
        if (await OwnerNodes.ForAsync(trivia.Token.Parent, model, ctx) is { } id) return id;
        return fileQn is null ? null : await ctx.Repository.GetNodeIdAsync(ctx.Project, fileQn);
    }

    private static async Task<int> WriteDocstringsAsync(SyntaxNode root, SemanticModel model, EnrichmentContext ctx)
    {
        int written = 0;
        foreach (var declaration in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax))
        {
            string? qn = model.GetDeclaredSymbol(declaration) switch
            {
                INamedTypeSymbol type => SymbolQn.ForType(type, ctx.Project, ctx.RepoRoot),
                IMethodSymbol method when GraphMethods.HasNode(method) => SymbolQn.ForMethod(method, ctx.Project, ctx.RepoRoot),
                _ => null
            };
            if (qn is null) continue;

            var (text, source) = DocText.For(declaration);
            if (text is null) continue;
            // Sin registrar fallo si el nodo no existe: un método de un fichero que cbm no indexó no es un QN mal resuelto.
            if (await ctx.Repository.PatchNodePropertiesAsync(ctx.Project, qn, new { docstring = text, docstring_source = source }) > 0)
                written++;
        }
        return written;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
