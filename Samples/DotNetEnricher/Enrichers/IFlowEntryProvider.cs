using Microsoft.CodeAnalysis;

namespace DotNetEnricher.Enrichers;

/// <summary>Provides the method symbols that are entry points for flow tracing.</summary>
public interface IFlowEntryProvider
{
    /// <summary>
    /// Returns (method, flowKind) pairs. flowKind is stored in the Flow node
    /// (e.g. "message_handler", "background_service", "http_endpoint").
    /// </summary>
    IEnumerable<(IMethodSymbol Method, string FlowKind)> GetEntryPoints(
        Compilation compilation,
        string project,
        string repoRoot,
        GraphRepository repository);
}
