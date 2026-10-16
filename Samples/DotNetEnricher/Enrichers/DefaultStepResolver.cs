using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// No-op resolver — falls back to normal Roslyn semantic model resolution.
/// </summary>
public class DefaultStepResolver : IStepResolver
{
    public IMethodSymbol? TryResolve(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Compilation compilation) => null;
}
