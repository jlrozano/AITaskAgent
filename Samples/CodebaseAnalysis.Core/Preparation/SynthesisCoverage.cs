using System.Globalization;
using System.Text;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// El resumen de cobertura que recibe la síntesis ejecutiva (T25, spec §5.3): qué parte del repositorio recorrió cada informe, para que
/// el resumen no presente como completo un análisis con subsistemas sin investigar, excluidos o no cubiertos. Sale del plan de
/// cobertura del disco (lo que se eligió y lo que se dejó fuera) y del apartado «Cobertura» de cada informe (lo que se cubrió de
/// verdad, incluidos los subsistemas cuya investigación falló, que el plan no sabe).
/// </summary>
/// <remarks>
/// Texto plano y función pura (mismos datos, mismo texto). Cadena vacía cuando no hay nada que decir (sin plan y sin apartados): la
/// plantilla lo tolera. Ningún informe sin cobertura declarada aparece; un informe al que el plan eligió subsistemas y que no lleva el
/// apartado se dice, porque tampoco declara lo que dejó sin ver.
/// </remarks>
public static class SynthesisCoverage
{
    /// <summary>Caracteres del apartado «Cobertura» de un informe que se copian al resumen.</summary>
    public const int MaxSectionChars = 1500;

    /// <summary>Nombres de subsistema que se listan por informe.</summary>
    public const int MaxListedNames = 12;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <param name="plan">El plan de cobertura del disco; null si no hay (preparación apagada, o informes de antes).</param>
    /// <param name="reports">Los informes que entran en la síntesis, con su texto.</param>
    public static string Build(CoveragePlan? plan, IEnumerable<(ReportType Type, string FileName, string Markdown)> reports)
    {
        var blocks = new List<string>();
        foreach (var (type, fileName, markdown) in reports.OrderBy(r => r.Type))
        {
            var coverage = plan?.Reports.FirstOrDefault(r => r.Report == type) is { Chosen.Count: > 0 } chosen ? chosen : null;
            var section = CoverageSection.Extract(markdown, MaxSectionChars);
            if (coverage == null && section == null)
                continue;

            var block = new StringBuilder();
            block.Append("- ").Append(fileName).Append(" (").Append(type).Append("):");
            if (coverage != null)
                block.Append(' ').Append(PlanLine(coverage, plan!));
            block.AppendLine();
            if (section == null)
                block.Append("  El informe no lleva apartado «Cobertura»: no declara qué subsistemas quedaron sin cubrir.");
            else if (section.Length == 0)
                block.Append("  Su apartado «Cobertura» está vacío.");
            else
                block.Append("  Apartado «Cobertura» del informe:\n").Append(Indented(section));
            blocks.Add(block.ToString().TrimEnd());
        }

        if (blocks.Count == 0)
            return string.Empty;

        var text = new StringBuilder();
        if (plan is { Scores.Count: > 0 } && plan.Reports.Any(r => r.Chosen.Count > 0))
        {
            text.Append("El repositorio tiene ").Append(plan.Scores.Count.ToString(Invariant)).Append(" subsistemas de producción puntuados; cada informe en modo B investiga como máximo ")
                .Append(plan.MaxSubsystemsPerReport.ToString(Invariant)).AppendLine(" de ellos y el resto queda sin investigar.").AppendLine();
        }
        text.Append(string.Join("\n\n", blocks));
        return text.ToString();
    }

    private static string PlanLine(ReportCoverage coverage, CoveragePlan plan)
    {
        var line = new StringBuilder();
        line.Append("el plan eligió ").Append(coverage.Chosen.Count.ToString(Invariant));
        if (plan.Scores.Count > 0)
            line.Append(" de ").Append(plan.Scores.Count.ToString(Invariant));
        line.Append(" subsistemas: ").Append(Joined(coverage.Chosen.OrderBy(c => c.Rank).Select(c => c.Name).ToList()));
        if (coverage.Excluded.Count > 0)
        {
            var reasons = coverage.Excluded.GroupBy(e => e.Reason, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Count().ToString(Invariant)} — {InvestigationTexts.ReasonText(g.Key)}");
            line.Append(". No investiga ").Append(coverage.Excluded.Count.ToString(Invariant)).Append(": ").Append(string.Join("; ", reasons));
        }
        return line.Append('.').ToString();
    }

    private static string Joined(IReadOnlyList<string> names) =>
        names.Count <= MaxListedNames
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(MaxListedNames)) + $" (+{(names.Count - MaxListedNames).ToString(Invariant)})";

    private static string Indented(string text) =>
        string.Join('\n', text.Split('\n').Select(l => l.Length == 0 ? l : "  | " + l));
}
