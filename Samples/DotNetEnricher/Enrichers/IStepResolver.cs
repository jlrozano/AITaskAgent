using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Hook for resolving indirect call targets (e.g. delegate-based dispatch).
/// Return null to fall back to normal Roslyn resolution.
/// </summary>
public interface IStepResolver
{
    IMethodSymbol? TryResolve(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        Compilation compilation);
}
