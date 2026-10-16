using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace CodebaseAnalysis.Core.Models;

/// <summary>
/// Entrada de la investigación de un subsistema para un informe en modo B (<c>SubsystemInvestigationReportStep</c>). Todas las
/// propiedades son texto plano que la plantilla de investigación (<c>report_*_investigacion_prompt</c>) lee como
/// <c>{{ subsystem_name }}</c>, <c>{{ subsystem_summary }}</c>, <c>{{ subsystem_members }}</c>, <c>{{ subsystem_entry_points }}</c>,
/// <c>{{ investigation_questions }}</c>, <c>{{ minimum_evidence }}</c> y <c>{{ repository_context }}</c>. El motor de plantillas no
/// tiene condicionales, así que ninguna es nula y un bloque sin datos lleva una frase que lo dice en lugar de quedar vacío.
/// </summary>
/// <remarks>
/// Los textos los arma <see cref="Preparation.InvestigationTexts"/> con topes de tamaño (un repositorio grande tiene subsistemas
/// de cientos de ficheros) y, cuando recortan, lo dicen en el propio texto para que el modelo sepa que tiene que buscar el resto
/// con las herramientas.
/// </remarks>
public sealed class SubsystemInvestigationInput : StepResult
{
    /// <summary>Nombre legible del subsistema.</summary>
    public string SubsystemName { get; set; } = string.Empty;

    /// <summary>Ficha del subsistema: identificador, descripción, tamaño, carpetas raíz, tablas y su puesto en el plan.</summary>
    public string SubsystemSummary { get; set; } = string.Empty;

    /// <summary>Tipos principales y ficheros del subsistema agrupados por carpeta, recortados si son muchos.</summary>
    public string SubsystemMembers { get; set; } = string.Empty;

    /// <summary>Puntos de entrada del subsistema con su <c>ruta:línea</c>.</summary>
    public string SubsystemEntryPoints { get; set; } = string.Empty;

    /// <summary>Preguntas de la investigación de este informe, numeradas.</summary>
    public string InvestigationQuestions { get; set; } = string.Empty;

    /// <summary>Evidencia mínima que debe reunir la investigación para dar por cubierto el subsistema.</summary>
    public string MinimumEvidence { get; set; } = string.Empty;

    /// <summary>Contexto del repositorio entero: módulos, lenguajes, código no productivo y los otros subsistemas que se investigan.</summary>
    public string RepositoryContext { get; set; } = string.Empty;

    public SubsystemInvestigationInput()
        : base(new EmptyStep(nameof(SubsystemInvestigationInput)))
    {
    }
}
