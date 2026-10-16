using Microsoft.CodeAnalysis;

namespace DotNetEnricher;

/// <summary>
/// Construcción unificada de qualified_names siguiendo la convención file-path del indexador
/// base (codebase-memory-mcp): <c>{project}.{ruta_relativa_dotted_sin_.cs}.{TypeName}[.{MethodName}]</c>.
///
/// ⚠️ CRÍTICO para clases parciales: cbm usa la RUTA DEL FICHERO, no el namespace declarado en C#.
/// Dos ficheros parciales de la misma clase (<c>Foo.cs</c> y <c>Foo.Steps.cs</c>) generan QN distintos.
/// El método usa la syntaxRef del propio símbolo para respetar esto.
///
/// Solo sirve para LOCALIZAR nodos del grafo base. Los QN propios con prefijo del enriquecedor
/// (<c>_di_</c>, <c>_proj_</c>, <c>_flow_</c>, <c>__route__</c>) NO usan esta clase.
/// </summary>
public static class SymbolQn
{
    /// <summary>
    /// QN de un tipo a partir de la ruta de su primera <see cref="ISymbol.DeclaringSyntaxReferences"/>.
    /// Formato: <c>{project}.{dotted}.{type.Name}</c> (el "dotted" ya termina en el nombre de fichero,
    /// por eso los tipos cuyo fichero se llama igual que la clase quedan con el nombre duplicado).
    /// Devuelve <c>null</c> si el símbolo no tiene syntaxRef o si repoRoot está vacío: el formato base
    /// de cbm es SIEMPRE file-path, no se cae a namespace.
    /// </summary>
    public static string? ForType(INamedTypeSymbol type, string project, string repoRoot)
    {
        var filePath = type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        var prefix   = FilePathToQnPrefix(filePath, repoRoot);
        return prefix is null ? null : $"{project}.{prefix}.{TypeChain(type)}";
    }

    /// <summary>
    /// Nombre del tipo con los tipos que lo contienen: cbm nombra un tipo anidado con toda la cadena
    /// (<c>{fichero}.Outer.Inner</c>). Antes solo se usaba <c>Inner</c> y no casaban ni el tipo ni sus métodos.
    /// </summary>
    private static string TypeChain(INamedTypeSymbol type)
    {
        var chain = type.Name;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            chain = $"{outer.Name}.{chain}";
        return chain;
    }

    /// <summary>
    /// Nombre del nodo de cbm para un método: el del tipo en un constructor (Roslyn da <c>.ctor</c>) y el del miembro de la
    /// interfaz en una implementación explícita (Roslyn da <c>Ns.IFoo.Bar</c>; cbm, <c>Bar</c>).
    /// </summary>
    public static string GraphName(IMethodSymbol m) => m.MethodKind switch
    {
        MethodKind.Constructor => m.ContainingType.Name,
        MethodKind.ExplicitInterfaceImplementation => m.ExplicitInterfaceImplementations.FirstOrDefault()?.Name
                                                      ?? m.Name[(m.Name.LastIndexOf('.') + 1)..],
        _ => m.Name
    };

    /// <summary>
    /// QN de un método usando la syntaxRef DEL MÉTODO (no la del tipo; importa para clases parciales).
    /// Formato: <c>{project}.{dotted-del-fichero-del-método}.{tipos contenedores}.{ContainingType.Name}.{m.Name}</c>.
    /// Devuelve <c>null</c> si no hay syntaxRef, si no tiene tipo contenedor o si repoRoot está vacío.
    /// </summary>
    public static string? ForMethod(IMethodSymbol m, string project, string repoRoot)
    {
        if (m.ContainingType is null) return null;
        var filePath = m.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        var prefix   = FilePathToQnPrefix(filePath, repoRoot);
        if (prefix is null) return null;

        // cbm nombra el constructor de instancia C# con el NOMBRE DE LA CLASE, no ".ctor"
        // (p.ej. AsyncAlertService.AsyncAlertService.AsyncAlertService). El símbolo Roslyn de un
        // constructor devuelve ".ctor" en .Name, así que hay que sustituirlo por el nombre del tipo
        // para que el QN case con el nodo del grafo base.
        // Las implementaciones explícitas de interfaz, por el nombre del miembro (ver GraphName).
        return $"{project}.{prefix}.{TypeChain(m.ContainingType)}.{GraphName(m)}";
    }

    /// <summary>
    /// QN del nodo <c>File</c> de cbm: <c>{project}.{dotted}.__file__</c>. Null si la ruta no es resoluble.
    /// </summary>
    public static string? ForFile(string absolutePath, string project, string repoRoot)
    {
        var prefix = FilePathToQnPrefix(absolutePath, repoRoot);
        return prefix is null ? null : $"{project}.{prefix}.__file__";
    }

    /// <summary>
    /// Relativiza una ruta absoluta contra repoRoot y normaliza separadores a '/'.
    /// (Movido desde <c>SemanticMethodEnricher.RelativizePath</c>.)
    /// </summary>
    public static string Relativize(string absolutePath, string repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(absolutePath))
            return absolutePath.Replace('\\', '/');

        return Path.GetRelativePath(repoRoot, absolutePath).Replace('\\', '/');
    }

    /// <summary>
    /// Ruta relativa "dotted" (sin extensión .cs) o <c>null</c> si no es resoluble
    /// (sin ruta, repoRoot vacío o ruta fuera del repo).
    /// </summary>
    private static string? FilePathToQnPrefix(string? absolutePath, string repoRoot)
    {
        if (string.IsNullOrEmpty(absolutePath) || string.IsNullOrEmpty(repoRoot)) return null;

        var rel = Path.GetRelativePath(repoRoot, absolutePath);
        if (rel.StartsWith("..")) return null;
        if (rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            rel = rel[..^3];

        return rel.Replace('\\', '.').Replace('/', '.');
    }
}
