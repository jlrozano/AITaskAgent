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
    public TimeSpan StreamingActivityTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
