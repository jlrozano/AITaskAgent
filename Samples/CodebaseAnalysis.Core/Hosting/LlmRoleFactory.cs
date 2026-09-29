using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>Un proveedor ya construido y su modelo por defecto.</summary>
public sealed record ConfiguredProvider(LlmProvider Provider, string? DefaultModel);

/// <summary>
/// Construye proveedores y roles LLM desde <c>LlmConfiguration</c>. Cada rol usa el proveedor que
/// nombra en <c>Provider</c>.
/// </summary>
public static class LlmRoleFactory
{
    /// <summary>MaxOutputTokens de un rol que no lo declara. gpt-5.4 descuenta de aquí también el razonamiento.</summary>
    public const int DefaultMaxOutputTokens = 65536;

    /// <summary>Roles que el pipeline necesita; si faltan en la configuración se crean con el proveedor por defecto.</summary>
    public static IReadOnlyList<string> RequiredRoles { get; } =
    [
        "Enricher", "FunctionalAuditor", "CodeSmellsAuditor", "DeadCodeAuditor",
        "FlowsAuditor", "SecurityBugAuditor", "ArchitectureAuditor", "ApiSecurityAuditor",
        "TechnicalDebtAuditor", "DiAuditor", "ExecutiveSynthesizer"
    ];

    /// <summary>
    /// Un <see cref="LlmProvider"/> por entrada de <c>LlmConfiguration:Providers</c>. El servicio se
    /// resuelve como <see cref="ILlmService"/> con clave igual al <c>ServiceType</c> en minúsculas. Para
    /// Azure siguen teniendo prioridad las claves <c>Asistente:*</c> (y la variable de entorno
    /// <c>Asistente__ApiKey</c>), que es como se configuraba antes.
    /// </summary>
    /// <remarks><c>log</c> recibe una línea por proveedor y otra con el estado de su clave.</remarks>
    public static Dictionary<string, ConfiguredProvider> BuildProviders(
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        Func<string, string?> getEnvVar,
        Action<string> log)
    {
        var llmConfig = configuration.GetSection("LlmConfiguration");
        var providers = new Dictionary<string, ConfiguredProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in llmConfig.GetSection("Providers").GetChildren())
        {
            var serviceType = section["ServiceType"] ?? LlmServiceTypes.AzureOpenAI;
            var serviceKey = serviceType.ToLowerInvariant();
            var service = serviceProvider.GetKeyedService<ILlmService>(serviceKey)
                ?? throw new InvalidOperationException(
                    $"El proveedor '{section.Key}' usa ServiceType '{serviceType}'; este sample admite azure-openai, openai y openrouter.");
            var isAzure = serviceKey == LlmServiceTypes.AzureOpenAI;

            var endpoint = ResolveEndpoint(configuration, section, getEnvVar);
            var apiKey = endpoint.ApiKey;
            var baseUrl = endpoint.BaseUrl
                ?? throw new InvalidOperationException($"El proveedor '{section.Key}' no tiene BaseUrl ni Endpoint.");
            var apiVersion = endpoint.ApiVersion;
            var model = (isAzure ? Value(configuration["Asistente:DespliegueChat"]) : null) ?? Value(section["Model"]);

            log($"🤖 Proveedor '{section.Key}' ({serviceType}): {baseUrl} | modelo por defecto: {model ?? "(ninguno)"}{(apiVersion != null ? $" | ApiVersion: {apiVersion}" : "")}");
            log(string.IsNullOrEmpty(apiKey)
                ? $"⚠️ ADVERTENCIA: el proveedor '{section.Key}' no tiene ApiKey."
                // Sin prefijo: ni cuatro caracteres de la clave llegan al log (auditoría, H-11).
                : $"🔑 ApiKey de '{section.Key}' configurada (longitud: {apiKey.Length})");

            providers[section.Key] = new ConfiguredProvider(
                new LlmProvider
                {
                    ApiKey = apiKey,
                    BaseUrl = baseUrl,
                    ApiVersion = apiVersion,
                    Service = service,
                    Inference = new LlmInferenceSettings
                    {
                        UseMaxCompletionTokens = section.GetValue<bool?>("Inference:UseMaxCompletionTokens") ?? true,
                        JsonSupport = section.GetValue<bool?>("Inference:JsonSupport") ?? true,
                        // Plazo por intento y de inactividad del streaming; sin la clave, los del servicio (10 y 2 min).
                        RequestTimeoutSeconds = section.GetValue<int?>("Inference:RequestTimeoutSeconds"),
                        StreamingInactivityTimeoutSeconds = section.GetValue<int?>("Inference:StreamingInactivityTimeoutSeconds")
                    },
                    Models = ReadModelPrices(section, log)
                },
                model);
        }

        if (providers.Count == 0)
        {
            throw new InvalidOperationException("LlmConfiguration:Providers no define ningún proveedor.");
        }

        return providers;
    }

    /// <summary>
    /// Precios por millón de tokens de <c>LlmConfiguration:Providers:&lt;p&gt;:Models</c>, con la forma de
    /// <see cref="LlmModelSettings"/> del framework (<c>Name</c>, <c>InputPricePerMillion</c>, <c>OutputPricePerMillion</c>), que
    /// es lo que lee <see cref="LlmProvider.CalculateCost"/>. Una entrada sin los dos precios no cuenta: el coste de ese modelo
    /// queda en null y no en 0, que parecería gratis. <c>Name</c> es el nombre del modelo del rol (en Azure, el despliegue).
    /// </summary>
    internal static List<LlmModelSettings> ReadModelPrices(IConfigurationSection provider, Action<string> log)
    {
        var models = new List<LlmModelSettings>();
        var unpriced = new List<string>();
        foreach (var entry in provider.GetSection("Models").GetChildren())
        {
            var name = Value(entry["Name"]);
            if (name == null)
                continue;

            var input = entry.GetValue<decimal?>("InputPricePerMillion");
            var output = entry.GetValue<decimal?>("OutputPricePerMillion");
            if (input is decimal i && output is decimal o && i >= 0 && o >= 0)
                models.Add(new LlmModelSettings(name, i, o, new LlmInferenceSettings()));
            else
                unpriced.Add(name);
        }

        if (models.Count > 0)
        {
            log($"💲 Precios de '{provider.Key}' (USD por millón de tokens, entrada/salida): " +
                string.Join(", ", models.Select(m => FormattableString.Invariant($"{m.Name} {m.InputPricePerMillion}/{m.OutputPricePerMillion}"))));
        }
        if (unpriced.Count > 0)
        {
            log($"💲 Sin precio en '{provider.Key}' para {string.Join(", ", unpriced)}: su coste no se calcula " +
                "(rellena InputPricePerMillion y OutputPricePerMillion en LlmConfiguration:Providers:<proveedor>:Models).");
        }

        return models;
    }

    // Una ${VARIABLE} que no existe queda literal tras la expansión: cuenta como ausente.
    private static string? Value(string? v) => string.IsNullOrWhiteSpace(v) || v.StartsWith("${", StringComparison.Ordinal) ? null : v;

    /// <summary>
    /// Tipo de servicio (en minúsculas), URL, clave y versión de API de un proveedor, con la prioridad de las claves
    /// <c>Asistente:*</c> en Azure. La usan <see cref="BuildProviders"/> y el cliente de embeddings (plan v2 §8.2.2).
    /// </summary>
    public static (string ServiceType, string? BaseUrl, string? ApiKey, string? ApiVersion) ResolveEndpoint(
        IConfiguration configuration, IConfigurationSection section, Func<string, string?> getEnvVar)
    {
        var serviceType = (section["ServiceType"] ?? LlmServiceTypes.AzureOpenAI).ToLowerInvariant();
        var isAzure = serviceType == LlmServiceTypes.AzureOpenAI;
        var apiKey = isAzure
            ? Value(configuration["Asistente:ApiKey"]) ?? Value(getEnvVar("Asistente__ApiKey")) ?? Value(section["ApiKey"])
            : Value(section["ApiKey"]);
        var baseUrl = (isAzure ? Value(configuration["Asistente:Endpoint"]) : null) ?? Value(section["BaseUrl"]) ?? Value(section["Endpoint"]);
        var apiVersion = (isAzure ? Value(configuration["Asistente:ApiVersion"]) : null) ?? Value(section["ApiVersion"]);
        return (serviceType, baseUrl, apiKey, apiVersion);
    }

    /// <summary>
    /// Proveedores (<see cref="BuildProviders"/>) y un rol por entrada de <c>LlmConfiguration:Roles</c>;
    /// los de <paramref name="requiredRoles"/> que falten se crean con el proveedor por defecto.
    /// </summary>
    /// <remarks>Sin <c>requiredRoles</c> se usan los <see cref="RequiredRoles"/>.</remarks>
    public static Dictionary<string, LlmRole> BuildRoles(
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        Func<string, string?> getEnvVar,
        Action<string> log,
        IEnumerable<string>? requiredRoles = null)
    {
        var llmConfig = configuration.GetSection("LlmConfiguration");
        var providers = BuildProviders(configuration, serviceProvider, getEnvVar, log);
        var defaultProviderName = llmConfig["DefaultProvider"] ?? providers.Keys.First();
        if (!providers.TryGetValue(defaultProviderName, out var defaultProvider))
        {
            throw new InvalidOperationException($"DefaultProvider '{defaultProviderName}' no está definido en LlmConfiguration:Providers.");
        }

        var roles = new Dictionary<string, LlmRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var rSection in llmConfig.GetSection("Roles").GetChildren())
        {
            var providerName = rSection["Provider"] ?? defaultProviderName;
            if (!providers.TryGetValue(providerName, out var provider))
            {
                throw new InvalidOperationException($"El rol '{rSection.Key}' usa el proveedor '{providerName}', que no está definido en LlmConfiguration:Providers.");
            }

            var inference = new LlmInferenceSettings
            {
                Temperature = rSection.GetValue<float?>("Temperature") ?? 0.1f,
                MaxTokens = rSection.GetValue<int?>("MaxOutputTokens") ?? DefaultMaxOutputTokens,
                TopP = rSection.GetValue<float?>("TopP"),
                UseMaxCompletionTokens = rSection.GetValue<bool?>("UseMaxCompletionTokens") ?? true,
                // Solo si el rol los declara: sin la clave manda la del proveedor (BaseLlmStep fusiona las Options de los dos).
                RequestTimeoutSeconds = rSection.GetValue<int?>("RequestTimeoutSeconds"),
                StreamingInactivityTimeoutSeconds = rSection.GetValue<int?>("StreamingInactivityTimeoutSeconds")
            };

            // Solo si el rol lo declara: sin la clave, la inferencia queda exactamente como antes
            // (UseStreaming sin fijar, que el framework lee como false).
            if (rSection.GetValue<bool?>("UseStreaming") is bool useStreaming)
            {
                inference = inference with { UseStreaming = useStreaming };
            }

            roles[rSection.Key] = new LlmRole
            {
                Provider = provider.Provider,
                Model = rSection["Model"] ?? provider.DefaultModel
                    ?? throw new InvalidOperationException($"El rol '{rSection.Key}' no tiene Model y su proveedor '{providerName}' tampoco."),
                SystemPrompt = rSection["SystemPrompt"],
                Inference = inference
            };
        }

        foreach (var roleName in requiredRoles ?? RequiredRoles)
        {
            if (!roles.ContainsKey(roleName))
            {
                roles[roleName] = new LlmRole
                {
                    Provider = defaultProvider.Provider,
                    Model = defaultProvider.DefaultModel
                        ?? throw new InvalidOperationException($"El rol '{roleName}' no está configurado y el proveedor por defecto no tiene Model."),
                    Inference = new LlmInferenceSettings
                    {
                        Temperature = 0.1f,
                        MaxTokens = DefaultMaxOutputTokens,
                        UseMaxCompletionTokens = true
                    }
                };
            }
        }

        return roles;
    }
}
