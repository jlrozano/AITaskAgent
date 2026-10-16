using AITaskAgent.Core.Abstractions;
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
using CodebaseAnalysis.Core.Steps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Anti-bucle (plan 2026-10-16, paquete A): el detector de repeticiones del framework (ventana de 10 iteraciones, 3 apariciones
/// de una llamada de solo lectura; las que modifican cortan a la segunda repetición consecutiva) y los pendientes del protocolo
/// de notas (recordatorio y retención de resultados sin anotar de turnos anteriores).
/// </summary>
public sealed class ToolLoopGuardTests
{
    // ───────────────────────────── Dobles ─────────────────────────────

    private sealed class FakeTool(bool readOnly, string name = "view_file") : ITool
    {
        public int Executions;
        public string Name => name;
        public string Description => "tool de prueba";
        public bool IsReadOnly => readOnly;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };

        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult($"contenido {Interlocked.Increment(ref Executions)}");
    }

    /// <summary>Pide una llamada por petición con los argumentos del guion y, agotado, responde con texto.</summary>
    private sealed class ScriptedCalls(params string[] keys) : ILlmService
    {
        public int Requests;
        public string Name => "scripted";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var index = Requests++;
            var response = index < keys.Length
                ? new LlmResponse
                {
                    Content = "",
                    FinishReason = FinishReason.ToolCalls,
                    ToolCalls = [new ToolCall { Id = $"call_{index}", Name = "view_file", Arguments = $"{{\"k\":\"{keys[index]}\"}}" }]
                }
                : new LlmResponse { Content = "respuesta final", FinishReason = FinishReason.Stop };
            return Task.FromResult(LlmResult.Success([response], tokensUsed: 10, promptTokens: 8, completionTokens: 2, model: request.Model));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private static async Task<(string? Content, int Requests, int Executions)> RunAsync(bool readOnly, params string[] keys)
    {
        var llm = new ScriptedCalls(keys);
        var tool = new FakeTool(readOnly);
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/" },
            Model = "fake-model",
            Inference = new LlmInferenceSettings { Temperature = 0.1f, MaxTokens = 1000 }
        };
        var step = new CustomStatelessTemplateLlmStep<AnalysisPipelineInput, LlmStringStepResult>("Guard_Prueba", role, templateProvider: null!, "Investiga.")
        {
            MaxToolIterations = 50,
            Tools = [tool]
        };

        var result = await new Pipeline().ExecuteAsync("run", [step], new AnalysisPipelineInput(), new PipelineContext()).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(result.HasError, result.Error?.Message);
        return ((result as LlmStringStepResult)?.Content, llm.Requests, tool.Executions);
    }

    // ───────────────────────────── Detector del framework ─────────────────────────────

    [Fact]
    public async Task A_two_call_cycle_is_cut_when_a_read_only_call_shows_up_a_third_time()
    {
        var (content, requests, executions) = await RunAsync(true, "A", "B", "A", "B", "A", "B", "A");

        Assert.Equal(5, requests);
        Assert.Equal(4, executions);
        Assert.Contains("Execution stopped", content);
        Assert.Contains("view_file", content);
    }

    [Fact]
    public async Task A_three_call_cycle_is_cut_on_the_third_round()
    {
        var (content, requests, _) = await RunAsync(true, "A", "B", "C", "A", "B", "C", "A", "B", "C");

        Assert.Equal(7, requests);
        Assert.Contains("Execution stopped", content);
    }

    [Fact]
    public async Task Legitimate_exploration_with_at_most_two_repeats_is_not_cut()
    {
        var (content, requests, executions) = await RunAsync(true, "A", "B", "A", "B", "C", "D", "E", "F", "G", "H");

        Assert.Equal("respuesta final", content);
        Assert.Equal(11, requests);
        Assert.Equal(10, executions);
    }

    // Los dos bordes de la ventana de 10 (la iteración 12 ve de la 3 a la 12): uno solo no distingue una ventana de 10 de una de 11
    // (o de 9).

    [Fact]
    public async Task A_repeat_just_outside_the_ten_iteration_window_does_not_count()
    {
        // A en las iteraciones 2, 11 y 12: en la 12.ª la 2.ª ya salió de la ventana (con 11 cortaría).
        var (content, requests, _) = await RunAsync(true, "b1", "A", "b3", "b4", "b5", "b6", "b7", "b8", "b9", "b10", "A", "A");

        Assert.Equal("respuesta final", content);
        Assert.Equal(13, requests);
    }

    [Fact]
    public async Task A_repeat_at_the_oldest_iteration_of_the_window_counts()
    {
        // A en las iteraciones 3, 11 y 12: la 3.ª es la más antigua de la ventana de la 12.ª (con 9 no cortaría).
        var (content, requests, _) = await RunAsync(true, "b1", "b2", "A", "b4", "b5", "b6", "b7", "b8", "b9", "b10", "A", "A");

        Assert.Contains("Execution stopped", content);
        Assert.Equal(12, requests);
    }

    [Fact]
    public async Task A_mutating_call_is_cut_on_its_second_consecutive_repetition()
    {
        var (content, requests, executions) = await RunAsync(false, "A", "A", "A", "A");

        Assert.Equal(3, requests);
        Assert.Equal(2, executions);
        Assert.Contains("Execution stopped", content);
    }

    [Fact]
    public async Task A_mutating_call_repeated_with_something_in_between_is_not_cut()
    {
        var (content, _, _) = await RunAsync(false, "A", "A", "B", "A", "A");

        Assert.Equal("respuesta final", content);
    }

    // ───────────────────────────── Pendientes de notas ─────────────────────────────

    private static (NoteProtocolTool Tool, FakeTool Inner, NoteSession Session, ConversationContext Conversation, PipelineContext Context) NewSession(NoteProtocolOptions? options = null)
    {
        var inner = new FakeTool(true);
        var conversation = new ConversationContext();
        var context = new PipelineContext();
        var session = NoteSessions.Begin(context, conversation, "Auditor", options);
        return (new NoteProtocolTool(inner), inner, session, conversation, context);
    }

    private static void Turn(ConversationContext conversation, params string[] toolContents)
    {
        conversation.AddAssistantMessageWithToolCalls(toolContents.Select((_, i) => new ToolCall { Id = $"t{i}", Name = "view_file", Arguments = "{}" }), "");
        for (var i = 0; i < toolContents.Length; i++)
            conversation.AddToolMessage($"t{i}", toolContents[i]);
    }

    private static Task<string> CallAsync((NoteProtocolTool Tool, FakeTool Inner, NoteSession Session, ConversationContext Conversation, PipelineContext Context) s) =>
        s.Tool.ExecuteAsync("{}", s.Context, "Auditor", NullLogger.Instance);

    [Fact]
    public async Task No_reminder_when_the_only_unannotated_results_belong_to_the_current_turn()
    {
        var s = NewSession();
        // Turno anterior anotado; en el actual, una llamada hermana ya devolvió R2 (sin anotar) pero es del mismo turno.
        Turn(s.Conversation, "[R1 leído; tu nota:] algo");
        s.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = "x", Name = "view_file", Arguments = "{}" }, new ToolCall { Id = "y", Name = "view_file", Arguments = "{}" }], "");
        s.Conversation.AddToolMessage("x", "[R2] hermana");

        var result = await CallAsync(s);

        Assert.Equal("[R1] contenido 1", result);
    }

    [Fact]
    public async Task The_reminder_lists_unannotated_results_from_earlier_turns_as_ranges()
    {
        var s = NewSession();
        Turn(s.Conversation, "[R1] a", "[R2] b", "[R3 leído; tu nota:] c", "[R4] d");
        Turn(s.Conversation, "[R5] e"); // turno actual: el assistant con la llamada actual es el último

        var result = await CallAsync(s);

        Assert.StartsWith("[R1] contenido 1", result);
        Assert.Contains("Pendientes de nota de turnos anteriores: R1-R2, R4 (3)", result);
    }

    [Fact]
    public async Task Eleven_pending_results_get_the_reminder_and_twelve_withhold_the_data()
    {
        var eleven = NewSession();
        Turn(eleven.Conversation, [.. Enumerable.Range(1, 11).Select(n => $"[R{n}] x")]);
        eleven.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = "z", Name = "view_file", Arguments = "{}" }], "");
        var reminded = await CallAsync(eleven);
        Assert.Contains("contenido 1", reminded);
        Assert.Contains("R1-R11 (11)", reminded);
        Assert.Equal(1, eleven.Inner.Executions);

        var twelve = NewSession();
        Turn(twelve.Conversation, [.. Enumerable.Range(1, 12).Select(n => $"[R{n}] x")]);
        twelve.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = "z", Name = "view_file", Arguments = "{}" }], "");
        var withheld = await CallAsync(twelve);
        Assert.StartsWith("[Retenido]", withheld);
        Assert.Contains("R1-R12", withheld);
        Assert.DoesNotContain("contenido", withheld);
        Assert.Equal(0, twelve.Inner.Executions);
        Assert.Empty(twelve.Session.EmittedIds);
    }

    [Fact]
    public async Task Annotating_the_pending_results_clears_the_reminder_and_the_withholding()
    {
        var s = NewSession();
        Turn(s.Conversation, [.. Enumerable.Range(1, 12).Select(n => $"[R{n}] x")]);
        s.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = "z", Name = "view_file", Arguments = "{}" }], "");
        Assert.StartsWith("[Retenido]", await CallAsync(s));

        // El modelo anota los doce: la etiqueta sustituye cada resultado por su nota.
        var messages = s.Conversation.History.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Content is { } text && text.StartsWith("[R", StringComparison.Ordinal))
            {
                var id = text[1..text.IndexOf(']')];
                s.Conversation.ReplaceMessage(i, messages[i] with { Content = NoteTagHandler.ReplacedContent(id, "nota") });
            }
        }

        var result = await CallAsync(s);

        Assert.Equal("[R1] contenido 1", result);
    }

    [Fact]
    public async Task The_thresholds_come_from_configuration_and_zero_turns_them_off()
    {
        var options = NoteProtocolOptions.FromConfiguration(TestConfig.FromPairs(
            ("CodebaseAnalysis:NoteProtocol:ReminderFromPending", "0"),
            ("CodebaseAnalysis:NoteProtocol:WithholdFromPending", "2")));
        Assert.Equal(0, options.ReminderFromPending);
        Assert.Equal(2, options.WithholdFromPending);

        var defaults = NoteProtocolOptions.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:NoteProtocol:WithholdFromPending", "muchos")));
        Assert.Equal(1, defaults.ReminderFromPending);
        Assert.Equal(12, defaults.WithholdFromPending);

        var s = NewSession(options);
        Turn(s.Conversation, "[R1] a");
        s.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = "z", Name = "view_file", Arguments = "{}" }], "");
        Assert.Equal("[R1] contenido 1", await CallAsync(s)); // un pendiente: ni recordatorio (0) ni retención (2)

        Turn(s.Conversation, "[R2] b");
        s.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = "w", Name = "view_file", Arguments = "{}" }], "");
        Assert.StartsWith("[Retenido]", await CallAsync(s)); // dos pendientes (R1 y R2 siguen crudos en la conversación manual)
    }
    // ───────────────────────────── Corte con respuesta final (P1-3, P2-6) ─────────────────────────────

    private const string FinalAnswerPrompt = "Entrega ya lo que tengas.";

    /// <summary>Guion por número de petición; la petición que prohíbe tools (respuesta final) recibe el informe.</summary>
    private sealed class FinalAnswerLlm(Func<int, LlmResponse> turn) : ILlmService
    {
        public readonly List<bool> ForbidsTools = [];
        public string Name => "scripted";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var forbids = FinalAnswerRequests.ForbidsTools(request);
            var index = ForbidsTools.Count;
            ForbidsTools.Add(forbids);
            var response = forbids ? new LlmResponse { Content = "# Informe\n\nRedactado con lo consultado.", FinishReason = FinishReason.Stop } : turn(index);
            return Task.FromResult(LlmResult.Success([response], tokensUsed: 10, promptTokens: 8, completionTokens: 2, model: request.Model));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    private static LlmResponse CallWithText(int index, string args, string content) => new()
    {
        Content = content,
        FinishReason = FinishReason.ToolCalls,
        ToolCalls = [new ToolCall { Id = $"call_{index}", Name = "view_file", Arguments = $"{{\"k\":\"{args}\"}}" }]
    };

    private static async Task<(IStepResult Result, FakeTool Inner)> RunNotesAsync(FinalAnswerLlm llm, string? finalAnswerPrompt, NoteProtocolOptions? options = null)
    {
        var inner = new FakeTool(true);
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/" },
            Model = "fake-model",
            Inference = new LlmInferenceSettings { Temperature = 0.1f, MaxTokens = 1000 }
        };
        var step = new CustomStatelessTemplateLlmStep<AnalysisPipelineInput, LlmStringStepResult>("Guard_Notas", role, templateProvider: null!, "Investiga.")
        {
            MaxToolIterations = 50,
            ToolLimitFinalAnswerPrompt = finalAnswerPrompt,
            TagHandlers = [NoteTagHandler.Instance],
            NoteOptions = options
        };
        step.Tools = [new NoteProtocolTool(inner)];

        var result = await new Pipeline().ExecuteAsync("run", [step], new AnalysisPipelineInput(), new PipelineContext()).WaitAsync(TimeSpan.FromSeconds(20));
        return (result, inner);
    }

    // Un turno que ejecuta una etiqueta (p. ej. una nota) es progreso: releer tras anotar es el comportamiento diseñado.

    private static async Task<(string? Content, int Requests)> RunWithNotesAsync(string[] keys, Func<int, bool> notes)
    {
        var llm = new FinalAnswerLlm(i => i < keys.Length
            ? CallWithText(i, keys[i], notes(i) ? $"<nota r=\"R{i}\">anotado</nota>" : "")
            : new LlmResponse { Content = "respuesta final", FinishReason = FinishReason.Stop });
        var (result, _) = await RunNotesAsync(llm, finalAnswerPrompt: null);
        Assert.False(result.HasError, result.Error?.Message);
        return (Assert.IsType<LlmStringStepResult>(result).Content, llm.ForbidsTools.Count);
    }

    [Fact]
    public async Task The_cycle_without_tags_is_still_cut_through_the_notes_pipeline()
    {
        var (content, requests) = await RunWithNotesAsync(["A", "B", "A", "B", "A", "B"], _ => false);

        Assert.Contains("Execution stopped", content);
        Assert.Equal(5, requests);
    }

    [Fact]
    public async Task The_same_cycle_with_a_tag_executed_every_turn_is_not_cut()
    {
        var (content, requests) = await RunWithNotesAsync(["A", "B", "A", "B", "A", "B", "A", "B"], i => i >= 1);

        Assert.Equal("respuesta final", content);
        Assert.Equal(9, requests);
    }

    [Fact]
    public async Task Tags_only_in_the_first_turns_do_not_excuse_a_later_cycle()
    {
        var (content, requests) = await RunWithNotesAsync(["x", "y", "A", "B", "A", "B", "A", "B"], i => i is 1 or 2);

        Assert.Contains("Execution stopped", content);
        Assert.Equal(7, requests);
    }

    [Fact]
    public async Task Rereading_the_same_files_after_annotating_each_turn_is_not_cut()
    {
        var (content, requests) = await RunWithNotesAsync(["F1", "F2", "F1", "F3", "F1", "F2", "F1", "F3"], i => i >= 1);

        Assert.Equal("respuesta final", content);
        Assert.Equal(9, requests);
    }

    [Fact]
    public async Task A_cycle_cut_on_a_turn_with_notes_ends_with_a_final_turn_without_tools()
    {
        // La misma lectura repetida: anotar cuenta como progreso, así que el corte llega a la 3.ª repetición desde el último turno con nota.
        var llm = new FinalAnswerLlm(i => CallWithText(i, "A", i == 1 ? "<nota r=\"R1\">sin interés</nota>" : ""));

        var (result, inner) = await RunNotesAsync(llm, FinalAnswerPrompt);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal([false, false, false, false, true], llm.ForbidsTools);
        Assert.Equal(3, inner.Executions);
        var content = Assert.IsType<LlmStringStepResult>(result).Content;
        Assert.Contains("Redactado con lo consultado.", content);
        Assert.DoesNotContain("nota R", content);
    }

    [Fact]
    public async Task Without_a_final_answer_prompt_the_cut_turn_stays_the_answer()
    {
        // Lo que ve cualquier otro consumidor del framework (el chat): una respuesta final, sin otra llamada al modelo.
        var llm = new FinalAnswerLlm(i => CallWithText(i, "A", "Miro A otra vez."));

        var (result, _) = await RunNotesAsync(llm, finalAnswerPrompt: null);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal([false, false, false], llm.ForbidsTools);
        var final = Assert.IsType<LlmStringStepResult>(result);
        Assert.Equal("Miro A otra vez.", final.Content);
        Assert.Contains("Execution stopped", final.ToolLoopStopMessage);
    }

    [Fact]
    public async Task Repeated_withholding_ends_the_step_with_a_final_turn_without_tools()
    {
        // Nunca anota y varía los argumentos: el detector no lo ve. R1 se ejecuta; las dos peticiones siguientes se retienen y la
        // segunda retención seguida corta el bucle.
        var llm = new FinalAnswerLlm(i => CallWithText(i, $"k{i}", ""));
        var options = new NoteProtocolOptions { ReminderFromPending = 0, WithholdFromPending = 1, StopAfterWithheldTurns = 2 };

        var (result, inner) = await RunNotesAsync(llm, FinalAnswerPrompt, options);

        Assert.False(result.HasError, result.Error?.Message);
        Assert.Equal([false, false, false, true], llm.ForbidsTools);
        Assert.Equal(1, inner.Executions);
        Assert.Contains("Redactado con lo consultado.", Assert.IsType<LlmStringStepResult>(result).Content);
    }

    [Fact]
    public async Task Withheld_turns_count_once_per_turn_reset_on_execution_and_zero_disables_the_cut()
    {
        var s = NewSession(new NoteProtocolOptions { ReminderFromPending = 0, WithholdFromPending = 1, StopAfterWithheldTurns = 2 });
        Turn(s.Conversation, "[R1] a");
        s.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = "x", Name = "view_file", Arguments = "{}" }, new ToolCall { Id = "y", Name = "view_file", Arguments = "{}" }], "");

        // Dos llamadas hermanas del mismo turno: un turno retenido, no dos. El texto pide anotar, no repetir.
        var first = await CallAsync(s);
        Assert.StartsWith("[Retenido]", first);
        Assert.DoesNotContain("repite", first);
        Assert.StartsWith("[Retenido]", await CallAsync(s));

        // Segundo turno seguido con retención: corta.
        s.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = "z", Name = "view_file", Arguments = "{}" }], "");
        var stop = await Assert.ThrowsAsync<ToolLoopStopRequestedException>(() => CallAsync(s));
        Assert.StartsWith("[Retenido]", stop.Message);

        var off = NewSession(new NoteProtocolOptions { ReminderFromPending = 0, WithholdFromPending = 1, StopAfterWithheldTurns = 0 });
        Turn(off.Conversation, "[R1] a");
        for (var turn = 0; turn < 6; turn++)
        {
            off.Conversation.AddAssistantMessageWithToolCalls([new ToolCall { Id = $"w{turn}", Name = "view_file", Arguments = "{}" }], "");
            Assert.StartsWith("[Retenido]", await CallAsync(off));
        }

        Assert.Equal(5, new NoteProtocolOptions().StopAfterWithheldTurns);
        Assert.Equal(3, NoteProtocolOptions.FromConfiguration(TestConfig.FromPairs(("CodebaseAnalysis:NoteProtocol:StopAfterWithheldTurns", "3"))).StopAfterWithheldTurns);
    }
}
