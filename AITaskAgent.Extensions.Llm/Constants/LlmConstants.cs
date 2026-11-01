namespace AITaskAgent.LLM.Constants;

public static partial class LlmConstants
{
    /// <summary>
    /// Message role constants for LLM conversations.
    /// </summary>
    public static class MessageRoles
    {
        /// <summary>System message role (instructions).</summary>
        public const string System = "system";

        /// <summary>User message role.</summary>
        public const string User = "user";

        /// <summary>Assistant message role (LLM response).</summary>
        public const string Assistant = "assistant";

        /// <summary>Tool message role (tool execution result).</summary>
        public const string Tool = "tool";
    }
}
