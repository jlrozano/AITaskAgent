using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.Support.Template;

namespace Samples.AdvancedLlmPipeline;

/// <summary>
/// Factory for creating story pipeline steps using template-based LLM steps.
/// </summary>
internal static class StoryPipelineFactory
{
    private static readonly ITemplateProvider TemplateProvider = new FileTemplateProvider(
        folderPath: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"),
        enableCache: true,
        ttl: TimeSpan.FromMinutes(5),
        maxCacheSizeBytes: 1_048_576, // 1MB
        validateTemplates: true  // Enable strict validation for development
    );

    /// <summary>
    /// Creates the new story creation pipeline.
    /// </summary>
    public static GroupStep<UserIntentionResponse> CreateNewStoryPipeline(
        ILlmService llmService,
        LlmProviderConfig outlinerProfile,
        LlmProviderConfig writersProfile,
        LlmProviderConfig criticProfile,
        LlmProviderConfig rewriterProfile,
        LlmProviderConfig publisherProfile,
        Action<string> onStorySaved)
    {
        var outlineStep = new StatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            llmService,
            TemplateProvider,
            name: "Outliner",
            profile: outlinerProfile,
            promptTemplateName: "outliner_prompt",
            systemPromptTemplateName: "outliner_system"
        );

        var writerStep1 = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            llmService,
            TemplateProvider,
            name: "Writer1",
            profile: writersProfile,
            promptTemplateName: "writer1_prompt",
            systemPromptTemplateName: "writer1_system"
        );

        var writerStep2 = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            llmService,
            TemplateProvider,
            name: "Writer2",
            profile: writersProfile,
            promptTemplateName: "writer2_prompt",
            systemPromptTemplateName: "writer2_system"
        );

        var writersStep = new ParallelStep<StepResult>(
            "ParallelWriters",
            [writerStep1, writerStep2]);

        var criticStep = new StatelessTemplateLlmStep<ParallelResult, CriticResult>(
            llmService,
            TemplateProvider,
            name: "Critic",
            profile: criticProfile,
            promptTemplateName: "critic_prompt",
            systemPromptTemplateName: "critic_system"
        );

        var rewriterStep = new StatelessRewriterStep(
            llmService,
            TemplateProvider,
            name: "Rewriter",
            profile: rewriterProfile,
            promptTemplateName: "rewriter_prompt",
            systemPromptTemplateName: "rewriter_system"
        );

        var publisherStep = new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            llmService,
            TemplateProvider,
            name: "Publisher",
            profile: publisherProfile,
            promptTemplateName: "publisher_prompt",
            systemPromptTemplateName: "publisher_system"
        );

        var saveStoryStep = new DelegatedStep<LlmStringStepResult, LlmStringStepResult>(
            "SaveToMemory",
            (input, context, attempt, result) =>
            {
                onStorySaved(input.Content ?? "");
                return Task.FromResult(input);
            }
        );

        List<IStep> newStoryPipeline = [outlineStep, writersStep, criticStep, rewriterStep, publisherStep, saveStoryStep];
        return new GroupStep<UserIntentionResponse>("NewStoryPhase", newStoryPipeline);
    }

    /// <summary>
    /// Creates the story review pipeline (stateless - uses self-contained optimizedPrompt).
    /// </summary>
    public static GroupStep<UserIntentionResponse> CreateReviewPipeline(
        ILlmService llmService,
        LlmProviderConfig nVideaProfile,
        Action<string> onStorySaved)
    {
        var reviewStep = new StatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            llmService,
            TemplateProvider,
            name: "StoryReviewer",
            profile: nVideaProfile,
            promptTemplateName: "reviewer_prompt",
            systemPromptTemplateName: "reviewer_system"
        );

        var saveRevisedStoryStep = new DelegatedStep<LlmStringStepResult, LlmStringStepResult>(
            "SaveRevisedStory",
            (input, context, attempt, errorresult) =>
            {
                onStorySaved(input.Content ?? "");
                return Task.FromResult(input);
            }
        );

        List<IStep> reviewPipeline = [reviewStep, saveRevisedStoryStep];
        return new GroupStep<UserIntentionResponse>("ReviewPhase", reviewPipeline);
    }

    /// <summary>
    /// Creates the questions pipeline for story-related queries.
    /// </summary>
    public static GroupStep<UserIntentionResponse> CreateQuestionsPipeline(
        ILlmService llmService,
        LlmProviderConfig nVideaProfile)
    {
        var questionsStep = new StatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            llmService,
            TemplateProvider,
            name: "QuestionsAssistant",
            profile: nVideaProfile,
            promptTemplateName: "questions_prompt",
            systemPromptTemplateName: "questions_system"
        );

        List<IStep> questionsPipeline = [questionsStep];
        return new GroupStep<UserIntentionResponse>("QuestionsPhase", questionsPipeline);
    }

    /// <summary>
    /// Creates the "other" pipeline for out-of-scope requests.
    /// </summary>
    public static GroupStep<UserIntentionResponse> CreateOtherPipeline()
    {
        var otherStep = new DelegatedStep<UserIntentionResponse, StepResult<string>>(
            "OutOfScopeHandler",
            (input, context, attemt, lastResult) =>
            {
                var message = "Lo siento, solo puedo ayudarte con la creación y revisión de cuentos. ¿Puedo ayudarte con algo relacionado?";
                return Task.FromResult<StepResult<string>>(new StepResult<string>(
                    new EmptyStep("OutOfScopeHandler"),
                    message));
            }
        );

        List<IStep> otherPipeline = [otherStep];
        return new GroupStep<UserIntentionResponse>("OtherPhase", otherPipeline);
    }
}

/// <summary>
/// Custom stateless rewriter step that merges critic result with parallel writer results.
/// </summary>
internal class StatelessRewriterStep(
    ILlmService llmService,
    ITemplateProvider templateProvider,
    string name,
    LlmProviderConfig profile,
    string promptTemplateName,
    string? systemPromptTemplateName = null)
    : StatelessTemplateLlmStep<CriticResult, LlmStringStepResult>(
        llmService,
        templateProvider,
        name,
        profile,
        promptTemplateName,
        systemPromptTemplateName)
{
    protected override async Task<LlmStringStepResult> ExecuteAsync(
        CriticResult input,
        PipelineContext context,
        int attempt,
        LlmStringStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        // Merge critic result with parallel writer results for template
        var results = (context.StepResults["Router/NewStoryPhase/ParallelWriters"] as ParallelResult)?.Value;

        var writer1Content = (results?["Writer1"] as LlmStringStepResult)?.Content ?? "";
        var writer2Content = (results?["Writer2"] as LlmStringStepResult)?.Content ?? "";

        input.Writer1 = writer1Content;
        input.Writer2 = writer2Content;


        return await base.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
    }
}
