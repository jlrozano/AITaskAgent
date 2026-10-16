using AITaskAgent.LLM.Constants;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using System.Globalization;

namespace CodebaseAnalysis.Core.Progress;

/// <summary>Consumo LLM de un paso (un auditor, la síntesis o el fallback del enriquecimiento) o de la ejecución entera.</summary>
/// <remarks>
/// <see cref="CostUsd"/> suma el coste de las llamadas con precio y es null si ninguna lo tiene; <see cref="UnpricedCalls"/>
/// dice cuántas no lo tienen, así que un coste con llamadas sin precio es un mínimo y no el total.
/// </remarks>
public sealed record LlmUsage
{
    /// <summary>Paso que hizo las llamadas (<c>Auditor_CodeSmells</c>, <c>SynthesizerLlm</c>...); null en el total.</summary>
    public string? Source { get; init; }

    /// <summary>Tipo de informe del auditor o de la investigación (<c>CodeSmells</c>); null si el paso no es ninguno de los dos.</summary>
    public string? Report { get; init; }

    public int Calls { get; init; }
    public long PromptTokens { get; init; }

    /// <summary>Tokens de entrada servidos desde la caché de prompt del proveedor; incluidos en <see cref="PromptTokens"/>.</summary>
    public long CachedPromptTokens { get; init; }

    public long CompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public decimal? CostUsd { get; init; }
    public int UnpricedCalls { get; init; }

    internal LlmUsage Add(LlmResponseEvent call) => this with
    {
        Calls = Calls + 1,
        PromptTokens = PromptTokens + (call.PromptTokens ?? 0),
        CachedPromptTokens = CachedPromptTokens + (call.CachedPromptTokens ?? 0),
        CompletionTokens = CompletionTokens + (call.CompletionTokens ?? 0),
        // Sin reparto, el total del evento; con él, la suma, que es lo que se cobra.
        TotalTokens = TotalTokens + (call.PromptTokens is null && call.CompletionTokens is null
            ? call.TokensUsed
            : (call.PromptTokens ?? 0) + (call.CompletionTokens ?? 0)),
        CostUsd = call.CostUsd is decimal cost ? (CostUsd ?? 0) + cost : CostUsd,
        UnpricedCalls = UnpricedCalls + (call.CostUsd is null ? 1 : 0)
    };

    /// <summary>Como diccionario con las claves en camelCase, para <c>AnalysisEventDto.Data</c> y <c>_run_meta.json</c>.</summary>
    public Dictionary<string, object?> ToData()
    {
        var data = new Dictionary<string, object?>();
        if (Source != null)
            data["source"] = Source;
        if (Report != null)
            data["report"] = Report;
        data["calls"] = Calls;
        data["promptTokens"] = PromptTokens;
        data["cachedPromptTokens"] = CachedPromptTokens;
        data["completionTokens"] = CompletionTokens;
        data["totalTokens"] = TotalTokens;
        data["costUsd"] = CostUsd;
        data["unpricedCalls"] = UnpricedCalls;
        return data;
    }

    /// <summary>
    /// Una línea en español: llamadas, tokens de entrada (y cuántos fueron caché, si alguno), de salida y el coste o por qué
    /// no lo hay.
    /// </summary>
    public string Describe()
    {
        var culture = CultureInfo.GetCultureInfo("es-ES");
        var cost = CostUsd switch
        {
            null => "sin precio configurado",
            decimal c when UnpricedCalls > 0 => string.Format(culture, "{0:0.000} $ ({1} llamadas sin precio: es un mínimo)", c, UnpricedCalls),
            decimal c => string.Format(culture, "{0:0.000} $", c)
        };
        var cached = CachedPromptTokens > 0 ? string.Format(culture, " ({0:N0} en caché)", CachedPromptTokens) : "";
        return string.Format(culture, "{0} llamadas · {1:N0} tokens de entrada{4} · {2:N0} de salida · coste {3}",
            Calls, PromptTokens, CompletionTokens, cost, cached);
    }
}

/// <summary>El consumo agregado de una ejecución: el total y uno por paso, en el orden en que llamaron al modelo.</summary>
public sealed record LlmUsageSummary(LlmUsage Total, IReadOnlyList<LlmUsage> BySource)
{
    public Dictionary<string, object?> ToData()
    {
        var data = Total.ToData();
        data["bySource"] = BySource.Select(s => (object?)s.ToData()).ToList();
        return data;
    }
}

/// <summary>
/// Suma el consumo LLM de una ejecución a partir de los <see cref="LlmResponseEvent"/> finales del canal, que llevan los
/// tokens de entrada y salida y el coste de cada llamada (una llamada, un evento final de la opción principal). Lo usan la
/// CLI y la web, que se suscriben al canal del pipeline; es seguro entre hilos porque los auditores corren en paralelo.
/// </summary>
public sealed class LlmUsageTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LlmUsage> _bySource = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private LlmUsage _total = new();

    /// <summary>Cuenta el evento si es la respuesta final de una llamada; el resto (trozos de streaming, razonamiento...) no.</summary>
    public void Observe(IProgressEvent evt)
    {
        if (evt is not LlmResponseEvent { FinishReason: not FinishReason.Streaming, IsThinking: false } call)
            return;

        // Las opciones alternativas no traen reparto ni coste: el uso va en el evento de la principal.
        if (call.PromptTokens is null && call.CompletionTokens is null && call.CostUsd is null && call.ChoiceIndex != 0)
            return;

        var source = string.IsNullOrEmpty(call.StepName) ? "(sin paso)" : call.StepName;
        lock (_lock)
        {
            if (!_bySource.TryGetValue(source, out var usage))
            {
                usage = new LlmUsage
                {
                    Source = source,
                    Report = ReportOf(source)
                };
                _order.Add(source);
            }
            _bySource[source] = usage.Add(call);
            _total = _total.Add(call);
        }
    }

    /// <summary>
    /// El informe al que se atribuye un paso: el de su auditor o el de una de sus investigaciones por subsistema (cuyo coste es
    /// del informe aunque cada una sea su propia fuente). null si no es ninguno de los dos.
    /// </summary>
    private static string? ReportOf(string source) =>
        AnalysisPipelineFactory.TryParseAuditorStepName(source, out var auditor) ? auditor.ToString()
        : AnalysisPipelineFactory.TryParseInvestigationStepName(source, out var investigated, out _) ? investigated.ToString()
        : null;

    public LlmUsageSummary Snapshot()
    {
        lock (_lock)
        {
            return new LlmUsageSummary(_total, [.. _order.Select(s => _bySource[s])]);
        }
    }
}
