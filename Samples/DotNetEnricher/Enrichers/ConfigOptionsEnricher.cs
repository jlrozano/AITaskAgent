using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Detecta clases de configuración (*Options, *Config, *Settings, *Configuration)
/// y llamadas Configure&lt;T&gt;(section) para enriquecer los nodos Class existentes
/// como ciudadanos de primera clase en el grafo de configuración.
///
/// Genérico: funciona sobre cualquier solución C# que use el patrón IOptions&lt;T&gt;.
/// </summary>
public class ConfigOptionsEnricher : IGraphEnricher
{
    public string Name  => "ConfigOptionsEnricher";
    public int    Order => 10;

    private static readonly string[] ConfigSuffixes =
        ["Options", "Config", "Settings", "Configuration"];

    private static readonly HashSet<string> ConfigureMethods =
        ["Configure", "AddOptions", "PostConfigure", "ConfigureAll"];

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        foreach (var compilation in ctx.Compilations)
            await EnrichCompilationAsync(compilation, ctx.Repository, ctx.Project, ctx.RepoRoot, filter, progress);
    }

    private static async Task EnrichCompilationAsync(
        Compilation compilation, GraphRepository graph, string project, string repoRoot,
        CbmExclusionFilter filter, IProgress<string>? progress)
    {
        var enrichedAt   = DateTime.UtcNow.ToString("O");
        int classesMarked = 0, sectionsLinked = 0, edgesCreated = 0;

        // ─── Paso 1: marcar clases *Options/*Config/*Settings/*Configuration ───
        foreach (var tree in compilation.SyntaxTrees
            .Where(t => !filter.ShouldExclude(t.FilePath)))
        {
            var model = compilation.GetSemanticModel(tree);
            var root  = tree.GetRoot();

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(classDecl);
                if (symbol is null || !IsConfigClass(symbol.Name)) continue;

                var qn = SymbolQn.ForType(symbol, project, repoRoot);
                if (qn is null) continue;
                var nodeId = await graph.GetNodeIdAsync(project, qn);

                var configProps   = ExtractConfigProperties(classDecl);
                var hasValidation = HasValidationAttributes(classDecl);

                if (nodeId.HasValue)
                {
                    await graph.PatchNodePropertiesByIdAsync(nodeId.Value, new
                    {
                        is_config_class   = true,
                        config_properties = configProps,
                        has_validation    = hasValidation,
                        enriched_by       = new[] { "ConfigOptionsEnricher" },
                        enriched_at       = enrichedAt
                    });
                }
                else
                {
                    var filePath  = SymbolQn.Relativize(classDecl.SyntaxTree.FilePath, repoRoot);
                    var startLine = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var endLine   = classDecl.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

                    await graph.UpsertNodeAsync(
                        project, "ConfigClass",
                        symbol.Name, qn, filePath, startLine,
                        new
                        {
                            is_config_class   = true,
                            config_properties = configProps,
                            has_validation    = hasValidation,
                            origin            = "enricher",
                            enriched_by       = new[] { "ConfigOptionsEnricher" },
                            enriched_at       = enrichedAt,
                            presentational    = true
                        },
                        endLine);
                }
                classesMarked++;
            }
        }

        // ─── Paso 2: detectar Configure<T>(section) y AddOptions<T>() ─────────
        foreach (var tree in compilation.SyntaxTrees
            .Where(t => !filter.ShouldExclude(t.FilePath)))
        {
            var model = compilation.GetSemanticModel(tree);
            var root  = tree.GetRoot();

            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol method) continue;
                if (!ConfigureMethods.Contains(method.Name))                     continue;
                if (method.TypeArguments.Length != 1)                            continue;

                var optionsType = method.TypeArguments[0];
                if (optionsType is not INamedTypeSymbol optionsNamed) continue;
                if (!IsConfigClass(optionsNamed.Name)) continue;

                var qn = SymbolQn.ForType(optionsNamed, project, repoRoot);
                if (qn is null) continue;
                var targetNodeId = await graph.GetNodeIdAsync(project, qn);
                if (!targetNodeId.HasValue) continue;

                var configSection = ExtractConfigSection(inv);

                if (configSection is not null)
                {
                    await graph.PatchNodePropertiesByIdAsync(targetNodeId.Value, new
                    {
                        config_section = configSection,
                        enriched_by    = new[] { "ConfigOptionsEnricher" },
                        enriched_at    = enrichedAt
                    });
                    sectionsLinked++;
                }

                var registrarQn = GetContainingClassQn(inv, model, project, repoRoot);
                if (registrarQn is not null)
                {
                    var sourceNodeId = await graph.GetNodeIdAsync(project, registrarQn);
                    if (sourceNodeId.HasValue)
                    {
                        await graph.UpsertEdgeAsync(project, sourceNodeId.Value, targetNodeId.Value,
                            "OPTIONS_CONFIGURES", new
                            {
                                config_section      = configSection,
                                registration_method = method.Name,
                                origin              = "enricher",
                                enriched_by         = new[] { "ConfigOptionsEnricher" },
                                enriched_at         = enrichedAt,
                                presentational      = true
                            });
                        edgesCreated++;
                    }
                }
            }
        }

        progress?.Report(
            $"ConfigOptions: {classesMarked} clases marcadas, " +
            $"{sectionsLinked} secciones vinculadas, {edgesCreated} aristas CONFIGURES.");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static bool IsConfigClass(string name) =>
        ConfigSuffixes.Any(s =>
            name.EndsWith(s, StringComparison.Ordinal) && name.Length > s.Length);

    private static object[] ExtractConfigProperties(ClassDeclarationSyntax classDecl)
    {
        return classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(p =>
            {
                var propName   = p.Identifier.Text;
                var propType   = p.Type.ToString();
                string? defaultVal = p.Initializer?.Value switch
                {
                    LiteralExpressionSyntax lit => lit.Token.Value?.ToString(),
                    DefaultExpressionSyntax     => "default",
                    _                           => null
                };
                return (object)new Dictionary<string, object?>
                {
                    ["name"]          = propName,
                    ["type"]          = propType,
                    ["default_value"] = defaultVal
                };
            })
            .ToArray();
    }

    private static bool HasValidationAttributes(ClassDeclarationSyntax classDecl)
    {
        var validationAttrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Range", "Required", "MinLength", "MaxLength", "RegularExpression", "Url", "EmailAddress" };

        return classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Any(p => p.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => validationAttrs.Contains(a.Name.ToString().Split('.').Last())));
    }

    private static string? ExtractConfigSection(InvocationExpressionSyntax inv)
    {
        foreach (var arg in inv.ArgumentList.Arguments)
        {
            if (arg.Expression is InvocationExpressionSyntax innerInv)
            {
                if (innerInv.Expression is MemberAccessExpressionSyntax ma &&
                    ma.Name.Identifier.Text == "GetSection" &&
                    innerInv.ArgumentList.Arguments.Count == 1)
                {
                    var sectionArg = innerInv.ArgumentList.Arguments[0].Expression;
                    if (sectionArg is LiteralExpressionSyntax lit)
                        return lit.Token.Value?.ToString();
                    if (sectionArg is InvocationExpressionSyntax nameofInv &&
                        nameofInv.Expression.ToString() == "nameof" &&
                        nameofInv.ArgumentList.Arguments.Count == 1)
                        return nameofInv.ArgumentList.Arguments[0].Expression.ToString();
                }
            }

            if (arg.Expression is LiteralExpressionSyntax directLit)
                return directLit.Token.Value?.ToString();
        }
        return null;
    }

    private static string? GetContainingClassQn(SyntaxNode node, SemanticModel model, string project, string repoRoot)
    {
        var classDecl = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl is null) return null;

        if (model.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol symbol) return null;

        return SymbolQn.ForType(symbol, project, repoRoot);
    }
}
