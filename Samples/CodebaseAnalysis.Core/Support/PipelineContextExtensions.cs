using AITaskAgent.Core.Models;

namespace CodebaseAnalysis.Core.Support;

public static class PipelineContextExtensions
{
    public static void Set<T>(this PipelineContext context, string key, T value)
    {
        context.Metadata[key] = value;
    }

    public static T? Get<T>(this PipelineContext context, string key)
    {
        return context.Metadata.TryGetValue(key, out var val) && val is T typed ? typed : default;
    }
}
