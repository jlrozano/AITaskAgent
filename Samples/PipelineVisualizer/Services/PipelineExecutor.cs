using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Support.Template;
using Microsoft.Extensions.Options;
using PipelineVisualizer.Models;

namespace PipelineVisualizer.Services;

/// <summary>
/// Orchestrates pipeline execution for the visualizer.
/// Wraps the StoryMachine pipeline with context broadcast middleware.
/// </summary>
public sealed class PipelineExecutor
{
    // Route names double as intention ids: the router matches the option chosen by the LLM
    // against these strings, so options and routes must be built from the same constants.
    private const string RouteNewStory = "NewStory";
    private const string RouteReviewStory = "ReviewStory";
    private const string RouteQuestions = "Questions";
    private const string RouteOther = "Other";

    private static readonly IList<IntentionOption> IntentionOptions =
    [
        new() { Id = RouteNewStory, Description = "User wants to create a completely new story from scratch" },
        new() { Id = RouteReviewStory, Description = "User wants to review, modify, or improve the existing story" },
        new() { Id = RouteQuestions, Description = "User is asking questions about the story, the creative process, or requesting clarification" },
        new() { Id = RouteOther, Description = "User request is out of scope (not related to story creation, review, or questions)" }
    ];

    private readonly ITemplateProvider _templateProvider;
    private readonly LlmEnvironment _llmEnvironment;
    private readonly IPipeline _pipeline;
    private readonly ILogger<PipelineExecutor> _logger;
    private readonly ContextStore _contextStore;
    private string? _lastStory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineExecutor"/> class.
    /// </summary>
    public PipelineExecutor(
        IOptions<LlmEnvironment> llmEnvironment,
        IPipeline pipeline,
        ILogger<PipelineExecutor> logger,
        IConfiguration configuration,
        ContextStore contextStore)
    {
        _llmEnvironment = llmEnvironment.Value;
        _pipeline = pipeline;
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
            // Create FRESH context. The factory already wires the DI event channel, which is what
            // the SSE broadcaster subscribes to.
            var context = PipelineContextFactory.Create(conversation: null, subscribers: null);

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
            var result = await _pipeline.ExecuteAsync(
                request.PipelineName, pipeline, input, context, cancellationToken: cancellationToken);

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

    /// <summary>
    /// Resolves a configured role, falling back to the environment default so a missing entry
    /// degrades to the default model instead of taking the whole visualizer down.
    /// </summary>
    private LlmRole ResolveRole(string roleName)
    {
        return _llmEnvironment.Roles.TryGetValue(roleName, out var role)
            ? role
            : _llmEnvironment.Roles[_llmEnvironment.DefaultRole];
    }

    private List<IStep> BuildStoryMachinePipeline()
    {
        var intentionRole = ResolveRole("IntentionAnalyzer");
        var outlinerRole = ResolveRole("Outliner");
        var writersRole = ResolveRole("Writers");
        var criticRole = ResolveRole("Critic");
        var rewriterRole = ResolveRole("Rewriter");
        var publisherRole = ResolveRole("Publisher");
        var reviewerRole = ResolveRole("StoryReviewer");
        var questionsRole = ResolveRole("QuestionsAssistant");

        // Create sub-pipelines
        var newStoryPipeline = CreateNewStoryPipeline(outlinerRole, writersRole, criticRole, rewriterRole, publisherRole);
        var reviewPipeline = CreateReviewPipeline(reviewerRole);
        var questionsPipeline = CreateQuestionsPipeline(questionsRole);
        var otherPipeline = CreateOtherPipeline();

        // Intention analysis step. The custom system prompt must render {{ context.options }}
        // itself: supplying one replaces the step's default template, which is where the option
        // list is normally injected.
        var intentionStep = new IntentionAnalyzerStep<StepResult>(
            "IntentionAnalyzer",
            intentionRole,
            IntentionOptions,
            systemPromptTemplateName: "You classify user intentions for a story creation system. "
                + "Always respond in Spanish.\n\nAvailable Options:\n{{ context.options }}");

        // Router step. Named "Router" because nested steps address their siblings by path
        // (see StatelessRewriterStep); the comparer makes the casing the LLM returns irrelevant.
        var routerStep = new RouterStep(
            "Router",
            routes: new Dictionary<string, IStep>(StringComparer.OrdinalIgnoreCase)
            {
                { RouteNewStory, newStoryPipeline },
                { RouteReviewStory, reviewPipeline },
                { RouteQuestions, questionsPipeline },
                { RouteOther, otherPipeline }
            },
            defaultRoute: questionsPipeline);

        return [intentionStep, routerStep];
    }

    private GroupStep<LlmIntentionResult> CreateNewStoryPipeline(
        LlmRole outlinerRole,
        LlmRole writersRole,
        LlmRole criticRole,
        LlmRole rewriterRole,
        LlmRole publisherRole)
    {
        var outlineStep = new StatelessTemplateLlmStep<LlmIntentionResult, LlmStringStepResult>(
            "Outliner", outlinerRole, _templateProvider,
            promtTemplateString: "outliner_prompt", systemTemplateString: "outliner_system");

        var writer1 = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Writer1", writersRole, _templateProvider,
            promtTemplateString: "writer1_prompt", systemTemplateString: "writer1_system");

        var writer2 = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Writer2", writersRole, _templateProvider,
            promtTemplateString: "writer2_prompt", systemTemplateString: "writer2_system");

        var writersStep = new ParallelStep<StepResult>("ParallelWriters", writer1, writer2);

        var criticStep = new StatelessTemplateLlmStep<ParallelResult, CriticResult>(
            "Critic", criticRole, _templateProvider,
            promtTemplateString: "critic_prompt", systemTemplateString: "critic_system");

        var rewriterStep = new StatelessRewriterStep(
            "Rewriter", rewriterRole, _templateProvider,
            "rewriter_prompt", "rewriter_system");

        var publisherStep = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "Publisher", publisherRole, _templateProvider,
            promtTemplateString: "publisher_prompt", systemTemplateString: "publisher_system");

        var saveStep = new DelegatedStep<LlmStringStepResult, LlmStringStepResult>(
            "SaveToMemory",
            (_, input, _, _, _) =>
            {
                _lastStory = input.Content;
                return Task.FromResult(input);
            });

        return new GroupStep<LlmIntentionResult>("NewStoryPhase",
            [outlineStep, writersStep, criticStep, rewriterStep, publisherStep, saveStep]);
    }

    private GroupStep<LlmIntentionResult> CreateReviewPipeline(LlmRole reviewerRole)
    {
        var reviewStep = new StatelessTemplateLlmStep<LlmIntentionResult, LlmStringStepResult>(
            "StoryReviewer", reviewerRole, _templateProvider,
            promtTemplateString: "reviewer_prompt", systemTemplateString: "reviewer_system");

        var saveStep = new DelegatedStep<LlmStringStepResult, LlmStringStepResult>(
            "SaveRevisedStory",
            (_, input, _, _, _) =>
            {
                _lastStory = input.Content;
                return Task.FromResult(input);
            });

        return new GroupStep<LlmIntentionResult>("ReviewPhase", [reviewStep, saveStep]);
    }

    private GroupStep<LlmIntentionResult> CreateQuestionsPipeline(LlmRole questionsRole)
    {
        var questionsStep = new StatelessTemplateLlmStep<LlmIntentionResult, LlmStringStepResult>(
            "QuestionsAssistant", questionsRole, _templateProvider,
            promtTemplateString: "questions_prompt", systemTemplateString: "questions_system");

        return new GroupStep<LlmIntentionResult>("QuestionsPhase", [questionsStep]);
    }

    private static GroupStep<LlmIntentionResult> CreateOtherPipeline()
    {
        var otherStep = new DelegatedStep<LlmIntentionResult, StepResult<string>>(
            "OutOfScopeHandler",
            (step, _, _, _, _) =>
            {
                const string message = "Lo siento, solo puedo ayudarte con la creación y revisión de cuentos. ¿Puedo ayudarte con algo relacionado?";
                return Task.FromResult(new StepResult<string>(step, message));
            });

        return new GroupStep<LlmIntentionResult>("OtherPhase", [otherStep]);
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
internal sealed class UserInputResult(string input) : StepResult(new EmptyStep("User"), input)
{
    public string Message => Value?.ToString() ?? string.Empty;
}

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
    string name,
    LlmRole role,
    ITemplateProvider templateProvider,
    string promptTemplateName,
    string? systemPromptTemplateName = null)
    : StatelessTemplateLlmStep<CriticResult, LlmStringStepResult>(
        name, role, templateProvider, null, promptTemplateName, systemPromptTemplateName)
{
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        // The critic only carries its verdict; the drafts it judged live in the parallel branch
        // two steps back, so the rewriter pulls them in by path before prompting.
        if (input is CriticResult critic)
        {
            var results = (context.StepResults["Router/NewStoryPhase/ParallelWriters"] as ParallelResult)?.Value;
            critic.Writer1 = (results?["Writer1"] as LlmStringStepResult)?.Content ?? "";
            critic.Writer2 = (results?["Writer2"] as LlmStringStepResult)?.Content ?? "";
        }

        return await base.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
    }
}
