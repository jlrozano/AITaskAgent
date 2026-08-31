namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// FileToolsConfiguration is process-wide static state (RootDirectory, AllowedDirectories).
/// xunit parallelizes across test classes by default, and every class here that reads or writes
/// that state - directly or through a tool's ResolvePath/ValidatePath - would otherwise race with
/// every other one. Grouping them into one non-parallel collection makes that state safe to
/// mutate per test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class FileToolsConfigurationCollection
{
    public const string Name = "FileToolsConfiguration";
}
