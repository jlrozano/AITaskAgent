using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Tools.Abstractions;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Steps;
using Microsoft.Extensions.Logging;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="CustomStatelessTemplateLlmStep{TIn, TOut}"/> guardaba en campos la conversación y el modo de respuesta final
/// (auditoría, H-11), justo lo que <c>BaseLlmStep</c> prohíbe: con el mismo paso en dos ejecuciones a la vez, la segunda
/// arrancaba en el modo final de la primera, sin tools y sobre su conversación.
/// </summary>
public sealed class CustomStatelessTemplateLlmStepTests
{
    private const string FinalAnswerPrompt = "Redacta ya el informe con lo consultado.";
    private const string Report = "informe final";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    private sealed class ReadOnlyTool : ITool
    {
        public string Name => "graph_search";
        public string Description => "tool de prueba";
        public bool IsReadOnly => true;
        public ToolDefinition GetDefinition() => new() { Name = Name, Description = Description, ParametersJsonSchema = "{}" };
        public Task<string> ExecuteAsync(string argumentsJson, PipelineContext context, string stepName, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult("resultado");
    }

    /// <summary>
    /// Con tools pide una llamada a <c>graph_search</c>; sin tools responde el informe. La primera petición sin tools, el
    /// intento final de la primera ejecución, espera a que llegue cualquier otra petición: así la segunda ejecución arranca
    /// mientras la primera está en su modo de respuesta final.
    /// </summary>
    private sealed class InterleavingLlmService : ILlmService
    {
        private readonly TaskCompletionSource _firstFinalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _anotherRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private readonly List<(bool WithTools, string? LastUserMessage, ConversationContextRef Conversation)> _requests = [];
        private int _finals;

        public Task FirstFinalStarted => _firstFinalStarted.Task;

        public IReadOnlyList<(bool WithTools, string? LastUserMessage, ConversationContextRef Conversation)> Requests
        {
            get
            {
                lock (_lock)
                {
                    return [.. _requests];
                }
            }
        }

        public string Name => "interleaving";

        public async Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var withTools = request.Tools is { Count: > 0 };
            var lastUser = request.Conversation.History.Messages.LastOrDefault(m => m.Role == LlmConstants.MessageRoles.User)?.Content;
            bool firstFinal;
            lock (_lock)
            {
                _requests.Add((withTools, lastUser, new ConversationContextRef(request.Conversation)));
                firstFinal = !withTools && ++_finals == 1;
            }

            if (firstFinal)
            {
                _firstFinalStarted.TrySetResult();
                await _anotherRequest.Task.WaitAsync(Guard, cancellationToken);
            }
            else if (_firstFinalStarted.Task.IsCompleted)
            {
                // Solo una petición posterior: las anteriores son de la propia primera ejecución.
                _anotherRequest.TrySetResult();
            }

            return withTools
                ? LlmResult.Success([new LlmResponse
                {
                    Content = string.Empty,
                    FinishReason = FinishReason.ToolCalls,
                    ToolCalls = [new ToolCall { Id = $"call_{Guid.NewGuid():N}", Name = "graph_search", Arguments = "{}" }]
                }], model: request.Model)
                : LlmResult.Success([new LlmResponse { Content = Report, FinishReason = FinishReason.Stop }], model: request.Model);
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;

        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }

    /// <summary>La conversación de una petición por identidad, para comparar sin depender de la igualdad del tipo.</summary>
    private sealed record ConversationContextRef(object Instance)
    {
        public bool Same(ConversationContextRef other) => ReferenceEquals(Instance, other.Instance);
    }

    [Fact]
    public async Task The_same_step_run_twice_in_parallel_keeps_each_final_answer_attempt_in_its_own_run()
    {
        var llm = new InterleavingLlmService();
        var role = new LlmRole
        {
            Provider = new LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/" },
            Model = "fake-model",
            Inference = new LlmInferenceSettings { Temperature = 0.1f, MaxTokens = 1000 }
        };
        var step = new CustomStatelessTemplateLlmStep<AnalysisPipelineInput, LlmStringStepResult>(
            // Sin proveedor de plantillas, el prompt se usa tal cual.
            "Auditor_Prueba", role, templateProvider: null!, "Audita el repositorio.")
        {
            MaxToolIterations = 1,
            ToolLimitFinalAnswerPrompt = FinalAnswerPrompt
        };
        step.Tools = [new ReadOnlyTool()];

        Task<AITaskAgent.Core.Abstractions.IStepResult> Run() =>
            new Pipeline().ExecuteAsync("run", [step], new AnalysisPipelineInput(), new PipelineContext());

        var first = Run();
        // Si la primera termina sin llegar a su intento final, el test no puede intercalar nada: se dice por qué.
        if (await Task.WhenAny(first, llm.FirstFinalStarted).WaitAsync(Guard) == first)
            Assert.Fail($"La primera ejecución terminó sin intento final: {(await first).Error?.Message ?? "sin error"} "
                + $"({string.Join("; ", llm.Requests.Select(r => $"tools={r.WithTools} user={r.LastUserMessage}"))}; "
                + $"respuesta: {((LlmStringStepResult)await first).AssistantMessage})");
        var second = Run();
        var results = await Task.WhenAll(first, second).WaitAsync(Guard);

        Assert.All(results, r => Assert.False(r.HasError, r.Error?.Message));
        Assert.All(results, r => Assert.Equal(Report, ((LlmStringStepResult)r).AssistantMessage));

        // Cada ejecución: una petición con tools, el tope y un intento final sin tools sobre su propia conversación.
        var requests = llm.Requests;
        var withTools = requests.Where(r => r.WithTools).ToList();
        var finals = requests.Where(r => !r.WithTools).ToList();
        Assert.Equal(2, withTools.Count);
        Assert.Equal(2, finals.Count);
        Assert.All(withTools, r => Assert.Equal("Audita el repositorio.", r.LastUserMessage));
        Assert.All(finals, r => Assert.Equal(FinalAnswerPrompt, r.LastUserMessage));
        Assert.False(finals[0].Conversation.Same(finals[1].Conversation));
        Assert.All(finals, f => Assert.Single(withTools, t => t.Conversation.Same(f.Conversation)));
    }
}
