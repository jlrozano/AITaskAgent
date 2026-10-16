using AITaskAgent.Core.Models;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Support;
using Key = CodebaseAnalysis.Core.Progress.AnalysisEventTypes.MetadataKeys;

namespace CodebaseAnalysis.Core.Tests;

public sealed class AnalysisProgressTests
{
    private static (PipelineContext Context, CapturingReporter Reporter) ContextWith(IEventChannel? channel)
    {
        var reporter = new CapturingReporter();
        var context = new PipelineContext(channel);
        context.Set(AnalysisContextKeys.Reporter, reporter);
        return (context, reporter);
    }

    /// <summary>Canal propio y la lista de lo que recibe. Al liberarlo se vacían sus suscriptores.</summary>
    private static (EventChannel Channel, List<IProgressEvent> Received) Channel()
    {
        var channel = new EventChannel();
        var received = new List<IProgressEvent>();
        channel.Subscribe((evt, _) =>
        {
            lock (received)
            {
                received.Add(evt);
            }
            return Task.CompletedTask;
        });
        return (channel, received);
    }

    [Fact]
    public void Without_channel_only_the_reporter_receives_the_message()
    {
        var (context, reporter) = ContextWith(channel: null);

        context.Report("DetectAndIndexGraphStep", AnalysisLogLevel.Warning, "⚠️ algo");
        context.ReportWritten("Auditor_CodeSmells", "C:/r/02_code_smells.md", ReportType.CodeSmells, "Green", null, truncated: false);

        Assert.Equal([(AnalysisLogLevel.Warning, "DetectAndIndexGraphStep", "⚠️ algo")], reporter.Entries);
    }

    [Fact]
    public async Task With_channel_Report_emits_a_log_event_with_level_and_channel()
    {
        var (channel, received) = Channel();
        var (context, reporter) = ContextWith(channel);

        context.Report("DotNetEnricherStep", AnalysisLogLevel.Warning, "⚠️ sin solución");
        context.Report("DetectAndIndexGraphStep", AnalysisLogLevel.Info, "   [indexador] línea", AnalysisEventTypes.Channels.Indexer);
        await channel.DisposeAsync();

        Assert.Equal(2, reporter.Entries.Count);
        Assert.Collection(
            received.Cast<StepProgressEvent>(),
            first =>
            {
                Assert.Equal(AnalysisEventTypes.Log, first.EventType);
                Assert.Equal("DotNetEnricherStep", first.StepName);
                Assert.Equal("⚠️ sin solución", first.Message);
                Assert.Equal(context.CorrelationId, first.CorrelationId);
                Assert.Equal("warning", first.Metadata![Key.Level]);
                Assert.Equal(AnalysisEventTypes.Channels.Pipeline, first.Metadata[Key.Channel]);
            },
            second =>
            {
                Assert.Equal("info", second.Metadata![Key.Level]);
                Assert.Equal(AnalysisEventTypes.Channels.Indexer, second.Metadata[Key.Channel]);
            });
    }

    [Theory]
    [InlineData(AnalysisLogLevel.Info, "info")]
    [InlineData(AnalysisLogLevel.Success, "success")]
    [InlineData(AnalysisLogLevel.Warning, "warning")]
    [InlineData(AnalysisLogLevel.Error, "error")]
    public void Level_wire_names(AnalysisLogLevel level, string expected)
    {
        Assert.Equal(expected, level.ToWireName());
    }

    [Fact]
    public async Task ReportWritten_emits_the_report_metadata_and_nothing_to_the_reporter()
    {
        var (channel, received) = Channel();
        var (context, reporter) = ContextWith(channel);
        var path = Path.Combine("C:", "repo", "reports", "05_errores_y_bugs.md");

        context.ReportWritten("Auditor_ErroresYBugs", path, ReportType.ErroresYBugs, "Red", 6, truncated: true);
        context.ReportWritten("ExecutiveSynthesizerStep", "00_resumen_ejecutivo.md", reportType: null, "Unknown", null, truncated: false);
        await channel.DisposeAsync();

        Assert.Empty(reporter.Entries);
        var events = received.Cast<StepProgressEvent>().ToList();
        Assert.All(events, e => Assert.Equal(AnalysisEventTypes.ReportWritten, e.EventType));

        var report = events[0];
        Assert.Equal("Auditor_ErroresYBugs", report.StepName);
        Assert.Equal("05_errores_y_bugs.md", report.Metadata![Key.FileName]);
        Assert.Equal(path, report.Metadata[Key.FilePath]);
        Assert.Equal("ErroresYBugs", report.Metadata[Key.ReportType]);
        Assert.Equal("Red", report.Metadata[Key.Health]);
        Assert.Equal(6, report.Metadata[Key.CriticalFindings]);
        Assert.Equal(true, report.Metadata[Key.Truncated]);

        Assert.Null(events[1].Metadata![Key.ReportType]);
    }
}
