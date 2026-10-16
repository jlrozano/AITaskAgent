using AITaskAgent.Configuration;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Volcado de los límites de tiempo efectivos al arrancar (auditoría §4, «Configuración»): una línea por grupo, con el valor
/// que manda de verdad y, en los plazos LLM, si sale del rol, del proveedor o del servicio.
/// </summary>
public sealed class EffectiveLimitsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddCodebaseAnalysisLlmServices();
        services.AddAITaskAgent();
        return services.BuildServiceProvider();
    }

    private const string Llm = LlmRoleFactory.SectionName;

    /// <summary>Un proveedor Azure y los roles del análisis y del chat.</summary>
    private static readonly (string, string?)[] AnalysisAndChatRoles =
        FakeLlmConfig.Pairs(LlmServiceTypes.AzureOpenAI, [.. LlmRoleFactory.RequiredRoles, ChatAgent.RoleName]);

    private static readonly (string, string?)[] Config =
    [
        ("CodebaseAnalysis:Timeouts:PipelineMinutes", "0"),
        ("CodebaseAnalysis:Timeouts:AuditorMinutes", "25"),
        ("JavaAnalyzer:TimeoutSeconds", "1200"),
        ("JavaAnalyzer:StepTimeoutSeconds", "0"),
        ("AITaskAgent:Timeouts:DefaultStepTimeout", "00:02:00"),
        .. AnalysisAndChatRoles,
        ($"{Llm}:Providers:Fake:Inference:RequestTimeoutSeconds", "300"),
        ($"{Llm}:Roles:FlowsAuditor:Inference:RequestTimeoutSeconds", "900"),
        ($"{Llm}:Roles:ChatAssistant:Inference:UseStreaming", "true"),
        ($"{Llm}:Roles:ChatAssistant:Inference:StreamingInactivityTimeoutSeconds", "240"),
        ("CodebaseAnalysis:Chat:TurnTimeoutMinutes", "7")
    ];

    [Fact]
    public void The_analysis_dump_has_one_line_per_group_with_the_effective_values()
    {
        var configuration = TestConfig.FromPairs(Config);
        using var services = Services(configuration);
        var roles = LlmRoleFactory.BuildRoles(services, _ => null, _ => { }, [.. LlmRoleFactory.RequiredRoles, ChatAgent.RoleName]);

        var lines = EffectiveLimits.DescribeAnalysis(services, roles);

        Assert.Equal(4, lines.Count);
        Assert.Equal("⏱️ Límites de CodebaseAnalysis:Timeouts: pipeline sin límite · detección 15 min · enriquecimiento 20 min · informes 60 min · " +
            "cada auditor 25 min · síntesis 15 min · verificador 10 min; fijos en el código: facetas 5 min y preparación de la regeneración 5 min.", lines[0]);
        Assert.Equal("⏱️ Límites de JavaAnalyzer: proceso Java 1200 s (TimeoutSeconds) · paso sin límite (StepTimeoutSeconds).", lines[1]);
        // Los once roles del análisis, agrupados; el del chat no está en esta línea.
        Assert.StartsWith("⏱️ Plazos LLM de los roles del análisis", lines[2]);
        Assert.Contains("por intento 300 s [proveedor] (sin streaming) en ApiSecurityAuditor, ArchitectureAuditor, CodeSmellsAuditor, DeadCodeAuditor, DiAuditor", lines[2]);
        Assert.Contains("por intento 900 s [rol] (sin streaming) en FlowsAuditor", lines[2]);
        Assert.DoesNotContain("ChatAssistant", lines[2]);
        Assert.Equal("⏱️ Límites de AITaskAgent:Timeouts (framework; para los pasos sin Timeout propio): pipeline 10 min (DefaultPipelineTimeout) · " +
            "paso 2 min (DefaultStepTimeout).", lines[3]);
    }

    [Fact]
    public void The_chat_dump_has_the_turn_limit_and_the_streaming_limits_of_its_role()
    {
        var configuration = TestConfig.FromPairs(Config);
        using var services = Services(configuration);
        var roles = LlmRoleFactory.BuildRoles(services, _ => null, _ => { }, [.. LlmRoleFactory.RequiredRoles, ChatAgent.RoleName]);

        var line = EffectiveLimits.DescribeChat(ChatOptions.FromConfiguration(configuration), roles[ChatAgent.RoleName]);

        Assert.Equal("⏱️ Límites del chat: turno 7 min (CodebaseAnalysis:Chat:TurnTimeoutMinutes) · por intento 300 s [proveedor] · " +
            "inactividad 240 s [rol] (con streaming).", line);
    }

    /// <summary>El volcado con la configuración del sample, tal como sale al arrancar un análisis y la web.</summary>
    [Fact]
    public void The_sample_configuration_dump()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json"))
            .Build();
        using var services = Services(configuration);
        var roles = LlmRoleFactory.BuildRoles(services, _ => null, _ => { }, [.. LlmRoleFactory.RequiredRoles, ChatAgent.RoleName]);

        var lines = EffectiveLimits.DescribeAnalysis(services, roles).Append(EffectiveLimits.DescribeChat(ChatOptions.FromConfiguration(configuration), roles[ChatAgent.RoleName]));

        foreach (var line in lines)
            output.WriteLine(line);
        Assert.Contains(lines, l => l.Contains("por intento 300 s [proveedor] (sin streaming) en ApiSecurityAuditor,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("turno 5 min", StringComparison.Ordinal) && l.Contains("inactividad 300 s [rol] (con streaming)", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_any_setting_the_service_defaults_are_named_as_such()
    {
        var configuration = TestConfig.FromPairs([.. AnalysisAndChatRoles, ($"{Llm}:Roles:ChatAssistant:Inference:UseStreaming", "true")]);
        using var services = Services(configuration);
        var roles = LlmRoleFactory.BuildRoles(services, _ => null, _ => { }, [.. LlmRoleFactory.RequiredRoles, ChatAgent.RoleName]);

        Assert.Equal("por intento 600 s [servicio] · inactividad 120 s [servicio] (con streaming)", EffectiveLimits.DescribeRole(roles[ChatAgent.RoleName]));
        // En el orden de la configuración, que ordena las claves.
        Assert.Equal("por intento 600 s [servicio] (sin streaming) en " + string.Join(", ", LlmRoleFactory.RequiredRoles.Order(StringComparer.OrdinalIgnoreCase)),
            EffectiveLimits.DescribeRoles(roles.Where(r => r.Key != ChatAgent.RoleName)));
        Assert.Contains("pipeline 10 min (DefaultPipelineTimeout) · paso 1 min (DefaultStepTimeout)", EffectiveLimits.DescribeFramework(null));
    }
}
