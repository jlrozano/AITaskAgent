using System.Text;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Tools.Base;
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Entrada y salida de las tool calls en los eventos (W2): <see cref="LlmTool"/> las pone en <c>AdditionalData</c> y los mappers del
/// análisis y del chat las sacan truncadas, sin cambiar nada cuando el evento no las trae.
/// </summary>
public sealed class ToolEventPayloadTests
{
    private static readonly string Auditor = AnalysisPipelineFactory.AuditorStepName(ReportType.CodeSmells);

    private sealed class EchoTool(Func<string, string> run) : LlmTool
    {
        public override string Name => "echo";
        public override string Description => "Devuelve lo que se le pide.";
        protected override BinaryData ParametersSchema => BinaryData.FromString("""{"type":"object"}""");

        protected override Task<string> InternalExecuteAsync(string argumentsJson, PipelineContext context, ILogger logger, CancellationToken cancellationToken) =>
            Task.FromResult(run(argumentsJson));
    }

    private static async Task<List<IProgressEvent>> RunToolAsync(LlmTool tool, string arguments)
    {
        await using var channel = new EventChannel();
        var received = new List<IProgressEvent>();
        channel.Subscribe((evt, _) =>
        {
            lock (received)
                received.Add(evt);
            return Task.CompletedTask;
        });
        var context = new PipelineContext(channel);
        try
        {
            await tool.ExecuteAsync(arguments, context, Auditor, NullLogger.Instance);
        }
        catch (InvalidOperationException)
        {
            // la tool que falla: el evento de fin ya ha salido
        }
        await channel.DisposeAsync();
        lock (received)
            return [.. received];
    }

    [Fact]
    public async Task LlmTool_puts_the_arguments_and_the_result_in_AdditionalData()
    {
        var events = await RunToolAsync(new EchoTool(args => $"recibido {args}"), """{"path":"src/A.cs"}""");

        var started = Assert.Single(events.OfType<ToolStartedEvent>());
        var completed = Assert.Single(events.OfType<ToolCompletedEvent>());
        Assert.Equal("""{"path":"src/A.cs"}""", started.AdditionalData![LlmTool.ParametersDataKey]);
        Assert.Equal("""{"path":"src/A.cs"}""", completed.AdditionalData![LlmTool.ParametersDataKey]);
        Assert.Equal("""recibido {"path":"src/A.cs"}""", completed.AdditionalData[LlmTool.ResultDataKey]);
    }

    [Fact]
    public async Task A_failed_tool_carries_its_arguments_but_no_result()
    {
        var events = await RunToolAsync(new EchoTool(_ => throw new InvalidOperationException("no existe")), "{}");

        var completed = Assert.Single(events.OfType<ToolCompletedEvent>());
        Assert.False(completed.Success);
        Assert.Equal("{}", completed.AdditionalData![LlmTool.ParametersDataKey]);
        Assert.False(completed.AdditionalData.ContainsKey(LlmTool.ResultDataKey));

        var dto = new AnalysisEventMapper("r").Map(completed)!;
        Assert.Equal("{}", dto.Data!["arguments"]);
        Assert.False(dto.Data.ContainsKey("output"));
    }

    [Fact]
    public void The_analysis_mapper_adds_arguments_and_a_truncated_output()
    {
        var output = new string('a', ToolEventPayload.MaxOutputBytes) + "ñandú";
        var mapper = new AnalysisEventMapper("r");

        var started = mapper.Map(new ToolStartedEvent { StepName = Auditor, ToolName = "view_file", AdditionalData = new() { ["parameters"] = """{"path":"A.cs"}""" } })!;
        var completed = mapper.Map(new ToolCompletedEvent
        {
            StepName = Auditor,
            ToolName = "view_file",
            Success = true,
            Duration = TimeSpan.FromMilliseconds(30),
            AdditionalData = new() { ["parameters"] = """{"path":"A.cs"}""", ["result"] = output }
        })!;

        Assert.Equal("""{"path":"A.cs"}""", started.Data!["arguments"]);
        Assert.False(started.Data.ContainsKey("output"));
        Assert.Equal("""{"path":"A.cs"}""", completed.Data!["arguments"]);
        Assert.Equal(true, completed.Data["outputTruncated"]);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(output), completed.Data["outputBytes"]);
        Assert.Equal(new string('a', ToolEventPayload.MaxOutputBytes), completed.Data["output"]);
        Assert.Equal("view_file (30 ms)", completed.Message);
    }

    [Fact]
    public void Without_AdditionalData_the_tool_events_are_as_before()
    {
        var mapper = new AnalysisEventMapper("r");

        var started = mapper.Map(new ToolStartedEvent { StepName = Auditor, ToolName = "view_file" })!;
        var completed = mapper.Map(new ToolCompletedEvent { StepName = Auditor, ToolName = "view_file", Success = true })!;
        var chat = Assert.Single(ChatEventMapper.Map(new ToolCompletedEvent { ToolName = "view_file", Success = true }));

        Assert.Equal(["tool"], started.Data!.Keys);
        Assert.Equal(["tool", "success", "durationMs", "error"], completed.Data!.Keys);
        Assert.Equal(["name", "success", "durationMs", "error"], chat.Fields.Keys);
    }

    [Fact]
    public void The_chat_mapper_adds_only_the_output()
    {
        var chat = Assert.Single(ChatEventMapper.Map(new ToolCompletedEvent
        {
            ToolName = "read_report",
            Success = true,
            AdditionalData = new() { ["parameters"] = "{}", ["result"] = "# Informe" }
        }));

        Assert.Equal("# Informe", chat.Fields["output"]);
        Assert.Equal(false, chat.Fields["outputTruncated"]);
        Assert.Equal(9L, chat.Fields["outputBytes"]);
        Assert.False(chat.Fields.ContainsKey("arguments"));
    }

    [Theory]
    [InlineData("abc", 10, "abc")]
    [InlineData("añb", 2, "a")] // la ñ (2 bytes) no cabe entera: se corta antes
    [InlineData("a😀b", 4, "a")] // el emoji (par suplente, 4 bytes) no se parte
    [InlineData("a😀b", 5, "a😀")]
    public void TruncateUtf8_never_splits_a_character(string text, int max, string expected)
    {
        var (cut, truncated, total) = ToolEventPayload.TruncateUtf8(text, max);

        Assert.Equal(expected, cut);
        Assert.Equal(cut != text, truncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), total);
    }
}
