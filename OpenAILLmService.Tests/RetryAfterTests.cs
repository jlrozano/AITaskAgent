using System.Net;
using System.Net.Http.Headers;

namespace OpenAILLmService.Tests;

/// <summary>
/// The retry wait honours the provider's hint: <c>retry-after-ms</c> (Azure OpenAI) or <c>Retry-After</c> in seconds or as
/// an HTTP date, capped at the policy's maximum delay; without a hint it is the exponential backoff.
/// </summary>
public class RetryAfterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage TooManyRequests(Action<HttpResponseHeaders>? headers = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        headers?.Invoke(response.Headers);
        return response;
    }

    [Fact]
    public void Retry_after_in_seconds_is_read()
    {
        using var response = TooManyRequests(h => h.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));

        Assert.Equal(TimeSpan.FromSeconds(7), OpenAILlmService.RetryAfterOf(response, Now));
    }

    [Fact]
    public void Retry_after_as_a_date_is_relative_to_now_and_never_negative()
    {
        using var later = TooManyRequests(h => h.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(12)));
        using var past = TooManyRequests(h => h.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(-5)));

        Assert.Equal(TimeSpan.FromSeconds(12), OpenAILlmService.RetryAfterOf(later, Now));
        Assert.Equal(TimeSpan.Zero, OpenAILlmService.RetryAfterOf(past, Now));
    }

    [Fact]
    public void Azure_retry_after_ms_wins_over_retry_after()
    {
        using var response = TooManyRequests(h =>
        {
            h.TryAddWithoutValidation("retry-after-ms", "1500");
            h.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        });

        Assert.Equal(TimeSpan.FromMilliseconds(1500), OpenAILlmService.RetryAfterOf(response, Now));
    }

    [Fact]
    public void Without_a_hint_or_with_an_unreadable_one_there_is_none()
    {
        using var none = TooManyRequests();
        using var garbage = TooManyRequests(h => h.TryAddWithoutValidation("retry-after-ms", "pronto"));

        Assert.Null(OpenAILlmService.RetryAfterOf(none, Now));
        Assert.Null(OpenAILlmService.RetryAfterOf(garbage, Now));
    }

    [Theory]
    [InlineData(2_000, null, 60_000, 2_000)]    // sin pista: el backoff
    [InlineData(2_000, 0, 60_000, 2_000)]       // «ahora mismo»: el backoff
    [InlineData(2_000, 9_000, 60_000, 9_000)]   // la pista, aunque sea mayor que el backoff
    [InlineData(4_000, 1_000, 60_000, 1_000)]   // y aunque sea menor
    [InlineData(2_000, 120_000, 60_000, 60_000)] // acotada al máximo de la política
    public void The_next_wait_is_the_hint_capped_or_the_backoff(int backoffMs, int? retryAfterMs, int maxMs, int expectedMs)
    {
        var wait = OpenAILlmService.NextRetryDelay(
            TimeSpan.FromMilliseconds(backoffMs),
            retryAfterMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            TimeSpan.FromMilliseconds(maxMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), wait);
    }
}
