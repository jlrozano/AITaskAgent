using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AITaskAgent.CSharp;

/// <summary>What the editor asks <see cref="CSharpUnitCompiler.CompleteAsync"/> to complete.</summary>
/// <param name="Text">With <paramref name="StepId"/>, the body of that <c>CSharpStep</c> (a method that receives <c>StepCall call</c>); without it, the text of <c>csharp/shared.cs</c>.</param>
/// <param name="Caret">Offset of the caret in <paramref name="Text"/> (0..length).</param>
/// <param name="StepId">The step whose body <paramref name="Text"/> is; null for the shared file.</param>
public sealed record CSharpCompletionRequest(string Text, int Caret, string? StepId = null);

/// <summary>One proposal of the completion (overloads of a method are one item).</summary>
/// <param name="Label">The name to insert.</param>
/// <param name="Kind">CodeMirror's kind: <c>variable</c>, <c>property</c>, <c>method</c>, <c>class</c>, <c>interface</c>, <c>enum</c>, <c>type</c>, <c>namespace</c>, <c>constant</c> or <c>keyword</c>.</param>
/// <param name="Detail">A signature (the first one, plus <c>(+N overloads)</c>).</param>
/// <param name="Boost">Rank: locals and parameters 3, members of the containing type 2, members after a dot 1, types 0, namespaces -1.</param>
public sealed record CSharpCompletionItem(string Label, string Kind, string? Detail = null, int Boost = 0)
{
    /// <summary>The summary of the symbol's XML documentation, as plain text, when its assembly ships one; null otherwise. Additive (2026-10-23).</summary>
    public string? Documentation { get; init; }

    /// <summary>The full metadata name of the type that declares the member (<c>AITaskAgent.Core.Hooks.PostProcessArgs</c>), or null for a type or namespace. Additive (2026-10-23).</summary>
    public string? Container { get; init; }
}

/// <summary>The proposals for the text between <paramref name="From"/> and <paramref name="To"/> (the identifier being typed; offsets of the request's text).</summary>
public sealed record CSharpCompletionList(int From, int To, IReadOnlyList<CSharpCompletionItem> Items)
{
    /// <summary>No proposal at <paramref name="caret"/>.</summary>
    public static CSharpCompletionList Empty(int caret) => new(caret, caret, []);
}

public sealed partial class CSharpUnitCompiler
{
    /// <summary>Most items one completion returns.</summary>
    public const int MaxCompletionItems = 300;

    /// <summary>
    /// Completes the identifier at the caret of a step body or of the shared file, with the symbols the unit would bind there (the same
    /// compilation as <see cref="CompileAsync"/>: references, usings, schema classes, shared file and generated step method). Nothing is
    /// emitted, loaded or cached. <paramref name="context"/> gives the shared file and the schemas (its steps are ignored; for the shared
    /// file itself, the request's text replaces <see cref="CSharpUnitSources.Shared"/>). Symbols only: keywords and snippets are the client's.
    /// </summary>
    public async Task<CSharpCompletionList> CompleteAsync(CSharpUnitSources context, CSharpCompletionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Caret);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Caret, request.Text.Length);

        // The compiler sees \n only: the caret moves back by the \r before it.
        string text = request.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        int caret = request.Caret - CountCarriageReturns(request.Text, request.Caret);
        int from = caret;
        while (from > 0 && IsIdentifierChar(text[from - 1]))
        {
            from--;
        }

        int originalFrom = request.Caret - (caret - from);
        string prefix = text[from..caret];

        CSharpUnitSources sources = request.StepId is { } stepId
            ? new CSharpUnitSources([new CSharpStepSource(stepId, text)], context.Shared, context.Schemas)
            : new CSharpUnitSources([], text, context.Schemas);

        ct.ThrowIfCancellationRequested();
        UnitTrees unit = await CreateCompilationAsync(sources, "Workflow_completion", forEditor: true);
        return await Task.Run(() =>
        {
            SyntaxTree tree;
            int offset;
            if (request.StepId != null)
            {
                tree = unit.StepsTree;
                (int line, int column) = LineAndColumn(text, from);
                TextLine generated = tree.GetText(ct).Lines[unit.Regions[0].BodyStart - 1 + line];
                offset = generated.Start + column;
            }
            else
            {
                tree = unit.Shared!;
                offset = from;
            }

            int textDelta = offset - from;
            SemanticModel model = unit.Compilation.GetSemanticModel(tree);
            List<CSharpCompletionItem> items = Complete(model, tree, text, from, offset, textDelta, prefix, ct);
            return new CSharpCompletionList(originalFrom, request.Caret, items);
        }, ct);
    }

    private static List<CSharpCompletionItem> Complete(SemanticModel model, SyntaxTree tree, string text, int from, int position, int textDelta, string prefix, CancellationToken ct)
    {
        // `x.`, `x?.`, `Ns.`: what is left of the dot (blanks between allowed, as in a fluent chain over lines).
        int before = from - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before]))
        {
            before--;
        }

        List<(ISymbol Symbol, int Boost)> found = [];
        if (before >= 0 && text[before] == '.')
        {
            if (LeftOfDot(tree.GetRoot(ct).FindToken(before + textDelta)) is not { } left)
            {
                return [];
            }

            foreach (ISymbol member in MembersAfterDot(model, left, position, ct))
            {
                found.Add((member, 1));
            }
        }
        else
        {
            ISymbol? enclosing = model.GetEnclosingSymbol(position, ct);
            INamedTypeSymbol? containing = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
            foreach (ISymbol symbol in model.LookupSymbols(position))
            {
                ISymbol target = symbol is IAliasSymbol alias ? alias.Target : symbol;
                int boost = target switch
                {
                    ILocalSymbol or IParameterSymbol or IRangeVariableSymbol => 3,
                    _ when IsMemberOf(target, containing) => 2,
                    INamespaceSymbol => -1,
                    _ => 0,
                };

                // Ctrl+Space with nothing typed: only what is close (locals, parameters, members of the type), not every type in scope.
                if (prefix.Length == 0 && boost < 2)
                {
                    continue;
                }

                found.Add((symbol, boost));
            }
        }

        return [.. found
            .Where(f => Offered(f.Symbol) && f.Symbol.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => (f.Symbol.Name, Kind: KindOf(f.Symbol)))
            .Select(g =>
            {
                ISymbol first = g.First().Symbol;
                int others = g.Select(f => f.Symbol).Distinct(SymbolEqualityComparer.Default).Count() - 1;
                string detail = Detail(first, model, position) + (others > 0 ? $" (+{others} overload{(others == 1 ? "" : "s")})" : "");
                ISymbol target = first is IAliasSymbol alias ? alias.Target : first;
                return new CSharpCompletionItem(first.Name, g.Key.Kind, detail, g.Max(f => f.Boost))
                {
                    Documentation = Summary(target, ct),
                    Container = target is INamespaceSymbol or INamedTypeSymbol || target.ContainingType is not { } owner
                        ? null
                        : owner.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + owner.MetadataName : owner.MetadataName,
                };
            })
            .OrderByDescending(i => i.Boost)
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Label, StringComparer.Ordinal)
            .Take(MaxCompletionItems)];
    }

    /// <summary>The expression or name left of the dot token at <paramref name="dot"/>.</summary>
    private static ExpressionSyntax? LeftOfDot(SyntaxToken dot)
    {
        if (!dot.IsKind(SyntaxKind.DotToken))
        {
            return null;
        }

        return dot.Parent switch
        {
            MemberAccessExpressionSyntax access => access.Expression,
            QualifiedNameSyntax qualified => qualified.Left,
            MemberBindingExpressionSyntax binding => binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
            _ => null,
        };
    }

    private static IEnumerable<ISymbol> MembersAfterDot(SemanticModel model, ExpressionSyntax left, int position, CancellationToken ct)
    {
        SymbolInfo info = model.GetSymbolInfo(left, ct);
        ISymbol? symbol = info.Symbol ?? (info.CandidateSymbols.Length > 0 ? info.CandidateSymbols[0] : null);
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        if (symbol is INamespaceSymbol ns)
        {
            return ns.GetMembers().Where(m => model.IsAccessible(position, m));
        }

        if (symbol is INamedTypeSymbol type)
        {
            return model.LookupStaticMembers(position, type);
        }

        ITypeSymbol? valueType = model.GetTypeInfo(left, ct).Type;
        if (valueType == null || valueType.TypeKind == TypeKind.Error)
        {
            return [];
        }

        return model.LookupSymbols(position, valueType, includeReducedExtensionMethods: true)
            .Where(m => !m.IsStatic || m is IMethodSymbol { MethodKind: MethodKind.ReducedExtension })
            .Where(m => m is not INamedTypeSymbol);
    }

    private static bool IsMemberOf(ISymbol symbol, INamedTypeSymbol? containing)
    {
        if (symbol.ContainingType is not { } owner || symbol is INamedTypeSymbol)
        {
            return false;
        }

        for (INamedTypeSymbol? type = containing; type != null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(owner, type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>False for what the author cannot write: constructors, operators, accessors, compiler-generated names, the unit's anchor class.</summary>
    private static bool Offered(ISymbol symbol)
    {
        ISymbol target = symbol is IAliasSymbol alias ? alias.Target : symbol;
        if (!symbol.CanBeReferencedByName || symbol.Name.Length == 0 || !SyntaxFacts.IsValidIdentifier(symbol.Name) || symbol is ILabelSymbol)
        {
            return false;
        }

        if (target is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.LocalFunction or MethodKind.DelegateInvoke) })
        {
            return false;
        }

        return !(target is INamedTypeSymbol { Name: "WorkflowNamespaceAnchor" } anchor && anchor.ContainingNamespace?.ToDisplayString() == Namespace);
    }

    private static string KindOf(ISymbol symbol) => (symbol is IAliasSymbol alias ? alias.Target : symbol) switch
    {
        ILocalSymbol or IParameterSymbol or IRangeVariableSymbol => "variable",
        IFieldSymbol field when field.IsConst || field.ContainingType?.TypeKind == TypeKind.Enum => "constant",
        IFieldSymbol or IPropertySymbol or IEventSymbol => "property",
        IMethodSymbol => "method",
        INamespaceSymbol => "namespace",
        INamedTypeSymbol { TypeKind: TypeKind.Class } => "class",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
        _ => "type",
    };

    private static string Detail(ISymbol symbol, SemanticModel model, int position)
    {
        ISymbol target = symbol is IAliasSymbol alias ? alias.Target : symbol;
        return target is INamespaceSymbol or INamedTypeSymbol
            ? target.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            : target.ToMinimalDisplayString(model, position);
    }

    /// <summary>The <c>&lt;summary&gt;</c> of the symbol's XML documentation as one line of plain text, or null.</summary>
    private static string? Summary(ISymbol symbol, CancellationToken ct)
    {
        string? xml = symbol.GetDocumentationCommentXml(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            System.Xml.Linq.XElement root = System.Xml.Linq.XElement.Parse(xml.Contains("<member", StringComparison.Ordinal) ? xml : $"<member>{xml}</member>");
            System.Xml.Linq.XElement? summary = root.Element("summary");
            if (summary == null)
            {
                return null;
            }

            string text = string.Concat(summary.Nodes().Select(n => n switch
            {
                System.Xml.Linq.XText t => t.Value,
                System.Xml.Linq.XElement e when e.Attribute("cref") is { } cref => cref.Value[(cref.Value.LastIndexOf('.') + 1)..],
                System.Xml.Linq.XElement e when e.Attribute("name") is { } name => name.Value,
                System.Xml.Linq.XElement e => e.Value,
                _ => "",
            }));
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim() is { Length: > 0 } line ? line : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int CountCarriageReturns(string text, int end)
    {
        int count = 0;
        for (int i = 0; i < end; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>0-based line and column of <paramref name="offset"/> in <paramref name="text"/> (lines end with <c>\n</c>).</summary>
    private static (int Line, int Column) LineAndColumn(string text, int offset)
    {
        int line = 0;
        int start = 0;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                start = i + 1;
            }
        }

        return (line, offset - start);
    }
}
