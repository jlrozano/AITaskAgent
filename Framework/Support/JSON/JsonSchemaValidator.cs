using System.Text.Json;

namespace AITaskAgent.Support.JSON;

/// <summary>
/// Simple JSON schema validator for basic validation needs.
/// </summary>
public sealed class JsonSchemaValidator
{
    /// <summary>
    /// Validates that JSON contains required properties.
    /// </summary>
    public static (bool IsValid, string? Error) ValidateRequiredProperties(
        string json,
        params string[] requiredProperties)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var prop in requiredProperties)
            {
                if (!root.TryGetProperty(prop, out _))
                {
                    return (false, $"Missing required property: {prop}");
                }
            }

            return (true, null);
        }
        catch (JsonException ex)
        {
            return (false, $"Invalid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that JSON matches expected structure.
    /// </summary>
    public static (bool IsValid, string? Error) ValidateStructure<T>(string json)
    {
        try
        {
            var obj = JsonSerializer.Deserialize<T>(json);
            return obj != null
                ? (true, null)
                : (false, "Deserialization resulted in null");
        }
        catch (JsonException ex)
        {
            return (false, $"Structure validation failed: {ex.Message}");
        }
    }
}

