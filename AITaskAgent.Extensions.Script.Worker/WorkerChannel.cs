using AITaskAgent.Script.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AITaskAgent.Script.Worker;

/// <summary>The worker's end of the line protocol (see <see cref="Msg"/>): one JSON object per line.</summary>
internal sealed class WorkerChannel(TextReader input, TextWriter output)
{
    private readonly Lock _writeLock = new();

    /// <summary>Writes one message and flushes it; the host reads continuously, so this never waits on it.</summary>
    public void Send(string kind, params (string Key, JToken? Value)[] fields)
    {
        JObject message = new() { [Msg.Kind] = kind };
        foreach ((string key, JToken? value) in fields)
        {
            message[key] = value ?? JValue.CreateNull();
        }

        string line = message.ToString(Formatting.None);
        lock (_writeLock)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }

    /// <summary>Reads one message; null when the host closed its end or sent something that is not a message.</summary>
    public JObject? Read()
    {
        string? line = input.ReadLine();
        if (line == null)
        {
            return null;
        }

        try
        {
            return Parse(line);
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    // Dates stay text: the envelope carries only strings and numbers, and the payloads are JSON *inside* strings.
    private static JObject Parse(string line)
    {
        using JsonTextReader reader = new(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }
}
