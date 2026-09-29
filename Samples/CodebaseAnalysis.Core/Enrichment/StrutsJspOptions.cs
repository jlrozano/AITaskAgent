using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>Sección <c>StrutsJsp</c>: el plugin estructural de Struts 1.x, Tiles y JSP.</summary>
public sealed record StrutsJspOptions
{
    public const string SectionName = "StrutsJsp";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Imports fuertes: expresiones regulares (.NET) que, al casar con algún <c>&lt;%@ page import %&gt;</c>, marcan por sí
    /// solas la JSP como sospechosa de llevar código de servidor (acceso a datos).
    /// </summary>
    public IReadOnlyList<string> SuspiciousImportPatterns { get; init; } =
    [
        @"^java\.sql(\.|$)",
        @"^javax\.sql(\.|$)",
        @"(?i)(^|\.)dao(\.|$)|DAO\b"
    ];

    /// <summary>
    /// Imports de negocio o de servicio: solo marcan la JSP si lo importado se usa en el código de un scriptlet
    /// <c>&lt;% %&gt;</c> o de una declaración <c>&lt;%! %&gt;</c>. Importar utilidades o constantes para pintarlas en una
    /// expresión <c>&lt;%= %&gt;</c> es habitual y no indica lógica de negocio en la pantalla.
    /// </summary>
    public IReadOnlyList<string> BusinessImportPatterns { get; init; } =
    [
        @"(?i)\.(negocio|business|service)\."
    ];

    public static StrutsJspOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new StrutsJspOptions();
        return new StrutsJspOptions
        {
            Enabled = section.GetValue<bool?>(nameof(Enabled)) ?? defaults.Enabled,
            SuspiciousImportPatterns = JavaAnalyzerOptions.ReadList(section, nameof(SuspiciousImportPatterns))
                ?? defaults.SuspiciousImportPatterns,
            BusinessImportPatterns = JavaAnalyzerOptions.ReadList(section, nameof(BusinessImportPatterns))
                ?? defaults.BusinessImportPatterns
        };
    }
}
