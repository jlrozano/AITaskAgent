namespace AITaskAgent.Core;

/// <summary>
/// Centralized constants for the AITaskAgent framework.
/// </summary>
public static partial class AITaskAgentConstants
{
    /// <summary>
    /// Validation type constants.
    /// </summary>
    public static class ValidationTypes
    {
        /// <summary>Structural validation (ValidateAsync).</summary>
        public const string Structural = "structural";

        /// <summary>Semantic validation (resultValidator).</summary>
        public const string Semantic = "semantic";
    }
}

