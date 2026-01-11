namespace AITaskAgent.Support.Template;

/// <summary>
/// Template engine for rendering prompts with parameters.
/// </summary>
public interface ITemplateEngine
{
    /// <summary>
    /// Renders a template with parameters from an object's properties.
    /// </summary>
    string Render(string template, object obj);
}

