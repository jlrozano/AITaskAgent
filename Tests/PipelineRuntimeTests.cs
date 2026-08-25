using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Newtonsoft.Json;

namespace AITaskAgent.Tests;

/// <summary>
/// The context holds the engine running it rather than resolving it from a container. These pin the
/// two properties that follow: a pipeline runs without DI, and the live references it carries stay
/// out of the persisted state.
/// </summary>
public class PipelineRuntimeTests
{
    private sealed class NoopStep(string name) : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
            => Task.FromResult<IStepResult>(new StepResult<string>(this, Name));
    }

    private static StepResult<string> Seed() => new(new EmptyStep("Seed"), "seed");

    [Fact]
    public async Task Composite_steps_run_without_a_container()
    {
        // ParallelStep executes its branches as sub-pipelines. It used to need IPipeline resolvable
        // from the context's provider; now it reaches the engine that started the run.
        var parallel = new ParallelStep("Fan", new NoopStep("A"), new NoopStep("B"));

        var result = await new Pipeline().ExecuteAsync("Test", [parallel], Seed(), new PipelineContext());

        Assert.False(result.HasError, result.Error?.Message);
    }

    [Fact]
    public async Task The_pipeline_attaches_itself_to_the_context()
    {
        var context = new PipelineContext();
        Assert.Null(context.Runtime);

        var pipeline = new Pipeline();
        await pipeline.ExecuteAsync("Test", [new NoopStep("Only")], Seed(), context);

        Assert.Same(pipeline, context.Runtime);
    }

    [Fact]
    public async Task Branch_contexts_inherit_the_engine()
    {
        IPipelineRuntime? observed = null;

        var probe = new ProbeStep("Probe", ctx => observed = ctx.Runtime);
        var parallel = new ParallelStep("Fan", probe);

        var pipeline = new Pipeline();
        await pipeline.ExecuteAsync("Test", [parallel], Seed(), new PipelineContext());

        Assert.Same(pipeline, observed);
    }

    private sealed class ProbeStep(string name, Action<PipelineContext> probe)
        : StepBase(name, typeof(IStepResult), typeof(StepResult<string>))
    {
        protected override Task<IStepResult> ExecuteAsync(
            IStepResult input, PipelineContext context, int attempt,
            IStepResult? lastStepResult, CancellationToken cancellationToken)
        {
            probe(context);
            return Task.FromResult<IStepResult>(new StepResult<string>(this, Name));
        }
    }

    [Fact]
    public void Live_references_are_left_out_of_the_serialized_state()
    {
        var context = new PipelineContext { Runtime = new Pipeline() };
        context.Metadata["business-key"] = "order-42";

        var json = JsonConvert.SerializeObject(context);

        Assert.DoesNotContain("Runtime", json);
        Assert.DoesNotContain("ServiceProvider", json);
        Assert.DoesNotContain("EventChannel", json);
        Assert.Contains("business-key", json);
    }

    [Fact]
    public void Persisted_state_is_restored_onto_a_context_that_already_has_its_wiring()
    {
        var suspended = new PipelineContext();
        suspended.Metadata["business-key"] = "order-42";
        var json = JsonConvert.SerializeObject(suspended);

        // The resume path: build the context first - so it carries the live references - then
        // populate the persisted data onto it, rather than deserializing a fresh instance.
        var pipeline = new Pipeline();
        var revived = new PipelineContext { Runtime = pipeline };
        JsonConvert.PopulateObject(json, revived);

        Assert.Equal("order-42", revived.Metadata["business-key"]);
        Assert.Same(pipeline, revived.Runtime);
    }
}
