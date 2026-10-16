using Microsoft.CodeAnalysis;

namespace DotNetEnricher;

/// <summary>
/// Correspondencia entre los símbolos del código fuente y los nodos del grafo de cbm, por el qualified_name de
/// <see cref="SymbolQn"/>. Se carga con una consulta de ids y un recorrido de los símbolos declarados, no con una consulta
/// por símbolo. Las sobrecargas comparten nodo (cbm nombra el método sin su firma), así que varios métodos pueden dar el
/// mismo id.
/// </summary>
internal sealed class GraphSymbols
{
    private static readonly string[] Labels = ["Method", "Function", "Class", "Interface", "Enum", "Field"];

    private readonly Dictionary<SymbolKey, long> _methods = [];
    private readonly Dictionary<SymbolKey, long> _types = [];

    /// <summary>Métodos, constructores e implementaciones explícitas del código fuente por nodo del grafo (<see cref="GraphMethods"/>).</summary>
    public Dictionary<long, List<IMethodSymbol>> MethodsByNode { get; } = [];

    /// <summary>Tipos del código fuente por nodo del grafo.</summary>
    public Dictionary<long, INamedTypeSymbol> TypesByNode { get; } = [];

    public static async Task<GraphSymbols> LoadAsync(EnrichmentContext ctx)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in await ctx.Repository.QueryAsync(
            "SELECT id, qualified_name FROM nodes WHERE project = @project AND label IN @labels",
            new { project = ctx.Project, labels = Labels }))
            ids[(string)row.qualified_name] = (long)row.id;

        var symbols = new GraphSymbols();
        foreach (var compilation in ctx.Compilations)
            symbols.Walk(compilation.Assembly.GlobalNamespace, ids, ctx);
        return symbols;
    }

    private void Walk(INamespaceOrTypeSymbol container, Dictionary<string, long> ids, EnrichmentContext ctx)
    {
        foreach (var member in container.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol ns:
                    Walk(ns, ids, ctx);
                    break;
                case INamedTypeSymbol type when !type.IsImplicitlyDeclared && type.DeclaringSyntaxReferences.Length > 0:
                    Register(type, ids, ctx);
                    Walk(type, ids, ctx);
                    break;
            }
        }
    }

    private void Register(INamedTypeSymbol type, Dictionary<string, long> ids, EnrichmentContext ctx)
    {
        if (SymbolKey.Of(type) is not { } typeKey) return;
        if (SymbolQn.ForType(type, ctx.Project, ctx.RepoRoot) is { } typeQn && ids.TryGetValue(typeQn, out var typeId))
        {
            _types[typeKey] = typeId;
            TypesByNode.TryAdd(typeId, type);
        }

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (!GraphMethods.HasNode(method) || SymbolKey.Of(method) is not { } methodKey)
                continue;
            if (SymbolQn.ForMethod(method, ctx.Project, ctx.RepoRoot) is not { } qn || !ids.TryGetValue(qn, out var id))
                continue;
            _methods[methodKey] = id;
            if (!MethodsByNode.TryGetValue(id, out var list)) MethodsByNode[id] = list = [];
            list.Add(method);
        }
    }

    /// <summary>Nodo propio del método, sin caer al tipo: null si el método no tiene nodo.</summary>
    public long? NodeOfMethod(IMethodSymbol method) =>
        SymbolKey.Of(method) is { } key && _methods.TryGetValue(key, out var id) ? id : null;

    /// <summary>Nodo de un símbolo referenciado: el método, o el tipo que declara al miembro (propiedad, campo, evento).</summary>
    public long? NodeOfTarget(ISymbol symbol)
    {
        if (symbol is IMethodSymbol && SymbolKey.Of(symbol) is { } key && _methods.TryGetValue(key, out var method))
            return method;
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        return type is not null && SymbolKey.Of(type) is { } typeKey && _types.TryGetValue(typeKey, out var id) ? id : null;
    }

    /// <summary>
    /// Nodo del miembro que contiene una referencia: el método, o el tipo si el miembro no tiene nodo propio (propiedad,
    /// campo, constructor estático…). Null si no hay nodo (código de nivel superior, tipo fuera del grafo): quien
    /// referencia no es alcanzable ni inalcanzable, y lo que nombra se trata como vivo.
    /// </summary>
    public long? NodeOfContainer(ISymbol? container)
    {
        if (container is null) return null;
        if (container is IMethodSymbol && SymbolKey.Of(container) is { } key && _methods.TryGetValue(key, out var method))
            return method;
        var type = container as INamedTypeSymbol ?? container.ContainingType;
        return type is not null && SymbolKey.Of(type) is { } typeKey && _types.TryGetValue(typeKey, out var id) ? id : null;
    }
}
