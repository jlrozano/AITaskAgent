namespace AITaskAgent.FileTools;

public static class FileToolsConfiguration
{
    /// <summary>
    /// Root directory for all file operations.
    /// If set, all paths provided to file tools will be validated to be within this directory.
    /// </summary>
    public static string? RootDirectory { get; set; }
}
