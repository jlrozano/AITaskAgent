using Newtonsoft.Json.Linq;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// A data converter using Newtonsoft.Json to rehydrate raw storage objects into specific step results.
/// This implementation fulfills the generic IDataConverter contract without coupling the Core abstractions to JSON.
/// </summary>
public class NewtonsoftJsonDataConverter
{
    /// <inheritdoc/>
    public static object? Convert(object rawData, Type targetType)
    {
        if (rawData == null)
        {
            return null;
        }

        if (targetType.IsAssignableFrom(rawData.GetType()))
        {
            return rawData;
        }

        // If the upstream storage already provided a JToken, use ToObject directly
        if (rawData is JToken token)
        {
            return token.ToObject(targetType);
        }

        // Otherwise, round-trip through JToken to map arbitrary Dictionaries/POCOs to the target type
        return JToken.FromObject(rawData).ToObject(targetType);
    }
}
