using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Text.RegularExpressions;

namespace AITaskAgent.JSON;

/// <summary>
/// Parses JSON from LLM responses with multiple fallback strategies.
/// </summary>
public sealed partial class JsonResponseParser
{
    private static readonly System.Buffers.SearchValues<char> jsonEndValues = System.Buffers.SearchValues.Create("}]");
    private static readonly System.Buffers.SearchValues<char> jsonStartValues = System.Buffers.SearchValues.Create("{[");
    private readonly ILogger<JsonResponseParser> _logger;
    private readonly JsonSerializerSettings _jsonSettings;

    public JsonResponseParser(ILogger<JsonResponseParser> logger, JsonSerializerSettings? jsonSettings = null)
    {
        _logger = logger;
        _jsonSettings = jsonSettings ?? new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };
    }

    /// <summary>
    /// Attempts to parse JSON from response using multiple strategies.
    /// </summary>
    public T? Parse<T>(string response) where T : class
    {
        // Strategy 1: Direct parse
        var result = TryDirectParse<T>(response);
        if (result != null)
        {
            return result;
        }

        // Strategy 2: Extract from markdown code blocks
        result = TryExtractFromCodeBlock<T>(response);
        if (result != null)
        {
            return result;
        }

        // Strategy 3: Find JSON object/array in text
        result = TryExtractJsonFromText<T>(response);
        if (result != null)
        {
            return result;
        }

        // Strategy 4: Clean and retry
        result = TryCleanAndParse<T>(response);
        if (result != null)
        {
            return result;
        }

        _logger.LogWarning("Failed to parse JSON from response after all strategies");
        return null;
    }

    private T? TryDirectParse<T>(string response) where T : class
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(response, _jsonSettings);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Direct JSON parse failed");
            return null;
        }
    }

    private T? TryExtractFromCodeBlock<T>(string response) where T : class
    {
        // Match ```json ... ``` or ``` ... ```
        var match = MarkDownCodeBlockRegEx().Match(response);

        if (!match.Success)
        {
            return null;
        }

        var json = match.Groups[1].Value.Trim();

        try
        {
            return JsonConvert.DeserializeObject<T>(json, _jsonSettings);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Code block JSON parse failed");
            return null;
        }
    }

    private T? TryExtractJsonFromText<T>(string response) where T : class
    {
        // Try to find JSON object
        var objectMatch = JsonObjectRegEx().Match(response);

        if (objectMatch.Success)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(objectMatch.Value, _jsonSettings);
            }
            catch (JsonException) { }
        }

        // Try to find JSON array
        var arrayMatch = JsonArrayRegEx().Match(response);

        if (arrayMatch.Success)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(arrayMatch.Value, _jsonSettings);
            }
            catch (JsonException) { }
        }

        return null;
    }

    private T? TryCleanAndParse<T>(string response) where T : class
    {
        // Remove common issues
        var cleaned = response
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        // Remove leading/trailing non-JSON text
        var startIndex = cleaned.AsSpan().IndexOfAny(jsonStartValues);
        if (startIndex > 0)
        {
            cleaned = cleaned[startIndex..];
        }

        var endIndex = cleaned.AsSpan().LastIndexOfAny(jsonEndValues);
        if (endIndex >= 0 && endIndex < cleaned.Length - 1)
        {
            cleaned = cleaned[..(endIndex + 1)];
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(cleaned, _jsonSettings);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Cleaned JSON parse failed");
            return null;
        }
    }

    [GeneratedRegex(@"```(?:json)?\s*\n?(.*?)\n?```", RegexOptions.IgnoreCase | RegexOptions.Singleline, "es-ES")]
    private static partial Regex MarkDownCodeBlockRegEx();
    [GeneratedRegex(@"\{(?:[^{}]|(?<open>\{)|(?<-open>\}))+(?(open)(?!))\}", RegexOptions.Singleline)]
    private static partial Regex JsonObjectRegEx();
    [GeneratedRegex(@"\[(?:[^\[\]]|(?<open>\[)|(?<-open>\]))+(?(open)(?!))\]", RegexOptions.Singleline)]
    private static partial Regex JsonArrayRegEx();
}

