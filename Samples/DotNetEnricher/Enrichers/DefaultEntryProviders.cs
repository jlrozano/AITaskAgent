using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Detects BackgroundService subclasses and returns their ExecuteAsync / StartAsync
/// methods as flow entry points with kind "background_service".
/// </summary>
public class BackgroundServiceEntryProvider : IFlowEntryProvider
{
    public IEnumerable<(IMethodSymbol Method, string FlowKind)> GetEntryPoints(
        Compilation compilation,
        string project,
        string repoRoot,
        GraphRepository repository)
    {
        var filter = new CbmExclusionFilter(repoRoot);
        var results = new List<(IMethodSymbol, string)>();

        foreach (var tree in compilation.SyntaxTrees
            .Where(t => !filter.ShouldExclude(t.FilePath)))
        {
            var model = compilation.GetSemanticModel(tree);

            foreach (var classDecl in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol typeSymbol) continue;
                if (!IsBackgroundServiceType(typeSymbol)) continue;

                foreach (var member in classDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (member.Identifier.Text is not ("ExecuteAsync" or "StartAsync")) continue;
                    if (model.GetDeclaredSymbol(member) is not IMethodSymbol methodSymbol) continue;

                    results.Add((methodSymbol, "background_service"));
                }
            }
        }

        return results;
    }

    private static bool IsBackgroundServiceType(INamedTypeSymbol? type)
    {
        if (type is null) return false;
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (b.Name is "BackgroundService") return true;
        return type.AllInterfaces.Any(i => i.Name is "IHostedService");
    }
}

/// <summary>
/// Queries the graph for HANDLES edges and resolves the target method symbols,
/// returning them as flow entry points with kind "http_endpoint".
/// </summary>
public class HandlesEntryProvider : IFlowEntryProvider
{
    public IEnumerable<(IMethodSymbol Method, string FlowKind)> GetEntryPoints(
        Compilation compilation,
        string project,
        string repoRoot,
        GraphRepository repository)
    {
        // Load target QNs from the graph synchronously (providers are called from sync context)
        var targetQns = GetHandlesTargetQnsAsync(repository, project).GetAwaiter().GetResult();
        if (targetQns.Count == 0) return [];

        var filter  = new CbmExclusionFilter(repoRoot);
        var results = new List<(IMethodSymbol, string)>();

        foreach (var tree in compilation.SyntaxTrees
            .Where(t => !filter.ShouldExclude(t.FilePath)))
        {
            var model = compilation.GetSemanticModel(tree);

            foreach (var methodDecl in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(methodDecl) is not IMethodSymbol methodSymbol) continue;

                // Build QN via SymbolQn (file-path based, igual que FlowEnricher)
                var qn = SymbolQn.ForMethod(methodSymbol, project, repoRoot);
                if (qn is not null && targetQns.Contains(qn))
                    results.Add((methodSymbol, "http_endpoint"));
            }
        }

        return results;
    }

    private static async Task<HashSet<string>> GetHandlesTargetQnsAsync(
        GraphRepository repository, string project)
    {
        var qns = await repository.GetHandlesTargetQnsAsync(project);
        return new HashSet<string>(qns, StringComparer.Ordinal);
    }
}
