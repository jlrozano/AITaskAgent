using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Conversation.Context;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Tests;

/// <summary>
/// A run that ends suspended is saved by the pipeline itself in the registered
/// <see cref="IPipelineContextStorage"/>; a failing storage propagates; a run that does not suspend is not saved.
/// </summary>
public sealed class SuspendPersistenceTests
{
    private const string SuspendYaml = """
        name: approval
        steps:
          - id: ask
            type: SuspendStep
            message: "Confirm?"
            marker: awaiting
        """;

    private sealed class FailingStorage : IPipelineContextStorage
    {
        public Task SaveAsync(string correlationId, PipelineContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("storage down");

        public Task<PipelineContext?> LoadAsync(string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<PipelineContext?>(null);

        public Task DeleteAsync(string correlationId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static LlmPipelineContext ContextWith(IPipelineContextStorage storage) =>
        new(null, new ServiceCollection().AddSingleton(storage).BuildServiceProvider());

    [Fact]
    public async Task A_suspended_run_is_saved_by_the_pipeline_under_its_correlation_id()
    {
        var storage = new InMemoryPipelineContextStorage();
        var context = ContextWith(storage);
        var pipeline = new YamlTestKit().Factory.CreateFromYaml(SuspendYaml);

        var result = await pipeline.ExecuteAsync(new JObject(), context);

        Assert.Equal("awaiting", result.SuspendInfo);
        Assert.NotNull(await storage.LoadAsync(context.CorrelationId, default));
    }

    [Fact]
    public async Task A_resumed_run_that_completes_deletes_its_saved_context()
    {
        var storage = new InMemoryPipelineContextStorage();
        var context = ContextWith(storage);
        var pipeline = new YamlTestKit().Factory.CreateFromYaml(SuspendYaml);
        await pipeline.ExecuteAsync(new JObject(), context);
        Assert.NotNull(await storage.LoadAsync(context.CorrelationId, default));

        var resumed = await pipeline.ExecuteAsync(new JObject { ["approved"] = true }, context);

        Assert.Null(resumed.SuspendInfo);
        Assert.False(resumed.HasError, resumed.Error?.Message);
        Assert.Null(await storage.LoadAsync(context.CorrelationId, default));
    }

    [Fact]
    public async Task A_storage_failure_propagates_instead_of_reporting_a_suspension_that_cannot_resume()
    {
        var pipeline = new YamlTestKit().Factory.CreateFromYaml(SuspendYaml);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.ExecuteAsync(new JObject(), ContextWith(new FailingStorage())));

        Assert.Equal("storage down", ex.Message);
    }

    [Fact]
    public async Task Without_a_registered_storage_the_suspended_run_still_returns()
    {
        var pipeline = new YamlTestKit().Factory.CreateFromYaml(SuspendYaml);
        var context = new LlmPipelineContext(null, new ServiceCollection().BuildServiceProvider());

        var result = await pipeline.ExecuteAsync(new JObject(), context);

        Assert.Equal("awaiting", result.SuspendInfo);
    }
}
