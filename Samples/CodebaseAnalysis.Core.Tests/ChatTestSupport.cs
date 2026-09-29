using AITaskAgent.Configuration;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="ILlmService"/> en streaming y sin red, con dos iteraciones por turno: si el último mensaje de la
/// conversación no es el resultado de una tool, emite unos deltas y pide <see cref="ToolName"/>; si lo es,
/// emite la respuesta final en deltas. Guarda los roles de la conversación de cada llamada.
/// </summary>
internal sealed class ScriptedStreamingLlmService(string toolName = "list_reports") : ILlmService
{
    public const string IntermediateText = "Voy a mirar los informes.";
    public static readonly string[] FinalChunks = ["El peor es ", "**05 Errores y bugs**", " (rojo)."];
    public static string FinalAnswer => string.Concat(FinalChunks);

    private readonly List<IReadOnlyList<(string Role, string? Content)>> _conversations = [];
    private int _toolCallCounter;

    public string ToolName { get; } = toolName;

    public string Name => "scripted-streaming";

    /// <summary>Si se fija, la iteración final se bloquea tras el primer delta hasta que se cancele.</summary>
    public bool HangOnFinal { get; init; }

    /// <summary>Espera antes de la respuesta final; por defecto ninguna.</summary>
    public TimeSpan FinalDelay { get; init; }

    /// <summary>Argumentos JSON de la tool que pide; por defecto <c>{}</c>.</summary>
    public string ToolArguments { get; init; } = "{}";

    /// <summary>Respuesta final en deltas; por defecto <see cref="FinalChunks"/>.</summary>
    public string[]? FinalAnswerChunks { get; init; }

    /// <summary>Se completa cuando la iteración final ha emitido su primer delta.</summary>
    public TaskCompletionSource FirstFinalDeltaSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<IReadOnlyList<(string Role, string? Content)>> Conversations
    {
        get
        {
            lock (_conversations)
            {
                return [.. _conversations];
            }
        }
    }

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Este servicio solo responde en streaming.");

    public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = request.Conversation.History.Messages;
        lock (_conversations)
        {
            _conversations.Add(messages.Select(m => (m.Role, m.Content)).ToList());
        }

        await Task.Yield();

        if (messages.Count == 0 || messages[^1].Role != LlmConstants.MessageRoles.Tool)
        {
            yield return new LlmStreamChunk { Delta = "Voy a mirar " };
            yield return new LlmStreamChunk { Delta = "los informes." };
            var id = $"call_{Interlocked.Increment(ref _toolCallCounter)}";
            yield return new LlmStreamChunk
            {
                Delta = string.Empty,
                ToolCallUpdates = [new ToolCallUpdate { Index = 0, ToolCallId = id, FunctionName = ToolName, FunctionArgumentsUpdate = ToolArguments[..1] }]
            };
            yield return new LlmStreamChunk
            {
                Delta = string.Empty,
                ToolCallUpdates = [new ToolCallUpdate { Index = 0, FunctionArgumentsUpdate = ToolArguments[1..] }]
            };
            yield return new LlmStreamChunk
            {
                Delta = string.Empty,
                IsComplete = true,
                FinishReason = FinishReason.ToolCalls,
                TokensUsed = 50,
                PromptTokens = 40,
                CompletionTokens = 10
            };
            yield break;
        }

        if (FinalDelay > TimeSpan.Zero)
            await Task.Delay(FinalDelay, cancellationToken);

        var final = FinalAnswerChunks ?? FinalChunks;
        for (var i = 0; i < final.Length; i++)
        {
            yield return new LlmStreamChunk { Delta = final[i] };
            if (i == 0)
            {
                FirstFinalDeltaSent.TrySetResult();
                if (HangOnFinal)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
            }
        }

        yield return new LlmStreamChunk
        {
            Delta = string.Empty,
            IsComplete = true,
            FinishReason = FinishReason.Stop,
            TokensUsed = 80,
            PromptTokens = 60,
            CompletionTokens = 20
        };
    }

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 1_000_000;
}

internal static class ChatTestHost
{
    /// <summary>
    /// Contenedor con el framework (el <c>IPipeline</c> y sus opciones) y sin red. Con <paramref name="pipeline"/>, ese es el
    /// <c>IPipeline</c> del contenedor.
    /// </summary>
    public static ServiceProvider BuildServices(AITaskAgent.Core.Execution.IPipeline? pipeline = null)
    {
        var services = new ServiceCollection();
        if (pipeline != null)
            services.AddSingleton(pipeline);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddAITaskAgent();
        var provider = services.BuildServiceProvider();
        provider.UseAITaskAgent();
        return provider;
    }

    public static LlmRole StreamingRole(ILlmService service, string? systemPrompt = "chat_system") => new()
    {
        Provider = new LlmProvider { Service = service, BaseUrl = "http://fake.invalid/" },
        Model = "fake-model",
        SystemPrompt = systemPrompt,
        Inference = new LlmInferenceSettings { UseStreaming = true, Temperature = 0.2f, MaxTokens = 1000 }
    };
}

/// <summary><see cref="ILlmStepResult"/> con cifras fijas, para los eventos <c>step.completed</c>.</summary>
internal sealed class FakeLlmStepResult() : StepResult(new EmptyStep("Fake")), ILlmStepResult
{
    public string AssistantMessage { get; set; } = "contenido que no debe serializarse";
    public decimal? CostUsd { get; init; }
    public FinishReason? FinishReason { get; init; }
    public string? Model { get; init; }
    public int? TokensUsed { get; init; }
    public string GetAssistantMessageOrDefault(string? defaultMessage = null) => AssistantMessage;
}
