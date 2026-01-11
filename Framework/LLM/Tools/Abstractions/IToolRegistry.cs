namespace AITaskAgent.LLM.Tools.Abstractions;

/// <summary>
/// Registry for managing tools available to LLMs.
/// </summary>
public interface IToolRegistry
{
    /// <summary>
    /// Registers a tool.
    /// </summary>
    void Register(ITool tool);

    /// <summary>
    /// Gets a tool by name.
    /// </summary>
    ITool? GetTool(string name);

    /// <summary>
    /// Gets all registered tools.
    /// </summary>
    IReadOnlyList<ITool> GetAllTools();

    /// <summary>
    /// Gets tools by their names.
    /// </summary>
    IReadOnlyList<ITool> GetTools(params string[] names);
}

