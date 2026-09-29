using AITaskAgent.LLM.Abstractions;
using CodebaseAnalysis.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

public sealed class LlmRoleFactoryTests
{
    private static readonly CannedLlmService Fake = new("unused");

    /// <summary>Contenedor con un servicio falso bajo cada clave que usan los tests.</summary>
    private static IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ILlmService>(LlmServiceTypes.AzureOpenAI, Fake);
        services.AddKeyedSingleton<ILlmService>("fake", Fake);
        return services.BuildServiceProvider();
    }

    private static readonly (string, string?)[] AzureProvider =
    [
        ("LlmConfiguration:DefaultProvider", "Main"),
        ("LlmConfiguration:Providers:Main:ServiceType", "azure-openai"),
        ("LlmConfiguration:Providers:Main:ApiKey", "section-key"),
        ("LlmConfiguration:Providers:Main:BaseUrl", "https://section.example/"),
        ("LlmConfiguration:Providers:Main:ApiVersion", "2024-01-01"),
        ("LlmConfiguration:Providers:Main:Model", "section-model")
    ];

    private static (Dictionary<string, ConfiguredProvider> Providers, List<string> Log) BuildProviders(
        IConfiguration configuration, Func<string, string?>? getEnvVar = null)
    {
        var log = new List<string>();
        var providers = LlmRoleFactory.BuildProviders(configuration, Services(), getEnvVar ?? (_ => null), log.Add);
        return (providers, log);
    }

    [Fact]
    public void Azure_prefers_Asistente_keys_then_the_environment_then_the_section()
    {
        var withAsistente = TestConfig.FromPairs([.. AzureProvider,
            ("Asistente:ApiKey", "asistente-key"), ("Asistente:Endpoint", "https://asistente.example/"),
            ("Asistente:ApiVersion", "2025-01-01"), ("Asistente:DespliegueChat", "asistente-model")]);
        var (fromAsistente, _) = BuildProviders(withAsistente, _ => "env-key");

        var main = fromAsistente["Main"];
        Assert.Equal("asistente-key", main.Provider.ApiKey);
        Assert.Equal("https://asistente.example/", main.Provider.BaseUrl);
        Assert.Equal("2025-01-01", main.Provider.ApiVersion);
        Assert.Equal("asistente-model", main.DefaultModel);
        Assert.Same(Fake, main.Provider.Service);

        var (fromEnv, _) = BuildProviders(TestConfig.FromPairs(AzureProvider), name => name == "Asistente__ApiKey" ? "env-key" : null);
        Assert.Equal("env-key", fromEnv["Main"].Provider.ApiKey);

        var (fromSection, _) = BuildProviders(TestConfig.FromPairs(AzureProvider));
        Assert.Equal("section-key", fromSection["Main"].Provider.ApiKey);
        Assert.Equal("https://section.example/", fromSection["Main"].Provider.BaseUrl);
        Assert.Equal("section-model", fromSection["Main"].DefaultModel);
    }

    [Fact]
    public void Asistente_keys_do_not_apply_to_other_service_types()
    {
        var configuration = TestConfig.FromPairs(
            ("Asistente:ApiKey", "asistente-key"),
            ("LlmConfiguration:Providers:Other:ServiceType", "fake"),
            ("LlmConfiguration:Providers:Other:ApiKey", "own-key"),
            ("LlmConfiguration:Providers:Other:Endpoint", "https://other.example/"));

        var (providers, _) = BuildProviders(configuration, _ => "env-key");

        Assert.Equal("own-key", providers["Other"].Provider.ApiKey);
        Assert.Equal("https://other.example/", providers["Other"].Provider.BaseUrl);
    }

    [Fact]
    public void Unexpanded_variable_counts_as_missing_key_and_is_warned()
    {
        var configuration = TestConfig.FromPairs(
            ("LlmConfiguration:Providers:P:ServiceType", "fake"),
            ("LlmConfiguration:Providers:P:ApiKey", "${NOT_DEFINED}"),
            ("LlmConfiguration:Providers:P:BaseUrl", "https://p.example/"));

        var (providers, log) = BuildProviders(configuration);

        Assert.Null(providers["P"].Provider.ApiKey);
        Assert.Equal(
            [
                "🤖 Proveedor 'P' (fake): https://p.example/ | modelo por defecto: (ninguno)",
                "⚠️ ADVERTENCIA: el proveedor 'P' no tiene ApiKey."
            ],
            log);
    }

    [Fact]
    public void Provider_log_masks_the_key()
    {
        var (_, log) = BuildProviders(TestConfig.FromPairs(AzureProvider));

        Assert.Equal(
            [
                "🤖 Proveedor 'Main' (azure-openai): https://section.example/ | modelo por defecto: section-model | ApiVersion: 2024-01-01",
                "🔑 ApiKey de 'Main' configurada (longitud: 11)"
            ],
            log);
        // Ni un prefijo de la clave (auditoría, H-11): la de AzureProvider empieza por «sect».
        Assert.DoesNotContain(log, line => line.Contains("sect***", StringComparison.Ordinal) || line.Contains("prefijo", StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_without_BaseUrl_nor_Endpoint_fails_with_the_same_message()
    {
        var configuration = TestConfig.FromPairs(("LlmConfiguration:Providers:P:ServiceType", "fake"));

        var ex = Assert.Throws<InvalidOperationException>(() => BuildProviders(configuration));

        Assert.Equal("El proveedor 'P' no tiene BaseUrl ni Endpoint.", ex.Message);
    }

    [Fact]
    public void Unknown_service_type_fails_with_the_same_message()
    {
        var configuration = TestConfig.FromPairs(
            ("LlmConfiguration:Providers:P:ServiceType", "gemini"),
            ("LlmConfiguration:Providers:P:BaseUrl", "https://p.example/"));

        var ex = Assert.Throws<InvalidOperationException>(() => BuildProviders(configuration));

        Assert.Equal("El proveedor 'P' usa ServiceType 'gemini'; este sample admite azure-openai, openai y openrouter.", ex.Message);
    }

    [Fact]
    public void No_providers_fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProviders(TestConfig.FromPairs()));

        Assert.Equal("LlmConfiguration:Providers no define ningún proveedor.", ex.Message);
    }

    [Fact]
    public void Role_with_undefined_provider_fails_with_the_same_message()
    {
        var configuration = TestConfig.FromPairs([.. AzureProvider, ("LlmConfiguration:Roles:DiAuditor:Provider", "Missing")]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => LlmRoleFactory.BuildRoles(configuration, Services(), _ => null, _ => { }));

        Assert.Equal("El rol 'DiAuditor' usa el proveedor 'Missing', que no está definido en LlmConfiguration:Providers.", ex.Message);
    }

    [Fact]
    public void Undefined_default_provider_fails()
    {
        var configuration = TestConfig.FromPairs(
            [.. AzureProvider.Where(p => p.Item1 != "LlmConfiguration:DefaultProvider"), ("LlmConfiguration:DefaultProvider", "Nope")]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => LlmRoleFactory.BuildRoles(configuration, Services(), _ => null, _ => { }));

        Assert.Equal("DefaultProvider 'Nope' no está definido en LlmConfiguration:Providers.", ex.Message);
    }

    [Fact]
    public void Missing_required_roles_are_filled_from_the_default_provider()
    {
        var roles = LlmRoleFactory.BuildRoles(TestConfig.FromPairs(AzureProvider), Services(), _ => null, _ => { });

        Assert.Equal(LlmRoleFactory.RequiredRoles.Order(), roles.Keys.Order());
        foreach (var role in roles.Values)
        {
            Assert.Equal("section-model", role.Model);
            Assert.Null(role.SystemPrompt);
            Assert.Equal(0.1f, role.Inference!.Temperature);
            Assert.Equal(LlmRoleFactory.DefaultMaxOutputTokens, role.Inference.MaxTokens);
            Assert.True(role.Inference.UseMaxCompletionTokens);
        }
    }

    [Fact]
    public void Configured_role_reads_its_settings()
    {
        var configuration = TestConfig.FromPairs([.. AzureProvider,
            ("LlmConfiguration:Roles:DiAuditor:SystemPrompt", "report_di_system"),
            ("LlmConfiguration:Roles:DiAuditor:Model", "role-model"),
            ("LlmConfiguration:Roles:DiAuditor:Temperature", "0.7"),
            ("LlmConfiguration:Roles:DiAuditor:MaxOutputTokens", "1234"),
            ("LlmConfiguration:Roles:DiAuditor:TopP", "0.9"),
            ("LlmConfiguration:Roles:DiAuditor:UseMaxCompletionTokens", "false")]);

        var role = LlmRoleFactory.BuildRoles(configuration, Services(), _ => null, _ => { })["DiAuditor"];

        Assert.Equal("role-model", role.Model);
        Assert.Equal("report_di_system", role.SystemPrompt);
        Assert.Equal(0.7f, role.Inference!.Temperature);
        Assert.Equal(1234, role.Inference.MaxTokens);
        Assert.Equal(0.9f, role.Inference.TopP);
        Assert.False(role.Inference.UseMaxCompletionTokens);
    }

    [Fact]
    public void UseStreaming_is_read_and_left_unset_by_default()
    {
        var configuration = TestConfig.FromPairs([.. AzureProvider,
            ("LlmConfiguration:Roles:Streamer:UseStreaming", "true"),
            ("LlmConfiguration:Roles:Plain:Temperature", "0.2")]);

        var roles = LlmRoleFactory.BuildRoles(configuration, Services(), _ => null, _ => { });

        Assert.True(roles["Streamer"].Inference!.UseStreaming);
        Assert.False(roles["Plain"].Inference!.UseStreaming);
        // Sin la clave, la inferencia queda como antes de que existiera: la opción no se fija.
        Assert.Null(roles["Plain"].Inference!.Options.Property("UseStreaming", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Auditoría H-4: el plazo por intento y el de inactividad del streaming se leen del proveedor
    /// (<c>Inference:*</c>) y del rol, que manda. Sin las claves la opción no se fija y el servicio aplica los suyos.
    /// </summary>
    [Fact]
    public void Llm_timeouts_are_read_from_the_provider_and_the_role()
    {
        var configuration = TestConfig.FromPairs([.. AzureProvider,
            ("LlmConfiguration:Providers:Main:Inference:RequestTimeoutSeconds", "300"),
            ("LlmConfiguration:Providers:Main:Inference:StreamingInactivityTimeoutSeconds", "0"),
            ("LlmConfiguration:Roles:Slow:RequestTimeoutSeconds", "900"),
            ("LlmConfiguration:Roles:Slow:StreamingInactivityTimeoutSeconds", "240"),
            ("LlmConfiguration:Roles:Plain:Temperature", "0.2")]);

        var roles = LlmRoleFactory.BuildRoles(configuration, Services(), _ => null, _ => { });
        var provider = roles["Plain"].Provider.Inference!;

        Assert.Equal(300, provider.RequestTimeoutSeconds);
        Assert.Equal(0, provider.StreamingInactivityTimeoutSeconds);
        Assert.Equal(900, roles["Slow"].Inference!.RequestTimeoutSeconds);
        Assert.Equal(240, roles["Slow"].Inference!.StreamingInactivityTimeoutSeconds);
        Assert.Null(roles["Plain"].Inference!.Options.Property("RequestTimeoutSeconds", StringComparison.OrdinalIgnoreCase));

        var unconfigured = LlmRoleFactory.BuildRoles(TestConfig.FromPairs(AzureProvider), Services(), _ => null, _ => { })["DiAuditor"];
        Assert.Null(unconfigured.Provider.Inference!.RequestTimeoutSeconds);
        Assert.Null(unconfigured.Provider.Inference!.StreamingInactivityTimeoutSeconds);
    }

    [Fact]
    public void Extra_required_roles_can_be_requested()
    {
        var roles = LlmRoleFactory.BuildRoles(TestConfig.FromPairs(AzureProvider), Services(), _ => null, _ => { }, ["ChatAssistant"]);

        Assert.Equal(["ChatAssistant"], roles.Keys);
    }

    [Fact]
    public void A_missing_role_fails_with_its_name_instead_of_borrowing_another_one()
    {
        // Antes, sin «ExecutiveSynthesizer», la factoría devolvía el primer rol del diccionario (auditoría, H-11).
        var roles = LlmRoleFactory.BuildRoles(TestConfig.FromPairs(AzureProvider), Services(), _ => null, _ => { });
        roles.Remove("ExecutiveSynthesizer");

        var ex = Assert.Throws<InvalidOperationException>(() => AnalysisPipelineFactory.CreatePipeline(
            AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, new Models.AnalysisPipelineInput()));

        Assert.StartsWith("No hay rol LLM 'ExecutiveSynthesizer' (roles recibidos: ApiSecurityAuditor, ArchitectureAuditor, ", ex.Message);
        Assert.Contains("LlmConfiguration:Roles", ex.Message);
        Assert.Same(roles["DiAuditor"], AnalysisPipelineFactory.ResolveRole(roles, "diauditor"));
    }

    [Fact]
    public void The_roles_built_by_the_factory_cover_every_role_the_pipeline_asks_for()
    {
        var roles = LlmRoleFactory.BuildRoles(TestConfig.FromPairs(AzureProvider), Services(), _ => null, _ => { });

        var pipeline = AnalysisPipelineFactory.CreatePipeline(
            AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, new Models.AnalysisPipelineInput { RequestedReports = [.. Enum.GetValues<Models.ReportType>()] });

        Assert.NotEmpty(pipeline.Steps);
    }
}
