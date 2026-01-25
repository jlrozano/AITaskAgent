using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Observability;
using AITaskAgent.Support.Template;
using PipelineVisualizer.Middleware;
using PipelineVisualizer.Models;
using System.ComponentModel;

namespace PipelineVisualizer.Services;

/// <summary>
/// Orchestrates pipeline execution for the visualizer.
/// Wraps the StoryMachine pipeline with context broadcast middleware.
/// </summary>
public sealed class PipelineExecutor
{
    private readonly ITemplateProvider _templateProvider;
    private readonly ILlmService _llmService;
    private readonly ILlmProviderResolver _profileResolver;
    private readonly IEventChannel _eventChannel;
    private readonly ILogger<PipelineExecutor> _logger;
    private readonly ContextStore _contextStore;
    private string? _lastStory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineExecutor"/> class.
    /// </summary>
    public PipelineExecutor(
        ILlmService llmService,
        ILlmProviderResolver profileResolver,
        IEventChannel eventChannel,
        ILogger<PipelineExecutor> logger,
        IConfiguration configuration,
        ContextStore contextStore)
    {
        _llmService = llmService;
        _profileResolver = profileResolver;
        _eventChannel = eventChannel;
        _logger = logger;
        _contextStore = contextStore;

        var templatesPath = configuration["AITaskAgent:TemplatesPath"] ?? "templates";
        // Ensure we look in the right place relative to execution
        var absolutePath = Path.IsPathRooted(templatesPath)
            ? templatesPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, templatesPath);

        _templateProvider = new FileTemplateProvider(
            folderPath: absolutePath,
            enableCache: true,
            ttl: TimeSpan.FromMinutes(5),
            maxCacheSizeBytes: 1_048_576,
            validateTemplates: true);
    }



    /// <summary>
    /// Executes the full pipeline for a user message.
    /// </summary>
    public async Task<ChatResponse> ExecuteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString();
        var conversationId = request.ConversationId ?? Guid.NewGuid().ToString();

        _logger.LogInformation("Starting pipeline execution. CorrelationId: {CorrelationId}, ConversationId: {ConversationId}, Message: {Message}",
            correlationId, conversationId, request.Message);

        try
        {
            // Create FRESH context with event channel
            var context = PipelineContextFactory.Create() with { EventChannel = _eventChannel };

            // Restore conversation history if exists
            var existingHistory = _contextStore.GetHistory(conversationId);
            if (existingHistory != null)
            {
                // We copy the history into the new context's conversation
                context.Conversation.History.CopyFrom(existingHistory);
            }

            // Build pipeline
            var pipeline = BuildPipeline(request.PipelineName);

            // Execute
            var input = new UserInputResult(request.Message);
            var result = await Pipeline.ExecuteAsync(request.PipelineName, pipeline, input, context);

            // Save updated history for next turn
            _contextStore.SaveHistory(conversationId, context.Conversation.History);

            if (result.HasError)
            {
                _logger.LogWarning("Pipeline completed with error. CorrelationId: {CorrelationId}, Error: {Error}",
                    correlationId, result.Error?.Message);

                return new ChatResponse
                {
                    CorrelationId = correlationId,
                    ConversationId = conversationId,
                    Error = result.Error?.Message
                };
            }

            var content = ExtractFinalContent(result);
            _logger.LogInformation("Pipeline completed successfully. CorrelationId: {CorrelationId}",
                correlationId);

            return new ChatResponse
            {
                CorrelationId = correlationId,
                ConversationId = conversationId,
                Content = content
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline execution failed. CorrelationId: {CorrelationId}", correlationId);
            return new ChatResponse
            {
                CorrelationId = correlationId,
                ConversationId = conversationId, // Ensure we return the ID even on error
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Gets the pipeline structure for schema generation.
    /// </summary>
    public List<IStep> GetPipeline(string name = "StoryMachine")
    {
        return BuildPipeline(name);
    }

    private List<IStep> BuildPipeline(string name)
    {
        return name switch
        {
            "StoryMachine" => BuildStoryMachinePipeline(),
            // Future pipelines can be added here
            _ => throw new ArgumentException($"Pipeline '{name}' not found", nameof(name))
        };
    }

    private List<IStep> BuildStoryMachinePipeline()
    {
        var intentionProfile = _profileResolver.GetProvider("IntentionAnalyzer");
        var outlinerProfile = _profileResolver.GetProvider("Outliner");
        var writersProfile = _profileResolver.GetProvider("Writers");
        var criticProfile = _profileResolver.GetProvider("Critic");
        var rewriterProfile = _profileResolver.GetProvider("Rewriter");
        var publisherProfile = _profileResolver.GetProvider("Publisher");
        var reviewerProfile = _profileResolver.GetProvider("StoryReviewer");
        var questionsProfile = _profileResolver.GetProvider("QuestionsAssistant");

        // Create sub-pipelines
        var newStoryPipeline = CreateNewStoryPipeline(outlinerProfile, writersProfile, criticProfile, rewriterProfile, publisherProfile);
        var reviewPipeline = CreateReviewPipeline(reviewerProfile);
        var questionsPipeline = CreateQuestionsPipeline(questionsProfile);
        var otherPipeline = CreateOtherPipeline();

        // Intention analysis step
        var intentionStep = new IntentionAnalyzerStep<StepResult, UserIntention>(
            _llmService,
            intentionProfile,
            name: "IntentionAnalyzer",
            systemPrompt: "You classify user intentions for a story creation system. Always respond in Spanish.");

        // Router step
        var routerStep = new IntentionRouterStep<UserIntention>(
            routes: new Dictionary<UserIntention, IStep>
            {
                { UserIntention.NewStory, newStoryPipeline },
                { UserIntention.ReviewStory, reviewPipeline },
                { UserIntention.Questions, questionsPipeline },
                { UserIntention.Other, otherPipeline }
            },
            defaultRoute: questionsPipeline);

        return [intentionStep, routerStep];
    }

    private GroupStep<UserIntentionResponse> CreateNewStoryPipeline(
        LlmProviderConfig outlinerProfile,
        LlmProviderConfig writersProfile,
        LlmProviderConfig criticProfile,
        LlmProviderConfig rewriterProfile,
        LlmProviderConfig publisherProfile)
    {
        var outlineStep = new StatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            _llmService, _templateProvider, "Outliner", outlinerProfile,
            "outliner_prompt", "outliner_system");

        var writer1 = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            _llmService, _templateProvider, "Writer1", writersProfile,
            "writer1_prompt", "writer1_system");

        var writer2 = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            _llmService, _templateProvider, "Writer2", writersProfile,
            "writer2_prompt", "writer2_system");

        var writersStep = new ParallelStep<StepResult>("ParallelWriters", writer1, writer2);

        var criticStep = new StatelessTemplateLlmStep<ParallelResult, CriticResult>(
            _llmService, _templateProvider, "Critic", criticProfile,
            "critic_prompt", "critic_system");

        var rewriterStep = new StatelessRewriterStep(
            _llmService, _templateProvider, "Rewriter", rewriterProfile,
            "rewriter_prompt", "rewriter_system");

        var publisherStep = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            _llmService, _templateProvider, "Publisher", publisherProfile,
            "publisher_prompt", "publisher_system");

        var saveStep = new DelegatedStep<LlmStringStepResult, LlmStringStepResult>(
            "SaveToMemory",
            (input, _, _, _) =>
            {
                _lastStory = input.Content;
                return Task.FromResult(input);
            });

        return new GroupStep<UserIntentionResponse>("NewStoryPhase",
            [outlineStep, writersStep, criticStep, rewriterStep, publisherStep, saveStep]);
    }

    private GroupStep<UserIntentionResponse> CreateReviewPipeline(LlmProviderConfig reviewerProfile)
    {
        var reviewStep = new StatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            _llmService, _templateProvider, "StoryReviewer", reviewerProfile,
            "reviewer_prompt", "reviewer_system");

        var saveStep = new DelegatedStep<LlmStringStepResult, LlmStringStepResult>(
            "SaveRevisedStory",
            (input, _, _, _) =>
            {
                _lastStory = input.Content;
                return Task.FromResult(input);
            });

        return new GroupStep<UserIntentionResponse>("ReviewPhase", [reviewStep, saveStep]);
    }

    private GroupStep<UserIntentionResponse> CreateQuestionsPipeline(LlmProviderConfig questionsProfile)
    {
        var questionsStep = new StatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            _llmService, _templateProvider, "QuestionsAssistant", questionsProfile,
            "questions_prompt", "questions_system");

        return new GroupStep<UserIntentionResponse>("QuestionsPhase", [questionsStep]);
    }

    private static GroupStep<UserIntentionResponse> CreateOtherPipeline()
    {
        var otherStep = new DelegatedStep<UserIntentionResponse, StepResult<string>>(
            "OutOfScopeHandler",
            (_, _, _, _) =>
            {
                const string message = "Lo siento, solo puedo ayudarte con la creación y revisión de cuentos. ¿Puedo ayudarte con algo relacionado?";
                return Task.FromResult<StepResult<string>>(new StepResult<string>(new EmptyStep("OutOfScopeHandler"), message));
            });

        return new GroupStep<UserIntentionResponse>("OtherPhase", [otherStep]);
    }

    private static string? ExtractFinalContent(IStepResult result)
    {
        return result switch
        {
            StepResult<string> stringResult => stringResult.Value,
            LlmStringStepResult llmResult => llmResult.Content,
            _ => result.Value?.ToString()
        };
    }
}

// Internal types
internal enum UserIntention
{
    [Description("User wants to create a completely new story from scratch")]
    NewStory,

    [Description("User wants to review, modify, or improve the existing story")]
    ReviewStory,

    [Description("User is asking questions about the story, the creative process, or requesting clarification")]
    Questions,

    [Description("User request is out of scope (not related to story creation, review, or questions)")]
    Other
}

internal sealed class UserInputResult(string input) : StepResult(new EmptyStep("User"), input)
{
    public string Message => Value?.ToString() ?? string.Empty;
}

internal sealed class UserIntentionResponse(IStep step) : LlmStepResult<Intention<UserIntention>>(step);

internal sealed class CriticResult(IStep step) : LlmStepResult<CriticData>(step)
{
    public string? Writer1 { get; set; }
    public string? Writer2 { get; set; }
}

internal sealed record CriticData
{
    public int Score { get; init; }
    public string? Recommendations { get; init; }
    public string? DetailedCritique { get; init; }
}

internal sealed class StatelessRewriterStep(
    ILlmService llmService,
    ITemplateProvider templateProvider,
    string name,
    LlmProviderConfig profile,
    string promptTemplateName,
    string? systemPromptTemplateName = null)
    : StatelessTemplateLlmStep<CriticResult, LlmStringStepResult>(
        llmService, templateProvider, name, profile, promptTemplateName, systemPromptTemplateName)
{
    protected override async Task<LlmStringStepResult> ExecuteAsync(
        CriticResult input,
        PipelineContext context,
        int attempt,
        LlmStringStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        var results = (context.StepResults["Router/NewStoryPhase/ParallelWriters"] as ParallelResult)?.Value;
        input.Writer1 = (results?["Writer1"] as LlmStringStepResult)?.Content ?? "";
        input.Writer2 = (results?["Writer2"] as LlmStringStepResult)?.Content ?? "";

        return await base.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
    }
}
