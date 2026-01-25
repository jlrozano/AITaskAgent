using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using FileInfo = Google.GenAI.Types.File;

namespace GeminiLlmService;

/// <summary>
/// Manager for Gemini Files API operations.
/// Allows uploading large files (up to 2GB) for reuse across multiple requests.
/// </summary>
/// <param name="client">Gemini client instance</param>
/// <param name="logger">Logger instance</param>
public sealed class GeminiFileManager(Client client, ILogger logger)
{
    private readonly Client _client = client;
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Uploads a file from a local path.
    /// </summary>
    /// <param name="filePath">Path to the local file</param>
    /// <param name="displayName">Optional display name (defaults to filename)</param>
    /// <returns>Uploaded file information with URI for use in requests</returns>
    public async Task<FileInfo> UploadAsync(
        string filePath,
        string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!System.IO.File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        var fileName = Path.GetFileName(filePath);
        displayName ??= fileName;

        _logger.LogInformation(
            "Uploading file '{DisplayName}' from {FilePath}",
            displayName, filePath);

        var response = await _client.Files.UploadAsync(
            filePath: filePath,
            config: new UploadFileConfig { DisplayName = displayName });

        _logger.LogInformation(
            "Uploaded file '{Name}' - URI: {Uri}, Size: {Size} bytes",
            response.Name, response.Uri, response.SizeBytes);

        return response;
    }

    /// <summary>
    /// Uploads a file from byte array.
    /// </summary>
    /// <param name="bytes">File content as bytes</param>
    /// <param name="fileName">File name including extension</param>
    /// <param name="displayName">Optional display name</param>
    /// <returns>Uploaded file information</returns>
    public async Task<FileInfo> UploadAsync(
        byte[] bytes,
        string fileName,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        displayName ??= fileName;

        _logger.LogInformation(
            "Uploading file '{DisplayName}' from bytes ({Size} bytes)",
            displayName, bytes.Length);

        var response = await _client.Files.UploadAsync(
            bytes: bytes,
            fileName: fileName,
            config: new UploadFileConfig { DisplayName = displayName });

        _logger.LogInformation(
            "Uploaded file '{Name}' - URI: {Uri}",
            response.Name, response.Uri);

        return response;
    }

    /// <summary>
    /// Uploads a file from a stream.
    /// </summary>
    /// <param name="stream">File content stream</param>
    /// <param name="fileName">File name including extension</param>
    /// <param name="displayName">Optional display name</param>
    /// <returns>Uploaded file information</returns>
    public async Task<FileInfo> UploadAsync(
        Stream stream,
        string fileName,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        var bytes = memoryStream.ToArray();

        return await UploadAsync(bytes, fileName, displayName);
    }

    /// <summary>
    /// Gets information about an uploaded file.
    /// </summary>
    /// <param name="fileName">File name (e.g., "files/abc123")</param>
    /// <returns>File information</returns>
    public async Task<FileInfo> GetAsync(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _logger.LogDebug("Getting file info: {FileName}", fileName);

        return await _client.Files.GetAsync(name: fileName);
    }

    /// <summary>
    /// Deletes an uploaded file.
    /// </summary>
    /// <param name="fileName">File name (e.g., "files/abc123")</param>
    public async Task DeleteAsync(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _logger.LogInformation("Deleting file: {FileName}", fileName);

        await _client.Files.DeleteAsync(name: fileName);
    }

    /// <summary>
    /// Lists all uploaded files.
    /// </summary>
    /// <returns>Async enumerable of files</returns>
    public async IAsyncEnumerable<FileInfo> ListAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing uploaded files");

        var pager = await _client.Files.ListAsync();

        await foreach (var file in pager.WithCancellation(cancellationToken))
        {
            yield return file;
        }
    }

    /// <summary>
    /// Waits for a file to be fully processed and ready for use.
    /// Some files (e.g., videos) may require processing time after upload.
    /// </summary>
    /// <param name="fileName">File name</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="pollInterval">Interval between status checks</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Processed file information</returns>
    public async Task<FileInfo> WaitForProcessingAsync(
        string fileName,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var actualTimeout = timeout ?? TimeSpan.FromMinutes(10);
        var actualPollInterval = pollInterval ?? TimeSpan.FromSeconds(5);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(actualTimeout);

        _logger.LogInformation(
            "Waiting for file '{FileName}' to be processed (timeout: {Timeout}s)",
            fileName, actualTimeout.TotalSeconds);

        while (!cts.Token.IsCancellationRequested)
        {
            var file = await GetAsync(fileName);

            if (file.State == FileState.ACTIVE)
            {
                _logger.LogInformation("File '{FileName}' is ready", fileName);
                return file;
            }

            if (file.State == FileState.FAILED)
            {
                throw new InvalidOperationException(
                    $"File processing failed for '{fileName}': {file.Error?.Message}");
            }

            _logger.LogDebug(
                "File '{FileName}' state: {State}, waiting {Interval}s",
                fileName, file.State, actualPollInterval.TotalSeconds);

            await Task.Delay(actualPollInterval, cts.Token);
        }

        throw new TimeoutException(
            $"Timeout waiting for file '{fileName}' to be processed");
    }

    /// <summary>
    /// Uploads multiple files in parallel.
    /// </summary>
    /// <param name="filePaths">Paths to local files</param>
    /// <param name="maxParallelism">Maximum concurrent uploads</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of uploaded file information</returns>
    public async Task<List<FileInfo>> UploadManyAsync(
        IEnumerable<string> filePaths,
        int maxParallelism = 4,
        CancellationToken cancellationToken = default)
    {
        var paths = filePaths.ToList();

        _logger.LogInformation(
            "Uploading {Count} files with parallelism {Parallelism}",
            paths.Count, maxParallelism);

        using var semaphore = new SemaphoreSlim(maxParallelism);
        var tasks = paths.Select(async path =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await UploadAsync(path);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return [.. results];
    }
}
