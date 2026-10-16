using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DotNetEnricher;

/// <summary>
/// Identidad estable de un símbolo declarado en el código fuente: clase de símbolo, fichero y posición de su primera
/// declaración. Sirve de clave entre compilaciones distintas (un proyecto que referencia a otro ve los símbolos del
/// segundo por su compilación, que no siempre es la misma instancia) y no existe para los símbolos de metadatos, que
/// quedan fuera del índice.
/// </summary>
internal readonly record struct SymbolKey(SymbolKind Kind, string File, int Start)
{
    public static SymbolKey? Of(ISymbol? symbol)
    {
        if (symbol is null) return null;
        var reference = Canonical(symbol).DeclaringSyntaxReferences.FirstOrDefault();
        return reference is null ? null : new SymbolKey(symbol.Kind, reference.SyntaxTree.FilePath, reference.Span.Start);
    }

    /// <summary>El símbolo sin instanciar: la definición genérica, y el método estático de una extensión reducida.</summary>
    public static ISymbol Canonical(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
            symbol = method.ReducedFrom ?? method;
        return symbol.OriginalDefinition;
    }
}

[Flags]
internal enum ReferenceFlags
{
    None = 0,
    Read = 1,
    Write = 2,

    /// <summary>El nombre está en la lista de bases de un tipo (<c>class A : IB</c>): declara, no usa.</summary>
    InBaseList = 4,

    /// <summary>El nombre es un argumento de tipo o un <c>typeof</c> de una llamada de registro de DI: registra, no consume.</summary>
    InDiRegistration = 8,

    /// <summary>Escritura implícita del inicializador de un campo (<c>int _x = 5;</c>); el contenedor es el tipo.</summary>
    Initializer = 16,

    // Cómo se nombra un método (las usa CallEnricher para las aristas CALLS exactas). Un nombre de método que no es
    // ninguna de ellas es una referencia como delegado (grupo de métodos).

    /// <summary>El nombre es el de la llamada: <c>Foo()</c>, <c>x.Foo()</c>, <c>x?.Foo()</c>.</summary>
    Invocation = 32,

    /// <summary>El constructor de un <c>new</c> (explícito o <c>new()</c>).</summary>
    Creation = 64,

    /// <summary>Un inicializador de constructor: <c>: base(..)</c> o <c>: this(..)</c>.</summary>
    ConstructorInitializer = 128,

    /// <summary>El constructor de un atributo: no es una llamada del código.</summary>
    Attribute = 256,

    /// <summary>Dentro de <c>nameof(..)</c>: no usa el símbolo.</summary>
    NameOf = 512,

    /// <summary>Candidato de una resolución fallida (sobrecarga ambigua, grupo de métodos sin destino): no es exacto.</summary>
    Candidate = 1024,

    /// <summary>
    /// Con <see cref="Candidate"/>: el único candidato. Pasa cuando un argumento no tiene tipo (un paquete NuGet sin
    /// restaurar): el método nombrado es ese sin duda, solo falla la comprobación de los argumentos.
    /// </summary>
    SingleCandidate = 2048
}

/// <summary>
/// Una referencia a un símbolo del código fuente. <see cref="Container"/> es el miembro que la contiene (método,
/// propiedad, campo, tipo…) y es null en el código de nivel superior (<c>Program.cs</c> sin clase) y en los atributos de
/// ensamblado. <see cref="Position"/> es el inicio del nombre en el fichero: los candidatos de una misma resolución fallida
/// comparten fichero y posición.
/// </summary>
internal readonly record struct SymbolReference(
    SymbolKey Target, ISymbol TargetSymbol, ISymbol? Container, SymbolKey? ContainerKey,
    string File, int Line, ReferenceFlags Flags, int Position = -1)
{
    /// <summary>Quien referencia, como clave: el miembro, o el fichero para el código de nivel superior.</summary>
    public SymbolKey Caller => ContainerKey ?? new SymbolKey(SymbolKind.Namespace, File, -1);
}

/// <summary>
/// Índice exacto de referencias entre símbolos del código fuente, construido de una sola pasada sobre todos los árboles con
/// el modelo semántico (un <c>GetSymbolInfo</c> por nombre). Equivale a lanzar <c>SymbolFinder.FindReferencesAsync</c> por
/// cada símbolo, pero sin cargar de nuevo la solución ni recorrer los árboles una vez por símbolo, y a diferencia de las
/// aristas USAGE/CALLS de cbm no resuelve por nombre: distingue sobrecargas, receptores y espacios de nombres.
///
/// Solo se guardan las referencias a símbolos declarados en el código fuente. Se cuentan nombres (tipos, métodos,
/// propiedades, campos, eventos), creaciones de objeto (al constructor), inicializadores de constructor, atributos e
/// indexadores. No se cuentan <c>var</c> ni los símbolos locales o parámetros.
/// </summary>
internal sealed class ReferenceIndex
{
    private readonly List<SymbolReference> _all = [];
    private readonly Dictionary<SymbolKey, List<int>> _byTarget = [];
    private readonly Dictionary<SymbolKey, List<int>> _byContainer = [];

    /// <summary>Nombres de llamadas de registro de DI: sus argumentos de tipo registran, no consumen.</summary>
    internal static bool IsDiRegistrationName(string name) =>
        (name.StartsWith("Add", StringComparison.Ordinal) || name.StartsWith("TryAdd", StringComparison.Ordinal))
        && (name.Contains("Scoped", StringComparison.Ordinal) || name.Contains("Singleton", StringComparison.Ordinal)
            || name.Contains("Transient", StringComparison.Ordinal) || name.Contains("HostedService", StringComparison.Ordinal));

    public IReadOnlyList<SymbolReference> All => _all;

    public static ReferenceIndex Build(IEnumerable<Compilation> compilations, CancellationToken ct = default)
    {
        var index = new ReferenceIndex();
        foreach (var compilation in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                var walker = new Walker(compilation.GetSemanticModel(tree), tree.GetText(ct), tree.FilePath, index);
                walker.Visit(tree.GetRoot(ct));
            }
        }
        return index;
    }

    /// <summary>Referencias al símbolo (los demás miembros y tipos que lo nombran).</summary>
    public IEnumerable<SymbolReference> To(ISymbol symbol) =>
        SymbolKey.Of(symbol) is { } key && _byTarget.TryGetValue(key, out var ids) ? ids.Select(i => _all[i]) : [];

    /// <summary>Referencias que salen del miembro: lo que su cuerpo y su firma nombran.</summary>
    public IEnumerable<SymbolReference> From(ISymbol container) =>
        SymbolKey.Of(container) is { } key && _byContainer.TryGetValue(key, out var ids) ? ids.Select(i => _all[i]) : [];

    /// <summary>
    /// Miembros que el método implementa u oculta por herencia: los de las interfaces y los que sobrescribe. Una llamada
    /// a <c>IFoo.Bar()</c> puede ejecutar <c>Foo.Bar()</c>, así que sus referencias cuentan también para la implementación
    /// (el «cascade» de <c>SymbolFinder</c>).
    /// </summary>
    public static IReadOnlyList<ISymbol> ImplementedMembers(IMethodSymbol method)
    {
        var result = new List<ISymbol>();
        for (var o = method.OverriddenMethod; o is not null; o = o.OverriddenMethod)
            result.Add(o);
        foreach (var explicitImpl in method.ExplicitInterfaceImplementations)
            result.Add(explicitImpl);
        if (method.ContainingType is { } type && method.DeclaredAccessibility == Accessibility.Public)
        {
            foreach (var iface in type.AllInterfaces)
                foreach (var member in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
                    if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method))
                        result.Add(member.OriginalDefinition);
        }
        return result;
    }

    /// <summary>
    /// Quienes usan un servicio de DI: los miembros distintos que lo nombran sin ser su lista de bases ni una llamada de
    /// registro. Incluye parámetros de constructor, <c>GetService&lt;T&gt;</c>, <c>[FromServices]</c>, <c>IEnumerable&lt;T&gt;</c>
    /// y factorías. Null si el servicio no está en el código fuente (no se sabe quién lo consume).
    /// </summary>
    public int? CountServiceConsumers(ITypeSymbol service)
    {
        if (SymbolKey.Of(service) is null) return null;
        return To(service)
            .Where(r => (r.Flags & (ReferenceFlags.InBaseList | ReferenceFlags.InDiRegistration)) == 0)
            .Select(r => r.Caller)
            .Distinct()
            .Count();
    }

    private void Add(SymbolReference reference)
    {
        var id = _all.Count;
        _all.Add(reference);
        Bucket(_byTarget, reference.Target).Add(id);
        if (reference.ContainerKey is { } container)
            Bucket(_byContainer, container).Add(id);
    }

    private static List<int> Bucket(Dictionary<SymbolKey, List<int>> map, SymbolKey key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        return list;
    }

    private sealed class Walker(SemanticModel model, SourceText text, string file, ReferenceIndex index) : CSharpSyntaxWalker
    {
        private readonly Stack<ISymbol?> _containers = new();

        private ISymbol? Container => _containers.Count == 0 ? null : _containers.Peek();

        public override void Visit(SyntaxNode? node)
        {
            if (node is null) return;
            var enters = node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax
                or ConstructorDeclarationSyntax or DestructorDeclarationSyntax or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
                or EventDeclarationSyntax
                || node is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax };
            if (enters) _containers.Push(model.GetDeclaredSymbol(node));
            base.Visit(node);
            if (enters) _containers.Pop();
        }

        public override void VisitVariableDeclarator(VariableDeclaratorSyntax node)
        {
            // La escritura del inicializador de un campo no tiene nombre que la delate: se añade aquí.
            if (node.Initializer is not null && Container is IFieldSymbol field)
                Add(field, field.ContainingType, node, ReferenceFlags.Write | ReferenceFlags.Initializer);
            base.VisitVariableDeclarator(node);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (!node.IsVar) Name(node);
            base.VisitIdentifierName(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            Name(node);
            base.VisitGenericName(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            Implicit(node, ReferenceFlags.Creation);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            Implicit(node, ReferenceFlags.Creation);
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
        {
            Implicit(node, ReferenceFlags.ConstructorInitializer);
            base.VisitConstructorInitializer(node);
        }

        public override void VisitAttribute(AttributeSyntax node)
        {
            Implicit(node, ReferenceFlags.Attribute);
            base.VisitAttribute(node);
        }

        public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            Implicit(node);
            base.VisitElementAccessExpression(node);
        }

        /// <summary>Referencias sin nombre propio: el constructor de un <c>new</c>, un inicializador <c>: base(..)</c>, el constructor de un atributo, un indexador.</summary>
        private void Implicit(SyntaxNode node, ReferenceFlags kind = ReferenceFlags.None)
        {
            var info = model.GetSymbolInfo(node);
            if (info.Symbol is { } symbol)
                Add(symbol, Container, node, ReferenceFlags.Read | kind);
            else if (kind is ReferenceFlags.Creation or ReferenceFlags.ConstructorInitializer && info.CandidateSymbols.Length == 1)
                // El constructor de un new con un argumento sin tipo (paquete sin restaurar): es ese, como en Name.
                Add(info.CandidateSymbols[0], Container, node, ReferenceFlags.Read | kind | ReferenceFlags.Candidate | ReferenceFlags.SingleCandidate);
        }

        private void Name(SimpleNameSyntax node)
        {
            var info = model.GetSymbolInfo(node);
            if (info.Symbol is { } symbol)
            {
                Add(symbol, Container, node, Flags(node, symbol) | MethodUse(node, symbol));
                return;
            }
            // Una resolución fallida (sobrecarga sin candidato único, grupo de métodos) no prueba que no se use.
            var single = info.CandidateSymbols.Length == 1 ? ReferenceFlags.SingleCandidate : ReferenceFlags.None;
            foreach (var candidate in info.CandidateSymbols)
                Add(candidate, Container, node, Flags(node, candidate) | MethodUse(node, candidate) | ReferenceFlags.Candidate | single);
        }

        /// <summary>Si el nombre de un método es el de una llamada o está dentro de un <c>nameof</c>; nada en otro caso (delegado).</summary>
        private static ReferenceFlags MethodUse(SimpleNameSyntax node, ISymbol symbol)
        {
            if (symbol is not IMethodSymbol) return ReferenceFlags.None;
            ExpressionSyntax callee = node.Parent switch
            {
                MemberAccessExpressionSyntax member when member.Name == node => member,
                MemberBindingExpressionSyntax binding when binding.Name == node => binding,
                _ => node
            };
            if (callee.Parent is InvocationExpressionSyntax invocation && invocation.Expression == callee)
                return ReferenceFlags.Invocation;
            for (var p = node.Parent; p is not null and not StatementSyntax and not MemberDeclarationSyntax; p = p.Parent)
                if (p is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
                    return ReferenceFlags.NameOf;
            return ReferenceFlags.None;
        }

        private void Add(ISymbol symbol, ISymbol? container, SyntaxNode node, ReferenceFlags flags)
        {
            if (symbol.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event))
                return;
            var canonical = SymbolKey.Canonical(symbol);
            if (SymbolKey.Of(canonical) is not { } target) return;
            index.Add(new SymbolReference(
                target, canonical, container, SymbolKey.Of(container), file,
                text.Lines.GetLinePosition(node.SpanStart).Line + 1, flags, node.SpanStart));
        }

        private ReferenceFlags Flags(SimpleNameSyntax node, ISymbol symbol)
        {
            var flags = symbol is IFieldSymbol ? Access(node) : ReferenceFlags.Read;
            if (symbol.Kind == SymbolKind.NamedType)
            {
                if (InBaseList(node)) flags |= ReferenceFlags.InBaseList;
                if (InDiRegistration(node)) flags |= ReferenceFlags.InDiRegistration;
            }
            return flags;
        }

        private static bool InBaseList(SyntaxNode node)
        {
            for (var p = node.Parent; p is not null; p = p.Parent)
            {
                if (p is BaseListSyntax) return true;
                if (p is not (QualifiedNameSyntax or AliasQualifiedNameSyntax or GenericNameSyntax or TypeArgumentListSyntax
                    or SimpleBaseTypeSyntax or NullableTypeSyntax or ArrayTypeSyntax or TupleTypeSyntax or TupleElementSyntax))
                    return false;
            }
            return false;
        }

        /// <summary>
        /// Argumento de tipo de <c>AddScoped&lt;IA, A&gt;()</c>, o <c>typeof</c> de <c>AddScoped(typeof(IA), typeof(A))</c>. Lo que
        /// hay dentro de una factoría (<c>sp =&gt; new A(sp.GetService&lt;IB&gt;())</c>) sí consume y no se marca.
        /// </summary>
        private static bool InDiRegistration(SyntaxNode node)
        {
            var p = node.Parent;
            while (p is QualifiedNameSyntax or AliasQualifiedNameSyntax or NullableTypeSyntax or ArrayTypeSyntax)
                p = p.Parent;
            switch (p)
            {
                case TypeArgumentListSyntax { Parent: GenericNameSyntax generic }:
                    var call = generic.Parent switch
                    {
                        MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax inv } member when member.Name == generic => inv,
                        InvocationExpressionSyntax inv when inv.Expression == generic => inv,
                        _ => null
                    };
                    return call is not null && IsDiRegistrationName(generic.Identifier.ValueText);
                case TypeOfExpressionSyntax { Parent: ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } } }:
                    return inv.Expression switch
                    {
                        MemberAccessExpressionSyntax m => IsDiRegistrationName(m.Name.Identifier.ValueText),
                        IdentifierNameSyntax i => IsDiRegistrationName(i.Identifier.ValueText),
                        _ => false
                    };
                default:
                    return false;
            }
        }

        /// <summary>
        /// Cómo se usa un campo: lectura, escritura o las dos (<c>+=</c>, <c>++</c>, <c>ref</c>). Por sintaxis: el destino
        /// de una asignación, un <c>out</c>/<c>ref</c> y la tupla de una deconstrucción escriben; <c>nameof</c> no hace nada.
        /// </summary>
        private static ReferenceFlags Access(SimpleNameSyntax node)
        {
            ExpressionSyntax expr = node;
            if (node.Parent is MemberAccessExpressionSyntax member && member.Name == node) expr = member;
            else if (node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node) expr = binding;

            var parent = expr.Parent;
            while (parent is ParenthesizedExpressionSyntax)
            {
                expr = (ExpressionSyntax)parent;
                parent = parent.Parent;
            }

            switch (parent)
            {
                case AssignmentExpressionSyntax assignment when assignment.Left == expr:
                    return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? ReferenceFlags.Write : ReferenceFlags.Read | ReferenceFlags.Write;
                case PrefixUnaryExpressionSyntax unary when unary.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                case PostfixUnaryExpressionSyntax post when post.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                case RefExpressionSyntax:
                    return ReferenceFlags.Read | ReferenceFlags.Write;
                case ArgumentSyntax argument:
                    if (argument.Parent is TupleExpressionSyntax tuple)
                        return tuple.Parent is AssignmentExpressionSyntax deconstruction && deconstruction.Left == tuple
                            ? ReferenceFlags.Write : ReferenceFlags.Read;
                    return argument.RefKindKeyword.Kind() switch
                    {
                        SyntaxKind.OutKeyword => ReferenceFlags.Write,
                        SyntaxKind.RefKeyword => ReferenceFlags.Read | ReferenceFlags.Write,
                        _ when argument.Parent?.Parent is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } => ReferenceFlags.None,
                        _ => ReferenceFlags.Read
                    };
                default:
                    return ReferenceFlags.Read;
            }
        }
    }
}
