
namespace AITaskAgent.LLM.Tools.Abstractions;

public interface ITools
{
    IReadOnlyList<ITool> GetAllTools(string? moduleName = null);
    ITool? GetTool(string name);
    IReadOnlyList<ITool> GetTools(params string[] names);
    void Register(ITool tool);
}