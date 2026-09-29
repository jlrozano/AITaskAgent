using AITaskAgent.Core.Models;
using CodebaseAnalysis.Core.Progress;

namespace CodebaseAnalysis.Core.Tests;

[Collection(ConsoleCollection.Name)]
public sealed class ConsoleAnalysisReporterTests
{
    private static string CaptureConsole(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    [Theory]
    [InlineData(AnalysisLogLevel.Info, "   [indexador] {\"project\":\"x\"}")]
    [InlineData(AnalysisLogLevel.Success, "✅ [LLM] Completado '09_inyeccion_dependencias.md' en 44,3s.")]
    [InlineData(AnalysisLogLevel.Warning, "⚠️ 2 auditor(es) fallaron; la síntesis sigue con los informes disponibles.")]
    [InlineData(AnalysisLogLevel.Error, "❌ [LLM] Falló (X) tras 1,0s: boom")]
    public void Writes_the_time_and_the_source_before_the_unaltered_message(AnalysisLogLevel level, string message)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 29, 7, 5, 9, TimeSpan.FromHours(2)));
        var output = CaptureConsole(() => new ConsoleAnalysisReporter(clock).Report(level, "Auditor_CodeSmells", message));

        Assert.Equal("07:05:09 [Auditor_CodeSmells] " + message + Environment.NewLine, output);
    }

    [Fact]
    public void Without_a_source_only_the_time_goes_before_the_message()
    {
        var at = new DateTimeOffset(2026, 9, 29, 23, 59, 1, TimeSpan.Zero);

        Assert.Equal("23:59:01 ℹ️ mensaje", ConsoleAnalysisReporter.Format(at, "", "ℹ️ mensaje"));
        Assert.Equal("23:59:01 ℹ️ mensaje", ConsoleAnalysisReporter.Format(at, null, "ℹ️ mensaje"));
    }

    [Theory]
    [InlineData(new[] { "{ Path = , Step = ParallelReportsExecution, CorrelationId = c1 }", "{ Path = ParallelReportsExecution, Step = Auditor_CodeSmells, CorrelationId = c1 }" }, "Auditor_CodeSmells")]
    [InlineData(new[] { "{ Path = , Step = SqlFacetExtractorStep, CorrelationId = c1 }", "otro scope sin paso" }, "SqlFacetExtractorStep")]
    [InlineData(new[] { "otro scope sin paso" }, null)]
    [InlineData(new string[0], null)]
    public void The_log_origin_is_the_innermost_pipeline_step(string[] scopes, string? expected)
    {
        Assert.Equal(expected, LogScopeOrigin.InnermostStep(scopes));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("fija", now.Offset, "fija", "fija");
    }

    [Fact]
    public void Context_without_reporter_writes_to_the_console()
    {
        var context = new PipelineContext();

        var output = CaptureConsole(() => context.Report("Step", AnalysisLogLevel.Info, "ℹ️ mensaje"));

        Assert.Same(ConsoleAnalysisReporter.Instance, context.GetReporter());
        Assert.Matches(@"^\d{2}:\d{2}:\d{2} \[Step\] ℹ️ mensaje\r?\n$", output);
    }
}
