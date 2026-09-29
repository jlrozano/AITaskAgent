using AITaskAgent.LLM.Configuration;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Hosting;

namespace CodebaseAnalysisWeb.Hosting;

/// <summary>
/// Lo que se construye una vez al arrancar desde la configuración: roles LLM (los del pipeline y el del
/// chat), plantillas y límites del chat. Un error aquí aborta el arranque con el mismo mensaje que la CLI.
/// </summary>
public sealed class AnalysisHost
{
    public AnalysisHost(IConfiguration configuration, IServiceProvider serviceProvider, ILogger<AnalysisHost> logger)
    {
        Roles = LlmRoleFactory.BuildRoles(
            configuration,
            serviceProvider,
            AnalysisConfiguration.GetEnvVar,
            message => logger.LogInformation("{Message}", message),
            [.. LlmRoleFactory.RequiredRoles, ChatAgent.RoleName]);
        ChatRole = ChatAgent.EnsureRole(Roles, configuration);
        TemplateProvider = AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        ChatOptions = ChatOptions.FromConfiguration(configuration);
        // Los del chat al arrancar el host; los del análisis, al arrancar cada análisis (AnalysisRunner).
        logger.LogInformation("{Limits}", EffectiveLimits.DescribeChat(ChatOptions, ChatRole));
        logger.LogInformation("{Limits}", EffectiveLimits.DescribeFramework(serviceProvider));

        // Describir imágenes (plan v2 §7.4): el rol de CodebaseAnalysis:Documents:VisionRole con su proveedor y clave.
        var visionRole = CodebaseAnalysis.Core.Documents.DocumentOptions.FromConfiguration(configuration).VisionRole;
        if (!Roles.TryGetValue(visionRole, out var vision))
        {
            ImageDescriptionDisabledReason = $"No hay rol '{visionRole}' para describir imágenes (CodebaseAnalysis:Documents:VisionRole).";
        }
        else if (string.IsNullOrEmpty(vision.Provider.ApiKey))
        {
            ImageDescriptionDisabledReason = $"El proveedor del rol '{visionRole}' no tiene clave: no se pueden describir imágenes.";
        }
        else
        {
            ImageDescriber = new CodebaseAnalysis.Core.Documents.ImageDescriber(vision, TemplateProvider, logger);
        }
    }

    /// <summary>Describe imágenes; null si no hay rol con visión utilizable (<see cref="ImageDescriptionDisabledReason"/>).</summary>
    public CodebaseAnalysis.Core.Documents.ImageDescriber? ImageDescriber { get; }

    public string? ImageDescriptionDisabledReason { get; }

    public Dictionary<string, LlmRole> Roles { get; }
    public LlmRole ChatRole { get; }
    public ITemplateProvider TemplateProvider { get; }
    public ChatOptions ChatOptions { get; }
}
