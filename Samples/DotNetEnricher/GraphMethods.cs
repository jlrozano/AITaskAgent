using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher;

/// <summary>
/// Los métodos que tienen nodo <c>Method</c> en el grafo de cbm: los ordinarios, los constructores de instancia declarados
/// (cbm los nombra con el nombre de la clase) y las implementaciones explícitas de interfaz (con el nombre del miembro).
/// Antes cada enriquecedor filtraba por su cuenta: <c>SemanticMethodEnricher</c>, <c>ContractEnricher</c> y
/// <c>AttributeEnricher</c> dejaban fuera los constructores, y todos las implementaciones explícitas, así que esos nodos se
/// quedaban sin <c>accessibility</c>, contrato ni atributos.
/// </summary>
internal static class GraphMethods
{
    public static bool HasNode(IMethodSymbol method) =>
        !method.IsImplicitlyDeclared
        && method.AssociatedSymbol is null
        && method.MethodKind switch
        {
            MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation => true,
            // El constructor primario (class A(int x)) se declara en el tipo: cbm no le da nodo.
            MethodKind.Constructor => method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ConstructorDeclarationSyntax,
            _ => false
        };

    /// <summary>
    /// Implementación explícita que comparte nodo con un método ordinario del mismo tipo y nombre (<c>IStep.ExecuteAsync</c>
    /// junto a un <c>protected abstract ExecuteAsync</c>): las propiedades de método del nodo son las del ordinario, que es
    /// el que se ve en el código; la explícita solo suma contrato (<c>overloads</c>) y aristas.
    /// </summary>
    public static bool IsShadowedExplicitImpl(IMethodSymbol method, string project, string repoRoot)
    {
        if (method.MethodKind != MethodKind.ExplicitInterfaceImplementation) return false;
        var qn = SymbolQn.ForMethod(method, project, repoRoot);
        return method.ContainingType.GetMembers(SymbolQn.GraphName(method)).OfType<IMethodSymbol>()
            .Any(o => o.MethodKind == MethodKind.Ordinary && !o.IsImplicitlyDeclared
                      && SymbolQn.ForMethod(o, project, repoRoot) == qn);
    }
}
