using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;

namespace Samples.AdvancedLlmPipeline;

/// <summary>
/// Factory for creating conversation bookmark steps.
/// Uses the native bookmark system of MessageHistory to preserve history before the bookmark point.
/// </summary>
internal static class ConversationBookmarkSteps
{
    private const string BookmarkIdKey = "ConversationBookmarkId";

    /// <summary>
    /// Creates a step that saves a bookmark of the current conversation position.
    /// Messages added after this point can be cleared by the restore step.
    /// </summary>
    public static DelegatedStep<IStepResult, IStepResult> CreateBookmarkStep()
    {
        return new DelegatedStep<IStepResult, IStepResult>(
            "BookmarkConversation",
            (input, context, attempt, result) =>
            {
                // Create a bookmark at current position using native system
                var bookmarkId = context.Conversation.CreateBookmark();
                context.Metadata[BookmarkIdKey] = bookmarkId;

                // Pass through the input unchanged
                return Task.FromResult(input);
            });
    }

    /// <summary>
    /// Creates a step that restores the conversation to the previously saved bookmark.
    /// This clears only messages added AFTER the bookmark, preserving earlier history.
    /// </summary>
    public static DelegatedStep<IStepResult, IStepResult> CreateRestoreStep()
    {
        return new DelegatedStep<IStepResult, IStepResult>(
            "RestoreConversation",
            (input, context, attempt, result) =>
            {
                // Restore to bookmark position - clears only messages after bookmark
                if (context.Metadata.TryGetValue(BookmarkIdKey, out var bookmarkObj) &&
                    bookmarkObj is string bookmarkId)
                {
                    try
                    {
                        context.Conversation.RestoreBookmark(bookmarkId);
                    }
                    catch (ArgumentException)
                    {
                        // Bookmark may have been cleared by another operation - safe to ignore
                    }
                    finally
                    {
                        context.Metadata.TryRemove(BookmarkIdKey, out _);
                    }
                }

                // Pass through the input unchanged
                return Task.FromResult(input);
            });
    }
}
