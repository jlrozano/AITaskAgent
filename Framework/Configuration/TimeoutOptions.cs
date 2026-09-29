namespace AITaskAgent.Configuration;

/// <summary>
/// Timeout configuration options.
/// </summary>
public sealed class TimeoutOptions
{
    /// <summary>Default timeout for entire pipeline execution.</summary>
    public TimeSpan DefaultPipelineTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Default timeout for individual step execution.</summary>
    public TimeSpan DefaultStepTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Timeout for tool execution.</summary>
    public TimeSpan DefaultToolTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for streaming activity (resets on each chunk).</summary>
    /// <remarks>
    /// Never read by anything: the inactivity limit depends on the model, so it lives with the provider's or the role's
    /// inference settings, where the <c>ILlmService</c> that reads the stream can see it. Kept only so existing
    /// configuration and code that set it still bind and compile.
    /// </remarks>
    [Obsolete("Not read by the framework. Use LlmInferenceSettings.StreamingInactivityTimeoutSeconds on the provider or the role.")]
    public TimeSpan StreamingActivityTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
