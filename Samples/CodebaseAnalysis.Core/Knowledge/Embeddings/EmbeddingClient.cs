using AITaskAgent.LLM.Configuration;
using CodebaseAnalysis.Core.Hosting;
using CodebaseAnalysis.Core.Reports;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Knowledge.Embeddings;

/// <summary>Sección <c>CodebaseAnalysis:Embeddings</c> (plan v2 §7.6, §8.2).</summary>
public sealed record EmbeddingOptions
{
    public const string SectionName = "CodebaseAnalysis:Embeddings";

    public bool Enabled { get; init; } = true;

    /// <summary>Nombre del proveedor en <c>LlmConfiguration:Providers</c>; por defecto, <c>LlmConfiguration:DefaultProvider</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>Despliegue en Azure o modelo en OpenAI.</summary>
    public string Model { get; init; } = "text-embedding-3-small";

    /// <summary>Dimensiones pedidas; null usa las del modelo (1536 en <c>text-embedding-3-small</c>).</summary>
    public int? Dimensions { get; init; }

    public int BatchSize { get; init; } = 32;
    public int MaxChunkChars { get; init; } = 1800;
    public int ChunkOverlapChars { get; init; } = 200;

    /// <summary>Dimensiones con las que se comparan las fuentes indexadas (las pedidas o las por defecto del modelo).</summary>
    public int EffectiveDimensions => Dimensions ?? 1536;

    public static EmbeddingOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new EmbeddingOptions();
        return new EmbeddingOptions
        {
            Enabled = section.GetValue<bool?>(nameof(Enabled)) ?? defaults.Enabled,
            Provider = section[nameof(Provider)] is { Length: > 0 } provider ? provider : null,
            Model = section[nameof(Model)] is { Length: > 0 } model ? model : defaults.Model,
            Dimensions = section.GetValue<int?>(nameof(Dimensions)),
            BatchSize = Math.Clamp(section.GetValue<int?>(nameof(BatchSize)) ?? defaults.BatchSize, 1, 256),
            MaxChunkChars = Math.Max(200, section.GetValue<int?>(nameof(MaxChunkChars)) ?? defaults.MaxChunkChars),
            ChunkOverlapChars = Math.Max(0, section.GetValue<int?>(nameof(ChunkOverlapChars)) ?? defaults.ChunkOverlapChars)
        };
    }
}

/// <summary>Cliente de embeddings: un vector por texto, en el mismo orden.</summary>
public interface IEmbeddingClient
{
    string Model { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}

/// <summary>Dónde y con qué clave se piden los embeddings.</summary>
public sealed record EmbeddingEndpoint(string ServiceType, string BaseUrl, string ApiKey, string? ApiVersion);

/// <summary>
/// Lo que el chat y la web necesitan de los embeddings: la configuración y, si están disponibles, el cliente; si no,
/// el motivo para el usuario.
/// </summary>
public sealed record EmbeddingsRuntime(EmbeddingOptions Options, IEmbeddingClient? Client, string? DisabledReason)
{
    public bool Enabled => Client != null;

    public KnowledgeEmbeddingService For(KnowledgePaths paths) => new(paths, Options, Client);

    /// <summary>
    /// Resuelve el proveedor con las mismas reglas de clave que <see cref="LlmRoleFactory.BuildProviders"/>. Sin
    /// <c>Enabled</c>, sin proveedor o sin clave, queda desactivado con el motivo.
    /// </summary>
    public static EmbeddingsRuntime FromConfiguration(IConfiguration configuration, Func<string, string?> getEnvVar, HttpClient? httpClient = null)
    {
        var options = EmbeddingOptions.FromConfiguration(configuration);
        if (!options.Enabled)
            return new EmbeddingsRuntime(options, null, "Los embeddings están desactivados en la configuración (CodebaseAnalysis:Embeddings:Enabled).");

        var providerName = options.Provider ?? configuration["LlmConfiguration:DefaultProvider"];
        var section = providerName == null ? null : configuration.GetSection($"LlmConfiguration:Providers:{providerName}");
        if (section == null || !section.Exists())
            return new EmbeddingsRuntime(options, null, $"No hay proveedor '{providerName}' en LlmConfiguration:Providers para los embeddings.");

        var endpoint = LlmRoleFactory.ResolveEndpoint(configuration, section, getEnvVar);
        if (endpoint.ServiceType is not (LlmServiceTypes.AzureOpenAI or "openai" or "openrouter"))
            return new EmbeddingsRuntime(options, null, $"El proveedor '{providerName}' ({endpoint.ServiceType}) no admite embeddings en este sample.");
        if (string.IsNullOrEmpty(endpoint.ApiKey) || string.IsNullOrEmpty(endpoint.BaseUrl))
            return new EmbeddingsRuntime(options, null, $"El proveedor '{providerName}' no tiene clave o URL: los embeddings quedan desactivados.");

        var client = new OpenAICompatibleEmbeddingClient(httpClient ?? new HttpClient(),
            new EmbeddingEndpoint(endpoint.ServiceType, endpoint.BaseUrl, endpoint.ApiKey, endpoint.ApiVersion ?? "2024-08-01-preview"), options);
        return new EmbeddingsRuntime(options, client, null);
    }
}

/// <summary>
/// Cliente HTTP de embeddings (plan v2 §8.2.2). Azure: <c>POST {BaseUrl}/openai/deployments/{Model}/embeddings?api-version=…</c>
/// con <c>api-key</c>; OpenAI y OpenRouter: <c>POST {BaseUrl}/embeddings</c> con <c>Authorization: Bearer</c> y
/// <c>model</c> en el cuerpo. Lotes de <see cref="EmbeddingOptions.BatchSize"/>, 60 s por lote y tres intentos con
/// espera exponencial en 429 y 5xx.
/// </summary>
public sealed class OpenAICompatibleEmbeddingClient(HttpClient http, EmbeddingEndpoint endpoint, EmbeddingOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null) : IEmbeddingClient
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public string Model => options.Model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var result = new List<float[]>(inputs.Count);
        foreach (var batch in inputs.Chunk(options.BatchSize))
            result.AddRange(await EmbedBatchAsync(batch, cancellationToken));
        return result;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(string[] batch, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(BatchTimeout);
            using var request = BuildRequest(batch);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt < MaxAttempts)
                {
                    await _delay(Backoff(attempt), ct);
                    continue;
                }
                throw new KnowledgeOperationException(KnowledgeErrorCodes.EmbeddingProviderError, $"El proveedor de embeddings no respondió: {ex.Message}");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct)
                        ?? throw new KnowledgeOperationException(KnowledgeErrorCodes.EmbeddingProviderError, "Respuesta de embeddings vacía.");
                    var vectors = body.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
                    if (vectors.Count != batch.Length)
                        throw new KnowledgeOperationException(KnowledgeErrorCodes.EmbeddingProviderError, $"Se pidieron {batch.Length} vectores y llegaron {vectors.Count}.");
                    return vectors;
                }

                var retriable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (retriable && attempt < MaxAttempts)
                {
                    await _delay(response.Headers.RetryAfter?.Delta ?? Backoff(attempt), ct);
                    continue;
                }

                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new KnowledgeOperationException(KnowledgeErrorCodes.EmbeddingProviderError,
                    $"El proveedor de embeddings respondió {(int)response.StatusCode}: {(detail.Length > 300 ? detail[..300] : detail)}");
            }
        }
    }

    private HttpRequestMessage BuildRequest(string[] batch)
    {
        var baseUrl = endpoint.BaseUrl.TrimEnd('/');
        var azure = endpoint.ServiceType == LlmServiceTypes.AzureOpenAI;
        var url = azure
            ? $"{baseUrl}/openai/deployments/{Uri.EscapeDataString(options.Model)}/embeddings?api-version={Uri.EscapeDataString(endpoint.ApiVersion ?? "2024-08-01-preview")}"
            : $"{baseUrl}/embeddings";

        var body = new Dictionary<string, object?> { ["input"] = batch };
        if (!azure)
            body["model"] = options.Model;
        if (options.Dimensions is int dimensions)
            body["dimensions"] = dimensions;

        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (azure)
            request.Headers.Add("api-key", endpoint.ApiKey);
        else
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        return request;
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingItem> Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}

internal static class EmbeddingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
