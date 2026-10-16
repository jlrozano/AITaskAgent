using AITaskAgent.Configuration;
using AITaskAgent.LLM.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>
/// Roles LLM de la app a partir de los del framework: <c>AITaskAgent:LlmProviders</c>, que <c>AddAITaskAgent()</c> enlaza en
/// <see cref="IOptions{LlmEnvironment}"/> (proveedores, roles, <c>Inference</c> con sus <c>Options</c>, fallbacks). Encima
/// solo va lo propio del sample: en Azure, la prioridad de las claves <c>Asistente:*</c>; una <c>${VARIABLE}</c> sin
/// expandir cuenta como clave ausente; el log de claves y precios, y los roles que el pipeline necesita.
/// </summary>
public static class LlmRoleFactory
{
    /// <summary>Sección del framework con proveedores y roles.</summary>
    public const string SectionName = AITaskAgentConfigurationKeys.LlmProviders;

    /// <summary>Roles que el pipeline necesita; si falta alguno en la configuración, <see cref="BuildRoles"/> falla con sus nombres.</summary>
    public static IReadOnlyList<string> RequiredRoles { get; } =
    [
        "Enricher", "FunctionalAuditor", "CodeSmellsAuditor", "DeadCodeAuditor",
        "FlowsAuditor", "SecurityBugAuditor", "ArchitectureAuditor", "ApiSecurityAuditor",
        "TechnicalDebtAuditor", "DiAuditor", "ExecutiveSynthesizer", "ReportVerifier"
    ];

    /// <summary>
    /// Los roles de <see cref="IOptions{LlmEnvironment}"/>, cada uno con su proveedor ajustado (<see cref="ResolveEndpoint"/>).
    /// Falla si no hay proveedores o si falta alguno de <paramref name="requiredRoles"/>; sin él, los <see cref="RequiredRoles"/> más los
    /// de la pasada compartida si está activada (<see cref="SharedPassRoles"/>). Una lista explícita se respeta tal cual: quien la pasa
    /// añade los de la pasada si los necesita.
    /// </summary>
    /// <remarks>
    /// <c>log</c> recibe, por proveedor, una línea con su URL, otra con el estado de su clave y las de sus precios. Los errores
    /// de la configuración en sí (rol sin <c>Model</c>, proveedor desconocido, <c>DefaultRole</c> que no existe…) los lanza el
    /// framework al enlazarla.
    /// </remarks>
    public static Dictionary<string, LlmRole> BuildRoles(
        IServiceProvider serviceProvider,
        Func<string, string?> getEnvVar,
        Action<string> log,
        IEnumerable<string>? requiredRoles = null)
    {
        var environment = serviceProvider.GetRequiredService<IOptions<LlmEnvironment>>().Value;
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        if (environment.Providers.Count == 0)
        {
            throw new InvalidOperationException($"{SectionName}:Providers no define ningún proveedor.");
        }

        // El rol del framework guarda el objeto proveedor, no su nombre: cada rol se rehace con el proveedor ajustado.
        var providers = environment.Providers.ToDictionary(p => p.Value, p => WithEndpoint(p.Value, configuration, getEnvVar));
        var roles = new Dictionary<string, LlmRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, role) in environment.Roles)
        {
            roles[name] = new LlmRole
            {
                Provider = providers[role.Provider],
                Model = role.Model,
                SystemPrompt = role.SystemPrompt,
                Inference = role.Inference,
                Fallbacks = role.Fallbacks?.Select(f => f with { Provider = providers[f.Provider] }).ToList(),
                Tools = role.Tools
            };
        }

        foreach (var (name, original) in environment.Providers)
        {
            var provider = providers[original];
            log($"🤖 Proveedor '{name}' ({provider.Service.Name}): {provider.BaseUrl}{(provider.ApiVersion != null ? $" | ApiVersion: {provider.ApiVersion}" : "")}");
            log(string.IsNullOrEmpty(provider.ApiKey)
                ? $"⚠️ ADVERTENCIA: el proveedor '{name}' no tiene ApiKey."
                // Sin prefijo: ni cuatro caracteres de la clave llegan al log (auditoría, H-11).
                : $"🔑 ApiKey de '{name}' configurada (longitud: {provider.ApiKey.Length})");
            LogModelPrices(name, provider, roles.Values, log);
        }

        var missing = (requiredRoles ?? RequiredRoles.Concat(SharedPassRoles(configuration))).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !roles.ContainsKey(r)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Faltan roles en {SectionName}:Roles: {string.Join(", ", missing)}. Cada uno necesita Provider y Model.");
        }

        return roles;
    }

    /// <summary>
    /// Los roles de la pasada compartida (lector y juez) si <c>CodebaseAnalysis:SharedPass:Enabled</c> es true; si no, ninguno:
    /// apagada, la configuración no tiene por qué declararlos.
    /// </summary>
    public static IReadOnlyList<string> SharedPassRoles(IConfiguration configuration) =>
        SharedPass.SharedPassOptions.FromConfiguration(configuration) is { Enabled: true } options ? [options.ReaderRole, options.JudgeRole] : [];

    /// <summary>
    /// Precios por millón de tokens de <c>Providers:&lt;p&gt;:Models</c>, que es lo que lee <see cref="LlmProvider.CalculateCost"/>,
    /// y los modelos de los roles de ese proveedor sin entrada o con algún precio a null: su coste queda en null y no en 0,
    /// que parecería gratis.
    /// </summary>
    private static void LogModelPrices(string name, LlmProvider provider, IEnumerable<LlmRole> roles, Action<string> log)
    {
        var priced = provider.Models.Where(m => m.InputPricePerMillion.HasValue && m.OutputPricePerMillion.HasValue).ToList();
        if (priced.Count > 0)
        {
            log($"💲 Precios de '{name}' (USD por millón de tokens, entrada/salida): " +
                string.Join(", ", priced.Select(m => FormattableString.Invariant($"{m.Name} {m.InputPricePerMillion}/{m.OutputPricePerMillion}"))));
        }

        var unpriced = roles
            .Where(r => ReferenceEquals(r.Provider, provider))
            .Select(r => r.Model)
            .Where(model => !priced.Any(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unpriced.Count > 0)
        {
            log($"💲 Sin precio en '{name}' para {string.Join(", ", unpriced)}: su coste no se calcula " +
                $"(añade una entrada con Name, InputPricePerMillion y OutputPricePerMillion en {SectionName}:Providers:{name}:Models).");
        }
    }

    private static LlmProvider WithEndpoint(LlmProvider provider, IConfiguration configuration, Func<string, string?> getEnvVar)
    {
        var isAzure = string.Equals(provider.Service.Name, LlmServiceTypes.AzureOpenAI, StringComparison.OrdinalIgnoreCase);
        var endpoint = ResolveEndpoint(configuration, isAzure, provider.BaseUrl, provider.ApiKey, provider.ApiVersion, getEnvVar);
        return new LlmProvider
        {
            // El framework ya exige BaseUrl: sin Asistente:Endpoint queda la suya.
            BaseUrl = endpoint.BaseUrl ?? provider.BaseUrl,
            ApiKey = endpoint.ApiKey,
            ApiVersion = endpoint.ApiVersion,
            Inference = provider.Inference,
            Models = provider.Models,
            Service = provider.Service
        };
    }

    // Una ${VARIABLE} que no existe queda literal tras la expansión: cuenta como ausente.
    private static string? Value(string? v) => string.IsNullOrWhiteSpace(v) || v.StartsWith("${", StringComparison.Ordinal) ? null : v;

    /// <summary>
    /// URL, clave y versión de API de un proveedor, con la prioridad de las claves <c>Asistente:*</c> (y de la variable de
    /// entorno <c>Asistente__ApiKey</c>) en Azure, que es como se configuraba antes. La usan <see cref="BuildRoles"/> y el
    /// cliente de embeddings (plan v2 §8.2.2).
    /// </summary>
    public static (string? BaseUrl, string? ApiKey, string? ApiVersion) ResolveEndpoint(
        IConfiguration configuration, bool isAzure, string? baseUrl, string? apiKey, string? apiVersion, Func<string, string?> getEnvVar)
    {
        var key = isAzure
            ? Value(configuration["Asistente:ApiKey"]) ?? Value(getEnvVar("Asistente__ApiKey")) ?? Value(apiKey)
            : Value(apiKey);
        var url = (isAzure ? Value(configuration["Asistente:Endpoint"]) : null) ?? Value(baseUrl);
        var version = (isAzure ? Value(configuration["Asistente:ApiVersion"]) : null) ?? Value(apiVersion);
        return (url, key, version);
    }
}
