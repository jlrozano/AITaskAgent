using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using AITaskAgent.FileTools.Streaming;
using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// A real FileToolsTestApp session answered with a "&lt;write_file&gt;" tag and no file was ever
/// written - just the tag text printed verbatim. WriteFileTagHandlerTests only exercises the
/// handler directly with hand-built attribute dictionaries; nothing exercised the path a real LLM
/// response actually takes to reach it. That path hid two independent, unrelated bugs, both fixed:
///
/// 1. Non-streaming tag processing lived only in BaseLlmStep.FinalizeAsync, which only
///    RetryMiddleware ever called - a step invoked directly (bypassing IPipeline, as
///    FileToolsTestApp and several other samples do) skipped it entirely. It now runs inline in
///    InvokeLlmAsync, mirroring the streaming path, so it no longer depends on the middleware
///    chain at all.
/// 2. StreamingTagParser's tag-opening character whitelist allowed letters, digits, whitespace,
///    '_', '=' and '"' but not '.' - any attribute value containing a dot, i.e. essentially every
///    real file name, made the parser decide mid-attribute that this was never a tag. Fixed by
///    treating everything inside a quoted attribute value as opaque.
/// </summary>
[Collection(FileToolsConfigurationCollection.Name)]
public sealed class WriteFileTagIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("WriteFileTagIntegrationTests").FullName;
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;

    public WriteFileTagIntegrationTests() => FileToolsConfiguration.RootDirectory = _dir;

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Answers every call with the same canned reply.</summary>
    /// <param name="content">The reply text.</param>
    /// <param name="asReasoning">
    /// Emit the reply through the reasoning channel with Content empty, which is what a reasoning
    /// model does whenever it also returns tool calls. The tag has to run from there too - see
    /// AITaskAgent.Tests.ReasoningChannelTagTests for the full account.
    /// </param>
    private sealed class CannedService(string content, bool asReasoning = false) : ILlmService
    {
        public string Name => "canned-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(LlmResult.Success(
                [asReasoning
                    ? new LlmResponse { Content = "", Reasoning = content, FinishReason = FinishReason.Stop }
                    : new LlmResponse { Content = content, FinishReason = FinishReason.Stop }]));

        public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new LlmStreamChunk
            {
                Delta = content,
                IsThinking = asReasoning,
                IsComplete = true,
                FinishReason = FinishReason.Stop
            };
        }

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static IStep BuildStep(
        string reply, bool useStreaming, string tempDir, bool asReasoning = false) => new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
        "FileAgent",
        new LlmRole
        {
            Provider = new LlmProvider { Service = new CannedService(reply, asReasoning) },
            Model = "test-model",
            Inference = new LlmInferenceSettings { UseStreaming = useStreaming }
        },
        templateProvider: null,
        promtTemplateString: "{{Input}}")
    {
        TagHandlers = [new WriteFileTagHandler(NullLogger<WriteFileTagHandler>.Instance)]
    };

    private static Task<IStepResult> RunAsync(IStep step) =>
        step.ExecuteAsync(
            new LlmStringStepResult(new EmptyStep("Seed")) { Content = "crea un archivo" },
            new PipelineContext(), attempt: 1, lastStepResult: null, CancellationToken.None);

    [Fact]
    public async Task Non_streaming_tag_is_processed_without_going_through_the_pipeline_middleware_chain()
    {
        const string reply = """
            <write_file path="analisis.md">contenido real</write_file>
            Listo.
            """;

        var result = await RunAsync(BuildStep(reply, useStreaming: false, _dir));

        Assert.True(File.Exists(Path.Combine(_dir, "analisis.md")),
            "step.ExecuteAsync was called directly (no IPipeline, no RetryMiddleware) - tag " +
            "processing must not depend on FinalizeAsync to run.");
        Assert.Equal("contenido real", await File.ReadAllTextAsync(Path.Combine(_dir, "analisis.md")));

        var visibleAnswer = (result as LlmStringStepResult)?.Content ?? string.Empty;
        Assert.DoesNotContain("<write_file", visibleAnswer);
    }

    [Fact]
    public async Task Non_streaming_tag_wrapped_in_an_unclosed_tool_call_block_still_writes_the_file()
    {
        const string reply = """
            <tool_call>
            <write_file path="analisis.md">
            # Analisis
            contenido real del archivo
            </write_file>
            He creado el archivo `analisis.md`.
            """;

        await RunAsync(BuildStep(reply, useStreaming: false, _dir));

        Assert.True(File.Exists(Path.Combine(_dir, "analisis.md")));
    }

    [Fact]
    public async Task Streaming_tag_parser_accepts_a_path_attribute_containing_a_dot()
    {
        const string reply = """
            <write_file path="analisis.md">contenido real</write_file>
            Listo.
            """;

        await RunAsync(BuildStep(reply, useStreaming: true, _dir));

        Assert.True(File.Exists(Path.Combine(_dir, "analisis.md")),
            "a dot in the path attribute (any real file extension) used to derail " +
            "StreamingTagParser's character-by-character tag detection.");
    }

    [Fact]
    public async Task Streaming_tag_parser_still_writes_the_file_when_the_path_has_no_dot()
    {
        const string reply = """
            <write_file path="analisisSinPunto">contenido real</write_file>
            Listo.
            """;

        await RunAsync(BuildStep(reply, useStreaming: true, _dir));

        Assert.True(File.Exists(Path.Combine(_dir, "analisisSinPunto")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tag_arriving_through_the_reasoning_channel_writes_the_file(bool useStreaming)
    {
        const string reply = """
            Now we need to output the analysis using the write_file tag.
            <write_file path="analisis.md" summary="Analysis of the project">contenido real</write_file>
            Then we explain what we did.
            """;

        await RunAsync(BuildStep(reply, useStreaming, _dir, asReasoning: true));

        var written = Path.Combine(_dir, "analisis.md");
        Assert.True(File.Exists(written),
            "a reasoning model puts its whole answer - tag included - in the reasoning channel "
            + "whenever it also emits tool calls; the write must not depend on which channel it used.");
        Assert.Equal("contenido real", await File.ReadAllTextAsync(written));
    }

    [Fact]
    public async Task Reference_left_in_place_of_the_tag_states_the_path_and_size()
    {
        const string reply = """
            <write_file path="analisis.md" summary="Analysis of the project">contenido real</write_file>
            """;

        var result = await RunAsync(BuildStep(reply, useStreaming: false, _dir));
        var answer = (result as LlmStringStepResult)?.Content ?? string.Empty;

        // The summary alone ("Analysis of the project") never said a file had been written, so a
        // model re-reading its own history could not tell the action had completed.
        Assert.Contains("write_file: analisis.md", answer);
        Assert.Contains("14 bytes", answer);
        Assert.Contains("Analysis of the project", answer);
        Assert.DoesNotContain("contenido real", answer);
    }
}
