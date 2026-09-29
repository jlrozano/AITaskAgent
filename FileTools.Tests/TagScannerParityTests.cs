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
using AITaskAgent.Observability;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// Tags used to go through two different implementations - a regex over the whole text in batch, a
/// character state machine in streaming - with different attribute grammars, different error
/// handling (batch inserted a placeholder, streaming threw and killed the turn) and no recovery for
/// a tag left open. Both transports now run the same scanner over the same text, reasoning included,
/// so the observable outcome must be identical: same file, same content, same placeholders.
/// </summary>
[Collection(FileToolsConfigurationCollection.Name)]
public sealed class TagScannerParityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("TagScannerParityTests").FullName;
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Replays one canned response either as a single batch result or as a stream cut into deltas
    /// of <paramref name="chunkSize"/> characters, reasoning first and then content, exactly as a
    /// reasoning model delivers it.
    /// </summary>
    /// <param name="reasoning">Reasoning channel text; may be null.</param>
    /// <param name="content">Content channel text.</param>
    /// <param name="chunkSize">Delta size used when streaming.</param>
    private sealed class ReplayService(string? reasoning, string content, int chunkSize) : ILlmService
    {
        public string Name => "replay-stub";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(LlmResult.Success(
                [new LlmResponse { Content = content, Reasoning = reasoning, FinishReason = FinishReason.Stop }]));

        public async IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            foreach (var slice in Slice(reasoning ?? string.Empty))
            {
                yield return new LlmStreamChunk { Delta = slice, IsThinking = true };
            }

            foreach (var slice in Slice(content))
            {
                yield return new LlmStreamChunk { Delta = slice, IsThinking = false };
            }

            yield return new LlmStreamChunk { Delta = string.Empty, IsComplete = true, FinishReason = FinishReason.Stop };
        }

        private IEnumerable<string> Slice(string text)
        {
            for (var i = 0; i < text.Length; i += chunkSize)
            {
                yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
            }
        }

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 8192;
    }

    private static IStep BuildStep(ILlmService service, bool useStreaming, string dir)
    {
        // WriteFileTagHandler resolves relative tag paths against FileToolsConfiguration's current
        // root - it no longer takes a base directory of its own - so each call point at a
        // different sandbox must switch the shared static root before building its step.
        FileToolsConfiguration.RootDirectory = dir;

        return new StatelessTemplateLlmStep<LlmStringStepResult, LlmStringStepResult>(
            "FileAgent",
            new LlmRole
            {
                Provider = new LlmProvider { Service = service },
                Model = "test-model",
                Inference = new LlmInferenceSettings { UseStreaming = useStreaming }
            },
            templateProvider: null,
            promtTemplateString: "{{Input}}")
        {
            TagHandlers = [new WriteFileTagHandler(NullLogger<WriteFileTagHandler>.Instance)]
        };
    }

    /// <summary>Runs the step and collects every event it emitted.</summary>
    /// <param name="step">Step under test.</param>
    private static async Task<(IStepResult Result, List<IProgressEvent> Events)> RunAsync(IStep step)
    {
        var events = new List<IProgressEvent>();
        var channel = new EventChannel();
        var subscription = channel.Subscribe((ev, _) => { lock (events) events.Add(ev); return Task.CompletedTask; });

        var result = await step.ExecuteAsync(
            new LlmStringStepResult(new EmptyStep("Seed")) { Content = "crea un archivo" },
            new PipelineContext(channel), attempt: 1, lastStepResult: null, CancellationToken.None);

        // Disposing drains the subscriber loop, so every buffered event has been delivered.
        subscription.Dispose();
        await channel.DisposeAsync();

        lock (events) return (result, [.. events]);
    }

    private static string Answer(IStepResult result) => (result as LlmStringStepResult)?.Content ?? string.Empty;

    private const string Reasoning = """
        Let's write the file first, then explain.
        <write_file path="notes/plan.v2.md" summary="Plan de trabajo">
        # Plan
        paso 1
        </write_file>
        Done thinking.
        """;

    private const string Content = """
        Aquí va el segundo archivo:
        <write_file path="src/main.cs" summary="Programa principal">
        Console.WriteLine("hola");
        </write_file>
        He creado ambos archivos.
        """;

    [Fact]
    public async Task Batch_and_streaming_produce_the_same_files_content_and_references()
    {
        var batchDir = Path.Combine(_dir, "batch");
        var streamDir = Path.Combine(_dir, "stream");

        var (batch, _) = await RunAsync(BuildStep(new ReplayService(Reasoning, Content, chunkSize: 7), useStreaming: false, batchDir));
        var (stream, _) = await RunAsync(BuildStep(new ReplayService(Reasoning, Content, chunkSize: 7), useStreaming: true, streamDir));

        foreach (var relative in new[] { "notes/plan.v2.md", "src/main.cs" })
        {
            var fromBatch = await File.ReadAllTextAsync(Path.Combine(batchDir, relative));
            var fromStream = await File.ReadAllTextAsync(Path.Combine(streamDir, relative));
            Assert.Equal(fromBatch, fromStream);
        }

        // The line breaks around the body belong to the tag syntax, not to the file.
        Assert.Equal("# Plan\npaso 1", await File.ReadAllTextAsync(Path.Combine(batchDir, "notes/plan.v2.md")));
        Assert.Equal("Console.WriteLine(\"hola\");", await File.ReadAllTextAsync(Path.Combine(batchDir, "src/main.cs")));

        var batchAnswer = Answer(batch);
        var streamAnswer = Answer(stream);
        Assert.Equal(batchAnswer, streamAnswer);

        // Tag bodies never reach the answer; the reference of the reasoning tag does, so the
        // history records the action even though the reasoning itself is not persisted.
        Assert.Contains("[write_file: src/main.cs", batchAnswer);
        Assert.Contains("[write_file: notes/plan.v2.md", batchAnswer);
        Assert.DoesNotContain("Console.WriteLine", batchAnswer);
        Assert.DoesNotContain("paso 1", batchAnswer);
        Assert.DoesNotContain("<write_file", batchAnswer);
        Assert.DoesNotContain("Let's write the file first", batchAnswer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(11)]
    public async Task Streaming_survives_a_tag_split_anywhere_including_inside_the_path_and_the_closing_tag(int chunkSize)
    {
        var (result, _) = await RunAsync(BuildStep(new ReplayService(null, Content, chunkSize), useStreaming: true, _dir));

        var written = Path.Combine(_dir, "src/main.cs");
        Assert.True(File.Exists(written), $"chunk size {chunkSize} lost the tag");
        Assert.Equal("Console.WriteLine(\"hola\");", await File.ReadAllTextAsync(written));
        Assert.DoesNotContain("<write_file", Answer(result));
        Assert.DoesNotContain("</write_file", Answer(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tag_left_open_comes_back_as_plain_text_and_creates_no_file(bool useStreaming)
    {
        const string unclosed = """
            Voy a escribir:
            <write_file path="never.md" summary="nunca">
            contenido que no termina
            """;

        var (result, events) = await RunAsync(BuildStep(new ReplayService(null, unclosed, chunkSize: 5), useStreaming, _dir));

        Assert.False(File.Exists(Path.Combine(_dir, "never.md")),
            "a handler must only run on a complete tag - opening one must not create anything");
        Assert.Contains("<write_file path=\"never.md\"", Answer(result));
        Assert.Contains("contenido que no termina", Answer(result));

        // The opening tag is announced as soon as it is recognised (so a UI can show the action in
        // progress); an unclosed one must then be closed with a failed completion, never left open.
        Assert.Contains(events, e => e is TagStartedEvent { TagName: "write_file" });
        Assert.Contains(events, e => e is TagCompletedEvent { TagName: "write_file", Success: false });

        // Nothing was opened, so the temp directory is not held by a dangling FileStream.
        Directory.Delete(_dir, recursive: true);
        Directory.CreateDirectory(_dir);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_handler_failure_leaves_an_error_placeholder_and_the_turn_survives(bool useStreaming)
    {
        const string noPath = """
            Intento:
            <write_file summary="sin ruta">sin destino</write_file>
            Sigo aquí.
            """;

        var (result, events) = await RunAsync(BuildStep(new ReplayService(null, noPath, chunkSize: 4), useStreaming, _dir));

        Assert.False(result.HasError, "a bad tag is the model's mistake, not a step failure");
        var answer = Answer(result);
        Assert.Contains("[Error processing tag write_file: write_file tag requires 'path' attribute]", answer);
        Assert.Contains("Intento:", answer);
        Assert.Contains("Sigo aquí.", answer);
        Assert.Contains(events, e => e is TagCompletedEvent { Success: false });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Other_markup_inside_the_body_belongs_to_the_file(bool useStreaming)
    {
        const string html = """
            <write_file path="index.html" summary="Página">
            <div class="x">a < b</div>
            </write_file>
            Listo.
            """;

        await RunAsync(BuildStep(new ReplayService(null, html, chunkSize: 3), useStreaming, _dir));

        Assert.Equal("<div class=\"x\">a < b</div>", await File.ReadAllTextAsync(Path.Combine(_dir, "index.html")));
    }
}
