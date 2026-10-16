using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Los textos planos de la investigación por subsistema (<see cref="InvestigationTexts"/>, T21): legibles, compactos, con topes de
/// tamaño que avisan cuando recortan, deterministas y tolerantes a la falta de datos (el motor de plantillas no tiene condicionales).
/// </summary>
public sealed class InvestigationTextsTests
{
    private static CoverageSelection Sel(string id, int rank = 1, long loc = 400) =>
        new() { SubsystemId = id, Name = char.ToUpperInvariant(id[0]) + id[1..], Rank = rank, Score = 0.5, Loc = loc };

    private static Subsystem Big(int folders, int filesPerFolder, int entryPoints = 0, int keyTypes = 0) => new()
    {
        Id = "big",
        Name = "Big",
        Roots = Enumerable.Range(0, 12).Select(i => $"src/root{i}").ToList(),
        Files = [.. Enumerable.Range(0, folders).SelectMany(f => Enumerable.Range(0, filesPerFolder).Select(i => $"src/pkg{f:000}/File{i:000}.java"))],
        KeyTypes = [.. Enumerable.Range(0, keyTypes).Select(i => $"com.acme.Type{i:000}")],
        EntryPoints = [.. Enumerable.Range(0, entryPoints).Select(i => new InventoryEntryPoint { Kind = EntryPointKinds.Route, Name = $"/r{i:000}", File = $"src/E{i:000}.java", Line = i + 1 })],
        Tables = [.. Enumerable.Range(0, 30).Select(i => $"T{i:00}")],
        Metrics = new SubsystemMetrics { Files = folders * filesPerFolder, Loc = 123456, EntryPoints = entryPoints, Tables = 30 }
    };

    private static ReportCoverage Coverage(params string[] chosen) => new()
    {
        Report = ReportType.FlujosEndToEnd,
        Chosen = [.. chosen.Select((id, i) => Sel(id, i + 1))],
        Questions = ["¿A?", "¿B?"],
        MinimumEvidence = ["Evidencia uno."]
    };

    [Fact]
    public void The_summary_card_has_the_id_the_size_the_roots_and_the_tables_and_no_thousands_separator()
    {
        var text = InvestigationTexts.Summary(Sel("big", 2), Big(3, 4), 8);

        Assert.StartsWith("Nombre: Big (id: big)\n".Replace("\n", Environment.NewLine), text);
        Assert.Contains("Puesto en el plan: 2 de 8 elegidos, puntuación 0.50.", text);
        Assert.Contains("12 ficheros, 123456 líneas de producción", text);
        // Las raíces y las tablas recortadas dicen cuántas faltan.
        Assert.Contains("(+4)", text);
        Assert.Contains("(+10)", text);
    }

    [Fact]
    public void Without_the_domain_map_the_card_says_so_and_the_other_blocks_still_have_text()
    {
        var summary = InvestigationTexts.Summary(Sel("billing"), null, 3);

        Assert.Contains("El mapa de dominios no está disponible", summary);
        Assert.Contains("400 líneas", summary);
        Assert.Contains("sin miembros conocidos", InvestigationTexts.Members(null));
        Assert.Contains("no registra puntos de entrada", InvestigationTexts.EntryPoints(null));
        Assert.Contains("no trae preguntas", InvestigationTexts.Questions([]));
        Assert.Contains("sin evidencia mínima", InvestigationTexts.Evidence([]));
        var input = InvestigationTexts.BuildInput(Sel("billing"), null, new ReportCoverage { Report = ReportType.FlujosEndToEnd }, null);
        Assert.All(
            new[] { input.SubsystemName, input.SubsystemSummary, input.SubsystemMembers, input.SubsystemEntryPoints, input.InvestigationQuestions, input.MinimumEvidence, input.RepositoryContext },
            text => Assert.False(string.IsNullOrWhiteSpace(text)));
    }

    [Fact]
    public void A_big_subsystem_lists_a_bounded_number_of_folders_and_names_and_tells_what_was_left_out()
    {
        // Nombres cortos para que las 40 carpetas que se listan quepan en el tope de caracteres y se vea el recorte por carpetas.
        var subsystem = new Subsystem
        {
            Id = "big", Name = "Big",
            KeyTypes = [.. Enumerable.Range(0, 80).Select(i => $"T{i:00}")],
            Files = [.. Enumerable.Range(0, 100).SelectMany(f => Enumerable.Range(0, 25).Select(i => $"s/p{f:000}/F{i:00}.java"))]
        };

        var text = InvestigationTexts.Members(subsystem);

        Assert.Contains("Tipos principales (80):", text);
        Assert.Contains($"(+{80 - InvestigationTexts.MaxKeyTypes})", text);
        Assert.Contains("Ficheros (2500 en total)", text);
        // Por carpeta se listan 10 nombres y se resumen los 15 restantes.
        Assert.Contains("- s/p000/ (25): F00.java, F01.java,", text);
        Assert.Contains("(+15)", text);
        Assert.Contains("y 60 carpeta(s) más (1500 ficheros)", text);
        Assert.DoesNotContain("s/p040/", text);
        Assert.DoesNotContain("[Recortado", text);
    }

    [Fact]
    public void The_members_block_cuts_whole_lines_over_the_character_cap_and_says_how_many()
    {
        var files = Enumerable.Range(0, 40).SelectMany(f => Enumerable.Range(0, 10).Select(i => $"src/{new string('p', 60)}{f:00}/{new string('F', 40)}{i}.java")).ToList();
        var text = InvestigationTexts.Members(new Subsystem { Id = "x", Name = "X", Files = files });

        Assert.Contains("[Recortado: faltan", text);
        Assert.Contains("graph_search y grep_search", text);
        Assert.True(text.Length < InvestigationTexts.MaxMembersChars + 400);
    }

    [Fact]
    public void Entry_points_show_their_location_and_cut_with_a_note_when_there_are_many()
    {
        var few = InvestigationTexts.EntryPoints(Big(1, 1, entryPoints: 2));
        var many = InvestigationTexts.EntryPoints(Big(1, 1, entryPoints: 100));

        Assert.Equal($"- [route] /r000 — src/E000.java:1{Environment.NewLine}- [route] /r001 — src/E001.java:2".Replace(Environment.NewLine, "\n"), few);
        Assert.Contains("… y 60 más (lista recortada).", many);
        Assert.Equal(InvestigationTexts.MaxEntryPoints + 1, many.Split('\n').Length);
    }

    [Fact]
    public void An_entry_point_without_file_or_line_is_listed_without_a_location()
    {
        var text = InvestigationTexts.EntryPoints(new Subsystem
        {
            Id = "x", Name = "X",
            EntryPoints = [new InventoryEntryPoint { Kind = EntryPointKinds.Marked, Name = "Foo" }, new InventoryEntryPoint { Kind = EntryPointKinds.Main, Name = "Bar", File = "src/Bar.java" }]
        });

        Assert.Equal("- [marked_entry_point] Foo\n- [main] Bar — src/Bar.java", text);
    }

    [Fact]
    public void The_repository_context_names_the_other_chosen_subsystems_the_languages_and_what_is_not_production()
    {
        var inventory = new RepositoryInventory
        {
            Modules = [new InventoryModule { Path = "a", Kind = "maven" }, new InventoryModule { Path = "b", Kind = "maven" }, new InventoryModule { Path = "c", Kind = "node" }],
            Languages = [new LanguageStatus { Language = "java", Files = 90, Status = LanguageStatuses.Enriched }],
            SourceSets =
            [
                new SourceSetSummary { SourceSet = SourceSets.Production, Files = 90, Lines = 9000 },
                new SourceSetSummary { SourceSet = SourceSets.Test, Files = 30, Lines = 3000 },
                new SourceSetSummary { SourceSet = SourceSets.Unknown, Files = 2, Lines = 20 },
                new SourceSetSummary { SourceSet = SourceSets.Qa, Files = 0, Lines = 0 }
            ],
            ProductionLoc = new ProductionLines { Total = 9000, Files = [new FileLines { Path = "a", Language = "java", Lines = 10 }] },
            GraphCoverage = new GraphCoverage { FilesOnDisk = 90, FilesNotInGraph = 5 }
        };

        var text = InvestigationTexts.RepositoryContext(Sel("billing"), Coverage("billing", "orders", "catalog"), inventory);

        Assert.Contains("2 maven, 1 node", text);
        Assert.Contains("java (90 ficheros, enriched)", text);
        Assert.Contains("test: 30 ficheros, 3000 líneas; sin clasificar: 2 ficheros, 20 líneas", text);
        Assert.DoesNotContain("qa:", text);
        Assert.Contains("no tiene nodos de 5 de los 90 ficheros", text);
        Assert.Contains("Orders, Catalog", text);
        Assert.DoesNotContain("Billing", text.Split('\n').Last());
    }

    [Fact]
    public void Notes_have_one_headed_block_per_covered_subsystem_in_plan_order_and_skip_the_failed()
    {
        var notes = InvestigationTexts.Notes(
        [
            new InvestigationOutcome(Sel("orders", 2), "Notas B.", null),
            new InvestigationOutcome(Sel("billing", 1), "  Notas A.  ", null),
            new InvestigationOutcome(Sel("catalog", 3), null, "falló"),
            new InvestigationOutcome(Sel("tail", 4), "Cortadas.", null, Truncated: true)
        ]);

        Assert.Equal(
            "=== SUBSISTEMA: Billing (id: billing, puesto 1) ===\nNotas A.\n\n" +
            "=== SUBSISTEMA: Orders (id: orders, puesto 2) ===\nNotas B.\n\n" +
            "=== SUBSISTEMA: Tail (id: tail, puesto 4) ===\n[Nota incompleta: la respuesta de la investigación se cortó al alcanzar el máximo de salida.]\nCortadas.",
            notes);
        Assert.Equal(string.Empty, InvestigationTexts.Notes([new InvestigationOutcome(Sel("x"), null, "falló")]));
    }

    [Fact]
    public void An_outcome_with_blank_notes_is_not_covered()
    {
        Assert.False(new InvestigationOutcome(Sel("x"), "   ", null).Covered);
        Assert.False(new InvestigationOutcome(Sel("x"), "texto", "falló").Covered);
        Assert.True(new InvestigationOutcome(Sel("x"), "texto", null).Covered);
    }

    [Fact]
    public void The_coverage_summary_lists_covered_failed_and_excluded_by_reason_and_what_the_inventory_says_is_not_production()
    {
        var coverage = Coverage("billing", "orders") with
        {
            Excluded =
            [
                new CoverageExclusion { SubsystemId = "t", Name = "Zeta", Reason = ExclusionReasons.TestSource },
                new CoverageExclusion { SubsystemId = "a", Name = "Alfa", Reason = ExclusionReasons.BelowTopK },
                new CoverageExclusion { SubsystemId = "b", Name = "Beta", Reason = ExclusionReasons.BelowTopK },
                new CoverageExclusion { SubsystemId = "g", Name = "Gen", Reason = ExclusionReasons.GeneratedSource }
            ],
            LongTail = [Sel("beta", 9)]
        };
        var inventory = new RepositoryInventory
        {
            SourceSets = [new SourceSetSummary { SourceSet = SourceSets.Production, Files = 9, Lines = 90 }, new SourceSetSummary { SourceSet = SourceSets.Test, Files = 3, Lines = 30 }]
        };

        var text = InvestigationTexts.Coverage(
            coverage,
            [new InvestigationOutcome(Sel("billing", 1, 1500), "n", null), new InvestigationOutcome(Sel("orders", 2), null, "la investigación superó su límite de 12 min")],
            inventory,
            unassignedFiles: 7).Replace("\r\n", "\n");

        Assert.Equal(
            "Subsistemas investigados (1 de 2 elegidos para este informe):\n" +
            "- Billing (id: billing), 1500 líneas\n\n" +
            "Subsistemas elegidos que NO se cubrieron (1):\n" +
            "- Orders (id: orders): la investigación superó su límite de 12 min\n\n" +
            "Subsistemas que este informe no investiga (4 en total):\n" +
            "- Fuera de los de mayor puntuación (no entran en el tope de subsistemas por informe) (2): Alfa, Beta\n" +
            "- Código generado (1): Gen\n" +
            "- Código de test (1): Zeta\n\n" +
            "Muestra de la cola larga sin investigar (para dar idea de lo que queda fuera): Beta.\n\n" +
            "Ficheros de producción que no pertenecen a ningún subsistema: 7.\n\n" +
            "Código que no es de producción y queda fuera del análisis (según el inventario): test: 3 ficheros, 30 líneas.",
            text);
    }

    [Fact]
    public void The_coverage_summary_with_nothing_covered_says_none_and_tolerates_no_exclusions_and_no_inventory()
    {
        var text = InvestigationTexts.Coverage(
            new ReportCoverage { Report = ReportType.FlujosEndToEnd }, [new InvestigationOutcome(Sel("billing"), null, "falló")], null);

        Assert.Contains("(0 de 1 elegidos", text);
        Assert.Contains("- (ninguno)", text);
        Assert.DoesNotContain("no investiga", text);
        Assert.DoesNotContain("según el inventario", text);
    }

    [Fact]
    public void A_long_list_of_excluded_names_is_capped_with_a_count()
    {
        var coverage = new ReportCoverage
        {
            Report = ReportType.FlujosEndToEnd,
            Excluded = [.. Enumerable.Range(0, 60).Select(i => new CoverageExclusion { SubsystemId = $"s{i:00}", Name = $"S{i:00}", Reason = ExclusionReasons.BelowTopK })]
        };

        var text = InvestigationTexts.Coverage(coverage, [], null);

        Assert.Contains("(60): S00,", text);
        Assert.Contains($"(+{60 - InvestigationTexts.MaxExcludedNames})", text);
        Assert.DoesNotContain("S59", text);
    }

    [Fact]
    public void The_texts_are_deterministic_and_do_not_depend_on_the_machine_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            var spanish = InvestigationTexts.Summary(Sel("big"), Big(3, 4), 8);
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            var english = InvestigationTexts.Summary(Sel("big"), Big(3, 4), 8);

            Assert.Equal(spanish, english);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(ExclusionReasons.BelowTopK, "Fuera de los de mayor puntuación")]
    [InlineData(ExclusionReasons.TrimmedByTimeBudget, "Quitados por el presupuesto de tiempo")]
    [InlineData(ExclusionReasons.TestSource, "Código de test")]
    [InlineData(ExclusionReasons.QaSource, "Código de QA")]
    [InlineData(ExclusionReasons.DebugSource, "Código de depuración")]
    [InlineData(ExclusionReasons.GeneratedSource, "Código generado")]
    [InlineData(ExclusionReasons.SamplesSource, "Ejemplos o muestras")]
    [InlineData("otro_motivo", "otro_motivo")]
    public void Each_exclusion_reason_has_a_sentence_and_an_unknown_one_is_written_as_it_comes(string reason, string expected)
    {
        Assert.StartsWith(expected, InvestigationTexts.ReasonText(reason));
    }
}
