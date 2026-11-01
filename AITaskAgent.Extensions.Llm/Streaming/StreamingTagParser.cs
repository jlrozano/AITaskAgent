namespace AITaskAgent.LLM.Streaming;

using AITaskAgent.Core.Models;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The one tag scanner for every LLM response, streamed or not.
///
/// Text is fed in as it arrives (one delta per chunk, or the whole text as a single delta) and
/// comes back purged: outside a tag it passes through, inside a tag nothing comes back until the
/// closing tag, at which point the handler runs once with the complete body and its placeholder
/// takes the tag's place. A batch response is therefore just a stream with one chunk, which is
/// what keeps both transports behaving identically - same grammar, same error handling, same
/// placeholders.
///
/// Handlers only ever see complete tags (<see cref="ITagHandler.OnCompleteTagAsync"/>): nothing is
/// created or opened when a tag merely starts, so an opening tag the model never closes leaves no
/// half-written file behind - <see cref="FlushAsync"/> hands its text back as plain text. What the
/// scanner does emit at the opening tag is a <see cref="TagStartedEvent"/>, so a UI can show that
/// an action is being written while the body is still arriving.
/// </summary>
public sealed partial class StreamingTagParser
{
    private enum ParserState
    {
        Normal,          // Outside any tag
        PotentialTag,    // Saw '<', deciding whether this opens a known tag
        InTag,           // Inside a known tag, accumulating its body
        PotentialClose   // Saw '<' inside a tag, deciding whether this is its closing tag
    }

    private readonly Dictionary<string, ITagHandler> _handlers;
    private readonly ILogger _logger;

    private readonly StringBuilder _tagBuffer = new();
    private readonly StringBuilder _bodyBuffer = new();
    private readonly HashSet<string> _executedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _completedTagReferences = [];

    private ParserState _state = ParserState.Normal;
    private ITagHandler? _activeHandler;
    private string? _activeTagName;
    private string _activeOpeningTag = string.Empty;
    private Dictionary<string, string> _activeAttributes = [];
    private bool _inQuotedAttributeValue;

    /// <summary>Creates a scanner for the given handlers.</summary>
    /// <param name="handlers">Tag handlers, keyed by <see cref="ITagHandler.TagName"/>.</param>
    /// <param name="logger">Logger for handler failures; optional.</param>
    public StreamingTagParser(List<ITagHandler> handlers, ILogger? logger = null)
    {
        _handlers = handlers.ToDictionary(h => h.TagName, h => h, StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Names of the tags whose handler ran successfully so far.</summary>
    public IReadOnlySet<string> ExecutedTags => _executedTags;

    /// <summary>
    /// Placeholders returned by handlers that ran successfully, in order. Callers that need to know
    /// which references came from a given stretch of text take the count before and after feeding it.
    /// </summary>
    public IReadOnlyList<string> CompletedTagReferences => _completedTagReferences;

    /// <summary>
    /// Feeds the next piece of text and returns what may be shown or persisted from it: plain text
    /// as-is, tags replaced by their handler's placeholder, and nothing at all for the part of a
    /// tag still waiting for its closing tag.
    /// </summary>
    /// <param name="chunk">Text delta, or the whole text for a non-streaming response.</param>
    /// <param name="context">Pipeline context handed to the handlers.</param>
    /// <param name="cancellationToken">Token cancelling the handlers.</param>
    public async Task<string> ProcessChunkAsync(
        string chunk,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        StringBuilder output = new();

        foreach (char ch in chunk)
        {
            switch (_state)
            {
                case ParserState.Normal:
                    if (ch == '<')
                    {
                        _state = ParserState.PotentialTag;
                        _tagBuffer.Clear();
                        _tagBuffer.Append(ch);
                        _inQuotedAttributeValue = false;
                    }
                    else
                    {
                        output.Append(ch);
                    }
                    break;

                case ParserState.PotentialTag:
                    _tagBuffer.Append(ch);

                    // Once inside a quoted attribute value, any character is valid - paths carry
                    // dots, slashes, spaces, etc. The strict whitelist below only makes sense for
                    // the tag/attribute-name skeleton around the quotes.
                    if (_inQuotedAttributeValue)
                    {
                        if (ch == '"')
                        {
                            _inQuotedAttributeValue = false;
                        }
                        break;
                    }

                    if (_tagBuffer.Length == 2 && !(char.IsLetter(ch) || ch == '_'))
                    {
                        // A tag name starts right after '<'. "x < y", "a <= b", "</x>", "<3": none of
                        // them can open one of ours, so give the text back at once instead of
                        // buffering it until some later character rules it out - in streaming that
                        // buffering shows up as the output stalling for several words.
                        DiscardPotentialTagBuffer(output, ch);
                    }
                    else if (ch == '"')
                    {
                        _inQuotedAttributeValue = true;
                    }
                    else if (ch == '>')
                    {
                        var (tagName, attributes) = ParseOpeningTag(_tagBuffer.ToString());

                        if (tagName != null && _handlers.TryGetValue(tagName, out var handler))
                        {
                            _activeHandler = handler;
                            _activeTagName = tagName;
                            _activeAttributes = attributes;
                            _activeOpeningTag = _tagBuffer.ToString();
                            _bodyBuffer.Clear();
                            _state = ParserState.InTag;

                            await context.SendEventAsync(new TagStartedEvent
                            {
                                StepName = context.CurrentStepPath,
                                TagName = tagName,
                                Attributes = attributes,
                                CorrelationId = context.CorrelationId
                            }, cancellationToken);
                        }
                        else
                        {
                            // Not one of ours: plain text.
                            output.Append(_tagBuffer);
                            _state = ParserState.Normal;
                        }

                        _tagBuffer.Clear();
                    }
                    else if (char.IsWhiteSpace(ch) || char.IsLetterOrDigit(ch) || ch == '_' || ch == '=' || ch == '/' || ch == '-')
                    {
                        // Still a plausible tag skeleton - keep buffering.
                    }
                    else
                    {
                        // Cannot be a tag: give the buffered text back untouched.
                        DiscardPotentialTagBuffer(output, ch);
                    }
                    break;

                case ParserState.InTag:
                    if (ch == '<')
                    {
                        _tagBuffer.Clear();
                        _tagBuffer.Append(ch);
                        _state = ParserState.PotentialClose;
                    }
                    else
                    {
                        _bodyBuffer.Append(ch);
                    }
                    break;

                case ParserState.PotentialClose:
                    _tagBuffer.Append(ch);

                    if (ch == '>')
                    {
                        if (ParseClosingTag(_tagBuffer.ToString()) == _activeTagName)
                        {
                            output.Append(await ExecuteActiveTagAsync(context, cancellationToken));
                        }
                        else
                        {
                            // Some other element inside the body (HTML, XML, a nested example):
                            // it belongs to the body.
                            _bodyBuffer.Append(_tagBuffer);
                            _state = ParserState.InTag;
                        }

                        _tagBuffer.Clear();
                    }
                    else if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '/' && ch != '-')
                    {
                        DiscardPotentialCloseBuffer(ch);
                    }
                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Gives back everything buffered for a tag attempt that turned out not to be one. When the
    /// character that disqualified it is itself '&lt;', it is not flushed as plain text along
    /// with the rest of the buffer - it starts a fresh potential tag instead, so a false start
    /// right before a real tag ("a &lt;3 &lt;write_file path=...&gt;") does not swallow the '&lt;'
    /// the real tag needed to be recognized.
    /// </summary>
    private void DiscardPotentialTagBuffer(StringBuilder output, char causingChar)
    {
        if (causingChar == '<')
        {
            if (_tagBuffer.Length > 1)
            {
                output.Append(_tagBuffer.ToString(0, _tagBuffer.Length - 1));
            }

            _tagBuffer.Clear();
            _tagBuffer.Append('<');
            _inQuotedAttributeValue = false;
            _state = ParserState.PotentialTag;
        }
        else
        {
            output.Append(_tagBuffer);
            _tagBuffer.Clear();
            _state = ParserState.Normal;
        }
    }

    /// <summary>
    /// Gives back a failed closing-tag attempt to the body it interrupted. Same reasoning as
    /// <see cref="DiscardPotentialTagBuffer"/>: a disqualifying '&lt;' restarts a fresh close
    /// attempt instead of being buried in the body, so a false start right before the real closing
    /// tag does not swallow it.
    /// </summary>
    private void DiscardPotentialCloseBuffer(char causingChar)
    {
        if (causingChar == '<')
        {
            if (_tagBuffer.Length > 1)
            {
                _bodyBuffer.Append(_tagBuffer.ToString(0, _tagBuffer.Length - 1));
            }

            _tagBuffer.Clear();
            _tagBuffer.Append('<');
            _state = ParserState.PotentialClose;
        }
        else
        {
            _bodyBuffer.Append(_tagBuffer);
            _tagBuffer.Clear();
            _state = ParserState.InTag;
        }
    }

    /// <summary>
    /// Ends the text. Anything still buffered belongs to a tag that never closed, so it was never a
    /// tag: it comes back as the plain text it was, and no handler runs for it. If a
    /// <see cref="TagStartedEvent"/> had been emitted for it, a failed <see cref="TagCompletedEvent"/>
    /// closes it, so listeners never see an action start without an outcome.
    /// </summary>
    /// <param name="context">Pipeline context for the completed event; optional.</param>
    /// <param name="cancellationToken">Token cancelling the event emission.</param>
    public async Task<string> FlushAsync(PipelineContext? context = null, CancellationToken cancellationToken = default)
    {
        var pending = _state switch
        {
            ParserState.PotentialTag => _tagBuffer.ToString(),
            ParserState.InTag => _activeOpeningTag + _bodyBuffer,
            ParserState.PotentialClose => _activeOpeningTag + _bodyBuffer + _tagBuffer,
            _ => string.Empty
        };

        if (_state != ParserState.Normal && _activeTagName != null)
        {
            _logger.LogWarning(
                "Tag <{TagName}> was opened but never closed; its text is kept as plain text and the handler did not run.",
                _activeTagName);

            if (context != null)
            {
                await context.SendEventAsync(new TagCompletedEvent
                {
                    StepName = context.CurrentStepPath,
                    TagName = _activeTagName,
                    Success = false,
                    ErrorMessage = "Tag was never closed; its text was kept as plain text and no action ran.",
                    CorrelationId = context.CorrelationId
                }, cancellationToken);
            }
        }

        ResetTagState();
        _tagBuffer.Clear();
        _state = ParserState.Normal;

        return pending;
    }

    /// <summary>
    /// Runs the active tag's handler with its complete body. A handler failure must not take the
    /// whole turn down (it used to, in streaming), so it becomes an error placeholder where the tag
    /// stood, plus a log line; the handler base class has already emitted the failed
    /// TagCompletedEvent.
    /// </summary>
    /// <param name="context">Pipeline context handed to the handler.</param>
    /// <param name="cancellationToken">Token cancelling the handler.</param>
    private async Task<string> ExecuteActiveTagAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var handler = _activeHandler!;
        var tagName = _activeTagName!;
        var attributes = _activeAttributes;
        var body = TrimTagBody(_bodyBuffer.ToString());

        string placeholder;
        try
        {
            placeholder = await handler.OnCompleteTagAsync(attributes, body, context, cancellationToken) ?? string.Empty;
            _executedTags.Add(tagName);
            _completedTagReferences.Add(placeholder);
        }
        catch (OperationCanceledException)
        {
            // A cancelled turn is not a tag failure: propagate it instead of turning it into an
            // error placeholder that would make the turn look like it completed normally.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing tag {TagName}", tagName);
            placeholder = $"[Error processing tag {tagName}: {ex.Message}]";
        }

        ResetTagState();
        _state = ParserState.Normal;
        return placeholder;
    }

    /// <summary>
    /// The line break right after the opening tag and the one right before the closing tag belong
    /// to the tag syntax, not to the body: models write the body on its own lines, and keeping
    /// those breaks gave every written file a blank first line. Only one break is removed at each
    /// end, so a body that deliberately starts or ends with a blank line keeps it.
    /// </summary>
    private static string TrimTagBody(string body)
    {
        var start = 0;
        if (body.StartsWith("\r\n", StringComparison.Ordinal)) start = 2;
        else if (body.StartsWith('\n')) start = 1;

        var end = body.Length;
        if (end - start >= 2 && body.AsSpan(end - 2, 2).SequenceEqual("\r\n")) end -= 2;
        else if (end - start >= 1 && body[end - 1] == '\n') end -= 1;

        return body[start..end];
    }

    private void ResetTagState()
    {
        _activeHandler = null;
        _activeTagName = null;
        _activeAttributes = [];
        _activeOpeningTag = string.Empty;
        _bodyBuffer.Clear();
        _inQuotedAttributeValue = false;
    }

    private static (string? TagName, Dictionary<string, string> Attributes) ParseOpeningTag(string tagText)
    {
        // Example: <write_file path="test.md" summary="x">
        var inner = tagText.Trim('<', '>').Trim();
        var parts = inner.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || parts[0].Length == 0 || parts[0][0] == '/')
        {
            return (null, []);
        }

        Dictionary<string, string> attributes = [];

        if (parts.Length > 1)
        {
            foreach (Match match in AttributeRegex().Matches(parts[1]))
            {
                attributes[match.Groups[1].Value] = match.Groups[2].Value;
            }
        }

        return (parts[0], attributes);
    }

    private static string ParseClosingTag(string tagText)
    {
        // Example: </write_file>
        return tagText.Trim('<', '>', '/').Trim();
    }

    // The one attribute grammar for every transport: double quotes, as GetInstructions() documents.
    [GeneratedRegex(@"(\w+)=""([^""]*)""")]
    private static partial Regex AttributeRegex();
}
