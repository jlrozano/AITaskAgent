using System.Globalization;
using System.Text;
using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>Resultado de la investigación de un subsistema, tal como lo recoge el envoltorio del informe.</summary>
/// <param name="Selection">El subsistema elegido en el plan.</param>
/// <param name="Notes">Texto de la investigación; null si falló o agotó su tiempo.</param>
/// <param name="FailureReason">Por qué no se cubrió (con el límite si fue el tiempo); null si se cubrió.</param>
/// <param name="Truncated">La respuesta se cortó al alcanzar <c>MaxOutputTokens</c>: las notas están, pero incompletas.</param>
public sealed record InvestigationOutcome(CoverageSelection Selection, string? Notes, string? FailureReason, bool Truncated = false)
{
    public bool Covered => FailureReason == null && !string.IsNullOrWhiteSpace(Notes);
}

/// <summary>
/// Los textos planos de la investigación por subsistema (T21): la entrada de cada investigación
/// (<see cref="SubsystemInvestigationInput"/>), las notas que reúnen todas y el resumen de cobertura que redacta el apartado
/// «Cobertura» del informe. Sin JSON, legibles y compactos; con topes de tamaño para repositorios grandes y una línea que avisa
/// cuando algo se recorta, porque un modelo que no sabe que falta información no la busca.
/// </summary>
/// <remarks>
/// Función pura (sin disco, sin reloj): mismos datos, mismo texto. Los números se escriben sin separador de millares y con la
/// cultura invariante, para que el texto no dependa de la máquina.
/// </remarks>
public static class InvestigationTexts
{
    /// <summary>Tipos principales que se listan de un subsistema.</summary>
    public const int MaxKeyTypes = 30;

    /// <summary>Carpetas distintas con ficheros que se listan de un subsistema.</summary>
    public const int MaxFolders = 40;

    /// <summary>Nombres de fichero que se listan por carpeta.</summary>
    public const int MaxFilesPerFolder = 10;

    /// <summary>Tope de caracteres del bloque de miembros (tipos y ficheros); lo que no cabe se resume en una línea.</summary>
    public const int MaxMembersChars = 6000;

    public const int MaxEntryPoints = 40;
    public const int MaxTables = 20;
    public const int MaxRoots = 8;
    public const int MaxQuestions = 20;
    public const int MaxEvidence = 10;

    /// <summary>Otros subsistemas que se nombran en el contexto del repositorio.</summary>
    public const int MaxOtherSubsystems = 20;

    /// <summary>Nombres que se listan por motivo de exclusión en el resumen de cobertura.</summary>
    public const int MaxExcludedNames = 25;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ───────────────────────────── Entrada de una investigación ─────────────────────────────

    /// <summary>
    /// La entrada de la investigación de <paramref name="selection"/> para el informe de <paramref name="coverage"/>.
    /// </summary>
    /// <param name="selection">El subsistema elegido (identificador, nombre, puesto, puntuación).</param>
    /// <param name="subsystem">Su ficha del mapa de dominios; null si el mapa no está (la ficha se reduce a lo que sabe el plan).</param>
    /// <param name="coverage">La cobertura del informe: preguntas, evidencia mínima y el resto de elegidos.</param>
    /// <param name="inventory">El inventario del repositorio; null si no está.</param>
    public static SubsystemInvestigationInput BuildInput(
        CoverageSelection selection, Subsystem? subsystem, ReportCoverage coverage, RepositoryInventory? inventory) =>
        new()
        {
            SubsystemName = selection.Name,
            SubsystemSummary = Summary(selection, subsystem, coverage.Chosen.Count),
            SubsystemMembers = Members(subsystem),
            SubsystemEntryPoints = EntryPoints(subsystem),
            InvestigationQuestions = Questions(coverage.Questions),
            MinimumEvidence = Evidence(coverage.MinimumEvidence),
            RepositoryContext = RepositoryContext(selection, coverage, inventory)
        };

    /// <summary>Ficha del subsistema: identificador, descripción, tamaño, raíces, tablas y puesto en el plan.</summary>
    public static string Summary(CoverageSelection selection, Subsystem? subsystem, int chosenCount)
    {
        var text = new StringBuilder();
        text.Append("Nombre: ").Append(selection.Name).Append(" (id: ").Append(selection.SubsystemId).AppendLine(")");
        if (subsystem is { Description.Length: > 0 })
            text.Append("Descripción: ").AppendLine(subsystem.Description);
        text.Append("Puesto en el plan: ").Append(Num(selection.Rank)).Append(" de ").Append(Num(chosenCount))
            .Append(" elegidos, puntuación ").Append(selection.Score.ToString("0.00", Invariant)).AppendLine(".");

        if (subsystem == null)
        {
            text.Append("Tamaño: ").Append(Num(selection.Loc)).AppendLine(" líneas de producción.");
            text.Append("El mapa de dominios no está disponible: no hay más ficha que esta. Delimita el subsistema con graph_search y grep_search.");
            return text.ToString().TrimEnd();
        }

        var m = subsystem.Metrics;
        text.Append("Tamaño: ").Append(Num(m.Files)).Append(" ficheros, ").Append(Num(m.Loc)).Append(" líneas de producción; ")
            .Append(Num(m.EntryPoints)).Append(" puntos de entrada, ").Append(Num(m.Tables)).Append(" tablas, ")
            .Append(Num(m.States)).Append(" enumerados, ").Append(Num(m.Integrations)).AppendLine(" integraciones externas.");

        if (subsystem.Roots.Count > 0)
            text.Append("Carpetas raíz: ").AppendLine(Joined(subsystem.Roots, MaxRoots));
        if (subsystem.DominantTerm.Length > 0)
            text.Append("Término dominante de sus nombres: ").AppendLine(subsystem.DominantTerm);
        if (subsystem.Tables.Count > 0)
            text.Append("Tablas que tocan sus clases: ").AppendLine(Joined(subsystem.Tables, MaxTables));

        return text.ToString().TrimEnd();
    }

    /// <summary>Tipos principales y ficheros agrupados por carpeta; recorta por carpetas, por nombres y por caracteres, y lo dice.</summary>
    public static string Members(Subsystem? subsystem)
    {
        if (subsystem == null || (subsystem.Files.Count == 0 && subsystem.KeyTypes.Count == 0))
            return "(sin miembros conocidos: el mapa de dominios no los trae. Localízalos con graph_search y grep_search.)";

        var lines = new List<string>();
        if (subsystem.KeyTypes.Count > 0)
            lines.Add($"Tipos principales ({Num(subsystem.KeyTypes.Count)}): {Joined(subsystem.KeyTypes, MaxKeyTypes)}");

        if (subsystem.Files.Count > 0)
        {
            lines.Add($"Ficheros ({Num(subsystem.Files.Count)} en total), por carpeta:");
            var folders = subsystem.Files
                .GroupBy(FolderOf, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
            foreach (var folder in folders.Take(MaxFolders))
            {
                var names = folder.Select(NameOf).Order(StringComparer.Ordinal).ToList();
                lines.Add($"- {(folder.Key.Length == 0 ? "(raíz)" : folder.Key + "/")} ({Num(names.Count)}): {Joined(names, MaxFilesPerFolder)}");
            }
            if (folders.Count > MaxFolders)
                lines.Add($"- … y {Num(folders.Count - MaxFolders)} carpeta(s) más ({Num(folders.Skip(MaxFolders).Sum(g => g.Count()))} ficheros).");
        }

        // Tope de caracteres: se quitan líneas enteras del final y se avisa de cuántas.
        var text = new StringBuilder();
        var written = 0;
        foreach (var line in lines)
        {
            if (text.Length + line.Length + 1 > MaxMembersChars && written > 0)
                break;
            text.AppendLine(line);
            written++;
        }
        if (written < lines.Count)
        {
            text.Append("[Recortado: faltan ").Append(Num(lines.Count - written))
                .Append(" línea(s) de esta lista por tamaño. Para el resto usa graph_search y grep_search dentro de las carpetas raíz.]");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Puntos de entrada del subsistema con su <c>ruta:línea</c>.</summary>
    public static string EntryPoints(Subsystem? subsystem)
    {
        if (subsystem == null || subsystem.EntryPoints.Count == 0)
            return "(el grafo no registra puntos de entrada para este subsistema: búscalos con graph_search y grep_search; que no figuren aquí no prueba que no existan.)";

        var lines = subsystem.EntryPoints.Take(MaxEntryPoints).Select(e => $"- [{e.Kind}] {e.Name}{Location(e)}").ToList();
        if (subsystem.EntryPoints.Count > MaxEntryPoints)
            lines.Add($"- … y {Num(subsystem.EntryPoints.Count - MaxEntryPoints)} más (lista recortada).");
        return string.Join('\n', lines);
    }

    /// <summary>Las preguntas numeradas, o una frase si el plan no las trae.</summary>
    public static string Questions(IReadOnlyList<string> questions)
    {
        if (questions.Count == 0)
            return "(el plan no trae preguntas para este informe: investiga el subsistema según el objetivo del informe.)";

        var lines = questions.Take(MaxQuestions).Select((q, i) => $"{Num(i + 1)}. {q}").ToList();
        if (questions.Count > MaxQuestions)
            lines.Add($"(… y {Num(questions.Count - MaxQuestions)} pregunta(s) más sin listar.)");
        return string.Join('\n', lines);
    }

    /// <summary>La evidencia mínima, en viñetas.</summary>
    public static string Evidence(IReadOnlyList<string> minimumEvidence) =>
        minimumEvidence.Count == 0
            ? "(sin evidencia mínima definida.)"
            : string.Join('\n', minimumEvidence.Take(MaxEvidence).Select(e => $"- {e}"));

    /// <summary>
    /// El repositorio visto desde el subsistema: módulos, lenguajes, lo que el análisis no mira (no productivo) y qué otros subsistemas
    /// se investigan en paralelo, para no repetirlos y marcar las dependencias hacia ellos.
    /// </summary>
    public static string RepositoryContext(CoverageSelection selection, ReportCoverage coverage, RepositoryInventory? inventory)
    {
        var lines = new List<string>();
        if (inventory != null)
        {
            var modules = inventory.Modules.Count == 0
                ? "sin módulos detectados"
                : string.Join(", ", inventory.Modules.GroupBy(m => m.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{Num(g.Count())} {g.Key}"));
            lines.Add($"Repositorio: {Num(inventory.ProductionLoc.Files.Count)} ficheros de producción y {Num(inventory.ProductionLoc.Total)} líneas; módulos: {modules}.");
            if (inventory.Languages.Count > 0)
            {
                lines.Add("Lenguajes (con el estado de su enriquecimiento del grafo): "
                    + string.Join(", ", inventory.Languages.Select(l => $"{l.Language} ({Num(l.Files)} ficheros, {l.Status})")) + ".");
            }
            if (inventory.GraphCoverage.FilesNotInGraph > 0)
            {
                lines.Add($"El grafo no tiene nodos de {Num(inventory.GraphCoverage.FilesNotInGraph)} de los {Num(inventory.GraphCoverage.FilesOnDisk)} ficheros de producción: "
                    + "lo que el grafo no muestra puede existir en disco.");
            }
            var notProduction = NotProduction(inventory);
            if (notProduction.Count > 0)
                lines.Add("Fuera del análisis por no ser producción: " + string.Join("; ", notProduction) + ".");
        }
        else
        {
            lines.Add("(el inventario del repositorio no está disponible.)");
        }

        var others = coverage.Chosen.Where(c => c.SubsystemId != selection.SubsystemId).Select(c => c.Name).ToList();
        if (others.Count > 0)
        {
            lines.Add("Otros subsistemas que se investigan en paralelo para este informe (no los repitas; si el tuyo depende de uno, señálalo y sigue): "
                + Joined(others, MaxOtherSubsystems) + ".");
        }
        return string.Join('\n', lines);
    }

    // ───────────────────────────── Notas y cobertura del informe ─────────────────────────────

    /// <summary>
    /// Las notas de todas las investigaciones que se cubrieron, un bloque por subsistema con su cabecera, en el orden del plan. Sin
    /// ninguna cubierta, cadena vacía (el consolidador trabaja entonces como un auditor normal).
    /// </summary>
    public static string Notes(IEnumerable<InvestigationOutcome> outcomes)
    {
        var blocks = new List<string>();
        foreach (var outcome in outcomes.Where(o => o.Covered).OrderBy(o => o.Selection.Rank).ThenBy(o => o.Selection.SubsystemId, StringComparer.Ordinal))
        {
            var header = $"=== SUBSISTEMA: {outcome.Selection.Name} (id: {outcome.Selection.SubsystemId}, puesto {Num(outcome.Selection.Rank)}) ===";
            var body = outcome.Notes!.Trim();
            blocks.Add(outcome.Truncated
                ? $"{header}\n[Nota incompleta: la respuesta de la investigación se cortó al alcanzar el máximo de salida.]\n{body}"
                : $"{header}\n{body}");
        }
        return string.Join("\n\n", blocks);
    }

    /// <summary>
    /// El resumen de cobertura del informe: lo investigado, lo elegido que no se pudo cubrir (con el motivo), lo que el informe no
    /// investiga (por motivo) y lo que el inventario dice que no es producción. Es lo que el consolidador convierte en el apartado «Cobertura».
    /// </summary>
    public static string Coverage(
        ReportCoverage coverage, IEnumerable<InvestigationOutcome> outcomes, RepositoryInventory? inventory, int unassignedFiles = 0)
    {
        var results = outcomes.OrderBy(o => o.Selection.Rank).ThenBy(o => o.Selection.SubsystemId, StringComparer.Ordinal).ToList();
        var covered = results.Where(o => o.Covered).ToList();
        var failed = results.Where(o => !o.Covered).ToList();

        var text = new StringBuilder();
        text.Append("Subsistemas investigados (").Append(Num(covered.Count)).Append(" de ").Append(Num(results.Count)).AppendLine(" elegidos para este informe):");
        if (covered.Count == 0)
            text.AppendLine("- (ninguno)");
        foreach (var outcome in covered)
        {
            text.Append("- ").Append(outcome.Selection.Name).Append(" (id: ").Append(outcome.Selection.SubsystemId).Append("), ")
                .Append(Num(outcome.Selection.Loc)).Append(" líneas");
            if (outcome.Truncated)
                text.Append(" — nota incompleta (la respuesta se cortó)");
            text.AppendLine();
        }

        if (failed.Count > 0)
        {
            text.AppendLine().Append("Subsistemas elegidos que NO se cubrieron (").Append(Num(failed.Count)).AppendLine("):");
            foreach (var outcome in failed)
            {
                text.Append("- ").Append(outcome.Selection.Name).Append(" (id: ").Append(outcome.Selection.SubsystemId).Append("): ")
                    .AppendLine(outcome.FailureReason ?? "sin respuesta utilizable");
            }
        }

        if (coverage.Excluded.Count > 0)
        {
            text.AppendLine().Append("Subsistemas que este informe no investiga (").Append(Num(coverage.Excluded.Count)).AppendLine(" en total):");
            foreach (var group in coverage.Excluded.GroupBy(e => e.Reason, StringComparer.Ordinal).OrderBy(g => ReasonOrder(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var names = group.Select(e => e.Name).Order(StringComparer.Ordinal).ToList();
                text.Append("- ").Append(ReasonText(group.Key)).Append(" (").Append(Num(names.Count)).Append("): ").AppendLine(Joined(names, MaxExcludedNames));
            }
        }

        if (coverage.LongTail.Count > 0)
        {
            text.AppendLine().Append("Muestra de la cola larga sin investigar (para dar idea de lo que queda fuera): ")
                .AppendLine(Joined(coverage.LongTail.Select(t => t.Name).ToList(), MaxExcludedNames) + ".");
        }

        if (unassignedFiles > 0)
            text.AppendLine().Append("Ficheros de producción que no pertenecen a ningún subsistema: ").Append(Num(unassignedFiles)).AppendLine(".");

        var notProduction = inventory == null ? [] : NotProduction(inventory);
        if (notProduction.Count > 0)
            text.AppendLine().Append("Código que no es de producción y queda fuera del análisis (según el inventario): ").Append(string.Join("; ", notProduction)).AppendLine(".");

        return text.ToString().TrimEnd();
    }

    /// <summary>Source sets no productivos del inventario con ficheros: <c>test: 120 ficheros, 12000 líneas</c>.</summary>
    public static IReadOnlyList<string> NotProduction(RepositoryInventory inventory) =>
        [.. inventory.SourceSets
            .Where(s => !SourceSets.IsProduction(s.SourceSet) && s.Files > 0)
            .OrderBy(s => SourceSets.All.ToList().IndexOf(s.SourceSet)).ThenBy(s => s.SourceSet, StringComparer.Ordinal)
            .Select(s => $"{(s.SourceSet == SourceSets.Unknown ? "sin clasificar" : s.SourceSet)}: {Num(s.Files)} ficheros, {Num(s.Lines)} líneas")];

    // ───────────────────────────── Utilidades ─────────────────────────────

    /// <summary>Motivo de exclusión en una frase (los de <see cref="ExclusionReasons"/>); un motivo desconocido se escribe tal cual.</summary>
    public static string ReasonText(string reason) => reason switch
    {
        ExclusionReasons.BelowTopK => "Fuera de los de mayor puntuación (no entran en el tope de subsistemas por informe)",
        ExclusionReasons.TrimmedByTimeBudget => "Quitados por el presupuesto de tiempo del análisis",
        ExclusionReasons.TestSource => "Código de test",
        ExclusionReasons.QaSource => "Código de QA",
        ExclusionReasons.DebugSource => "Código de depuración",
        ExclusionReasons.GeneratedSource => "Código generado",
        ExclusionReasons.SamplesSource => "Ejemplos o muestras",
        ExclusionReasons.SharedInfrastructure => "Infraestructura compartida (no es una capacidad que describir en este informe)",
        _ => reason
    };

    // Primero lo que dejó fuera la selección, después lo que no es producción.
    private static int ReasonOrder(string reason) => reason switch
    {
        ExclusionReasons.BelowTopK => 0,
        ExclusionReasons.TrimmedByTimeBudget => 1,
        _ => 2
    };

    private static string Location(InventoryEntryPoint entry) =>
        entry.File.Length == 0 ? string.Empty : entry.Line > 0 ? $" — {entry.File}:{Num(entry.Line)}" : $" — {entry.File}";

    private static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string NameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Los primeros <paramref name="max"/> separados por coma; si hay más, «(+N)».</summary>
    private static string Joined(IReadOnlyList<string> items, int max) =>
        items.Count <= max
            ? string.Join(", ", items)
            : string.Join(", ", items.Take(max)) + $" (+{Num(items.Count - max)})";

    private static string Num(long value) => value.ToString(Invariant);
}
