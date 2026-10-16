using AITaskAgent.Configuration;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Los roles salen de <c>AITaskAgent:LlmProviders</c> tal como los enlaza el framework; la factoría solo ajusta el proveedor
/// (claves <c>Asistente:*</c> en Azure), escribe el log y exige los roles del pipeline.
/// </summary>
public sealed class LlmRoleFactoryTests
{
    private const string Llm = LlmRoleFactory.SectionName;

    /// <summary>Servicio falso con el <see cref="ILlmService.Name"/> que pida el test; guarda la última petición.</summary>
    private sealed class CapturingService(string name) : ILlmService
    {
        public LlmRequest? LastRequest { get; private set; }

        public string Name => name;

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = "ok", FinishReason = FinishReason.Stop }]));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static readonly CapturingService Azure = new(LlmServiceTypes.AzureOpenAI);
    private static readonly CapturingService Other = new("other");

    /// <summary>Contenedor con la configuración y los dos servicios falsos, como lo montan la CLI y la web.</summary>
    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddSingleton<ILlmService>(Other);
        services.AddSingleton<ILlmService>(Azure);
        services.AddAITaskAgent();
        return services.BuildServiceProvider();
    }

    /// <summary>El proveedor «Fake» en Azure, con clave y versión, y los roles del análisis.</summary>
    private static readonly (string, string?)[] AzureProvider =
    [
        .. FakeLlmConfig.Pairs(LlmServiceTypes.AzureOpenAI),
        ($"{Llm}:Providers:Fake:ApiKey", "section-key"),
        ($"{Llm}:Providers:Fake:ApiVersion", "2024-01-01-preview")
    ];

    private static (Dictionary<string, LlmRole> Roles, List<string> Log) Build(
        IConfiguration configuration, Func<string, string?>? getEnvVar = null, IEnumerable<string>? requiredRoles = null)
    {
        var log = new List<string>();
        using var services = Services(configuration);
        var roles = LlmRoleFactory.BuildRoles(services, getEnvVar ?? (_ => null), log.Add, requiredRoles);
        return (roles, log);
    }

    [Fact]
    public void Azure_prefers_Asistente_keys_then_the_environment_then_the_provider()
    {
        var withAsistente = TestConfig.FromPairs([.. AzureProvider,
            ("Asistente:ApiKey", "asistente-key"), ("Asistente:Endpoint", "https://asistente.example/"),
            ("Asistente:ApiVersion", "2025-01-01"),
            ($"{Llm}:Roles:DiAuditor:Fallbacks:0:Provider", "Fake"), ($"{Llm}:Roles:DiAuditor:Fallbacks:0:Model", "other-model")]);
        var (fromAsistente, _) = Build(withAsistente, _ => "env-key");

        var role = fromAsistente["DiAuditor"];
        Assert.Equal("asistente-key", role.Provider.ApiKey);
        Assert.Equal("https://asistente.example/", role.Provider.BaseUrl);
        Assert.Equal("2025-01-01", role.Provider.ApiVersion);
        Assert.Same(Azure, role.Provider.Service);
        // Todos los roles, y sus fallbacks, comparten el proveedor ajustado.
        Assert.Same(role.Provider, fromAsistente["Enricher"].Provider);
        Assert.Same(role.Provider, Assert.Single(role.Fallbacks!).Provider);

        var (fromEnv, _) = Build(TestConfig.FromPairs(AzureProvider), name => name == "Asistente__ApiKey" ? "env-key" : null);
        Assert.Equal("env-key", fromEnv["DiAuditor"].Provider.ApiKey);

        var (fromProvider, _) = Build(TestConfig.FromPairs(AzureProvider));
        Assert.Equal("section-key", fromProvider["DiAuditor"].Provider.ApiKey);
        Assert.Equal("http://fake.invalid/", fromProvider["DiAuditor"].Provider.BaseUrl);
        Assert.Equal("2024-01-01-preview", fromProvider["DiAuditor"].Provider.ApiVersion);
    }

    [Fact]
    public void Asistente_keys_do_not_apply_to_other_service_types()
    {
        var configuration = TestConfig.FromPairs([.. FakeLlmConfig.Pairs("other"),
            ("Asistente:ApiKey", "asistente-key"), ("Asistente:Endpoint", "https://asistente.example/"),
            ($"{Llm}:Providers:Fake:ApiKey", "own-key")]);

        var (roles, _) = Build(configuration, _ => "env-key");

        Assert.Equal("own-key", roles["DiAuditor"].Provider.ApiKey);
        Assert.Equal("http://fake.invalid/", roles["DiAuditor"].Provider.BaseUrl);
        Assert.Same(Other, roles["DiAuditor"].Provider.Service);
    }

    [Fact]
    public void Unexpanded_variable_counts_as_missing_key_and_is_warned()
    {
        var configuration = TestConfig.FromPairs([.. FakeLlmConfig.Pairs("other"), ($"{Llm}:Providers:Fake:ApiKey", "${NOT_DEFINED_CBA_TEST}")]);

        var (roles, log) = Build(configuration);

        Assert.Null(roles["DiAuditor"].Provider.ApiKey);
        Assert.Equal(
            [
                "🤖 Proveedor 'Fake' (other): http://fake.invalid/",
                "⚠️ ADVERTENCIA: el proveedor 'Fake' no tiene ApiKey.",
                "💲 Sin precio en 'Fake' para fake-model: su coste no se calcula (añade una entrada con Name, InputPricePerMillion y " +
                    $"OutputPricePerMillion en {Llm}:Providers:Fake:Models)."
            ],
            log);
    }

    [Fact]
    public void A_model_listed_with_null_prices_is_warned_as_unpriced_and_left_out_of_the_prices()
    {
        // "InputPricePerMillion": null en appsettings llega a la configuración como cadena vacía.
        var configuration = TestConfig.FromPairs([.. FakeLlmConfig.Pairs("other"), ($"{Llm}:Providers:Fake:ApiKey", "own-key"),
            ($"{Llm}:Providers:Fake:Models:0:Name", "fake-model"),
            ($"{Llm}:Providers:Fake:Models:0:InputPricePerMillion", ""),
            ($"{Llm}:Providers:Fake:Models:0:OutputPricePerMillion", "")]);

        var (roles, log) = Build(configuration);

        Assert.Null(roles["DiAuditor"].Provider.CalculateCost("fake-model", 1000, 1000));
        Assert.DoesNotContain(log, line => line.StartsWith("💲 Precios", StringComparison.Ordinal));
        Assert.Contains(log, line => line.StartsWith("💲 Sin precio en 'Fake' para fake-model:", StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_log_masks_the_key()
    {
        var (_, log) = Build(TestConfig.FromPairs(AzureProvider));

        Assert.Equal("🤖 Proveedor 'Fake' (azure-openai): http://fake.invalid/ | ApiVersion: 2024-01-01-preview", log[0]);
        Assert.Equal("🔑 ApiKey de 'Fake' configurada (longitud: 11)", log[1]);
        // Ni un prefijo de la clave (auditoría, H-11): la de AzureProvider empieza por «sect».
        Assert.DoesNotContain(log, line => line.Contains("sect", StringComparison.Ordinal) || line.Contains("prefijo", StringComparison.Ordinal));
    }

    [Fact]
    public void No_providers_fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(TestConfig.FromPairs()));

        Assert.Equal($"{Llm}:Providers no define ningún proveedor.", ex.Message);
    }

    [Fact]
    public void Missing_required_roles_fail_with_their_names()
    {
        var configuration = TestConfig.FromPairs(FakeLlmConfig.Pairs("other", "Enricher", "DiAuditor"));

        var ex = Assert.Throws<InvalidOperationException>(() => Build(configuration));

        Assert.StartsWith($"Faltan roles en {Llm}:Roles: FunctionalAuditor, CodeSmellsAuditor, DeadCodeAuditor, FlowsAuditor, ", ex.Message);
        Assert.DoesNotContain("Enricher", ex.Message);
        Assert.EndsWith("Cada uno necesita Provider y Model.", ex.Message);
    }

    /// <summary>Lo que el framework rechaza al enlazar (aquí, un rol sin Model) sale tal cual: ya no hay modelo del proveedor que heredar.</summary>
    [Fact]
    public void Configuration_errors_are_the_frameworks()
    {
        var configuration = TestConfig.FromPairs([.. FakeLlmConfig.Pairs("other"), ($"{Llm}:Roles:Extra:Provider", "Fake")]);

        var ex = Assert.Throws<InvalidOperationException>(() => Build(configuration));

        Assert.Equal("LLM role 'Extra' does not declare a Model.", ex.Message);
    }

    [Fact]
    public void Configured_role_keeps_what_the_framework_binds()
    {
        var configuration = TestConfig.FromPairs([.. AzureProvider.Where(p => p.Item1 != $"{Llm}:Roles:DiAuditor:Model"),
            ($"{Llm}:Roles:DiAuditor:SystemPrompt", "report_di_system"),
            ($"{Llm}:Roles:DiAuditor:Model", "role-model"),
            ($"{Llm}:Roles:DiAuditor:Inference:Temperature", "0.7"),
            ($"{Llm}:Roles:DiAuditor:Inference:TopP", "0.9"),
            ($"{Llm}:Roles:DiAuditor:Inference:UseMaxCompletionTokens", "false"),
            ($"{Llm}:Roles:DiAuditor:Inference:UseStreaming", "true")]);

        var (roles, _) = Build(configuration);

        var role = roles["DiAuditor"];
        Assert.Equal("role-model", role.Model);
        Assert.Equal("report_di_system", role.SystemPrompt);
        Assert.Equal(0.7f, role.Inference!.Temperature);
        Assert.Equal(0.9f, role.Inference.TopP);
        Assert.False(role.Inference.UseMaxCompletionTokens);
        Assert.True(role.Inference.UseStreaming);
        // Sin Inference en el rol no se inventa ninguna: manda la del proveedor.
        Assert.Null(roles["Enricher"].Inference);
    }

    /// <summary>
    /// Lo que motivó pasar al framework: un parámetro del proveedor que no es de la fachada (p. ej. <c>reasoning_effort</c>)
    /// va en <c>Inference:Options</c> del rol o del proveedor y llega a la petición, que lo reenvía en el cuerpo tal cual.
    /// </summary>
    [Fact]
    public async Task Inference_options_of_the_role_and_the_provider_reach_the_request()
    {
        var configuration = TestConfig.FromPairs([.. AzureProvider,
            ($"{Llm}:Providers:Fake:Inference:RequestTimeoutSeconds", "300"),
            ($"{Llm}:Providers:Fake:Inference:Options:provider_flag", "on"),
            ($"{Llm}:Roles:DiAuditor:Inference:Temperature", "0.1"),
            ($"{Llm}:Roles:DiAuditor:Inference:Options:reasoning_effort", "none"),
            ($"{Llm}:Roles:DiAuditor:Inference:MaxTokens", "65536")]);
        var (roles, _) = Build(configuration);
        var role = roles["DiAuditor"];

        Assert.Equal("none", role.Inference!.GetOption<string>("reasoning_effort"));
        Assert.Equal("on", role.Provider.Inference!.GetOption<string>("provider_flag"));
        // Las claves de fachada que se ordenan antes de «Options» siguen ahí (antes, Options las pisaba).
        Assert.Equal(300, role.Provider.Inference.RequestTimeoutSeconds);
        Assert.Equal(65536, role.Inference.MaxTokens);

        var step = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Probe", role, templateProvider: null, promtTemplateString: "{{Input}}");
        await ((IStep)step).ExecuteAsync(
            new LlmStringStepResult(step) { Content = "hola" }, new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

        var request = Azure.LastRequest!;
        Assert.Equal("none", request.GetOption<string>("reasoning_effort"));
        Assert.Equal("on", request.GetOption<string>("provider_flag"));
        Assert.Equal(65536, request.MaxTokens);
        Assert.Equal(0.1f, request.Temperature);
    }

    /// <summary>
    /// Auditoría H-4: el plazo por intento y el de inactividad del streaming se leen del proveedor
    /// (<c>Inference:*</c>) y del rol, que manda. Sin las claves la opción no se fija y el servicio aplica los suyos.
    /// </summary>
    [Fact]
    public void Llm_timeouts_are_read_from_the_provider_and_the_role()
    {
        var configuration = TestConfig.FromPairs([.. AzureProvider,
            ($"{Llm}:Providers:Fake:Inference:RequestTimeoutSeconds", "300"),
            ($"{Llm}:Providers:Fake:Inference:StreamingInactivityTimeoutSeconds", "0"),
            ($"{Llm}:Roles:FlowsAuditor:Inference:RequestTimeoutSeconds", "900"),
            ($"{Llm}:Roles:FlowsAuditor:Inference:StreamingInactivityTimeoutSeconds", "240"),
            ($"{Llm}:Roles:DiAuditor:Inference:Temperature", "0.2")]);

        var (roles, _) = Build(configuration);
        var provider = roles["DiAuditor"].Provider.Inference!;

        Assert.Equal(300, provider.RequestTimeoutSeconds);
        Assert.Equal(0, provider.StreamingInactivityTimeoutSeconds);
        Assert.Equal(900, roles["FlowsAuditor"].Inference!.RequestTimeoutSeconds);
        Assert.Equal(240, roles["FlowsAuditor"].Inference!.StreamingInactivityTimeoutSeconds);
        Assert.Null(roles["DiAuditor"].Inference!.Options.Property("RequestTimeoutSeconds", StringComparison.OrdinalIgnoreCase));

        var (unconfigured, _) = Build(TestConfig.FromPairs(AzureProvider));
        Assert.Null(unconfigured["DiAuditor"].Provider.Inference);
    }

    [Fact]
    public void Required_roles_can_be_replaced()
    {
        var (roles, _) = Build(TestConfig.FromPairs(FakeLlmConfig.Pairs("other", ChatAgent.RoleName)), requiredRoles: [ChatAgent.RoleName]);

        Assert.Equal([ChatAgent.RoleName], roles.Keys);
    }

    [Fact]
    public void An_explicit_list_is_respected_even_with_the_shared_pass_enabled()
    {
        // La pasada encendida y sin sus roles declarados: con la lista explícita no se piden; con la de por defecto, sí.
        var configuration = TestConfig.FromPairs([.. FakeLlmConfig.Pairs("other", ChatAgent.RoleName), ("CodebaseAnalysis:SharedPass:Enabled", "true")]);

        var (roles, _) = Build(configuration, requiredRoles: [ChatAgent.RoleName]);
        Assert.Equal([ChatAgent.RoleName], roles.Keys);

        var withDefaults = TestConfig.FromPairs([.. FakeLlmConfig.Pairs("other"), ("CodebaseAnalysis:SharedPass:Enabled", "true")]);
        var ex = Assert.Throws<InvalidOperationException>(() => Build(withDefaults));
        Assert.Contains("SharedPassReader", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_role_fails_with_its_name_instead_of_borrowing_another_one()
    {
        // Antes, sin «ExecutiveSynthesizer», la factoría devolvía el primer rol del diccionario (auditoría, H-11).
        var (roles, _) = Build(TestConfig.FromPairs(AzureProvider));
        roles.Remove("ExecutiveSynthesizer");

        var ex = Assert.Throws<InvalidOperationException>(() => AnalysisPipelineFactory.CreatePipeline(
            AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, new Models.AnalysisPipelineInput()));

        Assert.StartsWith("No hay rol LLM 'ExecutiveSynthesizer' (roles recibidos: ApiSecurityAuditor, ArchitectureAuditor, ", ex.Message);
        Assert.Contains($"{Llm}:Roles", ex.Message);
        Assert.Same(roles["DiAuditor"], AnalysisPipelineFactory.ResolveRole(roles, "diauditor"));
    }

    [Fact]
    public void The_roles_built_by_the_factory_cover_every_role_the_pipeline_asks_for()
    {
        var (roles, _) = Build(TestConfig.FromPairs(AzureProvider));

        var pipeline = AnalysisPipelineFactory.CreatePipeline(
            AnalysisPipelineFactory.CreateDefaultTemplateProvider(), roles, new Models.AnalysisPipelineInput { RequestedReports = [.. Enum.GetValues<Models.ReportType>()] });

        Assert.NotEmpty(pipeline.Steps);
    }

    /// <summary>
    /// El JSON del sample: gpt-6-sol en todos los roles salvo el del chat (gpt-5.4), y en esos <c>reasoning_effort: none</c>
    /// (gpt-6-sol rechaza tools con otro valor en chat/completions). <c>MaxTokens</c> va en <c>Inference</c>, como el resto
    /// de la fachada, y <c>Options</c> solo lleva <c>reasoning_effort</c>. Los dos modelos están en <c>Models</c> sin precio:
    /// su coste no se calcula y el log lo avisa.
    /// </summary>
    [Fact]
    public void The_sample_configuration_sets_the_model_and_reasoning_effort_of_every_role()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, AnalysisConfiguration.ConfigFileName))
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddCodebaseAnalysisLlmServices();
        services.AddAITaskAgent();
        using var provider = services.BuildServiceProvider();

        var log = new List<string>();
        var roles = LlmRoleFactory.BuildRoles(provider, _ => null, log.Add, [.. LlmRoleFactory.RequiredRoles, ChatAgent.RoleName]);

        foreach (var name in LlmRoleFactory.RequiredRoles)
        {
            Assert.Equal("gpt-6-sol", roles[name].Model);
            Assert.Equal("none", roles[name].Inference!.GetOption<string>("reasoning_effort"));
            Assert.Equal(65536, roles[name].Inference!.MaxTokens);
            Assert.True(roles[name].Inference!.UseMaxCompletionTokens);

            var inference = configuration.GetSection($"{Llm}:Roles:{name}:Inference");
            Assert.Equal("65536", inference["MaxTokens"]);
            var option = Assert.Single(inference.GetSection("Options").GetChildren());
            Assert.Equal(("reasoning_effort", "none"), (option.Key, option.Value));
        }

        var models = roles[ChatAgent.RoleName].Provider.Models;
        Assert.Equal(["gpt-6-sol", "gpt-6-luna", "gpt-5.6-terra", "gpt-5.4", "gpt-4.1-mini", "gpt-6-sol-global", "gpt-4.1-nano",
            "gpt-5-mini", "gpt-5-nano", "o4-mini", "gpt-oss-120b"], models.Select(m => m.Name));
        Assert.Equal([(2m, 10m), (0.1m, 0.5m), (2m, 12m), (2.5m, 15m), (0.4m, 1.6m), (2m, 10m), (0.1m, 0.4m), (0.25m, 2m), (0.05m, 0.4m), (1.1m, 4.4m), (null, null)],
            models.Select(m => (m.InputPricePerMillion, m.OutputPricePerMillion)));
        Assert.Contains("💲 Precios de 'AzureOpenAI' (USD por millón de tokens, entrada/salida): " +
            "gpt-6-sol 2/10, gpt-6-luna 0.1/0.5, gpt-5.6-terra 2/12, gpt-5.4 2.5/15, gpt-4.1-mini 0.4/1.6, gpt-6-sol-global 2/10, " +
            "gpt-4.1-nano 0.1/0.4, gpt-5-mini 0.25/2, gpt-5-nano 0.05/0.4, o4-mini 1.1/4.4", log);
        // gpt-oss-120b no tiene tarifa: es el único modelo que el aviso de arranque nombra.
        Assert.Contains(log, l => l.StartsWith("💲 Sin precio en 'AzureOpenAI' para gpt-oss-120b:", StringComparison.Ordinal));

        // El chat por defecto es gpt-6-sol con su reasoning_effort none; gpt-5.4 es una variante que no lo necesita.
        var chat = roles[ChatAgent.RoleName];
        Assert.Equal("gpt-6-sol", chat.Model);
        Assert.Equal("none", chat.Inference!.GetOption<string>("reasoning_effort"));
        Assert.Null(roles["Chat.gpt-5.4"].Inference!.GetOption<string>("reasoning_effort"));
        Assert.Equal("none", roles["Chat.gpt-6-sol-global"].Inference!.GetOption<string>("reasoning_effort"));
        // gpt-4.1-* no admiten reasoning_effort; gpt-5-mini/nano y o4-mini tampoco llevan none ni temperatura propia.
        foreach (var id in new[] { "gpt-4.1-mini", "gpt-4.1-nano", "gpt-5-mini", "gpt-5-nano", "o4-mini", "gpt-oss-120b" })
            Assert.Null(roles[$"Chat.{id}"].Inference!.GetOption<string>("reasoning_effort"));
        foreach (var id in new[] { "gpt-5-mini", "gpt-5-nano", "o4-mini" })
            Assert.Null(roles[$"Chat.{id}"].Inference!.Temperature);
        Assert.Equal(32768, chat.Inference.MaxTokens);
        Assert.True(chat.Inference.UseStreaming);
        Assert.True(chat.Provider.Inference!.JsonSupport);
        Assert.Equal(300, chat.Provider.Inference.RequestTimeoutSeconds);
        Assert.Equal("2024-08-01-preview", chat.Provider.ApiVersion);
    }

    [Fact]
    public void The_call_limit_comes_from_Inference_Options_role_over_provider_default_nine_and_zero_means_none()
    {
        var provider = TestConfig.FromPairs([.. AzureProvider, ($"{Llm}:Providers:Fake:Inference:Options:MaxConcurrentRequests", "4")]);
        var overridden = TestConfig.FromPairs([.. AzureProvider, ($"{Llm}:Providers:Fake:Inference:Options:MaxConcurrentRequests", "4"),
            ($"{Llm}:Roles:FunctionalAuditor:Inference:Options:MaxConcurrentRequests", "2")]);
        var unlimited = TestConfig.FromPairs([.. AzureProvider, ($"{Llm}:Providers:Fake:Inference:Options:MaxConcurrentRequests", "0")]);
        var unset = TestConfig.FromPairs(AzureProvider);

        Assert.Equal(4, AnalysisPipelineFactory.CallLimit(Build(provider).Roles.Values));
        Assert.Equal(2, AnalysisPipelineFactory.CallLimit(Build(overridden).Roles.Values));
        Assert.Null(AnalysisPipelineFactory.CallLimit(Build(unlimited).Roles.Values));
        Assert.Equal(9, AnalysisPipelineFactory.CallLimit(Build(unset).Roles.Values));
    }
}
