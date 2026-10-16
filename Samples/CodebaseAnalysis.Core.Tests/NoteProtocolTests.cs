using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Conversation.Context;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Tools.Abstractions;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Notes;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Protocolo de notas (plan 2026-10-06, §4.3): las tools envueltas marcan cada resultado con <c>[Rn]</c>, la etiqueta
/// <c>&lt;nota r="Rn"&gt;</c> lo sustituye en la conversación por la nota, un resultado sin nota se queda entero con un aviso, y
/// sin sesión (el chat, un paso sin la etiqueta) nada cambia. También la respuesta final con <c>tool_choice: none</c> (T3).
/// </summary>
public sealed class NoteProtocolTests
{
    private const string Prompt = "Investiga el subsistema.";
    private const string FinalAnswerPrompt = "Entrega ya lo que tengas.";

    // ───────────────────────────── Dobles ─────────────────────────────

    private sealed class ReadTool(string name = "view_file") : ITool
    {
        private int _calls;
        public string Name => name;
        public string Description => "tool de prueba";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };

        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult($"contenido largo {Interlocked.Increment(ref _calls)}");
    }

    private sealed class ThrowingTool : ITool
    {
        public string Name => "graph_search";
        public string Description => "falla";
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };

        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sin grafo");
    }

    /// <summary>Lo que ve el modelo en una petición.</summary>
    private sealed record Seen(
        string System, IReadOnlyList<string> ToolMessages, IReadOnlyList<string> AssistantMessages, int ToolCount, bool ForbidsTools, string? ToolChoice);

    /// <summary>Responde según el número de petición con un guion de respuestas; la última se repite.</summary>
    private sealed class ScriptedLlm(params Func<int, LlmResponse>[] script) : ILlmService
    {
        private readonly Lock _lock = new();
        private readonly List<Seen> _seen = [];

        public IReadOnlyList<Seen> Seen
        {
            get
            {
                lock (_lock) return [.. _seen];
            }
        }

        public string Name => "scripted";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var messages = request.Conversation.History.Messages;
            int index;
            lock (_lock)
            {
                _seen.Add(new Seen(
                    request.SystemPrompt ?? "",
                    [.. messages.Where(m => m.Role == LlmConstants.MessageRoles.Tool).Select(m => m.Content ?? "")],
                    [.. messages.Where(m => m.Role == LlmConstants.MessageRoles.Assistant).Select(m => m.Content ?? "")],
                    request.Tools?.Count ?? 0,
                    FinalAnswerRequests.ForbidsTools(request),
                    request.Options.Value<string>(FinalAnswerRequests.ToolChoiceOption)));
                index = _seen.Count - 1;
            }

            var response = script[Math.Min(index, script.Length - 1)](index);
            return Task.FromResult(LlmResult.Success([response], tokensUsed: 10, promptTokens: 8, completionTokens: 2, model: request.Model));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private static Func<int, LlmResponse> Calls(string content, string tool = "view_file") => i => new LlmResponse
    {
        Content = content,
        FinishReason = FinishReason.ToolCalls,
        ToolCalls = [new ToolCall { Id = $"call_{i}", Name = tool, Arguments = $"{{\"n\":{i}}}" }]
    };

    private static Func<int, LlmResponse> Answer(string content) => _ => new LlmResponse { Content = content, FinishReason = FinishReason.Stop };

    private static LlmRole RoleOf(ILlmService llm) => new()
    {
        Provider = new LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/" },
        Model = "fake-model",
        Inference = new LlmInferenceSettings { Temperature = 0.1f, MaxTokens = 1000 }
    };

    private static CustomStatelessTemplateLlmStep<AnalysisPipelineInput, TOut> NewStep<TOut>(
        ILlmService llm, bool noteProtocol, ITool? tool = null, int maxToolIterations = 10, string? finalAnswerPrompt = null)
        where TOut : ILlmStepResult
    {
        // Sin proveedor de plantillas, el prompt se usa tal cual.
        var step = new CustomStatelessTemplateLlmStep<AnalysisPipelineInput, TOut>("Investigation_Prueba", RoleOf(llm), templateProvider: null!, Prompt)
        {
            MaxToolIterations = maxToolIterations,
            ToolLimitFinalAnswerPrompt = finalAnswerPrompt,
            TagHandlers = noteProtocol ? [NoteTagHandler.Instance] : []
        };
        step.Tools = [new NoteProtocolTool(tool ?? new ReadTool())];
        return step;
    }

    private static (PipelineContext Context, CapturingReporter Reporter) NewContext()
    {
        var reporter = new CapturingReporter();
        var context = new PipelineContext();
        context.Metadata[AnalysisContextKeys.Reporter] = reporter;
        return (context, reporter);
    }

    private static async Task<AITaskAgent.Core.Abstractions.IStepResult> RunAsync(AITaskAgent.Core.Abstractions.IStep step, PipelineContext context) =>
        await new Pipeline().ExecuteAsync("run", [step], new AnalysisPipelineInput(), context).WaitAsync(TimeSpan.FromSeconds(20));

    // ───────────────────────────── (a) sustitución en tres vueltas ─────────────────────────────

    [Fact]
    public async Task Annotated_results_are_replaced_by_their_notes_and_the_last_one_stays_whole()
    {
        var llm = new ScriptedLlm(
            Calls(""),
            Calls("<nota r=\"R1\">A.java:10 valida el importe.</nota>"),
            Calls("<nota r=\"R2\">sin interés</nota>"),
            Answer("<nota r=\"R3\">B.java:4 guarda el pedido.</nota>\nResumen final."));
        var (context, reporter) = NewContext();

        var result = await RunAsync(NewStep<LlmStringStepResult>(llm, noteProtocol: true), context);

        Assert.False(result.HasError, result.Error?.Message);
        var seen = llm.Seen;
        Assert.Equal(4, seen.Count);

        // Vuelta 2: R1 llega entero y marcado.
        Assert.Equal(["[R1] contenido largo 1"], seen[1].ToolMessages);
        // Vuelta 3: R1 ya es su nota; R2 entero. El resto del prefijo no cambia.
        Assert.Equal(["[R1 leído; tu nota:] A.java:10 valida el importe.", "[R2] contenido largo 2"], seen[2].ToolMessages);
        // Vuelta 4: R1 y R2 sustituidos; R3, el último, entero (su nota llega en la respuesta final).
        Assert.Equal(
            ["[R1 leído; tu nota:] A.java:10 valida el importe.", "[R2 leído; tu nota:] sin interés", "[R3] contenido largo 3"],
            seen[3].ToolMessages);
        // La etiqueta queda en el historial como su marcador, no como el texto de la nota.
        Assert.Contains("[nota R1 guardada]", seen[2].AssistantMessages[^1]);
        Assert.DoesNotContain("valida el importe", string.Join("\n", seen[3].AssistantMessages));

        Assert.True(NoteSessions.TryGet(context, out var session));
        Assert.False(session.IsActive);
        Assert.Equal(["R1", "R2", "R3"], session.Notes.Select(n => n.Key));
        Assert.Empty(session.Unannotated);
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
        // Las instrucciones de la etiqueta están en el system prompt.
        Assert.Contains("<nota r=\"Rn\">", seen[0].System);
    }

    // ───────────────────────────── (b) resultado sin nota ─────────────────────────────

    [Fact]
    public async Task A_result_without_a_note_stays_whole_and_raises_a_warning_but_the_step_succeeds()
    {
        var llm = new ScriptedLlm(Calls(""), Calls(""), Answer("Resumen sin notas."));
        var (context, reporter) = NewContext();

        var result = await RunAsync(NewStep<LlmStringStepResult>(llm, noteProtocol: true), context);

        Assert.False(result.HasError, result.Error?.Message);
        // R2 llega con el recordatorio de R1, que sigue sin nota de un turno anterior.
        var toolMessages = llm.Seen[^1].ToolMessages;
        Assert.Equal(2, toolMessages.Count);
        Assert.Equal("[R1] contenido largo 1", toolMessages[0]);
        Assert.StartsWith("[R2] contenido largo 2\n\nPendientes de nota de turnos anteriores: R1 (1)", toolMessages[1]);
        var warning = Assert.Single(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
        Assert.Contains("2 de 2 resultado(s) de herramientas sin nota", warning.Message);
        Assert.Contains("R1, R2", warning.Message);
    }

    [Fact]
    public async Task A_note_for_an_unknown_result_is_kept_and_reported_without_failing()
    {
        var llm = new ScriptedLlm(Calls(""), Answer("<nota r=\"R1\">ok</nota><nota r=\"R9\">inventada</nota>Fin."));
        var (context, reporter) = NewContext();

        var result = await RunAsync(NewStep<LlmStringStepResult>(llm, noteProtocol: true), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.True(NoteSessions.TryGet(context, out var session));
        Assert.Equal(["R1", "R9"], session.Notes.Select(n => n.Key));
        Assert.Contains(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning && e.Message.Contains("R9"));
    }

    // ───────────────────────────── (c) notas finales de una investigación ─────────────────────────────

    [Fact]
    public void Investigation_notes_are_the_session_notes_in_result_order_followed_by_the_final_answer()
    {
        var context = new PipelineContext();
        var session = NoteSessions.Begin(context, new ConversationContext(), "Investigation_Prueba");
        session.NextResultId();
        session.NextResultId();
        session.NextResultId();
        session.AddNote("R2", "B.java:4 guarda el pedido.");
        session.AddNote("R1", "A.java:10 valida el importe.");
        session.AddNote("R3", "Sin interés.");

        var notes = SubsystemInvestigationReportStep.WithSessionNotes(context, "Conclusión. [nota R3 guardada]");

        Assert.Equal("[R1]\nA.java:10 valida el importe.\n\n[R2]\nB.java:4 guarda el pedido.\n\nConclusión.", notes);
    }

    [Fact]
    public void Without_a_session_the_investigation_notes_are_the_answer_as_is()
    {
        Assert.Equal("Notas [nota R1 guardada]", SubsystemInvestigationReportStep.WithSessionNotes(new PipelineContext(), "Notas [nota R1 guardada]"));
    }

    // ───────────────────────────── (d) sin la opción ─────────────────────────────

    [Fact]
    public async Task Without_the_note_protocol_tools_do_not_tag_and_the_prompt_has_no_note_instructions()
    {
        var llm = new ScriptedLlm(Calls(""), Calls(""), Answer("Resumen."));
        var (context, reporter) = NewContext();

        var result = await RunAsync(NewStep<LlmStringStepResult>(llm, noteProtocol: false), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal(["contenido largo 1", "contenido largo 2"], llm.Seen[^1].ToolMessages);
        Assert.DoesNotContain("<nota", llm.Seen[0].System);
        Assert.False(NoteSessions.TryGet(context, out _));
        Assert.DoesNotContain(reporter.Entries, e => e.Level == AnalysisLogLevel.Warning);
    }

    [Theory]
    [InlineData(null, null, true, true)]
    [InlineData("false", "true", false, true)]
    [InlineData("true", "false", true, false)]
    [InlineData("quizá", "FALSE", true, false)]
    public void Options_are_read_from_their_section_with_both_on_by_default(string? investigations, string? auditors, bool expectedInvestigations, bool expectedAuditors)
    {
        var options = NoteProtocolOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:NoteProtocol:Investigations", investigations),
            ("CodebaseAnalysis:NoteProtocol:Auditors", auditors)));

        Assert.Equal(expectedInvestigations, options.Investigations);
        Assert.Equal(expectedAuditors, options.Auditors);
    }

    // ───────────────────────────── (e) sin sesión, la tool no etiqueta ─────────────────────────────

    [Fact]
    public async Task The_wrapped_tool_tags_only_while_its_context_has_an_active_session()
    {
        var tool = new NoteProtocolTool(new ReadTool());
        var chat = new PipelineContext();

        Assert.Equal("contenido largo 1", await tool.ExecuteAsync("{}", chat, "Chat", NullLogger.Instance));

        var context = new PipelineContext();
        var session = NoteSessions.Begin(context, new ConversationContext(), "Auditor");
        Assert.Equal("[R1] contenido largo 2", await tool.ExecuteAsync("{}", context, "Auditor", NullLogger.Instance));
        // El contexto de otro (una rama, el chat) no hereda la sesión.
        Assert.Equal("contenido largo 3", await tool.ExecuteAsync("{}", chat, "Chat", NullLogger.Instance));

        session.Deactivate();
        Assert.Equal("contenido largo 4", await tool.ExecuteAsync("{}", context, "Auditor", NullLogger.Instance));
        Assert.Equal(["R1"], session.EmittedIds);
    }

    [Fact]
    public async Task A_failing_tool_is_tagged_too_so_its_error_can_be_annotated()
    {
        var tool = new NoteProtocolTool(new ThrowingTool());
        var context = new PipelineContext();
        NoteSessions.Begin(context, new ConversationContext(), "Auditor");

        Assert.Equal("[R1] Error executing tool: sin grafo", await tool.ExecuteAsync("{}", context, "Auditor", NullLogger.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync("{}", new PipelineContext(), "Chat", NullLogger.Instance));
    }

    [Fact]
    public void The_wrapper_keeps_the_definition_and_the_read_only_flag_and_wraps_once()
    {
        var inner = new ReadTool("grep_search");
        var wrapped = NoteProtocolTool.Wrap(inner);

        Assert.Equal("grep_search", wrapped.Name);
        Assert.True(wrapped.IsReadOnly);
        Assert.Equal(inner.GetDefinition(), wrapped.GetDefinition());
        Assert.Same(wrapped, NoteProtocolTool.Wrap(wrapped));
    }

    // ───────────────────────────── (f) respuesta final con caché ─────────────────────────────

    [Fact]
    public async Task The_final_answer_attempt_keeps_the_tools_and_forbids_them_with_tool_choice_none()
    {
        var llm = new ScriptedLlm(Calls(""), Answer("Notas con lo consultado."));
        var (context, _) = NewContext();

        var result = await RunAsync(
            NewStep<LlmStringStepResult>(llm, noteProtocol: false, maxToolIterations: 1, finalAnswerPrompt: FinalAnswerPrompt), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal("Notas con lo consultado.", ((LlmStringStepResult)result).AssistantMessage);
        var (first, final) = (llm.Seen[0], llm.Seen[^1]);
        Assert.Equal(1, first.ToolCount);
        Assert.Null(first.ToolChoice);
        Assert.Equal(1, final.ToolCount);
        Assert.True(final.ForbidsTools);
        Assert.Equal("none", final.ToolChoice);
    }

    [Fact]
    public async Task With_the_note_protocol_the_final_answer_attempt_keeps_the_session_of_the_same_conversation()
    {
        var llm = new ScriptedLlm(Calls(""), Answer("<nota r=\"R1\">A.java:10 valida.</nota>Fin."));
        var (context, reporter) = NewContext();

        var result = await RunAsync(
            NewStep<LlmStringStepResult>(llm, noteProtocol: true, maxToolIterations: 1, finalAnswerPrompt: FinalAnswerPrompt), context);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.True(llm.Seen[^1].ForbidsTools);
        Assert.Equal(["[R1] contenido largo 1"], llm.Seen[^1].ToolMessages);
        Assert.True(NoteSessions.TryGet(context, out var session));
        Assert.Equal("A.java:10 valida.", Assert.Single(session.Notes).Value);
        Assert.DoesNotContain(reporter.Entries, e => e.Message.Contains("sin nota"));
    }

    [Fact]
    public void Forbids_tools_only_reads_a_none_tool_choice()
    {
        LlmRequest Request(string? toolChoice) => new()
        {
            Conversation = new ConversationContext(),
            Model = "m",
            Options = toolChoice is null ? new Newtonsoft.Json.Linq.JObject() : new Newtonsoft.Json.Linq.JObject { ["tool_choice"] = toolChoice }
        };

        Assert.False(FinalAnswerRequests.ForbidsTools(Request(null)));
        Assert.False(FinalAnswerRequests.ForbidsTools(Request("auto")));
        Assert.True(FinalAnswerRequests.ForbidsTools(Request("none")));
    }

    // ───────────────────────────── (g) marcadores fuera del informe ─────────────────────────────

    [Fact]
    public async Task Note_placeholders_do_not_reach_the_report()
    {
        var llm = new ScriptedLlm(
            Calls(""),
            Answer("<nota r=\"R1\">A.java:10.</nota>\n# Informe\n\n## Resumen\n\nTodo bien.\n\n<!-- analysis-meta: health=Green -->"));
        var (context, _) = NewContext();

        var result = await RunAsync(NewStep<SingleReportResult>(llm, noteProtocol: true), context);

        Assert.False(result.HasError, result.Error?.Message);
        var report = ((SingleReportResult)result).MarkdownContent;
        Assert.DoesNotContain("[nota", report);
        Assert.StartsWith("# Informe", report);
    }

    [Theory]
    [InlineData("Texto sin marcadores.", "Texto sin marcadores.")]
    [InlineData("[nota R1 guardada] [nota R2 guardada]\n# Informe", "# Informe")]
    [InlineData("Uno. [nota R12 guardada]\n\n\n\nDos.", "Uno.\n\nDos.")]
    [InlineData("Cita [nota] literal", "Cita [nota] literal")]
    public void Remove_placeholders(string text, string expected) =>
        Assert.Equal(expected, NoteTagHandler.RemovePlaceholders(text));
}
