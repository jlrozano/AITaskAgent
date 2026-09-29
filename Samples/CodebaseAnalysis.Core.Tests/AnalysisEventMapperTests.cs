using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.Observability.Events;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Progress;
using CodebaseAnalysis.Core.Steps;
using System.Text.Json;
using Key = CodebaseAnalysis.Core.Progress.AnalysisEventTypes.MetadataKeys;

namespace CodebaseAnalysis.Core.Tests;

public sealed class AnalysisEventMapperTests
{
    private const string RunId = "run123";
    private static readonly string Auditor = AnalysisPipelineFactory.AuditorStepName(ReportType.CodeSmells);

    private static AnalysisEventDto MapOne(AnalysisEventMapper mapper, AITaskAgent.Observability.IProgressEvent evt) =>
        mapper.Map(evt) ?? throw new Xunit.Sdk.XunitException($"{evt.EventType} no produjo evento");

    [Fact]
    public void Pipeline_events_have_no_phase_and_carry_their_counts()
    {
        var mapper = new AnalysisEventMapper(RunId);

        var started = MapOne(mapper, new PipelineStartedEvent { StepName = "CodebaseAnalysisPipeline", PipelineName = "CodebaseAnalysisPipeline", TotalSteps = 5 });
        var completed = MapOne(mapper, new PipelineCompletedEvent
        {
            StepName = "CodebaseAnalysisPipeline",
            PipelineName = "CodebaseAnalysisPipeline",
            Success = false,
            Duration = TimeSpan.FromMilliseconds(1500),
            ErrorMessage = "boom"
        });

        Assert.Equal("pipeline.started", started.Type);
        Assert.Equal(RunId, started.RunId);
        Assert.Null(started.Phase);
        Assert.Equal(5, started.Data!["totalSteps"]);
        Assert.Equal("pipeline.completed", completed.Type);
        Assert.Equal("error", completed.Level);
        Assert.Equal(false, completed.Data!["success"]);
        Assert.Equal(1500L, completed.Data["durationMs"]);
        Assert.Equal("boom", completed.Data["error"]);
    }

    [Theory]
    [InlineData(DetectAndIndexGraphStep.StepName, "detect")]
    [InlineData(LanguageEnricherStep.StepName, "enrich")]
    [InlineData(BdpEnricherStep.StepName, "enrich")]
    [InlineData(PrepareRegenerationStep.StepName, "facets")]
    [InlineData(SqlFacetExtractorStep.StepName, "facets")]
    [InlineData(AnalysisPipelineFactory.ParallelReportsStepName, "reports")]
    [InlineData(ExecutiveSynthesizerStep.StepName, "synthesis")]
    [InlineData("Auditor_ErroresYBugs", "reports")]
    [InlineData(AnalysisPipelineFactory.SemanticEnricherStepName, "enrich")]
    [InlineData(AnalysisPipelineFactory.SynthesizerStepName, "synthesis")]
    public void Phase_comes_from_the_step_name(string stepName, string phase)
    {
        var dto = MapOne(new AnalysisEventMapper(RunId), new StepStartedEvent { StepName = stepName, StepPath = "a/b/c" });

        Assert.Equal(phase, dto.Phase);
    }

    [Fact]
    public void Unknown_sources_inherit_the_phase_of_the_running_top_level_step()
    {
        var mapper = new AnalysisEventMapper(RunId);

        var before = MapOne(mapper, Log("WorkflowAndMarkdownEnricher", "info", "antes"));
        MapOne(mapper, new StepStartedEvent { StepName = DetectAndIndexGraphStep.StepName });
        var during = MapOne(mapper, Log("WorkflowAndMarkdownEnricher", "info", "durante"));
        MapOne(mapper, new StepStartedEvent { StepName = LanguageEnricherStep.StepName });
        var native = MapOne(mapper, Log("JavaAnalyzerRunner", "info", "nativo"));
        MapOne(mapper, new StepStartedEvent { StepName = BdpEnricherStep.StepName });
        var semantic = MapOne(mapper, Log("SemanticEnrichmentStep", "info", "semántico"));

        Assert.Null(before.Phase);
        Assert.Equal("detect", during.Phase);
        Assert.Equal("enrich", native.Phase);
        Assert.Equal("enrich", semantic.Phase);
    }

    [Fact]
    public void The_regeneration_step_is_top_level_and_opens_the_facets_phase_for_helpers()
    {
        var mapper = new AnalysisEventMapper(RunId);

        MapOne(mapper, new StepStartedEvent { StepName = PrepareRegenerationStep.StepName });
        var helper = MapOne(mapper, Log("KnowledgeGraphIngestor", "info", "retirando informes"));

        Assert.Equal("facets", helper.Phase);
    }

    [Fact]
    public void Step_started_retry_is_a_warning_with_the_attempt()
    {
        var mapper = new AnalysisEventMapper(RunId);

        var first = MapOne(mapper, new StepStartedEvent { StepName = Auditor, StepPath = "p", AttemptNumber = 1 });
        var retry = MapOne(mapper, new StepStartedEvent { StepName = Auditor, StepPath = "p", AttemptNumber = 2 });

        Assert.Equal("info", first.Level);
        Assert.Equal("warning", retry.Level);
        Assert.Equal(2, retry.Data!["attempt"]);
        Assert.Equal("p", retry.Data["stepPath"]);
        Assert.Equal("CodeSmells", retry.Report);
        Assert.Equal(Auditor, retry.Source);
    }

    [Fact]
    public void Step_completed_copies_llm_figures_and_never_the_result_object()
    {
        var mapper = new AnalysisEventMapper(RunId);

        var dto = MapOne(mapper, new StepCompletedEvent
        {
            StepName = Auditor,
            StepPath = "p",
            Success = true,
            Duration = TimeSpan.FromSeconds(9),
            StepResult = new FakeLlmStepResult { TokensUsed = 15000, CostUsd = 0.04m, FinishReason = FinishReason.Length, Model = "m" }
        });

        Assert.Equal("step.completed", dto.Type);
        Assert.Equal("success", dto.Level);
        Assert.Equal(15000, dto.Data!["tokens"]);
        Assert.Equal(0.04m, dto.Data["costUsd"]);
        Assert.Equal("Length", dto.Data["finishReason"]);
        Assert.Equal(9000L, dto.Data["durationMs"]);
        Assert.DoesNotContain(dto.Data.Values, v => v is FakeLlmStepResult);

        var json = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("contenido que no debe serializarse", json);
    }

    [Fact]
    public void Step_completed_with_error_is_an_error_without_llm_fields()
    {
        var dto = MapOne(new AnalysisEventMapper(RunId), new StepCompletedEvent
        {
            StepName = Auditor,
            Success = false,
            ErrorMessage = "HTTP 429"
        });

        Assert.Equal("error", dto.Level);
        Assert.Equal("HTTP 429", dto.Data!["error"]);
        Assert.False(dto.Data.ContainsKey("tokens"));
    }

    [Fact]
    public void Validation_and_timeout_events()
    {
        var mapper = new AnalysisEventMapper(RunId);

        var validation = MapOne(mapper, new StepValidationEvent { StepName = Auditor, IsValid = false, ValidationError = "vacío", AttemptNumber = 2 });
        var timeout = MapOne(mapper, new StepTimeoutEvent { StepName = Auditor, ConfiguredTimeout = TimeSpan.FromMinutes(15) });

        Assert.Equal("step.validation", validation.Type);
        Assert.Equal("warning", validation.Level);
        Assert.Equal(false, validation.Data!["isValid"]);
        Assert.Equal("vacío", validation.Data["error"]);
        Assert.Equal(2, validation.Data["attempt"]);
        Assert.Equal("step.timeout", timeout.Type);
        Assert.Equal("error", timeout.Level);
        Assert.Equal(900_000L, timeout.Data!["configuredTimeoutMs"]);
    }

    [Fact]
    public void Branch_events_take_the_report_from_the_branch_name()
    {
        var mapper = new AnalysisEventMapper(RunId);

        var started = MapOne(mapper, new BranchStartedEvent { StepName = AnalysisPipelineFactory.ParallelReportsStepName, BranchName = Auditor });
        var completed = MapOne(mapper, new BranchCompletedEvent
        {
            StepName = AnalysisPipelineFactory.ParallelReportsStepName,
            BranchName = Auditor,
            Success = true,
            Duration = TimeSpan.FromSeconds(2)
        });

        Assert.Equal("branch.started", started.Type);
        Assert.Equal("CodeSmells", started.Report);
        Assert.Equal(AnalysisPipelineFactory.ParallelReportsStepName, started.Source);
        Assert.Equal("reports", started.Phase);
        Assert.Equal(Auditor, started.Data!["branch"]);
        Assert.Equal("success", completed.Level);
        Assert.Equal(2000L, completed.Data!["durationMs"]);
    }

    [Fact]
    public void Streaming_llm_chunks_are_not_logged()
    {
        var dto = new AnalysisEventMapper(RunId).Map(new LlmResponseEvent { StepName = Auditor, Content = "delta", FinishReason = FinishReason.Streaming });

        Assert.Null(dto);
    }

    [Fact]
    public void Final_llm_response_carries_tool_calls_with_truncated_arguments_and_no_content()
    {
        var longArgs = "{\"path\":\"" + new string('x', 800) + "\"}";
        var dto = MapOne(new AnalysisEventMapper(RunId), new LlmResponseEvent
        {
            StepName = Auditor,
            Content = "texto del informe",
            FinishReason = FinishReason.ToolCalls,
            TokensUsed = 900,
            Model = "gpt",
            ToolCalls = [new ToolCall { Id = "c1", Name = "view_file", Arguments = longArgs }]
        });

        Assert.Equal("llm.response", dto.Type);
        Assert.Equal("ToolCalls", dto.Data!["finishReason"]);
        Assert.Equal(900, dto.Data["tokens"]);
        Assert.Equal("gpt", dto.Data["model"]);
        Assert.Equal(17, dto.Data["contentLength"]);
        var call = Assert.IsType<Dictionary<string, object?>>(Assert.Single((List<object?>)dto.Data["toolCalls"]!));
        Assert.Equal("view_file", call["name"]);
        Assert.Equal(AnalysisEventMapper.MaxToolArgumentsChars + 1, ((string)call["arguments"]!).Length);
        Assert.DoesNotContain("texto del informe", JsonSerializer.Serialize(dto));
    }

    [Fact]
    public void Tool_events()
    {
        var mapper = new AnalysisEventMapper(RunId);

        var started = MapOne(mapper, new ToolStartedEvent { StepName = Auditor, ToolName = "view_file" });
        var ok = MapOne(mapper, new ToolCompletedEvent { StepName = Auditor, ToolName = "view_file", Success = true, Duration = TimeSpan.FromMilliseconds(120) });
        var failed = MapOne(mapper, new ToolCompletedEvent { StepName = Auditor, ToolName = "graph_search", Success = false, ErrorMessage = "timeout" });

        Assert.Equal("tool.started", started.Type);
        Assert.Equal("view_file", started.Data!["tool"]);
        Assert.Equal("CodeSmells", started.Report);
        Assert.Equal("view_file (120 ms)", ok.Message);
        Assert.Equal(120L, ok.Data!["durationMs"]);
        Assert.Equal(true, ok.Data["success"]);
        Assert.Equal("warning", failed.Level);
        Assert.Equal("timeout", failed.Data!["error"]);
    }

    [Fact]
    public void Log_events_keep_level_channel_and_text()
    {
        var dto = MapOne(new AnalysisEventMapper(RunId),
            Log(DetectAndIndexGraphStep.StepName, "warning", "   [indexador] línea", AnalysisEventTypes.Channels.Indexer));

        Assert.Equal("log", dto.Type);
        Assert.Equal("warning", dto.Level);
        Assert.Equal("   [indexador] línea", dto.Message);
        Assert.Equal("indexer", dto.Data!["channel"]);
        Assert.Equal("detect", dto.Phase);
    }

    [Fact]
    public void Report_written_events()
    {
        var dto = MapOne(new AnalysisEventMapper(RunId), new StepProgressEvent
        {
            StepName = Auditor,
            EventType = AnalysisEventTypes.ReportWritten,
            Message = "02_code_smells.md [Red]",
            Metadata = new()
            {
                [Key.FileName] = "02_code_smells.md",
                [Key.FilePath] = "C:/r/02_code_smells.md",
                [Key.ReportType] = "CodeSmells",
                [Key.Health] = "Red",
                [Key.CriticalFindings] = 3,
                [Key.Truncated] = true
            }
        });

        Assert.Equal("report.written", dto.Type);
        Assert.Equal("warning", dto.Level);
        Assert.Equal("CodeSmells", dto.Report);
        Assert.Equal("02_code_smells.md", dto.Data!["fileName"]);
        Assert.Equal("Red", dto.Data["health"]);
        Assert.Equal(3, dto.Data["criticalFindings"]);
        Assert.Equal(true, dto.Data["truncated"]);
    }

    [Fact]
    public void Executive_report_written_has_no_report_type()
    {
        var dto = MapOne(new AnalysisEventMapper(RunId), new StepProgressEvent
        {
            StepName = ExecutiveSynthesizerStep.StepName,
            EventType = AnalysisEventTypes.ReportWritten,
            Message = "00",
            Metadata = new() { [Key.FileName] = "00_resumen_ejecutivo.md", [Key.ReportType] = null, [Key.Health] = "Amber", [Key.Truncated] = false }
        });

        Assert.Null(dto.Report);
        Assert.Null(dto.Data!["reportType"]);
        Assert.Null(dto.Data["criticalFindings"]);
        Assert.Equal("synthesis", dto.Phase);
        Assert.Equal("success", dto.Level);
    }

    private static StepProgressEvent Log(string source, string level, string message, string channel = AnalysisEventTypes.Channels.Pipeline) => new()
    {
        StepName = source,
        EventType = AnalysisEventTypes.Log,
        Message = message,
        Metadata = new() { [Key.Level] = level, [Key.Channel] = channel }
    };
}
