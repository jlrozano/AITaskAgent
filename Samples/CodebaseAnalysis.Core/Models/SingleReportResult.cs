using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Results;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Models;

public class SingleReportResult : LlmStepResult<string>
{
    public const string UnknownHealth = "Unknown";

    // Línea final que _common_rules.md exige a cada informe. Es un comentario HTML: no se ve al
    // renderizar el Markdown, pero da un veredicto que no depende de adivinar colores en el texto.
    private static readonly Regex AnalysisMetaRegex = new(
        @"<!--\s*analysis-meta\s*:(?<body>.*?)-->",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public ReportType ReportType { get; set; }
    public string ReportFileName { get; set; } = string.Empty;
    public string MarkdownContent
    {
        get => Value ?? AssistantMessage ?? string.Empty;
        set => Value = value;
    }
    public string ExecutiveSummarySnippet { get; set; } = string.Empty;
    public string QualityHealth { get; set; } = UnknownHealth; // Green, Amber, Red, Unknown

    /// <summary>Defectos P0+P1 declarados en la línea analysis-meta (solo el informe de errores la emite).</summary>
    public int? CriticalFindings { get; set; }

    /// <summary>False si el informe no trae la línea analysis-meta: su semáforo queda en Unknown.</summary>
    public bool HasAnalysisMeta { get; private set; }

    public SingleReportResult()
        : base(new EmptyStep(nameof(SingleReportResult)))
    {
    }

    public SingleReportResult(IStep step)
        : base(step)
    {
    }

    public SingleReportResult(IStep step, IStepError? error)
        : base(step)
    {
        Error = error;
    }

    public void PopulateDerivedMetadata()
    {
        var text = MarkdownContent;
        if (string.IsNullOrWhiteSpace(text))
            return;

        // El semáforo antes se deducía buscando "Red|Rojo|Crítico" en todo el texto: "reduce",
        // "credenciales" o "No se eleva a Rojo" bastaban para marcar en rojo un informe Ámbar.
        ParseAnalysisMeta(text);

        // Auto-extract ExecutiveSummarySnippet (first 5-10 lines or executive summary block)
        if (string.IsNullOrEmpty(ExecutiveSummarySnippet))
        {
            var match = Regex.Match(text, @"(?:##?\s*(?:Resumen|Objetivo|Conclusión|Conclusiones|Síntesis)[^\n]*\n)([\s\S]{50,500}?)(?=\n##|\Z)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                ExecutiveSummarySnippet = match.Groups[1].Value.Trim();
            }
            else
            {
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l))
                                .Take(4);
                ExecutiveSummarySnippet = string.Join(" ", lines).Trim();
                if (ExecutiveSummarySnippet.Length > 400)
                {
                    ExecutiveSummarySnippet = ExecutiveSummarySnippet[..397] + "...";
                }
            }
        }
    }

    private void ParseAnalysisMeta(string text)
    {
        var matches = AnalysisMetaRegex.Matches(text);
        HasAnalysisMeta = matches.Count > 0;
        QualityHealth = UnknownHealth;
        CriticalFindings = null;

        if (!HasAnalysisMeta)
            return;

        // Si el modelo repite la línea, vale la última: es la que cierra el informe.
        var body = matches[^1].Groups["body"].Value;
        foreach (var pair in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();

            if (key.Equals("health", StringComparison.OrdinalIgnoreCase))
            {
                QualityHealth = NormalizeHealth(value);
            }
            else if (key.Equals("critical", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var critical))
            {
                CriticalFindings = critical;
            }
        }
    }

    private static string NormalizeHealth(string value) => value.Trim().ToLowerInvariant() switch
    {
        "green" or "verde" => "Green",
        "amber" or "ámbar" or "ambar" or "yellow" => "Amber",
        "red" or "rojo" => "Red",
        _ => UnknownHealth
    };
}
