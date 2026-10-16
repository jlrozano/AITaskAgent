using AITaskAgent.Configuration;
using AITaskAgent.LLM.Configuration;
using OpenAILLmService;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Enrichment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace CodebaseAnalysis.Core.Hosting;

/// <summary>
/// Los límites de tiempo efectivos, una línea por grupo, para volcarlos al arrancar (auditoría §4, «Configuración»): están
/// repartidos en cinco sitios con tres unidades distintas, y el fallo del 28/09 (el chat cortado por el
/// <c>DefaultStepTimeout</c> del framework, que no aparece en ningún JSON) no se veía venir en el log. Cada valor dice de
/// dónde sale cuando no es obvio: del rol, del proveedor o del servicio.
/// </summary>
public static class EffectiveLimits
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Límites de un análisis: los de la app, los del analizador Java, los plazos LLM de los roles del pipeline y los del framework.</summary>
    public static IReadOnlyList<string> DescribeAnalysis(IServiceProvider? serviceProvider, IReadOnlyDictionary<string, LlmRole> roles)
    {
        var configuration = serviceProvider?.GetService(typeof(IConfiguration)) as IConfiguration;
        var timeouts = configuration != null ? AnalysisTimeouts.FromConfiguration(configuration) : new AnalysisTimeouts();
        var java = configuration != null ? JavaAnalyzerOptions.FromConfiguration(configuration) : new JavaAnalyzerOptions();
        var pipelineRoles = roles.Where(r => LlmRoleFactory.RequiredRoles.Contains(r.Key, StringComparer.OrdinalIgnoreCase));

        return
        [
            $"⏱️ Límites de {AnalysisTimeouts.SectionName}: pipeline {Minutes(timeouts.PipelineMinutes)} · detección {Minutes(timeouts.DetectMinutes)} · " +
            $"enriquecimiento {Minutes(timeouts.EnrichMinutes)} · informes {Minutes(timeouts.ReportsMinutes)} · cada auditor {Minutes(timeouts.AuditorMinutes)} · " +
            $"síntesis {Minutes(timeouts.SynthesisMinutes)} · verificador {Minutes(timeouts.VerifierMinutes)}; fijos en el código: facetas 5 min y preparación de la regeneración 5 min.",
            $"⏱️ Límites de {JavaAnalyzerOptions.SectionName}{(java.Enabled ? "" : " (desactivado)")}: proceso Java {Seconds(java.TimeoutSeconds)} (TimeoutSeconds) · " +
            $"paso {Seconds(java.StepTimeoutSeconds)} (StepTimeoutSeconds).",
            $"⏱️ Plazos LLM de los roles del análisis (RequestTimeoutSeconds por intento; StreamingInactivityTimeoutSeconds, solo con streaming): {DescribeRoles(pipelineRoles)}.",
            DescribeFramework(serviceProvider)
        ];
    }

    /// <summary>Límites del chat: el del turno y los plazos LLM de su rol.</summary>
    public static string DescribeChat(ChatOptions options, LlmRole chatRole) =>
        $"⏱️ Límites del chat: turno {Duration(options.TurnTimeout)} ({ChatOptions.SectionName}:TurnTimeoutMinutes) · " +
        $"{DescribeRole(chatRole)}.";

    /// <summary>
    /// <c>AITaskAgent:Timeouts</c>: solo cuentan para los pasos que no fijan su <c>Timeout</c>, que en el análisis y en el
    /// chat no son ninguno (lo comprueba un test), pero los usa cualquier pipeline que no lo haga.
    /// </summary>
    public static string DescribeFramework(IServiceProvider? serviceProvider)
    {
        var options = (serviceProvider?.GetService(typeof(IOptions<AITaskAgentOptions>)) as IOptions<AITaskAgentOptions>)?.Value.Timeouts
            ?? new TimeoutOptions();
        return $"⏱️ Límites de AITaskAgent:Timeouts (framework; para los pasos sin Timeout propio): pipeline {Duration(options.DefaultPipelineTimeout)} " +
            $"(DefaultPipelineTimeout) · paso {Duration(options.DefaultStepTimeout)} (DefaultStepTimeout).";
    }

    /// <summary>Los roles agrupados por sus plazos efectivos, para no repetir la misma línea once veces.</summary>
    internal static string DescribeRoles(IEnumerable<KeyValuePair<string, LlmRole>> roles)
    {
        var groups = roles
            .GroupBy(r => DescribeRole(r.Value))
            .Select(g => $"{g.Key} en {string.Join(", ", g.Select(r => r.Key))}")
            .ToList();
        return groups.Count == 0 ? "ninguno" : string.Join("; ", groups);
    }

    /// <summary>Los plazos efectivos de un rol: el suyo, si no el de su proveedor y si no el del servicio.</summary>
    internal static string DescribeRole(LlmRole role)
    {
        var streaming = role.Inference?.UseStreaming ?? role.Provider.Inference?.UseStreaming ?? false;
        var request = Effective(role, i => i.RequestTimeoutSeconds, OpenAILlmService.DefaultRequestTimeout);
        var inactivity = Effective(role, i => i.StreamingInactivityTimeoutSeconds, OpenAILlmService.DefaultStreamingInactivityTimeout);
        return streaming
            ? $"por intento {request} · inactividad {inactivity} (con streaming)"
            : $"por intento {request} (sin streaming)";
    }

    private static string Effective(LlmRole role, Func<LlmInferenceSettings, int?> read, TimeSpan serviceDefault)
    {
        if (role.Inference is { } inference && read(inference) is int fromRole)
            return $"{Seconds(fromRole)} [rol]";
        if (role.Provider.Inference is { } providerInference && read(providerInference) is int fromProvider)
            return $"{Seconds(fromProvider)} [proveedor]";
        // Los tres tipos de servicio del sample son OpenAILlmService; otro servicio aplica los suyos, o ninguno.
        return role.Provider.Service is OpenAILlmService
            ? $"{Seconds((int)serviceDefault.TotalSeconds)} [servicio]"
            : "el del servicio";
    }

    private static string Minutes(int minutes) =>
        minutes > 0 && minutes <= AnalysisTimeouts.MaxMinutes ? string.Format(Es, "{0} min", minutes) : "sin límite";

    private static string Seconds(int seconds) => seconds > 0 ? string.Format(Es, "{0} s", seconds) : "sin límite";

    private static string Duration(TimeSpan limit) =>
        limit == Timeout.InfiniteTimeSpan || limit <= TimeSpan.Zero ? "sin límite"
        : limit.TotalSeconds < 60 || limit.TotalSeconds % 60 != 0 ? string.Format(Es, "{0:0.#} s", limit.TotalSeconds)
        : string.Format(Es, "{0:0} min", limit.TotalMinutes);
}
