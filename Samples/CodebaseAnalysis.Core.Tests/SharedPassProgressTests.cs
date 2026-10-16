using AITaskAgent.Core.Models;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.SharedPass;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="SharedPassProgress"/> (W1): cada cambio sale como un evento con el estado entero, en orden aunque los lotes informen desde
/// varios hilos, y sin canal no hace nada.
/// </summary>
public sealed class SharedPassProgressTests
{
    private static (EventChannel Channel, List<IProgressEvent> Received) Channel()
    {
        var channel = new EventChannel();
        var received = new List<IProgressEvent>();
        channel.Subscribe((evt, _) =>
        {
            lock (received)
                received.Add(evt);
            return Task.CompletedTask;
        });
        return (channel, received);
    }

    private static Dictionary<string, object?> Batch(StepProgressEvent evt, int index) =>
        (Dictionary<string, object?>)((List<object?>)evt.Metadata!["batches"]!)[index]!;

    [Fact]
    public async Task Concurrent_updates_from_the_workers_come_out_in_order_and_the_last_event_is_the_final_state()
    {
        var (channel, received) = Channel();
        var options = new SharedPassOptions { MaxCostPerRunUsd = 10, MaxMinutes = 30 };
        using var budget = new SharedPassCostBudget(options);
        var progress = new SharedPassProgress(new PipelineContext(channel), "SharedPassStep", options, budget, DateTimeOffset.UtcNow);
        const int Workers = 4, CallsPerBatch = 50;
        progress.BeginReading(Enumerable.Range(0, Workers).Select(i => ($"b{i}", 3, 1000)));

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(i => Task.Run(() =>
        {
            progress.BatchStatus($"b{i}", SharedPassProgress.BatchStates.Reading);
            for (var call = 1; call <= CallsPerBatch; call++)
            {
                budget.Add(0.01m, 100);
                progress.BatchUsage($"b{i}", call, call * 0.01m, call * 100);
            }
        })));
        progress.Phase(SharedPassProgress.Phases.Consolidating);
        await channel.DisposeAsync();

        List<StepProgressEvent> events;
        lock (received)
            events = [.. received.OfType<StepProgressEvent>()];
        Assert.Equal(progress.Emitted, events.Count);
        Assert.All(events, e => Assert.Equal(AnalysisEventTypes.SharedPassProgress, e.EventType));

        // Por lote, las llamadas nunca bajan de un evento al siguiente: cada estado se construye y se envía bajo el mismo cerrojo.
        for (var i = 0; i < Workers; i++)
        {
            var calls = events.Select(e => (int)Batch(e, i)["calls"]!).ToList();
            Assert.Equal(calls.OrderBy(c => c), calls);
            Assert.Equal(CallsPerBatch, calls[^1]);
        }
        var last = events[^1].Metadata!;
        Assert.Equal("consolidating", last["phase"]);
        var totals = (Dictionary<string, object?>)last["totals"]!;
        Assert.Equal((Workers * CallsPerBatch, 2m, 10d, 30d), ((int)totals["calls"]!, (decimal)totals["costUsd"]!, (double?)totals["maxCostUsd"], (double?)totals["maxMinutes"]));
    }

    [Fact]
    public async Task A_finished_pass_reports_its_result_and_a_cut_one_its_reason()
    {
        var (channel, received) = Channel();
        var options = new SharedPassOptions();
        using var budget = new SharedPassCostBudget(options);
        var progress = new SharedPassProgress(new PipelineContext(channel), "SharedPassStep", options, budget, DateTimeOffset.UtcNow);
        progress.BeginReading([("b0", 2, 10)]);

        progress.BatchDone(new BatchReadResult { BatchId = "b0", Status = BatchReadStatus.NotRead, Reading = BatchReading.Empty("b0"), StopReason = "tope de coste" });
        progress.Finish(null, new Dictionary<string, int> { ["P0"] = 1 }, 3, 1);
        await channel.DisposeAsync();

        StepProgressEvent last;
        lock (received)
            last = received.OfType<StepProgressEvent>().Last();
        Assert.Equal("notRead", Batch(last, 0)["status"]);
        Assert.Equal("cut", last.Metadata!["phase"]);
        Assert.Equal("tope de coste", last.Metadata["stopReason"]);
        Assert.Equal("warning", last.Metadata[AnalysisEventTypes.MetadataKeys.Level]);
        var result = (Dictionary<string, object?>)last.Metadata["result"]!;
        Assert.Equal((3, 1), ((int)result["entries"]!, (int)result["rejected"]!));
    }

    [Fact]
    public void Without_a_channel_it_does_nothing()
    {
        var options = new SharedPassOptions();
        using var budget = new SharedPassCostBudget(options);
        var progress = new SharedPassProgress(new PipelineContext(), "SharedPassStep", options, budget, DateTimeOffset.UtcNow);

        progress.BeginReading([("b0", 1, 1)]);
        progress.Fail("boom");

        Assert.Equal(2, progress.Emitted);
    }
}
