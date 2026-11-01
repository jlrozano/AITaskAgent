using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.LLM.Tools.Abstractions;

/// <summary>
/// Tools of a plugin that need arguments or dependencies (a tool with a public parameterless constructor is discovered on its own). The
/// host creates the source (public parameterless constructor) and calls <see cref="Create"/> once per engine it builds.
/// </summary>
public interface IToolSource
{
    /// <summary>The tools, built for <paramref name="context"/> (the plugin's folder and configuration section).</summary>
    IEnumerable<ITool> Create(PluginContext context);
}
