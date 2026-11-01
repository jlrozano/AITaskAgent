namespace AITaskAgent.Tests;

/// <summary>
/// <c>CreateFromFolderAsync</c> joins every *.yaml file with <c>---</c>, but the merged text was deserialized as a
/// single document, so two or more files threw "Expected 'StreamEnd', got 'DocumentStart'". The factory reads every
/// document: steps are concatenated and the pipeline name is the first non-empty one. (Migrated to YAML v2.)
/// </summary>
public sealed class YamlPipelineFactoryMultiDocumentTests
{
    private static string Step(string id, string? dependsOn = null) =>
        $"""
          - id: {id}
            type: Probe
            label: {id}
        """ + (dependsOn == null ? "" : $"\n    dependsOn: [{dependsOn}]");

    [Fact]
    public void Two_documents_are_merged_into_one_pipeline()
    {
        var kit = new YamlTestKit();
        var yaml = $"name: first\nsteps:\n{Step("a")}\n---\nsteps:\n{Step("b", dependsOn: "a")}\n";

        var pipeline = kit.Factory.CreateFromYaml(yaml);

        Assert.Equal("first", pipeline.Name);
        Assert.Equal(["a", "b"], pipeline.Steps.Select(s => s.Name));
    }

    [Fact]
    public void A_single_document_still_works()
    {
        var kit = new YamlTestKit();

        var pipeline = kit.Factory.CreateFromYaml($"name: solo\nsteps:\n{Step("a")}\n");

        Assert.Equal("solo", pipeline.Name);
        Assert.Equal("a", Assert.Single(pipeline.Steps).Name);
    }

    [Fact]
    public async Task A_folder_with_two_yaml_files_builds_one_pipeline()
    {
        var kit = new YamlTestKit();
        var folder = Path.Combine(Path.GetTempPath(), "yaml-multidoc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "one.yaml"), $"name: folder\nsteps:\n{Step("a")}\n");
            await File.WriteAllTextAsync(Path.Combine(folder, "two.yaml"), $"steps:\n{Step("b", dependsOn: "a")}\n");

            var pipeline = await kit.Factory.CreateFromFolderAsync(folder);

            Assert.Equal("folder", pipeline.Name);
            Assert.Equal(["a", "b"], pipeline.Steps.Select(s => s.Name));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Empty_yaml_fails_with_a_clear_message()
    {
        var kit = new YamlTestKit();

        var ex = Assert.Throws<InvalidOperationException>(() => kit.Factory.CreateFromYaml(""));

        Assert.Contains("no documents", ex.Message);
    }
}
