using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="DomainNaming"/> (T11, criterio A2): el LLM solo nombra clusters que ya existen y su respuesta se valida en código.
/// Con una respuesta que devuelve menos subsistemas, texto basura o un error, el mapa conserva todos los clusters (con el nombre
/// derivado los que no respondió coherentemente); no se puede descartar ni inventar un subsistema.
/// </summary>
public sealed class DomainNamingTests
{
    private static Subsystem Sub(string id, string term = "", string name = "") => new()
    {
        Id = id,
        Name = name.Length > 0 ? name : (term.Length > 0 ? char.ToUpperInvariant(term[0]) + term[1..] : id),
        DominantTerm = term,
        Roots = [$"src/{id}"],
        Files = [$"src/{id}/A.java"],
        KeyTypes = [$"app.{id}.Alpha", $"app.{id}.Beta"],
        EntryPoints = [new InventoryEntryPoint { Kind = EntryPointKinds.StrutsAction, Name = $"/{id}.do" }],
        Tables = [$"{id.ToUpperInvariant()}_T"],
        Metrics = new SubsystemMetrics { Files = 1, Loc = 10 }
    };

    private static DomainMap MapOf(params Subsystem[] subsystems) => new() { Subsystems = subsystems };

    private static string Block(string id, string name, string description = "Una descripción.", string priority = "alta") =>
        $"### {id}\nNombre: {name}\nDescripción: {description}\nPrioridad: {priority}\n";

    [Fact]
    public void A_well_formed_answer_is_parsed_block_by_block()
    {
        var text = Block("alpha", "Gestión de envíos", "Registra y consulta envíos.", "alta") + "\n" + Block("beta", "Soporte", "Utilidades.", "baja");

        var parsed = DomainNaming.Parse(text, ["alpha", "beta"]);

        Assert.Equal(new SubsystemNaming("Gestión de envíos", "Registra y consulta envíos.", SubsystemPriority.High), parsed["alpha"]);
        Assert.Equal(new SubsystemNaming("Soporte", "Utilidades.", SubsystemPriority.Low), parsed["beta"]);
    }

    [Fact]
    public void The_usual_decorations_and_english_keys_are_tolerated()
    {
        var text = """
            Aquí tienes el resultado:

            ## `alpha`
            - **Name:** "Shipment tracking"
            - **Description:** Tracks parcels.
            - **Priority:** Medium

            **Subsistema:** beta
            * Nombre: Libro mayor
            * Descripcion: Asientos contables
            * Prioridad: BAJA.
            """;

        var parsed = DomainNaming.Parse(text, ["alpha", "beta"]);

        Assert.Equal(new SubsystemNaming("Shipment tracking", "Tracks parcels.", SubsystemPriority.Medium), parsed["alpha"]);
        Assert.Equal(new SubsystemNaming("Libro mayor", "Asientos contables", SubsystemPriority.Low), parsed["beta"]);
    }

    [Fact]
    public void Unknown_ids_blocks_without_a_usable_name_and_repeated_names_are_ignored()
    {
        var text = Block("alpha", "Envíos")
            + Block("alpha", "Otro nombre para alpha")           // id repetido: vale el primero
            + Block("invented", "Subsistema inventado")          // no existe
            + Block("beta", "<nombre corto>")                    // plantilla sin rellenar
            + Block("gamma", "")                                 // sin nombre
            + Block("delta", new string('x', DomainNaming.MaxNameLength + 1))
            + Block("epsilon", "envíos");                        // mismo nombre que alpha, sin mirar mayúsculas
        var known = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" };

        var parsed = DomainNaming.Parse(text, known);

        Assert.Equal(["alpha"], parsed.Keys);
        Assert.Equal("Envíos", parsed["alpha"].Name);
    }

    [Fact]
    public void A_heading_that_is_not_an_id_closes_the_block_so_its_lines_are_not_credited_to_the_previous_subsystem()
    {
        var text = Block("alpha", "Envíos") + "\n### Notas finales\nNombre: Esto no es de alpha\nPrioridad: baja\n";

        var parsed = DomainNaming.Parse(text, ["alpha"]);

        Assert.Equal(new SubsystemNaming("Envíos", "Una descripción.", SubsystemPriority.High), parsed["alpha"]);
    }

    [Fact]
    public void A_missing_priority_is_unspecified_and_a_long_description_is_cut()
    {
        var text = $"### alpha\nNombre: Envíos\nDescripción: {new string('y', 600)}\nPrioridad: urgentísima\n";

        var parsed = DomainNaming.Parse(text, ["alpha"]);

        Assert.Equal(SubsystemPriority.Unspecified, parsed["alpha"].Priority);
        Assert.Equal(DomainNaming.MaxDescriptionLength + 1, parsed["alpha"].Description.Length);
        Assert.EndsWith("…", parsed["alpha"].Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("No puedo ayudar con eso.")]
    [InlineData("{ \"alpha\": \"Envíos\" }")]
    public void An_answer_without_the_requested_format_gives_nothing(string? text) =>
        Assert.Empty(DomainNaming.Parse(text, ["alpha"]));

    [Fact]
    public void Applying_names_keeps_every_subsystem_and_marks_only_the_named_ones()
    {
        var map = MapOf(Sub("alpha", "alpha"), Sub("beta", "beta"), Sub("gamma"));
        var naming = new Dictionary<string, SubsystemNaming>
        {
            ["alpha"] = new("Envíos", "Registra envíos.", SubsystemPriority.High),
            // El LLM llama a beta igual que el nombre derivado de otro subsistema: el derivado se distingue.
            ["beta"] = new("gamma", "", SubsystemPriority.Low)
        };

        var result = DomainNaming.Apply(map, naming);

        Assert.Equal(["alpha", "beta", "gamma"], result.Subsystems.Select(s => s.Id));
        Assert.Equal([SubsystemNameOrigins.Llm, SubsystemNameOrigins.Llm, SubsystemNameOrigins.Derived], result.Subsystems.Select(s => s.NameOrigin));
        Assert.Equal(["Envíos", "gamma", "gamma (gamma)"], result.Subsystems.Select(s => s.Name));
        Assert.Equal("Registra envíos.", result.Subsystems[0].Description);
        Assert.Equal(SubsystemPriority.High, result.Subsystems[0].Priority);
        Assert.Equal(map.Subsystems[0].Files, result.Subsystems[0].Files);
    }

    [Fact]
    public async Task An_answer_with_fewer_subsystems_than_candidates_loses_none_of_them()
    {
        var map = MapOf(Sub("alpha", "alpha"), Sub("beta", "beta"), Sub("gamma", "gamma"));
        var warnings = new List<string>();

        var result = await DomainNaming.NameAsync(map, (_, _) => Task.FromResult<string?>(Block("beta", "Libro mayor")), warnings, CancellationToken.None);

        Assert.Equal(["alpha", "beta", "gamma"], result.Subsystems.Select(s => s.Id));
        Assert.Equal(["Alpha", "Libro mayor", "Gamma"], result.Subsystems.Select(s => s.Name));
        Assert.Equal([SubsystemNameOrigins.Derived, SubsystemNameOrigins.Llm, SubsystemNameOrigins.Derived], result.Subsystems.Select(s => s.NameOrigin));
        Assert.Contains("1 de 3", Assert.Single(warnings));
    }

    [Theory]
    [InlineData("texto sin ningún formato")]
    [InlineData(null)]
    public async Task A_garbage_answer_leaves_all_the_derived_names_and_warns(string? answer)
    {
        var map = MapOf(Sub("alpha", "alpha"), Sub("beta"));
        var warnings = new List<string>();

        var result = await DomainNaming.NameAsync(map, (_, _) => Task.FromResult(answer), warnings, CancellationToken.None);

        Assert.Equal(map.Subsystems.Select(s => (s.Id, s.Name)), result.Subsystems.Select(s => (s.Id, s.Name)));
        Assert.All(result.Subsystems, s => Assert.Equal(SubsystemNameOrigins.Derived, s.NameOrigin));
        Assert.Contains("no se pudo usar", Assert.Single(warnings));
    }

    [Fact]
    public async Task A_failing_llm_is_a_warning_and_the_map_keeps_every_cluster()
    {
        var map = MapOf(Sub("alpha", "alpha"), Sub("beta", "beta"));
        var warnings = new List<string>();

        var result = await DomainNaming.NameAsync(map, (_, _) => throw new InvalidOperationException("sin red"), warnings, CancellationToken.None);

        Assert.Equal(["alpha", "beta"], result.Subsystems.Select(s => s.Id));
        Assert.All(result.Subsystems, s => Assert.Equal(SubsystemNameOrigins.Derived, s.NameOrigin));
        Assert.Contains("sin red", Assert.Single(warnings));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var map = MapOf(Sub("alpha"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DomainNaming.NameAsync(map, (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }, [], cts.Token));
    }

    [Fact]
    public async Task Big_maps_are_named_in_batches_and_a_failed_batch_only_affects_its_own_subsystems()
    {
        var subsystems = Enumerable.Range(0, DomainNaming.BatchSize + 5).Select(i => Sub($"s{i:000}")).ToArray();
        var map = MapOf(subsystems);
        var sizes = new List<int>();
        var warnings = new List<string>();

        var result = await DomainNaming.NameAsync(map, (prompt, _) =>
        {
            var ids = prompt.Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)).Select(l => l[4..]).ToList();
            sizes.Add(ids.Count);
            if (sizes.Count == 2)
                throw new TimeoutException("agotó el límite");
            return Task.FromResult<string?>(string.Concat(ids.Select(id => Block(id, $"Nombre {id}"))));
        }, warnings, CancellationToken.None);

        Assert.Equal([DomainNaming.BatchSize, 5], sizes);
        Assert.Equal(subsystems.Length, result.Subsystems.Count);
        Assert.Equal(DomainNaming.BatchSize, result.Subsystems.Count(s => s.NameOrigin == SubsystemNameOrigins.Llm));
        Assert.Equal(5, result.Subsystems.Count(s => s.NameOrigin == SubsystemNameOrigins.Derived));
        Assert.Contains("agotó el límite", Assert.Single(warnings));
    }

    [Fact]
    public void The_prompt_summarizes_each_cluster_with_what_is_needed_to_name_it_and_nothing_else()
    {
        var subsystem = Sub("alpha", "parcel") with { Metrics = new SubsystemMetrics { Files = 12, Loc = 3400 } };

        var prompt = DomainNaming.BuildPrompt([subsystem, Sub("beta")]);

        Assert.StartsWith("### alpha\n", prompt.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("Tamaño: 12 ficheros, 3400 líneas", prompt);
        Assert.Contains("Término dominante: parcel", prompt);
        Assert.Contains("Carpetas: src/alpha", prompt);
        Assert.Contains("Tipos clave: Alpha, Beta", prompt);
        Assert.Contains("Entradas: struts_action /alpha.do", prompt);
        Assert.Contains("Tablas: ALPHA_T", prompt);
        Assert.DoesNotContain("src/alpha/A.java", prompt);
        // Un subsistema sin término dominante no lo lleva.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(prompt, "Término dominante"));
    }
}
