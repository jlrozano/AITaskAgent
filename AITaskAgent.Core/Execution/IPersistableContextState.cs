using AITaskAgent.Core.Models;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Implemented by a derived <see cref="PipelineContext"/> that carries state of its own beyond
/// <c>Metadata</c> and <c>StepResults</c> (the LLM context's conversation), so that
/// <see cref="PipelineContextSerializer"/> can persist and restore it.
/// </summary>
public interface IPersistableContextState
{
    /// <summary>The context's own state as JSON, or null when it has none worth keeping.</summary>
    JToken? SaveExtensionState();

    /// <summary>
    /// Returns a context like this one carrying <paramref name="state"/> (records return a copy made
    /// with <c>with</c>, since their state is usually init-only).
    /// </summary>
    PipelineContext RestoreExtensionState(JToken state);
}
