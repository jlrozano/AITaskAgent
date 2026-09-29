namespace AITaskAgent.FileTools.CodeItems;

/// <summary>
/// Finds declarations (types, methods, members) by name in a source file for <c>view_code_item</c>. The tool asks its
/// locators in order and uses the first answer that is not null; the built-in <see cref="CSharpCodeItemLocator"/> (Roslyn)
/// always answers last, so without locators the tool behaves as it always did. A host adds languages by passing locators
/// to the tool's constructor or registering <see cref="ICodeItemLocator"/> in the service collection.
/// </summary>
public interface ICodeItemLocator
{
    /// <summary>
    /// One lookup per <see cref="CodeItemRequest.NodePaths"/> entry, in the same order; null when this locator does not
    /// handle the file, so the next one is asked.
    /// </summary>
    ValueTask<IReadOnlyList<CodeItemLookup>?> LocateAsync(CodeItemRequest request, CancellationToken cancellationToken);
}

/// <param name="FilePath">Absolute path of the file, already inside the sandbox.</param>
/// <param name="SourceText">The file decoded with the repository rule (<see cref="TextFileEncoding"/>).</param>
/// <param name="NodePaths">
/// What was asked for, e.g. <c>Order</c>, <c>Order.Total</c>, <c>Outer.Inner.run</c> or, to pick an overload,
/// <c>Order.total(int,String)</c>.
/// </param>
public sealed record CodeItemRequest(string FilePath, string SourceText, IReadOnlyList<string> NodePaths);

/// <summary>What a locator found for one node path: nothing, one declaration or several (overloads).</summary>
/// <param name="Matches">Declarations that answer the node path. More than one means it is ambiguous.</param>
/// <param name="AvailableNodes">Node paths the file offers, for the message when nothing matched.</param>
public sealed record CodeItemLookup(IReadOnlyList<CodeItemMatch> Matches, IReadOnlyList<string> AvailableNodes)
{
    public static CodeItemLookup Found(CodeItemMatch match) => new([match], []);

    public static CodeItemLookup NotFound(IReadOnlyList<string> availableNodes) => new([], availableNodes);
}

/// <summary>A declaration, by its 1-based inclusive line range.</summary>
public sealed record CodeItemMatch
{
    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    /// <summary>Node path that selects only this declaration, offered when several match (e.g. <c>Order.total(int)</c>).</summary>
    public string? NodePath { get; init; }

    /// <summary>One-line description, such as the signature.</summary>
    public string? Detail { get; init; }

    /// <summary>Code to show; null shows lines <see cref="StartLine"/> to <see cref="EndLine"/> of the file.</summary>
    public string? Code { get; init; }
}
