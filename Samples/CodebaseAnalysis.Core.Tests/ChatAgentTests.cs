using AITaskAgent.LLM.Tools.Implementation;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Tools;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Un turno de chat completo sin red: el servicio falso hace streaming, pide <c>list_reports</c> en la primera
/// iteración y responde en la segunda. El registro de tools es estático, así que van en la colección sin
/// paralelismo.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ChatAgentTests
{
    private static (ChatWorkspace Workspace, ChatSessionStore Store) Setup(TempDirectory dir)
    {
        var ws = new ChatWorkspace(dir.Path, Path.Combine(dir.Path, ".codebase", "codebase.db"), Path.Combine(dir.Path, "reports"));
        Directory.CreateDirectory(ws.ReportsPath);
        File.WriteAllText(Path.Combine(ws.ReportsPath, "05_errores_y_bugs.md"), "# Errores\n<!-- analysis-meta: health=Red; critical=6 -->");
        AvailableTools.Instance.Register(new ListReportsTool());
        AvailableTools.Instance.Register(new ReadReportTool());
        return (ws, new ChatSessionStore(Path.Combine(dir.Path, ".codebase", "chat")));
    }

    [Fact]
    public async Task Streams_deltas_and_tools_persists_the_turn_and_rebuilds_the_history()
    {
        using var dir = new TempDirectory();
        var (ws, store) = Setup(dir);
        await using var services = ChatTestHost.BuildServices();
        var llm = new ScriptedStreamingLlmService();
        var agent = new ChatAgent(services, ChatTestHost.StreamingRole(llm), AnalysisPipelineFactory.CreateDefaultTemplateProvider(), new ChatOptions());
        var session = store.Create(ws.RepositoryPath);

        var events = new List<ChatStreamEvent>();
        var turn = await agent.RunTurnAsync(store, session, "¿Qué informes hay y cuál es el peor?", ws, events.Add, CancellationToken.None);

        Assert.Equal(
            ["turn.started", "delta", "delta", "tool.calls", "tool.started", "tool.completed", "delta", "delta", "delta", "turn.completed"],
            events.Select(e => e.Type));
        Assert.Equal("t-0001", events[0].Fields["turnId"]);
        Assert.Equal("Voy a mirar ", events[1].Fields["text"]);
        Assert.Equal("list_reports", Assert.Single((IEnumerable<Dictionary<string, object?>>)events[3].Fields["calls"]!)["name"]);
        Assert.Equal(true, events[5].Fields["success"]);
        Assert.Same(turn, events[^1].Fields["turn"]);

        Assert.Equal(ChatTurnStatus.Completed, turn.Status);
        Assert.Equal(ScriptedStreamingLlmService.FinalAnswer, turn.Answer);
        Assert.Equal("Stop", turn.FinishReason);
        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal("list_reports", call.Name);
        Assert.Equal("{}", call.Arguments);
        Assert.True(call.Success);

        var persisted = store.TryGet(session.Id)!;
        Assert.Equal("¿Qué informes hay y cuál es el peor?", persisted.Title);
        var persistedTurn = Assert.Single(persisted.Turns);
        Assert.Equal(ScriptedStreamingLlmService.FinalAnswer, persistedTurn.Answer);
        Assert.Equal("list_reports", Assert.Single(persistedTurn.ToolCalls).Name);

        // La herramienta leyó la carpeta de informes del contexto: el modelo recibió el catálogo.
        var secondIteration = llm.Conversations[1];
        Assert.Equal("tool", secondIteration[^1].Role);
        Assert.Contains("05_errores_y_bugs.md", secondIteration[^1].Content);

        // Segundo turno: el contexto es el par del primero y la pregunta nueva.
        await agent.RunTurnAsync(store, persisted, "¿Y el segundo peor?", ws, _ => { }, CancellationToken.None);

        var firstCallOfSecondTurn = llm.Conversations[2];
        Assert.Equal(["user", "assistant", "user"], firstCallOfSecondTurn.Select(m => m.Role));
        Assert.Equal("¿Qué informes hay y cuál es el peor?", firstCallOfSecondTurn[0].Content);
        Assert.Equal(ScriptedStreamingLlmService.FinalAnswer, firstCallOfSecondTurn[1].Content);
        Assert.Equal("¿Y el segundo peor?", firstCallOfSecondTurn[2].Content);
        Assert.Equal(["t-0001", "t-0002"], store.TryGet(session.Id)!.Turns.Select(t => t.Id));
    }

    [Fact]
    public async Task The_turn_is_audited_against_what_its_tools_read_before_the_conversation_is_compacted()
    {
        using var dir = new TempDirectory();
        var (ws, store) = Setup(dir);
        Directory.CreateDirectory(Path.Combine(dir.Path, "src"));
        File.WriteAllLines(Path.Combine(dir.Path, "src", "Orders.cs"), Enumerable.Range(1, 30).Select(i => $"// línea {i}"));
        var previousRoot = AITaskAgent.FileTools.FileToolsConfiguration.RootDirectory;
        AITaskAgent.FileTools.FileToolsConfiguration.RootDirectory = dir.Path;
        try
        {
            AvailableTools.Instance.Register(new AITaskAgent.FileTools.Tools.ViewFileTool());
            await using var services = ChatTestHost.BuildServices();
            var llm = new ScriptedStreamingLlmService("view_file")
            {
                ToolArguments = """{"AbsolutePath":"src/Orders.cs","StartLine":5,"EndLine":12}""",
                FinalAnswerChunks = ["[V] Valida en `src/Orders.cs:6-9`. ", "[V] Y reintenta en `src/Orders.cs:20`. ", "[I] Parece idempotente."]
            };
            var agent = new ChatAgent(services, ChatTestHost.StreamingRole(llm), AnalysisPipelineFactory.CreateDefaultTemplateProvider(), new ChatOptions());
            var session = store.Create(ws.RepositoryPath);

            var turn = await agent.RunTurnAsync(store, session, "¿Dónde valida?", ws, _ => { }, CancellationToken.None);

            Assert.Equal(ChatTurnStatus.Completed, turn.Status);
            Assert.NotNull(turn.Audit);
            Assert.Equal([ChatClaimStatus.Verified, ChatClaimStatus.Unchecked, ChatClaimStatus.Inferred], turn.Audit!.Claims.Select(c => c.Status));
            Assert.Equal([ChatReferenceStatus.Ok, ChatReferenceStatus.NotRead], turn.Audit.References.Select(r => r.Status));
            // Se persiste con el turno.
            Assert.Equal(3, store.TryGet(session.Id)!.Turns.Single().Audit!.Claims.Count);
        }
        finally
        {
            AITaskAgent.FileTools.FileToolsConfiguration.RootDirectory = previousRoot;
        }
    }

    [Fact]
    public async Task A_cancelled_turn_is_persisted_as_cancelled_and_left_out_of_the_history()
    {
        using var dir = new TempDirectory();
        var (ws, store) = Setup(dir);
        await using var services = ChatTestHost.BuildServices();
        var llm = new ScriptedStreamingLlmService { HangOnFinal = true };
        var agent = new ChatAgent(services, ChatTestHost.StreamingRole(llm), AnalysisPipelineFactory.CreateDefaultTemplateProvider(), new ChatOptions());
        var session = store.Create(ws.RepositoryPath);
        using var cts = new CancellationTokenSource();

        var events = new List<ChatStreamEvent>();
        var running = agent.RunTurnAsync(store, session, "pregunta", ws, e => { lock (events) events.Add(e); }, cts.Token);
        await llm.FirstFinalDeltaSent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();
        var turn = await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ChatTurnStatus.Cancelled, turn.Status);
        Assert.Equal("turn.failed", events[^1].Type);
        Assert.Equal("cancelled", events[^1].Fields["status"]);
        var persisted = Assert.Single(store.TryGet(session.Id)!.Turns);
        Assert.Equal(ChatTurnStatus.Cancelled, persisted.Status);
        // El texto que el usuario ya vio (el segmento abierto tras la tool) sobrevive a la recarga.
        Assert.Equal(ScriptedStreamingLlmService.FinalChunks[0], persisted.Answer);
        Assert.Empty(ChatSessionStore.BuildHistory(store.TryGet(session.Id)!, 20));
    }

    [Fact]
    public async Task The_framework_default_step_timeout_does_not_cut_the_turn()
    {
        // H-4: sin Timeout propio, el paso del turno heredaba DefaultStepTimeout (1 min) y el chat se cortaba a los 60 s
        // aunque TurnTimeoutMinutes fuera 5. Aquí el motor tiene un DefaultStepTimeout de 200 ms y el turno dura más.
        using var dir = new TempDirectory();
        var (ws, store) = Setup(dir);
        await using var services = ChatTestHost.BuildServices(new AITaskAgent.Core.Execution.Pipeline(defaultStepTimeout: TimeSpan.FromMilliseconds(200)));
        var llm = new ScriptedStreamingLlmService { FinalDelay = TimeSpan.FromMilliseconds(600) };
        var agent = new ChatAgent(services, ChatTestHost.StreamingRole(llm), AnalysisPipelineFactory.CreateDefaultTemplateProvider(), new ChatOptions());

        var turn = await agent.RunTurnAsync(store, store.Create(ws.RepositoryPath), "pregunta", ws, _ => { }, CancellationToken.None);

        Assert.Equal(ChatTurnStatus.Completed, turn.Status);
        Assert.Equal(ScriptedStreamingLlmService.FinalAnswer, turn.Answer);
    }

    [Fact]
    public async Task A_turn_over_TurnTimeout_fails_with_the_limit_and_the_key_that_controls_it()
    {
        using var dir = new TempDirectory();
        var (ws, store) = Setup(dir);
        await using var services = ChatTestHost.BuildServices();
        var llm = new ScriptedStreamingLlmService { HangOnFinal = true };
        var limit = TimeSpan.FromMilliseconds(500);
        var agent = new ChatAgent(services, ChatTestHost.StreamingRole(llm), AnalysisPipelineFactory.CreateDefaultTemplateProvider(),
            new ChatOptions { TurnTimeout = limit });
        var session = store.Create(ws.RepositoryPath);

        var events = new List<ChatStreamEvent>();
        var turn = await agent.RunTurnAsync(store, session, "pregunta", ws, e => { lock (events) events.Add(e); }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ChatTurnStatus.Failed, turn.Status);
        Assert.Equal(ChatAgent.TurnTimeoutMessage(limit), turn.Error);
        Assert.Equal("El turno superó el límite de 5 min (CodebaseAnalysis:Chat:TurnTimeoutMinutes); súbelo o pon 0 para quitarlo.",
            ChatAgent.TurnTimeoutMessage(TimeSpan.FromMinutes(5)));
        Assert.Equal("turn.failed", events[^1].Type);
        Assert.Equal(ChatAgent.TurnTimeoutMessage(limit), events[^1].Fields["error"]);
        // Lo que el usuario ya vio se conserva.
        Assert.Equal(ScriptedStreamingLlmService.FinalChunks[0], Assert.Single(store.TryGet(session.Id)!.Turns).Answer);
    }

    [Theory]
    [InlineData(null, 5.0)]
    [InlineData("12", 12.0)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    public void TurnTimeoutMinutes_of_zero_or_less_removes_the_limit(string? minutes, double? expected)
    {
        var options = ChatOptions.FromConfiguration(minutes == null
            ? TestConfig.FromPairs()
            : TestConfig.FromPairs(("CodebaseAnalysis:Chat:TurnTimeoutMinutes", minutes)));

        Assert.Equal(expected is { } m ? TimeSpan.FromMinutes(m) : Timeout.InfiniteTimeSpan, options.TurnTimeout);
    }

    [Fact]
    public void EnsureRole_turns_on_streaming_only_for_a_role_created_by_default()
    {
        var llm = new ScriptedStreamingLlmService();
        var baseRole = new AITaskAgent.LLM.Configuration.LlmRole
        {
            Provider = new AITaskAgent.LLM.Configuration.LlmProvider { Service = llm },
            Model = "m",
            Inference = new AITaskAgent.LLM.Configuration.LlmInferenceSettings { MaxTokens = 10 }
        };

        var roles = new Dictionary<string, AITaskAgent.LLM.Configuration.LlmRole> { [ChatAgent.RoleName] = baseRole };
        var created = ChatAgent.EnsureRole(roles, TestConfig.FromPairs());
        Assert.True(created.Inference!.UseStreaming);
        Assert.Equal(10, created.Inference.MaxTokens);

        var configured = new Dictionary<string, AITaskAgent.LLM.Configuration.LlmRole> { [ChatAgent.RoleName] = baseRole };
        var kept = ChatAgent.EnsureRole(configured, TestConfig.FromPairs(($"LlmConfiguration:Roles:{ChatAgent.RoleName}:Model", "m")));
        Assert.Same(baseRole, kept);
    }
}
