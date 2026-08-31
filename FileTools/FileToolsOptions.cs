namespace AITaskAgent.FileTools;

/// <summary>
/// Configuration for the file tools, bindable from IConfiguration
/// (section <c>AITaskAgent:FileTools</c>) or set in code.
/// </summary>
public sealed class FileToolsOptions
{
    /// <summary>
    /// Primary root directory. Relative paths resolve against it, and it is always an allowed
    /// location. When left empty the tools fall back to the executable's directory.
    /// </summary>
    public string? RootDirectory { get; set; }

    /// <summary>
    /// Extra directories the tools may read from and write to, on top of
    /// <see cref="RootDirectory"/>. Relative paths never resolve against these: reaching them
    /// requires an absolute path.
    /// </summary>
    public IList<string> AdditionalAllowedDirectories { get; set; } = [];
}
