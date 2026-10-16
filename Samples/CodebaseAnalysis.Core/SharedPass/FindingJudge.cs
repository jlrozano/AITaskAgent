using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Results;
using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;

namespace CodebaseAnalysis.Core.SharedPass;

/// <summary>Lo que se pinta en las plantillas del juez: la tanda de candidatos y su número.</summary>
public sealed record SharedPassJudgeMessage(string Candidates, string Count = "");

/// <summary>Entrada del paso del juez (<c>{{ input.candidates }}</c>, <c>{{ input.count }}</c>).</summary>
public sealed class SharedPassJudgeInput : StepResult<SharedPassJudgeMessage>
{
    public SharedPassJudgeInput(SharedPassJudgeMessage message)
        : base(new EmptyStep(nameof(SharedPassJudgeInput)), message)
    {
    }
}

/// <summary>
/// El paso LLM del juez: sin herramientas y sin conversación. Los candidatos llevan código, que puede traer <c>{{ … }}</c> o
/// <c>@[ruta]</c>: como en <see cref="SharedPassReaderStep"/>, se pintan marcadores y se sustituyen después.
/// </summary>
public sealed class SharedPassJudgeStep(string name, LlmRole role, ITemplateProvider templateProvider, string promptTemplateName, string systemPromptTemplateName)
    : CustomStatelessTemplateLlmStep<SharedPassJudgeInput, LlmStringStepResult>(name, role, templateProvider, promptTemplateName, systemPromptTemplateName)
{
    private const string CandidatesMark = "\u0001SHARED-PASS-CANDIDATES\u0001";

    protected override async Task<string?> BuildUserMessageAsync(IStepResult input, PipelineContext context)
    {
        if (input is not SharedPassJudgeInput { Value: { } message })
            return await base.BuildUserMessageAsync(input, context);
        var rendered = await base.BuildUserMessageAsync(new SharedPassJudgeInput(message with { Candidates = CandidatesMark }), context);
        return rendered?.Replace(CandidatesMark, message.Candidates, StringComparison.Ordinal);
    }
}

/// <summary>El veredicto del juez sobre un candidato, ya leído y validado.</summary>
/// <param name="Verdict"><c>confirm</c>, <c>downgrade</c>, <c>upgrade</c> o <c>reject</c> (tal como lo dio el modelo).</param>
/// <param name="Severity">P0-P3; null si no la dio o si rechaza.</param>
/// <param name="SeverityBasis">El camino de activación y el factor que fija el nivel, en una línea.</param>
/// <param name="Reason">Por qué confirma, cambia o rechaza.</param>
/// <param name="Activation"><c>default</c>, <c>non-default</c> o <c>misuse</c>; null si no lo dijo.</param>
public sealed record JudgeVerdict(string Verdict, string? Severity, string SeverityBasis, string Reason, string? Activation);

/// <summary>El resultado del juez: las entradas con la severidad final y el consumo.</summary>
public sealed record JudgeResult
{
    public IReadOnlyList<RegistryEntry> Entries { get; init; } = [];

    /// <summary>Entradas que cumplían el criterio de revisión (<see cref="FindingJudge.IsCandidate"/>).</summary>
    public int Candidates { get; init; }

    /// <summary>Candidatos con veredicto válido del juez.</summary>
    public int Reviewed { get; init; }

    public int Calls { get; init; }
    public decimal CostUsd { get; init; }
    public int UnpricedCalls { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>El tope de coste que cortó la etapa, si alguno la cortó.</summary>
    public string? StopReason { get; init; }
}

/// <summary>
/// D3 de la pasada compartida, parte LLM: el juez revisa los candidatos (rúbrica P0/P1, seguridad y confianza del lector menor que
/// <see cref="SharedPassOptions.JudgeConfidenceThreshold"/>) por tandas comparativas de <see cref="SharedPassOptions.JudgeBatchSize"/>,
/// con una llamada por tanda, sin herramientas ni agente: cada candidato lleva el fragmento real de código
/// (<see cref="SharedPassOptions.JudgeContextLines"/> líneas antes y después del ancla), leído aquí. Devuelve por candidato veredicto,
/// severidad y <c>severityBasis</c>.
/// </summary>
/// <remarks>
/// <para><b>Nunca falla duro</b>: si una tanda falla (error del LLM, límite de tiempo, JSON ilegible) o un tope de coste la impide,
/// sus candidatos se quedan con la severidad de la rúbrica y <c>reverified = false</c>; lo mismo un candidato sin veredicto.</para>
/// <para><b>Lo que el juez no puede saltarse</b>: la deuda no pasa de P2, y si el juez dice que la activación no es la de por
/// defecto (<c>non-default</c>, <c>misuse</c>), tampoco; si dice <c>default</c>, el tope por la activación que puso la rúbrica se
/// levanta (la rúbrica lo detecta por texto, el juez lo ve en el código). El veredicto final se recalcula comparando con la rúbrica
/// (<c>confirm</c>, <c>downgrade</c>, <c>upgrade</c>) salvo el rechazo.</para>
/// <para><b>Coste</b>: <see cref="SharedPassCostBudget"/> (etapa <c>consolidación</c>), comprobado antes de cada llamada; el tope por
/// lote (<see cref="SharedPassOptions.MaxCostPerBatchUsd"/>) se aplica a una tanda con lo que costaría enviarla (tokens estimados
/// por el precio de entrada), porque es una sola llamada. Las tandas van en paralelo limitado por
/// <see cref="SharedPassOptions.MaxParallelBatches"/>.</para>
/// </remarks>
public sealed class FindingJudge
{
    public const string StepName = "SharedPassJudge";
    public const string SystemTemplate = "shared_pass_judge_system";
    public const string PromptTemplate = "shared_pass_judge_prompt";

    /// <summary>Líneas de código como mucho por fragmento (un ancla muy larga se recorta).</summary>
    public const int MaxSnippetLines = 120;

    /// <summary>Otros sitios (instancias) que se listan por candidato.</summary>
    public const int MaxInstancesListed = 10;

    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(20);

    private readonly LlmRole _role;
    private readonly ITemplateProvider _templates;
    private readonly string _repositoryRoot;
    private readonly SharedPassOptions _options;
    private readonly Encoding _encoding;
    private readonly TimeSpan _callTimeout;
    private readonly Dictionary<string, List<string>?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _filesLock = new();

    public FindingJudge(LlmRole role, ITemplateProvider templates, string repositoryRoot, SharedPassOptions options,
        Encoding? defaultEncoding = null, TimeSpan? callTimeout = null)
    {
        _role = role;
        _templates = templates;
        _repositoryRoot = repositoryRoot;
        _options = options;
        _encoding = defaultEncoding ?? SourceEncoding.ResolveReadFallback(repositoryRoot, null);
        _callTimeout = callTimeout ?? DefaultCallTimeout;
    }

    /// <summary>Si la entrada va al juez: rúbrica P0/P1, seguridad o confianza baja.</summary>
    public static bool IsCandidate(RegistryEntry entry, SharedPassOptions options) =>
        SeverityRubric.Rank(entry.Rubric.Severity) <= 1
        || string.Equals(entry.Kind, "security", StringComparison.OrdinalIgnoreCase)
        || entry.Confidence < options.JudgeConfidenceThreshold;

    /// <summary>Revisa los candidatos de <paramref name="entries"/> en tandas en paralelo; el resto queda con la rúbrica.</summary>
    /// <param name="entries">Los registros consolidados.</param>
    /// <param name="context">Contexto del pipeline: cada llamada usa una rama suya.</param>
    /// <param name="ct">Cancelación.</param>
    /// <param name="budget">Topes compartidos con la lectura; null = unos nuevos.</param>
    /// <param name="progress">Estado para la interfaz (<c>sharedpass.progress</c>): candidatos y tandas hechas. Null = no se informa.</param>
    public async Task<JudgeResult> JudgeAsync(IReadOnlyList<RegistryEntry> entries, PipelineContext context, CancellationToken ct, SharedPassCostBudget? budget = null,
        SharedPassProgress? progress = null)
    {
        using var owned = budget == null ? new SharedPassCostBudget(_options) : null;
        budget ??= owned!;
        budget.BeginStage("consolidación");
        var baseline = entries.Select(e => e with { Severity = e.Rubric.Severity, SeverityBasis = e.Rubric.Basis, Reverified = false, JudgeVerdict = null }).ToList();
        var candidates = baseline.Where(e => IsCandidate(e, _options))
            .OrderBy(e => SeverityRubric.Rank(e.Rubric.Severity))
            .ThenBy(e => SeverityRubric.Fold(e.Category), StringComparer.Ordinal)
            .ThenBy(e => e.ContentId, StringComparer.Ordinal)
            .ToList();
        var size = Math.Max(1, _options.JudgeBatchSize);
        var tandas = candidates.Chunk(size).ToList();
        context.Report(StepName, AnalysisLogLevel.Info,
            $"⚖️ Juez: {candidates.Count} de {entries.Count} hallazgos a revisar (P0/P1 por rúbrica, seguridad o confianza < {_options.JudgeConfidenceThreshold.ToString(CultureInfo.InvariantCulture)}), en {tandas.Count} tanda(s).");
        progress?.BeginJudge(candidates.Count, tandas.Count);

        var verdicts = new Dictionary<string, JudgeVerdict>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var lockObj = new Lock();
        int calls = 0, unpriced = 0;
        decimal cost = 0;
        string? stop = null;

        using (var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelBatches)))
        {
            await Task.WhenAll(tandas.Select(async (tanda, index) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var keyed = tanda.Select((e, i) => (Key: $"C{i + 1}", Entry: e)).ToList();
                    var message = new SharedPassJudgeMessage(DescribeCandidates(keyed.Select(k => (k.Key, k.Entry, Snippet(k.Entry.Anchor))).ToList()),
                        keyed.Count.ToString(CultureInfo.InvariantCulture));
                    // Una tanda es una llamada: su tope es lo que costaría enviarla.
                    var estimate = SharedPassCostBudget.EstimateInputCostUsd(_role, SharedPassCostBudget.EstimateTokens(message.Candidates.Length)) ?? 0;
                    if (budget.StopReason(estimate) is { } reason)
                    {
                        lock (lockObj)
                        {
                            stop ??= reason;
                            warnings.Add($"Tanda {index + 1}: no se revisa ({reason}); sus {tanda.Length} candidatos quedan con la rúbrica.");
                        }
                        progress?.JudgeTandaDone(reason);
                        return;
                    }
                    var call = await CallAsync(message, context, budget, ct);
                    budget.Add(call.CostUsd, call.Tokens ?? 0);
                    var local = new List<string>();
                    var parsed = call.Error == null
                        ? ParseVerdicts(call.Text, keyed.Select(k => k.Key).ToHashSet(StringComparer.Ordinal), local)
                        : [];
                    lock (lockObj)
                    {
                        calls++;
                        if (call.CostUsd is decimal c)
                            cost += c;
                        else
                            unpriced++;
                        if (call.Error != null)
                            warnings.Add($"Tanda {index + 1}: el juez falló ({call.Error}); sus {tanda.Length} candidatos quedan con la rúbrica.");
                        else if (call.Finish == FinishReason.Length)
                            warnings.Add($"Tanda {index + 1}: la respuesta del juez se cortó; lo que no llegó queda con la rúbrica.");
                        warnings.AddRange(local.Select(w => $"Tanda {index + 1}: {w}"));
                        foreach (var (key, entry) in keyed)
                        {
                            if (parsed.TryGetValue(key, out var verdict))
                                verdicts[entry.ContentId] = verdict;
                            else if (call.Error == null)
                                warnings.Add($"Tanda {index + 1}: sin veredicto para {key} ({entry.ContentId}); queda con la rúbrica.");
                        }
                    }
                    progress?.JudgeTandaDone();
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        var result = baseline.Select(e => verdicts.TryGetValue(e.ContentId, out var v) ? Apply(e, v) : e).ToList();
        var judged = new JudgeResult
        {
            Entries = result,
            Candidates = candidates.Count,
            Reviewed = verdicts.Count,
            Calls = calls,
            CostUsd = cost,
            UnpricedCalls = unpriced,
            Warnings = warnings,
            StopReason = stop
        };
        context.Report(StepName, verdicts.Count == candidates.Count ? AnalysisLogLevel.Info : AnalysisLogLevel.Warning, string.Format(CultureInfo.InvariantCulture,
            "⚖️ Juez: {0} de {1} candidatos revisados ({2} rechazados, {3} con otra severidad), {4} llamadas, {5:0.###} USD{6}.",
            verdicts.Count, candidates.Count, result.Count(e => e.JudgeVerdict == "reject"),
            result.Count(e => e.JudgeVerdict is "downgrade" or "upgrade"), calls, cost, stop is { } s ? $" (cortado: {s})" : ""));
        return judged;
    }

    /// <summary>La entrada con el veredicto aplicado (ver las reglas del tipo).</summary>
    internal static RegistryEntry Apply(RegistryEntry entry, JudgeVerdict verdict)
    {
        if (verdict.Verdict == "reject")
        {
            return entry with
            {
                Status = RegistryEntry.StatusRejected,
                RejectReason = verdict.Reason.Length > 0 ? verdict.Reason : verdict.SeverityBasis,
                Severity = null,
                SeverityBasis = verdict.SeverityBasis.Length > 0 ? verdict.SeverityBasis : verdict.Reason,
                Reverified = true,
                JudgeVerdict = "reject"
            };
        }

        var severity = verdict.Severity ?? entry.Rubric.Severity;
        var basis = verdict.SeverityBasis.Length > 0 ? verdict.SeverityBasis : $"{entry.Rubric.Basis} (el juez no dio base)";
        var activationCap = verdict.Activation switch
        {
            "non-default" or "misuse" => true,
            "default" => false,
            _ => entry.Rubric.ActivationCapped
        };
        if (activationCap && SeverityRubric.Rank(severity) < 2)
        {
            severity = "P2";
            basis += $" (tope P2: activación {(verdict.Activation ?? "no por defecto según la rúbrica")})";
        }
        if (string.Equals(entry.Kind, "debt", StringComparison.OrdinalIgnoreCase) && SeverityRubric.Rank(severity) < 2)
        {
            severity = "P2";
            basis += " (tope P2: deuda)";
        }
        var rank = SeverityRubric.Rank(severity);
        var rubric = SeverityRubric.Rank(entry.Rubric.Severity);
        return entry with
        {
            Severity = severity,
            SeverityBasis = basis,
            Reverified = true,
            JudgeVerdict = rank == rubric ? "confirm" : rank > rubric ? "downgrade" : "upgrade",
            RejectReason = null,
            Status = RegistryEntry.StatusConfirmed
        };
    }

    /// <summary>
    /// Los veredictos de la respuesta, por clave de candidato (<c>C1</c>…). Lo que no es de la tanda, no trae un veredicto conocido o
    /// trae una severidad inválida sin ser un rechazo se descarta con aviso en <paramref name="warnings"/>.
    /// </summary>
    internal static Dictionary<string, JudgeVerdict> ParseVerdicts(string? text, IReadOnlySet<string> keys, List<string> warnings)
    {
        var result = new Dictionary<string, JudgeVerdict>(StringComparer.Ordinal);
        if (BatchReadingJson.ExtractObject(text, out var error) is not { } root)
        {
            warnings.Add($"la respuesta del juez no se pudo leer ({error}); la tanda queda con la rúbrica.");
            return result;
        }
        if (root["verdicts"] is not JsonArray items)
        {
            warnings.Add("la respuesta del juez no tiene 'verdicts'; la tanda queda con la rúbrica.");
            return result;
        }
        foreach (var node in items.OfType<JsonObject>())
        {
            var id = Str(node, "id")?.Trim() ?? "";
            if (!keys.Contains(id))
            {
                warnings.Add($"veredicto para un candidato que no es de la tanda ('{id}'): se ignora.");
                continue;
            }
            var verdict = NormalizeVerdict(Str(node, "verdict"));
            var severity = Str(node, "severity")?.Trim().ToUpperInvariant();
            if (severity != null && !SeverityRubric.Severities.Contains(severity))
                severity = null;
            if (verdict == null || (verdict != "reject" && severity == null && verdict != "confirm"))
            {
                warnings.Add($"veredicto de {id} sin 'verdict' válido o sin 'severity' P0-P3: se ignora.");
                continue;
            }
            var activation = Str(node, "activation")?.Trim().ToLowerInvariant() switch
            {
                "default" or "por-defecto" or "por defecto" => "default",
                "non-default" or "nondefault" or "no-por-defecto" or "no por defecto" => "non-default",
                "misuse" or "uso-erroneo" or "uso erróneo" => "misuse",
                _ => null
            };
            result.TryAdd(id, new JudgeVerdict(verdict, verdict == "reject" ? null : severity, Str(node, "severityBasis")?.Trim() ?? "",
                Str(node, "reason")?.Trim() ?? "", activation));
        }
        return result;
    }

    private static string? NormalizeVerdict(string? verdict) => verdict?.Trim().ToLowerInvariant() switch
    {
        "confirm" or "confirmed" or "confirmar" or "confirmado" => "confirm",
        "downgrade" or "degradar" or "degradado" => "downgrade",
        "upgrade" or "subir" or "subido" => "upgrade",
        "reject" or "rejected" or "rechazar" or "rechazado" => "reject",
        _ => null
    };

    private static string? Str(JsonObject node, string key) => node[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null
    };

    /// <summary>Los candidatos de una tanda en Markdown: los datos del lector, la exposición, la rúbrica, los otros sitios y el código.</summary>
    internal static string DescribeCandidates(IReadOnlyList<(string Key, RegistryEntry Entry, string Snippet)> candidates)
    {
        var sb = new StringBuilder();
        foreach (var (key, e, snippet) in candidates)
        {
            sb.Append("## ").Append(key).Append(" · ").Append(e.Kind).Append(" · ").AppendLine(e.Category.Length > 0 ? e.Category : "(sin categoría)");
            sb.Append("- Título: ").AppendLine(OneLine(e.Title));
            sb.Append("- Mecanismo según el lector: ").AppendLine(OneLine(e.Rationale));
            if (!string.IsNullOrWhiteSpace(e.Symbol))
                sb.Append("- Símbolo: `").Append(e.Symbol).AppendLine("`");
            if (!string.IsNullOrWhiteSpace(e.SeedRef))
                sb.Append("- Semilla del compilador: ").AppendLine(e.SeedRef);
            sb.Append("- Camino de activación: ").AppendLine(e.ActivationPath.Length > 0 ? OneLine(e.ActivationPath) : "(no lo dio)");
            sb.Append(CultureInfo.InvariantCulture, $"- Lector: impact {e.Impact}, likelihood {e.Likelihood}, confidence {e.Confidence:0.##}").AppendLine();
            sb.Append("- Exposición en el grafo: ").Append(e.Exposure.Score.ToString(CultureInfo.InvariantCulture)).Append(" — ").AppendLine(e.Exposure.Basis);
            sb.Append("- Propuesta de la rúbrica: ").Append(e.Rubric.Severity).Append(" — ").AppendLine(e.Rubric.Basis);
            if (e.Instances.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- Otros sitios ({e.Instances.Count}):").AppendLine();
                foreach (var i in e.Instances.Take(MaxInstancesListed))
                    sb.Append(CultureInfo.InvariantCulture, $"  - {i.Path}:{i.StartLine}-{i.EndLine} `{OneLine(i.Quote)}`").AppendLine();
                if (e.Instances.Count > MaxInstancesListed)
                    sb.Append(CultureInfo.InvariantCulture, $"  - (… y {e.Instances.Count - MaxInstancesListed} más)").AppendLine();
            }
            sb.Append(CultureInfo.InvariantCulture, $"- Ancla: {e.Anchor.Path}:{e.Anchor.StartLine}-{e.Anchor.EndLine} (las líneas del ancla van marcadas con `>`)").AppendLine();
            sb.AppendLine("```").AppendLine(snippet).AppendLine("```").AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>El código alrededor del ancla, numerado con la línea real y con <c>&gt;</c> en las del ancla.</summary>
    internal string Snippet(ReadAnchor anchor)
    {
        var lines = Lines(anchor.Path);
        if (lines == null)
            return $"(no se pudo leer {anchor.Path})";
        if (lines.Count == 0)
            return $"({anchor.Path} está vacío)";
        var start = Math.Clamp(anchor.StartLine, 1, lines.Count);
        var end = Math.Clamp(Math.Max(anchor.EndLine, anchor.StartLine), start, lines.Count);
        var from = Math.Max(1, start - _options.JudgeContextLines);
        var to = Math.Min(lines.Count, end + _options.JudgeContextLines);
        var sb = new StringBuilder();
        var shown = 0;
        for (var n = from; n <= to; n++)
        {
            if (++shown > MaxSnippetLines)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  (… recortado: faltan las líneas {n}-{to})").AppendLine();
                break;
            }
            sb.Append(n >= start && n <= end ? "> " : "  ").Append(n.ToString(CultureInfo.InvariantCulture)).Append(": ").AppendLine(lines[n - 1]);
        }
        return sb.ToString().TrimEnd();
    }

    private List<string>? Lines(string path)
    {
        lock (_filesLock)
        {
            if (_files.TryGetValue(path, out var cached))
                return cached;
        }
        List<string>? lines;
        try
        {
            var full = Path.Combine(_repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            lines = PacketBuilder.SplitLines(SourceEncoding.ReadFile(full, _encoding).Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            lines = null;
        }
        lock (_filesLock)
        {
            _files[path] = lines;
        }
        return lines;
    }

    private sealed record JudgeCall(string? Text, FinishReason? Finish, decimal? CostUsd, int? Tokens, string? Error);

    /// <summary>
    /// Una llamada del juez. Cortada por su límite o por el plazo de la pasada devuelve un error con los tokens enviados estimados y su
    /// coste de entrada; solo la cancelación del llamante (<paramref name="ct"/>) se propaga.
    /// </summary>
    private async Task<JudgeCall> CallAsync(SharedPassJudgeMessage message, PipelineContext context, SharedPassCostBudget budget, CancellationToken ct)
    {
        var step = new SharedPassJudgeStep(StepName, _role, _templates, PromptTemplate, _role.SystemPrompt ?? SystemTemplate) { Timeout = _callTimeout };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Deadline);
        if (_callTimeout > TimeSpan.Zero && _callTimeout != Timeout.InfiniteTimeSpan)
            limit.CancelAfter(_callTimeout);

        IStepResult result;
        try
        {
            result = await step.ExecuteStepAsync(new SharedPassJudgeInput(message), context.CloneForBranch(), 1, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Cut();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JudgeCall(null, null, null, null, ex.Message);
        }
        ct.ThrowIfCancellationRequested();

        var llm = result as ILlmStepResult;
        if (result.HasError && limit.IsCancellationRequested && llm?.CostUsd is null)
            return Cut();
        if (result.HasError)
            return new JudgeCall(null, llm?.FinishReason, llm?.CostUsd, llm?.TokensUsed, result.Error?.Message ?? "error sin mensaje");
        return new JudgeCall((result as LlmStringStepResult)?.Content ?? result.Value?.ToString(), llm?.FinishReason, llm?.CostUsd, llm?.TokensUsed, null);

        JudgeCall Cut()
        {
            var sent = SharedPassCostBudget.EstimateTokens(message.Candidates.Length);
            var why = budget.DeadlineExpired ? $"cortada: {budget.DeadlineReason}"
                : $"superó el límite de {_callTimeout.TotalMinutes.ToString("0.###", CultureInfo.InvariantCulture)} min";
            return new JudgeCall(null, null, SharedPassCostBudget.EstimateInputCostUsd(_role, sent), sent, why);
        }
    }
}
