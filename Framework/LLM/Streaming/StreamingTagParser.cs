namespace AITaskAgent.LLM.Streaming;

using AITaskAgent.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// State machine parser for detecting and processing XML tags in streaming LLM responses.
/// </summary>
public sealed class StreamingTagParser(List<IStreamingTagHandler> handlers)
{
    private enum ParserState
    {
        Normal,          // Buffering normal text
        PotentialTag,    // Detected '<', checking if it's a tag
        InTag,           // Inside a tag, routing content to handler
        PotentialClose   // Detected '</', checking if it's closing tag
    }

    private readonly Dictionary<string, IStreamingTagHandler> _handlers = handlers.ToDictionary(h => h.TagName, h => h);
    private readonly StringBuilder _normalBuffer = new();
    private readonly StringBuilder _tagBuffer = new();

    private ParserState _state = ParserState.Normal;
    private IStreamingTagHandler? _activeHandler;
    private ITagContext? _activeContext;
    private string? _activeTagName;

    /// <summary>
    /// Processes a chunk of streaming text, detecting and executing tags.
    /// </summary>
    /// <returns>Text to include in conversation (non-tag content + placeholders)</returns>
    public async Task<string> ProcessChunkAsync(
        string chunk,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();

        foreach (var ch in chunk)
        {
            switch (_state)
            {
                case ParserState.Normal:
                    if (ch == '<')
                    {
                        _state = ParserState.PotentialTag;
                        _tagBuffer.Clear();
                        _tagBuffer.Append(ch);
                    }
                    else
                    {
                        _normalBuffer.Append(ch);
                    }
                    break;

                case ParserState.PotentialTag:
                    _tagBuffer.Append(ch);

                    if (ch == '/')
                    {
                        // Potential closing tag
                        _state = ParserState.PotentialClose;
                    }
                    else if (ch == '>')
                    {
                        // Complete tag opening: <tagname attr="value">
                        var tagContent = _tagBuffer.ToString();
                        var (tagName, attributes) = ParseOpeningTag(tagContent);

                        if (tagName != null && _handlers.TryGetValue(tagName, out var handler))
                        {
                            // Flush normal buffer to output
                            if (_normalBuffer.Length > 0)
                            {
                                _ = outputBuilder.Append(_normalBuffer);
                                _normalBuffer.Clear();
                            }

                            // Start tag processing
                            _activeHandler = handler;
                            _activeTagName = tagName;
                            _activeContext = await handler.OnTagStartAsync(attributes, context, cancellationToken);
                            _state = ParserState.InTag;
                        }
                        else
                        {
                            // Not a recognized tag, treat as normal text
                            _ = _normalBuffer.Append(_tagBuffer);
                            _state = ParserState.Normal;
                        }

                        _tagBuffer.Clear();
                    }
                    else if (char.IsWhiteSpace(ch) || char.IsLetterOrDigit(ch) || ch == '_' || ch == '=' || ch == '"')
                    {
                        // Continue building tag
                    }
                    else
                    {
                        // Invalid tag character, treat as normal text
                        _normalBuffer.Append(_tagBuffer.ToString());
                        _state = ParserState.Normal;
                        _tagBuffer.Clear();
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
                        // Stream content to handler
                        if (_activeHandler != null && _activeContext != null)
                        {
                            await _activeHandler.OnContentAsync(_activeContext, ch.ToString(), cancellationToken);
                        }
                    }
                    break;

                case ParserState.PotentialClose:
                    _tagBuffer.Append(ch);

                    if (ch == '>')
                    {
                        // Complete closing tag: </tagname>
                        var closeTag = _tagBuffer.ToString();
                        var closingTagName = ParseClosingTag(closeTag);

                        if (closingTagName == _activeTagName && _activeHandler != null && _activeContext != null)
                        {
                            // End tag processing
                            var placeholder = await _activeHandler.OnTagEndAsync(_activeContext, cancellationToken);

                            if (!string.IsNullOrEmpty(placeholder))
                            {
                                outputBuilder.Append(placeholder);
                            }

                            await _activeContext.DisposeAsync();
                            _activeHandler = null;
                            _activeContext = null;
                            _activeTagName = null;
                            _state = ParserState.Normal;
                        }
                        else
                        {
                            // Mismatched closing tag, treat as content
                            if (_activeHandler != null && _activeContext != null)
                            {
                                await _activeHandler.OnContentAsync(_activeContext, _tagBuffer.ToString(), cancellationToken);
                            }
                            _state = ParserState.InTag;
                        }

                        _tagBuffer.Clear();
                    }
                    else if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '/')
                    {
                        // Invalid closing tag, treat as content
                        if (_activeHandler != null && _activeContext != null)
                        {
                            await _activeHandler.OnContentAsync(_activeContext, _tagBuffer.ToString(), cancellationToken);
                        }
                        _state = ParserState.InTag;
                        _tagBuffer.Clear();
                    }
                    break;
            }
        }

        // Flush remaining normal buffer
        if (_normalBuffer.Length > 0)
        {
            outputBuilder.Append(_normalBuffer.ToString());
            _normalBuffer.Clear();
        }

        return outputBuilder.ToString();
    }

    private static (string? TagName, Dictionary<string, string> Attributes) ParseOpeningTag(string tagContent)
    {
        // Example: <write_file path="test.md">
        // Remove < and >
        var inner = tagContent.Trim('<', '>').Trim();

        var parts = inner.Split(' ', 2);
        var tagName = parts[0];
        Dictionary<string, string> attributes = [];

        if (parts.Length > 1)
        {
            // Simple regex for key="value" pairs
            var attrRegex = new Regex(@"(\w+)=""([^""]*)""");
            var matches = attrRegex.Matches(parts[1]);

            foreach (Match match in matches)
            {
                attributes[match.Groups[1].Value] = match.Groups[2].Value;
            }
        }

        return (tagName, attributes);
    }

    private static string? ParseClosingTag(string tagContent)
    {
        // Example: </write_file>
        var inner = tagContent.Trim('<', '>', '/').Trim();
        return inner;
    }
}
