using AITaskAgent.LLM.Tools.Abstractions;

namespace AITaskAgent.LLM.Tools.Implementation;

/// <summary>
/// Thread-safe registry for managing tools available to LLMs.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ITool> _tools = [];

    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        lock (_lock)
        {
            _tools[tool.Name] = tool;
        }
    }

    public ITool? GetTool(string name)
    {
        lock (_lock)
        {
            return _tools.TryGetValue(name, out var tool) ? tool : null;
        }
    }

    public IReadOnlyList<ITool> GetAllTools()
    {
        lock (_lock)
        {
            return [.. _tools.Values];
        }
    }

    public IReadOnlyList<ITool> GetTools(params string[] names)
    {
        lock (_lock)
        {
            return [.. names.Select(GetTool)
                        .Where(t => t is not null)
                        .Cast<ITool>()];
        }
    }
}

