using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Steps;

namespace AITaskAgent.Tests;

/// <summary>
/// The route name is whatever the model wrote, so casing is exactly what cannot be trusted here.
/// RouterStep documented a case-insensitive match but resolved against the raw constructor
/// dictionary, sending anything differently cased to the default route instead.
/// </summary>
public class RouterStepTests
{
    private sealed class FakeRouterResult(IStep step, string selected)
        : StepResult<string>(step, selected), IRouterResult
    {
        public string SelectedOption { get; } = selected;
    }

    /// <summary>No container: a step falls back to NullLoggerFactory when nothing supplies one.</summary>
    private static PipelineContext Context() => new();

    private static (RouterStep Router, IStep Target, IStep Fallback) Build()
    {
        var target = new EmptyStep("NewStory");
        var fallback = new EmptyStep("Fallback");

        // A plain dictionary: the caller supplies no comparer, which is the case that used to break.
        var router = new RouterStep("Router",
            new Dictionary<string, IStep> { ["NewStory"] = target },
            fallback);

        return (router, target, fallback);
    }

    [Theory]
    [InlineData("NewStory")]
    [InlineData("newstory")]
    [InlineData("NEWSTORY")]
    [InlineData("NewSTORY")]
    public async Task Routes_regardless_of_casing(string selectedOption)
    {
        var (router, target, _) = Build();
        var input = new FakeRouterResult(new EmptyStep("Analyzer"), selectedOption);

        await ((IStep)router).ExecuteAsync(input, Context(), 1, null, CancellationToken.None);

        Assert.Contains(target, input.NextSteps);
    }

    [Fact]
    public async Task Falls_back_when_the_option_is_unknown()
    {
        var (router, _, fallback) = Build();
        var input = new FakeRouterResult(new EmptyStep("Analyzer"), "SomethingElse");

        await ((IStep)router).ExecuteAsync(input, Context(), 1, null, CancellationToken.None);

        Assert.Contains(fallback, input.NextSteps);
    }

    [Fact]
    public void Exposes_its_route_names_as_options()
    {
        var (router, _, _) = Build();

        Assert.Equal(["NewStory"], ((IRouterOptions)router).Options);
    }
}
