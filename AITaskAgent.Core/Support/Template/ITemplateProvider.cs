namespace AITaskAgent.Support.Template;

/// <summary>
/// Provides templates by name.
/// </summary>
public interface ITemplateProvider
{
    /// <summary>
    /// Gets a template by name. Returns null if not found.
    /// </summary>
    /// <param name="name">Template name (without extension)</param>
    /// <returns>Template content or null if not found</returns>
    string? GetTemplate(string name);
    string? Render(string name, object model);
}
