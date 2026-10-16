using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Concurrencia con el modelo semántico. En cada nodo de método o constructor con alguna marca escribe:
///
///   - <c>concurrency_markers</c>: <c>[{kind, line, api?}]</c>, ordenada por línea. Tipos: <c>lock</c> (la sentencia y
///     <c>Monitor.Enter/TryEnter</c>), <c>Interlocked</c>, <c>async-void</c> (el método, una función local o una lambda
///     <c>async</c> convertida a un delegado que devuelve <c>void</c>), <c>fire-and-forget</c> (una sentencia cuyo valor es una
///     <c>Task</c>/<c>ValueTask</c> que no se espera ni se guarda, también <c>_ = …</c>), <c>blocking-wait</c> (<c>.Result</c>,
///     <c>.Wait()</c>/<c>WaitAll</c>/<c>WaitAny</c>, <c>GetAwaiter().GetResult()</c>), <c>Task.Run</c> (y
///     <c>TaskFactory.StartNew</c>), <c>Parallel</c> (<c>Parallel.*</c>), <c>thread</c> (<c>new Thread</c>,
///     <c>ThreadPool.QueueUserWorkItem</c>) y <c>writes-static-mutable</c> (asigna un campo estático no readonly, con las
///     referencias exactas de <see cref="ReferenceIndex"/>, o modifica una colección estática no concurrente:
///     <c>Add</c>/<c>Remove</c>/<c>Clear</c>… o una asignación por índice).
///   - <c>concurrency_kinds</c>: los tipos, sin repetir.
///
/// Y en cada nodo <c>Field</c> que crea <see cref="FieldEnricher"/>: <c>is_static_mutable</c> (estático y reasignable, o
/// estático readonly de una colección o un array no concurrente) e <c>is_thread_safe_type</c> (el tipo está pensado para
/// uso concurrente o es inmutable: <c>string</c>, <c>System.Collections.Concurrent</c>/<c>Immutable</c>/<c>Frozen</c>,
/// primitivas de <c>System.Threading</c>, <c>Lazy</c> y canales). <c>blocking-wait</c> y <c>async-void</c> coinciden con las
/// reglas <c>blocking-async</c> y <c>async-void</c> de <see cref="IssueEnricher"/>: aquí son hechos (sin excepción para los
/// manejadores de eventos), allí hallazgos.
/// </summary>
public sealed class ConcurrencyEnricher : IGraphEnricher
{
    public string Name  => "ConcurrencyEnricher";
    public int    Order => 9;

    private static readonly HashSet<string> MutableContainers = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List", "System.Collections.Generic.Dictionary", "System.Collections.Generic.HashSet",
        "System.Collections.Generic.Queue", "System.Collections.Generic.Stack", "System.Collections.Generic.SortedDictionary",
        "System.Collections.Generic.SortedList", "System.Collections.Generic.SortedSet", "System.Collections.Generic.LinkedList",
        "System.Collections.ArrayList", "System.Collections.Hashtable", "System.Text.StringBuilder"
    };

    private static readonly HashSet<string> MutatingMethods = new(StringComparer.Ordinal)
    {
        "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "Insert", "InsertRange", "Enqueue",
        "Dequeue", "TryDequeue", "Push", "Pop", "TryPop", "TryAdd", "Sort", "Reverse", "Append", "AppendLine", "AppendFormat", "UnionWith",
        "ExceptWith", "IntersectWith", "AddFirst", "AddLast", "RemoveFirst", "RemoveLast"
    };

    private static readonly HashSet<string> ThreadSafeTypes = new(StringComparer.Ordinal)
    {
        "System.Threading.Lock", "System.Threading.SemaphoreSlim", "System.Threading.Semaphore", "System.Threading.Mutex",
        "System.Threading.ReaderWriterLockSlim", "System.Threading.ThreadLocal", "System.Threading.AsyncLocal",
        "System.Threading.CancellationToken", "System.Threading.CancellationTokenSource", "System.Threading.ManualResetEventSlim",
        "System.Threading.CountdownEvent", "System.Threading.Barrier", "System.Lazy"
    };

    private static readonly string[] ThreadSafeNamespaces =
        ["System.Collections.Concurrent.", "System.Collections.Immutable.", "System.Collections.Frozen.", "System.Threading.Channels."];

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph = ctx.Repository;
        await graph.ExecuteAsync($"""
            UPDATE nodes SET properties = json_remove(properties, '$.concurrency_markers', '$.concurrency_kinds')
            WHERE project = @project AND label IN ('Method','Function') AND json_type(properties, '$.concurrency_markers') IS NOT NULL
              AND {LanguageScope.NotJava()}
            """, new { project = ctx.Project });

        var markers = new Dictionary<long, HashSet<(int Line, string Kind, string? Api)>>();
        foreach (var body in await MethodBodies.LoadAsync(ctx, Name))
        {
            var found = Analyze(body).Concat(StaticFieldWrites(body, ctx.References)).ToList();
            if (found.Count == 0) continue;
            if (!markers.TryGetValue(body.NodeId, out var set)) markers[body.NodeId] = set = [];
            set.UnionWith(found);
        }

        foreach (var (nodeId, set) in markers.OrderBy(m => m.Key))
        {
            var ordered = set.OrderBy(m => m.Line).ThenBy(m => m.Kind, StringComparer.Ordinal).ThenBy(m => m.Api, StringComparer.Ordinal).ToList();
            await graph.PatchNodePropertiesByIdAsync(nodeId, new Dictionary<string, object?>
            {
                ["concurrency_markers"] = ordered.Select(m =>
                {
                    var marker = new Dictionary<string, object?> { ["kind"] = m.Kind, ["line"] = m.Line };
                    if (m.Api is not null) marker["api"] = m.Api;
                    return marker;
                }).ToList(),
                ["concurrency_kinds"] = ordered.Select(m => m.Kind).Distinct().Order(StringComparer.Ordinal).ToArray()
            });
            ctx.RecordPatchHit(Name);
        }

        // Campos: los nodos Field que acaba de crear FieldEnricher ({QN del tipo}.{campo}).
        var symbols = await ctx.GetGraphSymbolsAsync();
        int fields = 0, staticMutable = 0;
        foreach (var (_, type) in symbols.TypesByNode.OrderBy(t => t.Key))
        {
            if (type.TypeKind == TypeKind.Enum || SymbolQn.ForType(type, ctx.Project, ctx.RepoRoot) is not { } typeQn) continue;
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsImplicitlyDeclared || field.AssociatedSymbol is not null || field.DeclaringSyntaxReferences.Length == 0) continue;
                var isStaticMutable = IsStaticMutable(field);
                if (await graph.PatchNodePropertiesAsync(ctx.Project, $"{typeQn}.{field.Name}", new Dictionary<string, object?>
                    {
                        ["is_static_mutable"] = isStaticMutable,
                        ["is_thread_safe_type"] = IsThreadSafeType(field.Type)
                    }) == 0)
                    continue;
                fields++;
                if (isStaticMutable) staticMutable++;
            }
        }

        var byKind = markers.Values.SelectMany(s => s.Select(m => m.Kind).Distinct())
            .GroupBy(k => k).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        ctx.EnricherDetails["concurrency"] = new { methods = markers.Count, methods_by_kind = byKind, fields, static_mutable_fields = staticMutable };
        progress?.Report($"Concurrency: {markers.Count} method(s) with markers; {staticMutable} static mutable field(s) of {fields}.");
    }

    private static IEnumerable<(int Line, string Kind, string? Api)> Analyze(MethodBody body)
    {
        var model = body.Model;
        if (body.Method.IsAsync && body.Method.ReturnsVoid)
            yield return (body.LineOf(body.Declaration), "async-void", null);

        foreach (var node in body.Declaration.DescendantNodes())
        {
            switch (node)
            {
                case LockStatementSyntax:
                    yield return (body.LineOf(node), "lock", null);
                    break;

                case LocalFunctionStatementSyntax local
                    when local.Modifiers.Any(SyntaxKind.AsyncKeyword) && model.GetDeclaredSymbol(local) is IMethodSymbol { ReturnsVoid: true }:
                    yield return (body.LineOf(node), "async-void", local.Identifier.ValueText);
                    break;

                case AnonymousFunctionExpressionSyntax lambda
                    when lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)
                         && model.GetTypeInfo(lambda).ConvertedType is INamedTypeSymbol { DelegateInvokeMethod.ReturnsVoid: true }:
                    yield return (body.LineOf(node), "async-void", "lambda");
                    break;

                case InvocationExpressionSyntax invocation when MethodBodies.MethodOf(invocation, model) is { } method:
                    if (Invocation(method, invocation, model) is { } marker)
                        yield return (body.LineOf(node), marker.Kind, marker.Api);
                    break;

                case BaseObjectCreationExpressionSyntax creation
                    when MethodBodies.MethodOf(creation, model)?.ContainingType is { } created && MethodBodies.FullName(created) == "System.Threading.Thread":
                    yield return (body.LineOf(node), "thread", "new System.Threading.Thread");
                    break;

                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Result" } access
                    when model.GetSymbolInfo(access).Symbol is IPropertySymbol { ContainingType: { } owner } && IsTaskLike(owner):
                    yield return (body.LineOf(node), "blocking-wait", $"{MethodBodies.FullName(owner)}.Result");
                    break;

                case ExpressionStatementSyntax statement when Discarded(statement, model) is { } discarded && IsTaskLike(discarded):
                    yield return (body.LineOf(node), "fire-and-forget", MethodBodies.FullName(discarded));
                    break;

                case AssignmentExpressionSyntax { Left: ElementAccessExpressionSyntax element }
                    when StaticMutableReceiver(element.Expression, model) is { } field:
                    yield return (body.LineOf(node), "writes-static-mutable", FieldName(field));
                    break;
            }
        }
    }

    private static (string Kind, string? Api)? Invocation(IMethodSymbol method, InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (definition.ContainingType is not { } owner) return null;
        var ownerName = MethodBodies.FullName(owner);
        var api = $"{ownerName}.{definition.Name}";
        switch (ownerName)
        {
            case "System.Threading.Interlocked": return ("Interlocked", api);
            case "System.Threading.Monitor" when definition.Name is "Enter" or "TryEnter": return ("lock", api);
            case "System.Threading.Tasks.Task" when definition.Name == "Run": return ("Task.Run", api);
            case "System.Threading.Tasks.TaskFactory" when definition.Name == "StartNew": return ("Task.Run", api);
            case "System.Threading.Tasks.Parallel": return ("Parallel", api);
            case "System.Threading.ThreadPool" when definition.Name.Contains("QueueUserWorkItem", StringComparison.Ordinal): return ("thread", api);
        }
        if (definition.Name is "Wait" or "WaitAll" or "WaitAny" && IsTaskLike(owner)) return ("blocking-wait", api);
        if (definition.Name == "GetResult" && owner.Name.Contains("Awaiter", StringComparison.Ordinal)
            && ownerName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal))
            return ("blocking-wait", api);

        // Modificar una colección estática no concurrente: _cache.Add(x).
        if (!definition.IsStatic && MutatingMethods.Contains(definition.Name)
            && invocation.Expression is MemberAccessExpressionSyntax access
            && StaticMutableReceiver(access.Expression, model) is { } field)
            return ("writes-static-mutable", FieldName(field));
        return null;
    }

    /// <summary>Asignaciones a campos estáticos reasignables (las referencias de escritura exactas que ve el índice).</summary>
    private static IEnumerable<(int Line, string Kind, string? Api)> StaticFieldWrites(MethodBody body, ReferenceIndex references) =>
        references.From(body.Method)
            .Where(r => (r.Flags & ReferenceFlags.Write) != 0 && (r.Flags & ReferenceFlags.Initializer) == 0
                        && r.TargetSymbol is IFieldSymbol { IsStatic: true, IsReadOnly: false, IsConst: false })
            .Select(r => (r.Line, "writes-static-mutable", (string?)FieldName((IFieldSymbol)r.TargetSymbol)));

    /// <summary>El tipo del valor que una sentencia descarta (<c>Foo();</c> o <c>_ = Foo();</c>), o null si no descarta nada.</summary>
    private static INamedTypeSymbol? Discarded(ExpressionStatementSyntax statement, SemanticModel model)
    {
        var expression = statement.Expression switch
        {
            AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "_" } left, Right: var right }
                when model.GetSymbolInfo(left).Symbol is null or IDiscardSymbol => right,
            AssignmentExpressionSyntax => null,
            AwaitExpressionSyntax => null,
            var other => other
        };
        return expression is null ? null : model.GetTypeInfo(expression).Type as INamedTypeSymbol;
    }

    private static bool IsTaskLike(INamedTypeSymbol type) =>
        MethodBodies.FullName(type) is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask";

    /// <summary>El campo estático y mutable (<see cref="IsStaticMutable"/>) al que se refiere una expresión, o null.</summary>
    private static IFieldSymbol? StaticMutableReceiver(ExpressionSyntax expression, SemanticModel model) =>
        model.GetSymbolInfo(expression).Symbol is IFieldSymbol field && IsStaticMutable(field) && field.DeclaringSyntaxReferences.Length > 0
            ? field
            : null;

    private static string FieldName(IFieldSymbol field) => $"{MethodBodies.FullName(field.ContainingType)}.{field.Name}";

    internal static bool IsStaticMutable(IFieldSymbol field) =>
        field.IsStatic && !field.IsConst
        && (!field.IsReadOnly || (IsMutableContainer(field.Type) && !IsThreadSafeType(field.Type)));

    private static bool IsMutableContainer(ITypeSymbol type) =>
        type is IArrayTypeSymbol || (type is INamedTypeSymbol named && MutableContainers.Contains(MethodBodies.FullName(named)));

    internal static bool IsThreadSafeType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String) return true;
        if (type is not INamedTypeSymbol named) return false;
        var name = MethodBodies.FullName(named);
        return ThreadSafeTypes.Contains(name) || ThreadSafeNamespaces.Any(ns => name.StartsWith(ns, StringComparison.Ordinal));
    }
}
