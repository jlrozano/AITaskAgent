using AITaskAgent.LLM.Tools.Abstractions;

namespace AITaskAgent.LLM.Tools.Implementation;

/// <summary>
/// Thread-safe registry for managing tools available to LLMs.
/// </summary>
public sealed class AvailableTools : ITools
{
    public static AvailableTools Instance { get; } = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.InvariantCultureIgnoreCase);

    public AvailableTools()
    {
    }

    public AvailableTools(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
        {
            Register(tool);
        }
    }

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

    public IReadOnlyList<ITool> GetAllTools(string? module = null)
    {
        lock (_lock)
        {

            return string.IsNullOrEmpty(module)
                ? [.. _tools.Values]
                : _tools.Values.Where(t => string.Equals(t.GetType().Assembly.GetName().Name, module, StringComparison.InvariantCultureIgnoreCase)).ToList();
        }
    }

    public IReadOnlyList<ITool> GetTools(params string[] names)
    {
        lock (_lock)
        {
            List<ITool> res = [];
            foreach (var name in names)
            {
                if (_tools.TryGetValue(name, out var tool))
                {
                    res.Add(tool);
                }
            }
            return res;
        }
    }
}

