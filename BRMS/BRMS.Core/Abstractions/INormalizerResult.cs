namespace BRMS.Core.Abstractions;

public interface INormalizerResult : IRuleResult
{
    bool Changed { get; }
    object? NewValue { get; set; }
    object? OldValue { get; set; }
}
