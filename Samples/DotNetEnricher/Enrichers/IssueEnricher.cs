using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Patrones de error por método con las reglas del compilador, en lugar de un modelo que los busque leyendo código. Escribe
/// en el nodo del método (o del campo, o del tipo si no hay otro) dos propiedades, las que ya leen las facetas
/// <c>suspect_patterns</c> y <c>critical_hotspots_exceptions</c>:
///
///   - <c>detected_issues</c>: lista de textos <c>"{regla}: {descripción} (línea N)"</c>, por línea.
///   - <c>issue_rules</c>: las reglas que saltan, sin repetir.
///
/// Reglas del compilador (<c>Compilation.GetDiagnostics()</c>): <c>null-deref</c> (CS8602), <c>unused-field</c> (CS0414,
/// CS0169, en el nodo Field), <c>unawaited-task</c> (CS4014), <c>unreachable-code</c> (CS0162), <c>obsolete-api</c>
/// (CS0618, CS0612). Reglas propias sobre la sintaxis y las operaciones: <c>async-void</c> (fuera de un manejador de
/// eventos), <c>empty-catch</c> (catch vacío, o que descarta la excepción de un <c>catch (Exception)</c> sin registrarla ni
/// relanzarla; no el de una cancelación), <c>blocking-async</c> (<c>.Result</c>, <c>.Wait()</c>,
/// <c>GetAwaiter().GetResult()</c>), <c>undisposed-resource</c> (un <c>IDisposable</c> creado con <c>new</c> que ni se cierra
/// ni se entrega a nadie) y <c>captive-dependency</c> (un Singleton cuyo constructor pide un servicio Scoped o Transient,
/// cruzando con los registros de <see cref="DiEnricher"/>).
/// </summary>
public class IssueEnricher : IGraphEnricher
{
    public string Name  => "IssueEnricher";
    public int    Order => 7;

    private sealed record Issue(string Rule, string Text, int Line);

    private static readonly HashSet<string> DiagnosticIds = ["CS8602", "CS0414", "CS0169", "CS4014", "CS0162", "CS0618", "CS0612"];

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph = ctx.Repository;
        var project = ctx.Project;

        // Idempotencia: el parche solo suma, así que se retira lo de la pasada anterior antes de recalcular.
        await graph.ExecuteAsync($"""
            UPDATE nodes SET properties = json_remove(properties, '$.detected_issues', '$.issue_rules')
            WHERE project = @project AND json_type(properties, '$.detected_issues') IS NOT NULL AND {LanguageScope.NotJava()}
            """, new { project });

        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        var issues = new Dictionary<long, List<Issue>>();

        void Add(long? nodeId, Issue issue)
        {
            if (nodeId is not { } id) return;
            if (!issues.TryGetValue(id, out var list)) issues[id] = list = [];
            if (!list.Contains(issue)) list.Add(issue);
        }

        foreach (var compilation in ctx.Compilations)
        {
            var models = new Dictionary<SyntaxTree, SemanticModel>();
            SemanticModel ModelOf(SyntaxTree tree) =>
                models.TryGetValue(tree, out var m) ? m : models[tree] = compilation.GetSemanticModel(tree);

            // Diagnósticos del compilador.
            foreach (var diagnostic in ctx.DiagnosticsOf(compilation))
            {
                if (!DiagnosticIds.Contains(diagnostic.Id) || diagnostic.Location.SourceTree is not { } tree
                    || filter.ShouldExclude(tree.FilePath))
                    continue;
                var node = (await tree.GetRootAsync()).FindNode(diagnostic.Location.SourceSpan);
                var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                Add(await OwnerNodes.ForAsync(node, ModelOf(tree), ctx), FromDiagnostic(diagnostic, node, line));
            }

            // Reglas sobre la sintaxis y las operaciones.
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = ModelOf(tree);
                var text = await tree.GetTextAsync();
                foreach (var node in (await tree.GetRootAsync()).DescendantNodes())
                {
                    foreach (var issue in Rules(node, model, text))
                        Add(await OwnerNodes.ForAsync(node, model, ctx), issue);
                }
            }
        }

        await AddCaptiveDependenciesAsync(ctx, Add);

        foreach (var (id, list) in issues)
        {
            var ordered = list.OrderBy(i => i.Line).ThenBy(i => i.Rule, StringComparer.Ordinal).ToList();
            await graph.PatchNodePropertiesByIdAsync(id, new
            {
                detected_issues = ordered.Select(i => $"{i.Rule}: {i.Text} (línea {i.Line})").ToArray(),
                issue_rules     = ordered.Select(i => i.Rule).Distinct().Order(StringComparer.Ordinal).ToArray()
            });
        }

        progress?.Report($"Issues: {issues.Values.Sum(l => l.Count)} finding(s) on {issues.Count} node(s).");
    }

    // ─── Diagnósticos del compilador ────────────────────────────────────────────

    private static Issue FromDiagnostic(Diagnostic d, SyntaxNode node, int line)
    {
        var name = node.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText;
        return d.Id switch
        {
            "CS8602" => new Issue("null-deref", $"posible desreferencia de un valor nulo en '{Short(node)}' (CS8602)", line),
            "CS0414" => new Issue("unused-field", $"el campo '{name}' se asigna pero su valor no se lee nunca (CS0414)", line),
            "CS0169" => new Issue("unused-field", $"el campo '{name}' no se usa nunca (CS0169)", line),
            "CS4014" => new Issue("unawaited-task", "llamada asíncrona sin await: la tarea se descarta y sus errores se pierden (CS4014)", line),
            "CS0162" => new Issue("unreachable-code", "código inalcanzable (CS0162)", line),
            _        => new Issue("obsolete-api", $"{d.GetMessage()} ({d.Id})", line)
        };
    }

    private static string Short(SyntaxNode node)
    {
        var text = node.ToString();
        return text.Length <= 60 ? text : text[..60] + "…";
    }

    // ─── Reglas propias ─────────────────────────────────────────────────────────

    private static IEnumerable<Issue> Rules(SyntaxNode node, SemanticModel model, Microsoft.CodeAnalysis.Text.SourceText text)
    {
        int LineOf(SyntaxNode n) => text.Lines.GetLinePosition(n.SpanStart).Line + 1;

        switch (node)
        {
            case MethodDeclarationSyntax { ReturnType: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword } } method
                when method.Modifiers.Any(SyntaxKind.AsyncKeyword) && !IsEventHandler(method.ParameterList.Parameters, model):
                yield return new Issue("async-void", $"'{method.Identifier.ValueText}' es async void fuera de un manejador de eventos: sus excepciones no se pueden capturar ni esperar", LineOf(method));
                break;

            case LocalFunctionStatementSyntax { ReturnType: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword } } local
                when local.Modifiers.Any(SyntaxKind.AsyncKeyword):
                yield return new Issue("async-void", $"la función local '{local.Identifier.ValueText}' es async void: sus excepciones no se pueden capturar ni esperar", LineOf(local));
                break;

            case CatchClauseSyntax @catch when SwallowsException(@catch, model) is { } what:
                yield return new Issue("empty-catch", what, LineOf(@catch));
                break;

            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Result" } access
                when model.GetSymbolInfo(access).Symbol is IPropertySymbol { ContainingType: { } owner } && IsTask(owner):
                yield return new Issue("blocking-async", "'.Result' bloquea el hilo esperando una tarea (riesgo de interbloqueo y de AggregateException)", LineOf(access));
                break;

            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Wait" or "WaitAll" or "WaitAny" or "GetResult" } call } invocation
                when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { ContainingType: { } owner } method
                     && (IsTask(owner) || (method.Name == "GetResult" && owner.Name.Contains("Awaiter", StringComparison.Ordinal))):
                yield return new Issue("blocking-async", $"'.{call.Name.Identifier.ValueText}()' bloquea el hilo esperando una tarea (riesgo de interbloqueo)", LineOf(invocation));
                break;

            case ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax when UndisposedResource(node, model) is { } typeName:
                yield return new Issue("undisposed-resource", $"'{typeName}' se crea y no se libera: sin using ni Dispose(), y no se entrega a nadie", LineOf(node));
                break;
        }
    }

    private static bool IsTask(INamedTypeSymbol type) =>
        type.OriginalDefinition is { Name: "Task", ContainingNamespace: { Name: "Tasks", ContainingNamespace.Name: "Threading" } };

    /// <summary>Un manejador de eventos: <c>(object sender, XxxEventArgs e)</c>, el único caso en que async void es lo correcto.</summary>
    private static bool IsEventHandler(SeparatedSyntaxList<ParameterSyntax> parameters, SemanticModel model) =>
        parameters.Count == 2
        && model.GetDeclaredSymbol(parameters[0]) is { Type.SpecialType: SpecialType.System_Object }
        && model.GetDeclaredSymbol(parameters[1]) is { } second && InheritsEventArgs(second.Type);

    private static bool InheritsEventArgs(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Name == "EventArgs") return true;
        return false;
    }

    /// <summary>
    /// Descripción del catch si no hace nada con la excepción; null si la trata o la relanza. La reutiliza
    /// <see cref="ThrowsEnricher"/> para <c>swallows_exceptions</c>, sin repetir el hallazgo en <c>detected_issues</c>.
    /// </summary>
    internal static string? SwallowsException(CatchClauseSyntax clause, SemanticModel model)
    {
        var declared = clause.Declaration is null ? null : model.GetTypeInfo(clause.Declaration.Type).Type;
        if (declared is not null && InheritsFrom(declared, "OperationCanceledException")) return null;   // tragarse una cancelación es lo normal

        var statements = clause.Block.Statements;
        var type = clause.Declaration?.Type.ToString() ?? "todo";
        if (statements.Count == 0)
            return $"catch ({type}) vacío: la excepción se descarta sin registrarla ni relanzarla";

        var general = declared is null || declared.SpecialType == SpecialType.System_Object || declared.Name is "Exception" or "SystemException";
        var inert = statements.All(s => s is EmptyStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax
            || s is ReturnStatementSyntax { Expression: null or LiteralExpressionSyntax or DefaultExpressionSyntax });
        return general && inert
            ? $"catch ({type}) que solo devuelve o continúa: la excepción se descarta sin registrarla ni relanzarla"
            : null;
    }

    internal static bool InheritsFrom(ITypeSymbol type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Name == name) return true;
        return false;
    }

    // ─── IDisposable sin liberar ────────────────────────────────────────────────

    /// <summary>
    /// Nombre del tipo si <paramref name="creation"/> crea un <c>IDisposable</c> que no se libera: ni <c>using</c> ni
    /// <c>Dispose()</c>/<c>Close()</c> (si se guarda en una variable local), y que tampoco se devuelve, se guarda en un
    /// campo o se entrega a una llamada. Pasarlo al constructor de otro objeto solo cuenta como entregarlo si ese otro
    /// objeto se libera o se entrega a su vez (un <c>SqlCommand</c> no cierra la conexión que recibe).
    /// </summary>
    private static string? UndisposedResource(SyntaxNode creation, SemanticModel model)
    {
        if (!creation.Ancestors().Any(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
                                                or AnonymousFunctionExpressionSyntax))
            return null;   // un inicializador de campo: lo gestiona el tipo
        if (model.GetOperation(creation) is not IObjectCreationOperation op || op.Type is not { } type || !IsDisposable(type))
            return null;
        return Escapes(op, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)) ? null : type.Name;
    }

    private static bool IsDisposable(ITypeSymbol type) =>
        !(type is INamedTypeSymbol named && IsTask(named))
        && (type.SpecialType == SpecialType.System_IDisposable
            || type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_IDisposable || i.Name == "IAsyncDisposable"));

    private static readonly HashSet<string> CloseMethods = ["Dispose", "DisposeAsync", "Close", "CloseAsync"];

    /// <summary>¿Llega el valor a un sitio que se encarga de él (using, Dispose, return, campo, argumento, captura)?</summary>
    private static bool Escapes(IOperation value, HashSet<ILocalSymbol> tracked)
    {
        var parent = value.Parent;
        while (parent is IConversionOperation or IParenthesizedOperation)
        {
            value = parent;
            parent = parent.Parent;
        }

        switch (parent)
        {
            case null or IReturnOperation or IThrowOperation or IUsingOperation:
                return true;

            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }:
                // using var x = ... / using (var x = ...): el compilador lo libera.
                for (var p = declarator.Parent; p is not null; p = p.Parent)
                    if (p is IUsingOperation or IUsingDeclarationOperation) return true;
                return LocalEscapes(declarator.Symbol, FindRoot(value), tracked);

            case ISimpleAssignmentOperation { Target: ILocalReferenceOperation local } assignment when assignment.Value == value:
                return LocalEscapes(local.Local, FindRoot(value), tracked);

            case IAssignmentOperation:
                return true;   // a un campo, propiedad, elemento o parámetro

            case IArgumentOperation { Parent: IObjectCreationOperation wrapper }:
                // El envoltorio se queda con él; vale si el envoltorio no es desechable o si este se libera.
                return wrapper.Type is null || !IsDisposable(wrapper.Type) || Escapes(wrapper, tracked);

            case IArgumentOperation:
                return true;   // una llamada se hace cargo

            case IExpressionStatementOperation:
                return false;  // new X(); descartado

            case IInvocationOperation invocation when invocation.Instance == value:
                return CloseMethods.Contains(invocation.TargetMethod.Name);   // new X().Dispose() vale; new X().Foo() no

            case IMemberReferenceOperation or IConditionalAccessOperation or IAwaitOperation:
                return false;

            default:
                return true;   // conservador: un ternario, un switch, una tupla…
        }
    }

    /// <summary>Si alguna referencia a la variable la libera o la entrega a otro sitio.</summary>
    private static bool LocalEscapes(ILocalSymbol local, IOperation root, HashSet<ILocalSymbol> tracked)
    {
        if (!tracked.Add(local)) return false;   // ya en estudio (asignaciones en ciclo)
        foreach (var reference in root.DescendantsAndSelf().OfType<ILocalReferenceOperation>())
        {
            if (!SymbolEqualityComparer.Default.Equals(reference.Local, local) || reference.IsDeclaration) continue;

            for (var p = reference.Parent; p is not null; p = p.Parent)
                if (p is IAnonymousFunctionOperation or ILocalFunctionOperation) return true;   // capturada

            var parent = reference.Parent;
            if (parent is IInvocationOperation call && call.Instance == reference)
            {
                if (CloseMethods.Contains(call.TargetMethod.Name)) return true;
                continue;
            }
            if (parent is IConditionalAccessOperation { WhenNotNull: IInvocationOperation cond } && CloseMethods.Contains(cond.TargetMethod.Name))
                return true;
            if (parent is IMemberReferenceOperation or IConditionalAccessOperation or IAwaitOperation) continue;
            if (Escapes(reference, tracked)) return true;
        }
        return false;
    }

    private static IOperation FindRoot(IOperation op)
    {
        while (op.Parent is not null) op = op.Parent;
        return op;
    }

    // ─── Captive dependency ─────────────────────────────────────────────────────

    /// <summary>Singleton cuyo constructor pide un servicio Scoped o Transient: el Singleton lo retiene toda la vida de la aplicación.</summary>
    private static async Task AddCaptiveDependenciesAsync(EnrichmentContext ctx, Action<long?, Issue> add)
    {
        var registrations = ctx.DiRegistrations;
        if (registrations.Count == 0) return;

        var byService = registrations
            .Where(r => SymbolKey.Of(r.ServiceType) is not null)
            .GroupBy(r => SymbolKey.Of(r.ServiceType)!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var singleton in registrations.Where(r => r.Lifetime == "Singleton"
                     && r.ImplementationType is INamedTypeSymbol && SymbolKey.Of(r.ImplementationType) is not null)
                     .DistinctBy(r => SymbolKey.Of(r.ImplementationType)))
        {
            var impl = (INamedTypeSymbol)singleton.ImplementationType;
            foreach (var ctor in impl.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared && c.DeclaringSyntaxReferences.Length > 0))
            {
                var reported = new HashSet<SymbolKey>();
                foreach (var parameter in ctor.Parameters)
                {
                    if (Dependency(parameter.Type) is not { } dependency || SymbolKey.Of(dependency) is not { } key
                        || !reported.Add(key) || !byService.TryGetValue(key, out var candidates))
                        continue;

                    // Scoped es el error; Transient es una sospecha menor: el Singleton lo conserva pero no se comparte entre peticiones.
                    var captured = candidates.FirstOrDefault(c => c.Lifetime == "Scoped") ?? candidates.FirstOrDefault(c => c.Lifetime == "Transient");
                    if (captured is null) continue;

                    var reference = ctor.DeclaringSyntaxReferences[0];
                    var line = reference.SyntaxTree.GetLineSpan(reference.Span).StartLinePosition.Line + 1;
                    var qn = SymbolQn.ForMethod(ctor, ctx.Project, ctx.RepoRoot);
                    var nodeId = qn is null ? null : await ctx.Repository.GetNodeIdAsync(ctx.Project, qn);
                    if (nodeId is null && SymbolQn.ForType(impl, ctx.Project, ctx.RepoRoot) is { } typeQn)
                        nodeId = await ctx.Repository.GetNodeIdAsync(ctx.Project, typeQn);

                    add(nodeId, new Issue("captive-dependency",
                        $"'{impl.Name}' (Singleton, registrado en {singleton.FilePath}:{singleton.Line}) depende de '{dependency.Name}' "
                        + $"({captured.Lifetime}, registrado en {captured.FilePath}:{captured.Line}): el Singleton retiene esa instancia toda la vida de la aplicación",
                        line));
                }
            }
        }
    }

    /// <summary>El servicio que pide un parámetro: él mismo o el elemento de una colección. Una factoría (<c>Func</c>, <c>Lazy</c>) no captura.</summary>
    private static ITypeSymbol? Dependency(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true } generic)
        {
            if (generic.Name is "Func" or "Lazy" or "IOptions" or "IOptionsMonitor" or "IOptionsSnapshot") return null;
            if (generic.Name is "IEnumerable" or "IReadOnlyCollection" or "IReadOnlyList" or "ICollection" or "IList" or "List")
                return generic.TypeArguments[0];
        }
        return type;
    }
}
