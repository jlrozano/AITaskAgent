using AITaskAgent.Core.Abstractions;

namespace AITaskAgent.Support.Template;

/// <summary>
/// Templates a plugin brings. The host creates the source (public parameterless constructor) and chains the provider it returns after the
/// templates of the workflow package itself.
/// </summary>
public interface ITemplateSource
{
    /// <summary>The provider for <paramref name="context"/> (for instance over <c>context.Folder/templates</c>); null when it has none.</summary>
    ITemplateProvider? Create(PluginContext context);
}
