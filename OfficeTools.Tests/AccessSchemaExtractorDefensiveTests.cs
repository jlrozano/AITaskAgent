using OfficeTools.Extractors;

namespace OfficeTools.Tests;

public class AccessSchemaExtractorDefensiveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _corruptFilePath;

    public AccessSchemaExtractorDefensiveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AccessDefensiveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _corruptFilePath = Path.Combine(_tempDir, "corrupted_database.accdb");
        File.WriteAllText(_corruptFilePath, "NOT AN ACCESS DATABASE FILE CONTENT - INVALID HEADER");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void CanConnect_NonExistentFile_ReturnsFalseWithoutThrowing()
    {
        var extractor = new AccessSchemaExtractor();
        string nonExistentPath = Path.Combine(_tempDir, "does_not_exist.accdb");

        bool canConnect = extractor.CanConnect(nonExistentPath, null, out string? errorMessage);

        Assert.False(canConnect);
        Assert.NotNull(errorMessage);
        Assert.Contains("does not exist", errorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CanConnect_NullOrWhitespaceFile_ReturnsFalseWithoutThrowing(string? filePath)
    {
        var extractor = new AccessSchemaExtractor();

        bool canConnect = extractor.CanConnect(filePath!, null, out string? errorMessage);

        Assert.False(canConnect);
        Assert.NotNull(errorMessage);
    }

    [Fact]
    public void CanConnect_InvalidFormatFile_ReturnsFalseWithoutThrowing()
    {
        var extractor = new AccessSchemaExtractor();

        bool canConnect = extractor.CanConnect(_corruptFilePath, null, out string? errorMessage);

        Assert.False(canConnect);
        Assert.NotNull(errorMessage);
    }

    [Fact]
    public void ExtractSchema_NonExistentFile_ThrowsCleanFileNotFoundException()
    {
        var extractor = new AccessSchemaExtractor();
        string nonExistentPath = Path.Combine(_tempDir, "non_existent.accdb");

        var ex = Assert.Throws<FileNotFoundException>(() => extractor.ExtractSchema(nonExistentPath));
        Assert.Contains("non_existent.accdb", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ExtractSchema_NullOrWhitespaceFile_ThrowsCleanArgumentException(string? filePath)
    {
        var extractor = new AccessSchemaExtractor();

        Assert.Throws<ArgumentException>(() => extractor.ExtractSchema(filePath!));
    }

    [Fact]
    public void ExtractSchema_InvalidFormatFile_ThrowsMeaningfulException()
    {
        var extractor = new AccessSchemaExtractor();

        // Should throw either InvalidOperationException (provider failure / corrupt format) or PlatformNotSupportedException cleanly
        var ex = Record.Exception(() => extractor.ExtractSchema(_corruptFilePath));

        Assert.NotNull(ex);
        Assert.True(
            ex is InvalidOperationException || ex is PlatformNotSupportedException,
            $"Expected InvalidOperationException or PlatformNotSupportedException, but got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task ExtractSchemaAsync_NonExistentFile_ThrowsFileNotFoundException()
    {
        var extractor = new AccessSchemaExtractor();
        string nonExistentPath = Path.Combine(_tempDir, "async_non_existent.accdb");

        await Assert.ThrowsAsync<FileNotFoundException>(() => extractor.ExtractSchemaAsync(nonExistentPath));
    }

    [Fact]
    public async Task ExtractSchemaAsync_CanceledToken_ThrowsOperationCanceledException()
    {
        var extractor = new AccessSchemaExtractor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractSchemaAsync(_corruptFilePath, cancellationToken: cts.Token));
    }
}
