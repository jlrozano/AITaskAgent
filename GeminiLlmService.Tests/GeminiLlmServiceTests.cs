using Microsoft.Extensions.Logging.Abstractions;

namespace GeminiLlmService.Tests;

/// <summary>
/// Only the pieces of GeminiLlmService that need no live client: everything else (InvokeAsync,
/// streaming, CacheManager/FileManager) talks to the real Google.GenAI SDK and needs an API key,
/// which is out of scope for unit tests.
/// </summary>
public class GeminiLlmServiceTests
{
    private static GeminiLlmService.GeminiLlmService Service() =>
        new(NullLogger<GeminiLlmService.GeminiLlmService>.Instance);

    [Theory]
    [InlineData("", 0)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void EstimateTokenCount_rounds_up_to_the_nearest_token(string text, int expected)
    {
        Assert.Equal(expected, Service().EstimateTokenCount(text));
    }

    [Fact]
    public void CacheManager_throws_before_the_service_has_been_invoked()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Service().CacheManager);
        Assert.Contains("not available yet", ex.Message);
    }

    [Fact]
    public void FileManager_throws_before_the_service_has_been_invoked()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Service().FileManager);
        Assert.Contains("not available yet", ex.Message);
    }
}
