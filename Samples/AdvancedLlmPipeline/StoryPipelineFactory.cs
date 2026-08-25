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
        templateEngine: new TinyTemplateRendered(),
        enableCache: true,
        ttl: TimeSpan.FromMinutes(5),
        maxCacheSizeBytes: 1_048_576, // 1MB
        validateTemplates: true  // Enable strict validation for development
    );

    /// <summary>
    /// Creates the new story creation pipeline.
    /// </summary>
    public static GroupStep<UserIntentionResponse> CreateNewStoryPipeline(
        LlmRole outlinerRole,
        LlmRole writersRole,
        LlmRole criticRole,
        LlmRole rewriterRole,
        LlmRole publisherRole,
        Action<string> onStorySaved)
    {
        var outlineStep = new CustomStatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            name: "Outliner",
            role: outlinerRole,
            templateProvider: TemplateProvider,
            promptTemplateName: "outliner_prompt",
            systemPromptTemplateName: "outliner_system"
        );

        var writerStep1 = new CustomStatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            name: "Writer1",
            role: writersRole,
            templateProvider: TemplateProvider,
            promptTemplateName: "writer1_prompt",
            systemPromptTemplateName: "writer1_system"
        );

        var writerStep2 = new CustomStatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            name: "Writer2",
            role: writersRole,
            templateProvider: TemplateProvider,
            promptTemplateName: "writer2_prompt",
            systemPromptTemplateName: "writer2_system"
        );

        var writersStep = new ParallelStep<StepResult>(
            "ParallelWriters",
            [writerStep1, writerStep2]);

        var criticStep = new CustomStatelessTemplateLlmStep<ParallelResult, CriticResult>(
            name: "Critic",
            role: criticRole,
            templateProvider: TemplateProvider,
            promptTemplateName: "critic_prompt",
            systemPromptTemplateName: "critic_system"
        );

        var rewriterStep = new StatelessRewriterStep(
            name: "Rewriter",
            role: rewriterRole,
            templateProvider: TemplateProvider,
            promptTemplateName: "rewriter_prompt",
            systemPromptTemplateName: "rewriter_system"
        );

        var publisherStep = new CustomStatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            name: "Publisher",
            role: publisherRole,
            templateProvider: TemplateProvider,
            promptTemplateName: "publisher_prompt",
            systemPromptTemplateName: "publisher_system"
        );

        var saveStoryStep = new DelegatedStep<LlmStringStepResult, LlmStringStepResult>(
            "SaveToMemory",
            (step, input, context, attempt, result) =>
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
        LlmRole storyReviewerRole,
        Action<string> onStorySaved)
    {
        var reviewStep = new CustomStatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            name: "StoryReviewer",
            role: storyReviewerRole,
            templateProvider: TemplateProvider,
            promptTemplateName: "reviewer_prompt",
            systemPromptTemplateName: "reviewer_system"
        );

        var saveRevisedStoryStep = new DelegatedStep<LlmStringStepResult, LlmStringStepResult>(
            "SaveRevisedStory",
            (step, input, context, attempt, errorresult) =>
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
        LlmRole questionsRole)
    {
        var questionsStep = new CustomStatelessTemplateLlmStep<UserIntentionResponse, LlmStringStepResult>(
            name: "QuestionsAssistant",
            role: questionsRole,
            templateProvider: TemplateProvider,
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
            (step, input, context, attemt, lastResult) =>
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
/// Custom local step to handle template names via overrides as requested.
/// </summary>
internal class CustomStatelessTemplateLlmStep<TIn, TOut> : StatelessTemplateLlmStep<TIn, TOut>
    where TIn : IStepResult
    where TOut : ILlmStepResult
{
    public CustomStatelessTemplateLlmStep(
        string name,
        LlmRole role,
        ITemplateProvider templateProvider,
        string promptTemplateName,
        string? systemPromptTemplateName = null)
        : base(name, role, templateProvider)
    {
        Prompt = promptTemplateName;
        SystemPrompt = systemPromptTemplateName;
    }
}

/// <summary>
/// Custom stateless rewriter step that merges critic result with parallel writer results.
/// </summary>
internal class StatelessRewriterStep(
    string name,
    LlmRole role,
    ITemplateProvider templateProvider,
    string promptTemplateName,
    string? systemPromptTemplateName = null)
    : CustomStatelessTemplateLlmStep<CriticResult, LlmStringStepResult>(
        name,
        role,
        templateProvider,
        promptTemplateName,
        systemPromptTemplateName)
{
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input,
        PipelineContext context,
        int attempt,
        IStepResult? lastStepResult,
        CancellationToken cancellationToken)
    {
        if (input is CriticResult criticResult)
        {
            // Merge critic result with parallel writer results for template
            var results = (context.StepResults["Router/NewStoryPhase/ParallelWriters"] as ParallelResult)?.Value;

            var writer1Content = (results?["Writer1"] as LlmStringStepResult)?.Content ?? "";
            var writer2Content = (results?["Writer2"] as LlmStringStepResult)?.Content ?? "";

            criticResult.Writer1 = writer1Content;
            criticResult.Writer2 = writer2Content;
        }

        return await base.ExecuteAsync(input, context, attempt, lastStepResult, cancellationToken);
    }
}
