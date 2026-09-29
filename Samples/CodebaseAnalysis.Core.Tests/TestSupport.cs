using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using CodebaseAnalysis.Core.Progress;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Tests que escriben o capturan la consola (o ejecutan código que puede escribir en ella) no corren
/// en paralelo entre sí: <see cref="Console.Out"/> es global al proceso.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}

internal static class TestConfig
{
    public static IConfiguration FromArgs(params string[] args) =>
        new ConfigurationBuilder().AddCommandLine(args).Build();

    public static IConfiguration FromPairs(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
}

/// <summary>Carpeta temporal propia de cada test; se borra al terminar.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "cba-core-tests", Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Un fichero aún abierto no debe hacer fallar el test; queda en %TEMP%.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class CapturingReporter : IAnalysisReporter
{
    private readonly Lock _lock = new();
    private readonly List<(AnalysisLogLevel Level, string Source, string Message)> _entries = [];

    public IReadOnlyList<(AnalysisLogLevel Level, string Source, string Message)> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public void Report(AnalysisLogLevel level, string source, string message)
    {
        lock (_lock)
        {
            _entries.Add((level, source, message));
        }
    }
}

/// <summary>
/// <see cref="ILlmService"/> sin red que siempre responde con el mismo texto y cuenta las llamadas.
/// </summary>
internal sealed class CannedLlmService(string content) : ILlmService
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public string Name => "canned";

    public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(LlmResult.Success(
            [new LlmResponse { Content = content, FinishReason = FinishReason.Stop }],
            tokensUsed: 10, promptTokens: 5, completionTokens: 5, model: "fake-model"));
    }

    public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Los roles de estos tests no hacen streaming.");

    public int EstimateTokenCount(string text) => text.Length / 4;

    public int GetMaxContextTokens(string? model = null) => 1_000_000;
}

/// <summary>
/// Quita de la línea <c>analysis-meta</c> las claves de procedencia que añade el pipeline (<c>ReportProvenance</c>), para
/// comparar un informe escrito con la respuesta del modelo falso.
/// </summary>
internal static class ProvenanceKeys
{
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"; (generated_at|model|provider|response_model|system_sha|prompt_sha|graph|enrichment|app|fallback)=[^;]*?(?=; | -->)");

    public static string Strip(string markdown) => Pattern.Replace(markdown, string.Empty);
}
