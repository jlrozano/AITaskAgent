using AITaskAgent.Support.Template;
using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// El plan de cobertura determinista (T13, spec §4.3): puntúa cada subsistema de producción del mapa de dominios con los pesos de
/// <see cref="PreparationOptions.Weights"/>, deja fuera lo que no es producción, y para cada informe en modo B fija los K
/// primeros, una cola larga de muestra, los excluidos con su motivo, las preguntas de la investigación y la evidencia mínima.
/// Sin LLM y sin disco (salvo leer las plantillas de preguntas): mismo mapa y mismas opciones, mismo plan salvo
/// <see cref="CoveragePlan.GeneratedAt"/>.
/// </summary>
/// <remarks>
/// <para>La puntuación es la media ponderada de seis señales normalizadas a 0-1 dividiendo por el máximo entre los subsistemas de
/// producción (una señal que nadie tiene vale 0), de modo que el resultado también va de 0 a 1 y no depende de la escala de los
/// pesos. «Número y variedad de entradas» es la mitad de cada una: cuántas entradas tiene y de cuántos tipos distintos. Los
/// subsistemas de test, qa, debug, samples y generado no puntúan ni cuentan para la normalización; <c>unknown</c> no se adivina y
/// sigue siendo elegible (no hay motivo de exclusión para él).</para>
/// <para>La puntuación es del subsistema y del informe: los pesos base (<see cref="PreparationOptions.Weights"/>) se multiplican por el
/// perfil de cada informe (<see cref="PreparationOptions.ReportProfiles"/>), de modo que el funcional y el de flujos prefieren lo
/// que tiene entradas, estados y tablas, el de errores lo que toca integraciones y uso dinámico, y el de arquitectura lo más
/// central. Antes de los perfiles los cuatro elegían los mismos K con una puntuación casi toda centralidad, que favorece la
/// infraestructura compartida. <see cref="CoveragePlan.Scores"/> sigue siendo la puntuación con los pesos base. Además, lo que
/// distingue a un informe son sus preguntas (<c>templates/_investigation_questions_*.md</c>, editables).</para>
/// <para>La cola larga es una muestra de los subsistemas que no entraron entre los K, repartida de forma regular por su ranking
/// (los puestos centrales de cada tramo) y no los K+1 primeros: lo que importa es no mirar solo la cabeza de la lista. El orden
/// es el del ranking, así que no hay azar y dos ejecuciones dan la misma cola.</para>
/// </remarks>
public static class CoveragePlanner
{
    /// <summary>Informes que investigan por subsistema (modo B), en el orden del catálogo.</summary>
    public static readonly IReadOnlyList<ReportType> ModeBReports =
        [ReportType.AnalisisFuncional, ReportType.FlujosEndToEnd, ReportType.ErroresYBugs, ReportType.ArquitecturaAcoplamiento];

    /// <summary>Informes de capacidades de negocio: no investigan la infraestructura compartida (<see cref="SubsystemKinds.Shared"/>).</summary>
    public static readonly IReadOnlyList<ReportType> ReportsWithoutShared = [ReportType.AnalisisFuncional, ReportType.FlujosEndToEnd];

    /// <summary>
    /// Lo que debe reunir la investigación de cada subsistema para dar por cubierto un informe. Genérico: ninguna palabra del
    /// repositorio analizado. Texto, no JSON.
    /// </summary>
    public static readonly IReadOnlyList<string> MinimumEvidence =
    [
        "Todos los puntos de entrada del subsistema, cada uno con su ruta:línea.",
        "Al menos un flujo completo desde una entrada hasta su operación terminal, con la llamada concreta que une cada paso.",
        "Los caminos alternativos de ese flujo (error, entrada vacía o duplicada, falta de permisos, cancelación) y dónde termina cada uno.",
        "Al menos un contraejemplo: algo que parecía un hallazgo o una regla y que el código desmiente tras leerlo."
    ];

    /// <summary>Prefijo de las plantillas de preguntas; el sufijo es el del informe (<c>funcional</c>, <c>flujos</c>...).</summary>
    public const string QuestionsTemplatePrefix = "_investigation_questions_";

    private const int Decimals = 6;

    /// <summary>Plan sin informes: lo que deja <c>CoveragePlanningStep</c> cuando no hay mapa de dominios del que partir.</summary>
    public static CoveragePlan Empty(PreparationOptions options, string warning, DateTimeOffset generatedAt) =>
        new()
        {
            GeneratedAt = generatedAt,
            MaxSubsystemsPerReport = options.MaxSubsystemsPerReport,
            Warnings = [warning]
        };

    /// <param name="map">El mapa de dominios.</param>
    /// <param name="options">K, tamaño de la cola larga, pesos y perfiles por informe.</param>
    /// <param name="templates">De donde se leen las preguntas; null deja los informes sin preguntas y lo avisa.</param>
    /// <param name="generatedAt">Marca de tiempo del artefacto (lo único que cambia entre dos ejecuciones iguales).</param>
    /// <param name="requested">
    /// Los informes que se van a escribir: solo los de modo B entre ellos se planifican (en el orden del catálogo). Null planifica los cuatro.
    /// Con <c>--reports deadcode</c> no hay ninguno que investigar, y el plan, su log y el presupuesto de tiempo no hablan de informes que no corren.
    /// </param>
    public static CoveragePlan Plan(
        DomainMap map, PreparationOptions options, ITemplateProvider? templates, DateTimeOffset generatedAt, IReadOnlyCollection<ReportType>? requested = null)
    {
        var warnings = new List<string>();
        var eligible = map.Subsystems.Where(s => IsEligible(s.SourceSet)).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        var scores = Score(eligible, options.Weights, null);
        var byId = eligible.ToDictionary(s => s.Id, StringComparer.Ordinal);

        var excludedByRule = map.Subsystems.Where(s => !IsEligible(s.SourceSet))
            .Select(s => new CoverageExclusion { SubsystemId = s.Id, Name = s.Name, Reason = ReasonOf(s.SourceSet) }).ToList();

        if (map.Subsystems.Count == 0)
            warnings.Add("⚠️ El mapa de dominios no tiene subsistemas: los informes en modo B no tienen nada que investigar y trabajan como un auditor normal.");
        else if (eligible.Count == 0)
            warnings.Add("⚠️ Ningún subsistema del mapa es de producción (todos son test, qa, generado...): los informes en modo B no tienen nada que investigar.");

        if (templates == null)
            warnings.Add("⚠️ No hay proveedor de plantillas: los informes en modo B quedan sin las preguntas de investigación.");

        var k = Math.Max(options.MaxSubsystemsPerReport, 0);
        var reports = new List<ReportCoverage>();
        foreach (var report in ModeBReports.Where(r => requested == null || requested.Contains(r)))
        {
            // Cada informe se puntúa con su perfil: el ranking (puntuación descendente y, a igualdad, por identificador), los K
            // elegidos, la cola larga y los excluidos por quedar bajo el K son los de ese informe.
            // La infraestructura compartida no es una capacidad: el funcional y el de flujos no la eligen (ni la cuentan al normalizar) y queda
            // anotada como excluida; el de arquitectura y el de errores sí la ven.
            var skipShared = ReportsWithoutShared.Contains(report);
            var candidates = skipShared ? eligible.Where(s => !s.IsShared).ToList() : eligible;
            var reportScores = Score(candidates, options.Weights, options.ReportProfiles.Of(report));
            var ranking = reportScores.Select(s => (Score: s, Subsystem: byId[s.SubsystemId])).ToList();
            var chosen = ranking.Take(k).Select((r, i) => Select(r.Score, r.Subsystem, i + 1)).ToList();
            var below = ranking.Skip(k).Select((r, i) => Select(r.Score, r.Subsystem, k + i + 1)).ToList();
            var excluded = excludedByRule
                .Concat(skipShared ? eligible.Where(s => s.IsShared).Select(s => new CoverageExclusion { SubsystemId = s.Id, Name = s.Name, Reason = ExclusionReasons.SharedInfrastructure }) : [])
                .Concat(below.Select(b => new CoverageExclusion { SubsystemId = b.SubsystemId, Name = b.Name, Reason = ExclusionReasons.BelowTopK }))
                .OrderBy(e => e.SubsystemId, StringComparer.Ordinal).ToList();

            reports.Add(new ReportCoverage
            {
                Report = report,
                Scores = reportScores,
                Chosen = chosen,
                LongTail = SampleTail(below, options.LongTailSize),
                Excluded = excluded,
                Questions = templates == null ? [] : ReadQuestions(templates, report, warnings),
                MinimumEvidence = MinimumEvidence
            });
        }

        return new CoveragePlan
        {
            GeneratedAt = generatedAt,
            GraphFingerprint = map.GraphFingerprint,
            MaxSubsystemsPerReport = options.MaxSubsystemsPerReport,
            Scores = scores,
            Reports = reports,
            Warnings = warnings
        };
    }

    /// <summary>
    /// El plan <paramref name="plan"/>, de solo algunos informes en modo B, completado para el disco con los de los que no se pidieron, tomados de
    /// <paramref name="previous"/> (el <c>_coverage_plan.json</c> que ya había) si se calculó sobre el mismo grafo: regenerar un informe no
    /// debe borrar del artefacto lo que se eligió para los demás, que siguen en disco y que el validador de cobertura comprueba contra él.
    /// Devuelve <paramref name="plan"/> tal cual si ya los trae todos, si no hay plan anterior, o si la huella del grafo falta o difiere.
    /// </summary>
    public static CoveragePlan WithCarriedOverReports(CoveragePlan plan, CoveragePlan? previous)
    {
        if (previous == null || string.IsNullOrEmpty(plan.GraphFingerprint) || plan.GraphFingerprint != previous.GraphFingerprint)
            return plan;

        var carried = previous.Reports.Where(r => ModeBReports.Contains(r.Report) && plan.Reports.All(p => p.Report != r.Report)).ToList();
        if (carried.Count == 0)
            return plan;
        return plan with { Reports = [.. plan.Reports.Concat(carried).OrderBy(r => ModeBReports.ToList().IndexOf(r.Report))] };
    }

    /// <summary>Nombre de la plantilla de preguntas de un informe en modo B (<c>_investigation_questions_funcional</c>...).</summary>
    public static string QuestionsTemplateName(ReportType report) =>
        QuestionsTemplatePrefix + AnalysisPipelineInput.GetTemplatePrefix(report)["report_".Length..];

    /// <summary>Producción y <c>unknown</c> (sin evidencia no se adivina) son elegibles; el resto tiene motivo de exclusión.</summary>
    private static bool IsEligible(string sourceSet) => sourceSet is SourceSets.Production or SourceSets.Unknown;

    private static string ReasonOf(string sourceSet) => sourceSet switch
    {
        SourceSets.Test => ExclusionReasons.TestSource,
        SourceSets.Qa => ExclusionReasons.QaSource,
        SourceSets.Debug => ExclusionReasons.DebugSource,
        SourceSets.Generated => ExclusionReasons.GeneratedSource,
        _ => ExclusionReasons.SamplesSource
    };

    private static CoverageSelection Select(SubsystemScore score, Subsystem subsystem, int rank) =>
        new() { SubsystemId = score.SubsystemId, Name = score.Name, Rank = rank, Score = score.Score, Loc = subsystem.Metrics.Loc };

    /// <summary>
    /// Muestra estable de <paramref name="size"/> subsistemas de la lista ya ordenada por ranking: el del centro de cada una de
    /// las <paramref name="size"/> partes iguales en que se divide. Si no hay más que <paramref name="size"/>, todos.
    /// </summary>
    private static List<CoverageSelection> SampleTail(List<CoverageSelection> below, int size)
    {
        if (size <= 0 || below.Count == 0)
            return [];
        if (below.Count <= size)
            return [.. below];
        return [.. Enumerable.Range(0, size).Select(i => below[(int)((i + 0.5) * below.Count / size)])];
    }

    /// <summary>
    /// Puntúa a los subsistemas elegibles con los pesos base y, si se da, el perfil de un informe (factor por señal sobre el peso
    /// base; null = sin perfil). El peso efectivo de cada señal es el de sus componentes, y el total con que se normaliza la media.
    /// </summary>
    private static List<SubsystemScore> Score(List<Subsystem> eligible, SubsystemScoreWeights weights, SubsystemScoreProfile? profile)
    {
        profile ??= new SubsystemScoreProfile();
        (string Name, double Weight, Func<Subsystem, double> Raw)[] signals =
        [
            ("centrality", weights.Centrality * profile.Centrality, s => s.Metrics.Centrality),
            ("tables", weights.Tables * profile.Tables, s => s.Metrics.Tables),
            ("states", weights.States * profile.States, s => s.Metrics.States),
            ("integrations", weights.Integrations * profile.Integrations, s => s.Metrics.Integrations),
            ("dynamic_use_risk", weights.DynamicUseRisk * profile.DynamicUseRisk, s => s.Metrics.DynamicUseSites)
        ];
        var entryWeight = weights.EntryPoints * profile.EntryPoints;

        double Max(Func<Subsystem, double> raw) => eligible.Select(raw).DefaultIfEmpty(0).Max();
        double Normalized(double value, double max) => max > 0 ? Math.Round(value / max, Decimals) : 0;

        var maxEntries = Max(s => s.Metrics.EntryPoints);
        var maxKinds = Max(s => s.Metrics.EntryPointKinds);
        var maxima = signals.ToDictionary(x => x.Name, x => Max(x.Raw), StringComparer.Ordinal);
        var totalWeight = signals.Sum(x => x.Weight) + entryWeight;

        var scores = new List<SubsystemScore>();
        foreach (var subsystem in eligible)
        {
            var components = new List<ScoreComponent>();

            // El orden de los componentes es fijo: centralidad, entradas, tablas, estados, integraciones, uso dinámico.
            components.Add(Component("centrality", Normalized(subsystem.Metrics.Centrality, maxima["centrality"]), signals[0].Weight));
            var entries = 0.5 * Normalized(subsystem.Metrics.EntryPoints, maxEntries) + 0.5 * Normalized(subsystem.Metrics.EntryPointKinds, maxKinds);
            components.Add(Component("entry_points", Math.Round(entries, Decimals), entryWeight));
            foreach (var (name, weight, raw) in signals.Skip(1))
                components.Add(Component(name, Normalized(raw(subsystem), maxima[name]), weight));

            var score = totalWeight > 0 ? Math.Round(components.Sum(c => c.Weighted) / totalWeight, Decimals) : 0;
            scores.Add(new SubsystemScore { SubsystemId = subsystem.Id, Name = subsystem.Name, Score = score, Components = components });
        }

        return [.. scores.OrderByDescending(s => s.Score).ThenBy(s => s.SubsystemId, StringComparer.Ordinal)];
    }

    private static ScoreComponent Component(string name, double value, double weight) =>
        new() { Name = name, Value = value, Weight = weight, Weighted = Math.Round(value * weight, Decimals) };

    /// <summary>Las preguntas de la plantilla: una por línea que empieza por «- ». Sin plantilla, ninguna y un aviso.</summary>
    private static List<string> ReadQuestions(ITemplateProvider templates, ReportType report, List<string> warnings)
    {
        var name = QuestionsTemplateName(report);
        var text = templates.GetTemplate(name);
        if (text == null)
        {
            warnings.Add($"⚠️ No existe la plantilla de preguntas '{name}': el informe {report} se investiga sin preguntas guiadas.");
            return [];
        }

        return
        [
            .. text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).Where(l => l.Length > 0)
        ];
    }
}
