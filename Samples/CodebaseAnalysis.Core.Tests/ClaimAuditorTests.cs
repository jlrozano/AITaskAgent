using AITaskAgent.LLM.Models;
using CodebaseAnalysis.Core.Chat;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// La auditoría de las afirmaciones del chat: la evidencia sale del resultado de las tools del turno, y una marca que no
/// se puede respaldar queda «sin comprobar» con su motivo, nunca como error.
/// </summary>
public sealed class ClaimAuditorTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ChatWorkspace _ws;

    public ClaimAuditorTests()
    {
        _ws = new ChatWorkspace(_dir.Path, Path.Combine(_dir.Path, ".codebase", "codebase.db"), Path.Combine(_dir.Path, "reports"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "src"));
        File.WriteAllLines(Path.Combine(_dir.Path, "src", "Orders.cs"), Enumerable.Range(1, 60).Select(i => $"// línea {i}"));
        Directory.CreateDirectory(_ws.ReportsPath);
        File.WriteAllText(Path.Combine(_ws.ReportsPath, "05_errores_y_bugs.md"), "# Errores y bugs\n\n## BUG-001\nTexto");
    }

    public void Dispose() => _dir.Dispose();

    private string Full(string relative) => Path.Combine(_dir.Path, relative.Replace('/', Path.DirectorySeparatorChar));

    private static List<Message> Turn(params (string Tool, string Arguments, string Result)[] calls)
    {
        var messages = new List<Message> { new() { Role = "user", Content = "pregunta" } };
        for (var i = 0; i < calls.Length; i++)
        {
            var id = $"call_{i}";
            messages.Add(new Message
            {
                Role = "assistant",
                Content = string.Empty,
                ToolCalls = [new ToolCall { Id = id, Name = calls[i].Tool, Arguments = calls[i].Arguments }]
            });
            messages.Add(new Message { Role = "tool", ToolCallId = id, Content = calls[i].Result });
        }
        return messages;
    }

    private string ViewFileResult(string relative, int start, int end) =>
        $"File Path: `file:///{Full(relative).Replace('\\', '/')}`\nTotal Lines: 60\nShowing lines {start} to {end}\n{start}: // línea {start}";

    private ChatTurnAudit Audit(string answer, List<Message> messages) =>
        ClaimAuditor.Audit(answer, TurnEvidence.FromMessages(messages, _dir.Path), _ws);

    [Fact]
    public void A_verified_claim_needs_the_cited_lines_read_in_the_turn()
    {
        var messages = Turn(("view_file", """{"AbsolutePath":"src/Orders.cs","StartLine":10,"EndLine":30}""", ViewFileResult("src/Orders.cs", 10, 30)));
        var answer = """
            [V] La validación está en `src/Orders.cs:12-18`.
            [V] El total se calcula en `src/Orders.cs:25-40`.
            [V] El log está en `src/Orders.cs:50`.
            [V] El reintento está en `src/Orders.cs:70`.
            [V] Y esto no cita nada.
            """;

        var audit = Audit(answer, messages);

        Assert.Equal(
            [ChatClaimStatus.Verified, ChatClaimStatus.Unchecked, ChatClaimStatus.Unchecked, ChatClaimStatus.Unchecked, ChatClaimStatus.Unchecked],
            audit.Claims.Select(c => c.Status));
        Assert.Equal(
            [ChatReferenceStatus.Ok, ChatReferenceStatus.Partial, ChatReferenceStatus.NotRead, ChatReferenceStatus.OutOfRange],
            audit.References.Select(r => r.Status));
        Assert.Contains("parte", audit.Claims[1].Reason);
        Assert.Contains("no se leyó", audit.Claims[2].Reason);
        Assert.Contains("no existen", audit.Claims[3].Reason);
        Assert.Contains("Sin referencia al código", audit.Claims[4].Reason);
        Assert.Equal("src/Orders.cs", audit.References[0].Target);
        Assert.Equal((12, 18), (audit.References[0].StartLine, audit.References[0].EndLine));
    }

    [Fact]
    public void Grep_hits_and_graph_code_count_as_reads_and_a_partial_path_matches_the_file_read()
    {
        var messages = Turn(
            ("grep_search", """{"SearchPath":"src","Query":"línea 44"}""", """[{"Filename":"Orders.cs","LineNumber":44,"LineContent":"// línea 44"}]"""),
            ("graph_search", """{"name_pattern":"Total","include_code":true}""", "# Búsqueda\n\n#### Código · `src/Orders.cs:5-8` (Total)\n```\n5: x\n```"));
        var answer = "- [V] Coincidencia en `src/Orders.cs:44`.\n- [V] El método está en `Orders.cs:5-8`.";

        var audit = Audit(answer, messages);

        Assert.All(audit.Claims, c => Assert.Equal(ChatClaimStatus.Verified, c.Status));
        Assert.Equal("src/Orders.cs", audit.References[1].Target);
    }

    [Fact]
    public void Graph_neighbors_domain_and_search_without_code_are_not_code_reads()
    {
        var messages = Turn(
            ("graph_neighbors", """{"qualified_name":"Orders.Total","direction":"in"}""",
                "# Vecinos en el grafo de `Orders.Total`\n\n| `Method` | **Run** | `Jobs.Run` | `src/Orders.cs:12` | 1 llamada · línea 14 de `src/Orders.cs` |"),
            ("graph_domain", """{"view":"routes"}""", "| `GET` | `/orders` | `src/Orders.cs:20` |"),
            ("graph_search", """{"name_pattern":"Total"}""", "| `Method` | **Total** | `Orders.Total` | `src/Orders.cs` | 30 |\n\n### Propiedades\n\n- **Total** `Orders.Total`: signature: `int Total()`"));
        var answer = "- [V] `Jobs.Run` llama a `Orders.Total` en `src/Orders.cs:14`.\n- [V] La ruta está en `src/Orders.cs:20`.\n- [V] Total está en `src/Orders.cs:30`.";

        var audit = Audit(answer, messages);

        Assert.All(audit.Claims, c => Assert.Equal(ChatClaimStatus.Unchecked, c.Status));
        Assert.All(audit.References, r => Assert.Equal(ChatReferenceStatus.NotRead, r.Status));
    }

    [Fact]
    public void Documented_claims_need_the_report_read_and_inferred_ones_are_left_alone()
    {
        var read = Turn(("read_report", """{"file_name":"05_errores_y_bugs.md"}""", "# Errores y bugs"));
        var answer = "[D] Hay un bug de filtros `05_errores_y_bugs.md › BUG-001`. [I] Parece que afecta a la exportación. [D] Sin fuente.";

        var audit = Audit(answer, read);

        Assert.Equal([ChatClaimStatus.Documented, ChatClaimStatus.Inferred, ChatClaimStatus.Unchecked], audit.Claims.Select(c => c.Status));
        Assert.Equal(ChatReferenceKinds.Source, audit.References.Single().Kind);
        Assert.Equal("05_errores_y_bugs.md", audit.References.Single().Target);

        var notRead = Audit(answer, Turn());
        Assert.Equal(ChatClaimStatus.Unchecked, notRead.Claims[0].Status);
        Assert.Equal(ChatReferenceStatus.NotRead, notRead.References.Single().Status);
    }

    [Fact]
    public void Marks_and_references_inside_code_are_ignored_and_a_mark_ends_with_its_paragraph()
    {
        var messages = Turn(("view_file", """{"AbsolutePath":"src/Orders.cs"}""", ViewFileResult("src/Orders.cs", 1, 60)));
        var answer = """
            [V] El fichero tiene sesenta líneas
            que siguen aquí `src/Orders.cs:1-60`.

            Esto es una propuesta sin marca `src/Orders.cs:3`, y `[V]` en un span no cuenta.

            ```csharp
            // [V] tampoco dentro de un bloque `src/Orders.cs:2`
            ```
            """;

        var audit = Audit(answer, messages);

        var claim = Assert.Single(audit.Claims);
        Assert.Equal(ChatClaimStatus.Verified, claim.Status);
        Assert.Equal(["src/Orders.cs:1-60"], claim.References);
        // La referencia suelta también se audita (la UI la pinta), pero no pertenece a ninguna afirmación.
        Assert.Equal(["src/Orders.cs:1-60", "src/Orders.cs:3"], audit.References.Select(r => r.Text));
    }

    [Fact]
    public void Paths_outside_the_repository_or_missing_files_are_never_verified()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fuera.cs");
        var messages = Turn(("view_file", "{}", $"File Path: `file:///{outside.Replace('\\', '/')}`\nShowing lines 1 to 5"));
        var answer = "[V] Fuera `../fuera.cs:1`. [V] No existe `src/Nada.cs:1`. Identificador `Pedidos.MontarFiltro` sin línea.";

        var audit = Audit(answer, messages);

        Assert.All(audit.Claims, c => Assert.Equal(ChatClaimStatus.Unchecked, c.Status));
        Assert.Equal(2, audit.References.Count);
        Assert.All(audit.References, r => Assert.Equal(ChatReferenceStatus.Missing, r.Status));
    }

    [Fact]
    public void Tool_errors_are_not_evidence()
    {
        var messages = Turn(("view_file", """{"AbsolutePath":"src/Orders.cs"}""", "Error: File not found at C:\\x"));

        var audit = Audit("[V] Algo `src/Orders.cs:1`.", messages);

        Assert.Equal(ChatReferenceStatus.NotRead, audit.References.Single().Status);
    }

    [Theory]
    [InlineData("TOOL_ERROR: File 'src/Orders.cs' does not exist.\nGuidance: …")]
    [InlineData("TOOL_ERROR: None of the requested node paths were found.\nNot found: Orders.Total")]
    [InlineData("TOOL_CALL_ERROR: NodePaths is required but was not provided.")]
    [InlineData("  TOOL_CALL_ERROR: Invalid arguments format.")]
    public void A_view_code_item_error_is_not_code_read(string result)
    {
        var messages = Turn(("view_code_item", """{"File":"src/Orders.cs","NodePaths":["Orders.Total"]}""", result));

        var audit = Audit("[V] El total está en `src/Orders.cs:12`.", messages);

        Assert.Equal(ChatReferenceStatus.NotRead, audit.References.Single().Status);
        Assert.NotEqual(ChatClaimStatus.Verified, audit.Claims.Single().Status);
    }

    [Fact]
    public void View_code_item_covers_only_the_lines_of_the_nodes_it_returned()
    {
        var result = """
            === Orders.Total ===
            Location: src/Orders.cs:10-20

            // línea 10

            ⚠️ Not found: Orders.Missing
            """;
        var messages = Turn(("view_code_item", """{"File":"src/Orders.cs","NodePaths":["Orders.Total","Orders.Missing"]}""", result));

        var audit = Audit("[V] El total está en `src/Orders.cs:12-18`. [V] El log está en `src/Orders.cs:50`.", messages);

        Assert.Equal([ChatReferenceStatus.Ok, ChatReferenceStatus.NotRead], audit.References.Select(r => r.Status));
        Assert.Equal([ChatClaimStatus.Verified, ChatClaimStatus.Unchecked], audit.Claims.Select(c => c.Status));
    }
}
