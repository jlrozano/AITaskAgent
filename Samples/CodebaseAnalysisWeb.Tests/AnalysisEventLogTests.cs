using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysisWeb.Analysis;

namespace CodebaseAnalysisWeb.Tests;

/// <summary>
/// Tamaño del registro que se reenvía al reconectar (W1, W2): las salidas de tool y los estados de la pasada compartida se acotan en lo
/// guardado sin tocar el <c>seq</c> ni lo que reciben los lectores en vivo.
/// </summary>
public sealed class AnalysisEventLogTests
{
    private static AnalysisEventDto ToolCompleted(string output) => new()
    {
        RunId = "r",
        Type = AnalysisEventDtoTypes.ToolCompleted,
        Data = new Dictionary<string, object?> { ["tool"] = "view_file", ["success"] = true, ["output"] = output, ["outputBytes"] = (long)output.Length }
    };

    private static AnalysisEventDto SharedPass(string phase) => new()
    {
        RunId = "r",
        Type = AnalysisEventDtoTypes.SharedPassProgress,
        Data = new Dictionary<string, object?> { ["phase"] = phase, ["batches"] = new List<object?> { "b0" }, ["totals"] = new Dictionary<string, object?>() }
    };

    [Fact]
    public async Task Past_the_output_budget_the_oldest_outputs_are_dropped_from_the_replay_but_not_from_the_live_readers()
    {
        var log = new AnalysisEventLog("r", maxToolOutputBytes: 25);
        var (_, live) = log.Subscribe(0);

        log.Append(ToolCompleted(new string('a', 10)));
        log.Append(ToolCompleted(new string('b', 10)));
        log.Append(ToolCompleted(new string('c', 10)));
        log.Complete();

        var (replay, _) = log.Subscribe(0);
        Assert.Equal([1L, 2L, 3L], replay.Select(e => e.Seq));
        Assert.False(replay[0].Data!.ContainsKey("output"));
        Assert.Equal(true, replay[0].Data!["outputDropped"]);
        Assert.Equal(10L, replay[0].Data!["outputBytes"]);
        Assert.Equal(new string('b', 10), replay[1].Data!["output"]);
        Assert.Equal(new string('c', 10), replay[2].Data!["output"]);

        var received = new List<AnalysisEventDto>();
        await foreach (var evt in live.Reader.ReadAllAsync())
            received.Add(evt);
        Assert.Equal(new string('a', 10), received[0].Data!["output"]);
    }

    [Fact]
    public void Only_the_last_shared_pass_state_keeps_its_batches()
    {
        var log = new AnalysisEventLog("r");

        log.Append(SharedPass("reading"));
        log.Append(new AnalysisEventDto { RunId = "r", Type = AnalysisEventDtoTypes.Log, Message = "x" });
        log.Append(SharedPass("judging"));
        log.Append(SharedPass("done"));

        var (replay, _) = log.Subscribe(0);
        Assert.Equal([1L, 2L, 3L, 4L], replay.Select(e => e.Seq));
        Assert.Equal(true, replay[0].Data!["superseded"]);
        Assert.False(replay[0].Data!.ContainsKey("batches"));
        Assert.Equal("reading", replay[0].Data!["phase"]);
        Assert.Equal(true, replay[2].Data!["superseded"]);
        Assert.True(replay[3].Data!.ContainsKey("batches"));
        Assert.False(replay[3].Data!.ContainsKey("superseded"));
    }
}
