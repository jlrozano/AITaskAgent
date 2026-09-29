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
    public void Writes_the_message_unaltered(AnalysisLogLevel level, string message)
    {
        var output = CaptureConsole(() => ConsoleAnalysisReporter.Instance.Report(level, "AnySource", message));

        Assert.Equal(message + Environment.NewLine, output);
    }

    [Fact]
    public void Context_without_reporter_writes_to_the_console()
    {
        var context = new PipelineContext();

        var output = CaptureConsole(() => context.Report("Step", AnalysisLogLevel.Info, "ℹ️ mensaje"));

        Assert.Same(ConsoleAnalysisReporter.Instance, context.GetReporter());
        Assert.Equal("ℹ️ mensaje" + Environment.NewLine, output);
    }
}
