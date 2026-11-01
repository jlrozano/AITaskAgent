namespace AITaskAgent.Core.Execution;

/// <summary>
/// Options for pipeline execution timeouts. Injectable via IOptions&lt;PipelineOptions&gt;.
/// Apply via Pipeline.Configure(options) from DI startup.
/// </summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>
    /// Default timeout for the entire pipeline execution.
    /// </summary>
    public TimeSpan DefaultPipelineTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Default timeout for individual steps.
    /// Individual steps can override this via IStep.Timeout.
    /// </summary>
    public TimeSpan DefaultStepTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
