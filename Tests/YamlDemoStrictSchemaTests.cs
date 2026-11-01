using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AITaskAgent.Tests;

/// <summary>
/// The OpenAI-compatible service sends every <c>outputSchema</c> as a strict <c>json_schema</c>
/// response format (OpenAILlmService sets <c>strict: true</c>). Azure/OpenAI reject a strict schema
/// unless every object lists all its properties as required, sets <c>additionalProperties: false</c>
/// and uses no free-form map. YamlPipelineDemo broke that on its first real run (HTTP 400,
/// "'required' is required to be supplied and to be an array including every key in properties").
/// </summary>
public class YamlDemoStrictSchemaTests
{
    private static string DemoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AITaskAgent.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "Samples", "YamlPipelineDemo");
    }

    public static TheoryData<string> OutputSchemas()
    {
        var yaml = File.ReadAllText(Path.Combine(DemoDir(), "pipelines", "main.yaml"));
        var data = new TheoryData<string>();
        foreach (var name in Regex.Matches(yaml, @"outputSchema:\s*(\w+)").Select(m => m.Groups[1].Value).Distinct())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OutputSchemas))]
    public void Demo_output_schema_is_valid_for_strict_response_format(string schemaName)
    {
        var schema = JObject.Parse(File.ReadAllText(Path.Combine(DemoDir(), "schemas", schemaName + ".json")));
        var problems = new List<string>();
        Check(schema, "$", problems);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static void Check(JObject node, string path, List<string> problems)
    {
        if (node["type"]?.ToString() == "object")
        {
            if (node["additionalProperties"] is not JValue { Value: false })
            {
                problems.Add($"{path}: additionalProperties must be false");
            }

            var props = (node["properties"] as JObject)?.Properties().Select(p => p.Name).ToList() ?? [];
            var required = (node["required"] as JArray)?.Select(t => t.ToString()).ToHashSet() ?? [];
            foreach (var missing in props.Where(p => !required.Contains(p)))
            {
                problems.Add($"{path}: '{missing}' is not in required");
            }
        }

        foreach (var child in (node["properties"] as JObject)?.Properties() ?? [])
        {
            if (child.Value is JObject o) Check(o, $"{path}.{child.Name}", problems);
        }

        if (node["items"] is JObject items) Check(items, $"{path}[]", problems);
    }
}
