using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;

namespace CodebaseAnalysis.Core.Models;

/// <summary>
/// Entrada del verificador: un único texto con tres secciones (borrador, fallos de la validación y evidencias) que la
/// plantilla recibe como <c>{{ input }}</c>. Es un texto y no propiedades tipadas porque una lista o un objeto se pintan como
/// JSON en la plantilla (<see cref="Support.ReportVerification.BuildInput"/>).
/// </summary>
public sealed class VerificationInput : StepResult<string>
{
    public VerificationInput(string text)
        : base(new EmptyStep(nameof(VerificationInput)), text)
    {
    }
}

/// <summary>Resultados posibles de verificar un informe (<see cref="VerificationResult.Outcome"/>).</summary>
public static class VerificationOutcomes
{
    /// <summary>Sin correcciones y sin avisos de validación.</summary>
    public const string Verified = "verified";

    /// <summary>El informe se reescribió con correcciones y valida.</summary>
    public const string Corrected = "corrected";

    /// <summary>Con avisos de validación que quedan (citas rotas...), se haya corregido algo o no.</summary>
    public const string Warnings = "warnings";

    /// <summary>No se pudo verificar: el informe queda como estaba.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Resultado de <see cref="Steps.VerifyReportStep"/>. Verificar no falla el paso aunque el verificador no responda: el informe
/// original sigue siendo válido, así que el fallo va en <see cref="Outcome"/>. No lleva <c>Value</c>, para que la síntesis no
/// lo tome por el texto de un informe.
/// </summary>
public sealed class VerificationResult : StepResult
{
    public string FileName { get; set; } = string.Empty;
    public string Outcome { get; set; } = VerificationOutcomes.Failed;
    public int Changes { get; set; }

    /// <summary>Avisos de validación que quedan tras verificar (sin contar la línea de metadatos).</summary>
    public int Warnings { get; set; }

    /// <summary>Versión que «Deshacer» restaura; null si el informe no se reescribió.</summary>
    public string? PreviousVersionId { get; set; }
    public string? VersionId { get; set; }
    public string? FailureReason { get; set; }

    public VerificationResult()
        : base(new EmptyStep(nameof(VerificationResult)))
    {
    }

    public VerificationResult(IStep step)
        : base(step)
    {
    }
}
