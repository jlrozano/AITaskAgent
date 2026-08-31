namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// FileToolsConfiguration is the single source of truth every tool checks before touching disk
/// (see BaseFileTool.ValidatePath). It exposes no reset method, so each test restores the
/// RootDirectory/AllowedDirectories it captured on construction - xunit builds a fresh instance
/// of this class per test, which doubles as setup/teardown here.
/// </summary>
[Collection(FileToolsConfigurationCollection.Name)]
public class FileToolsConfigurationTests : IDisposable
{
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        FileToolsConfiguration.ClearAdditionalAllowedDirectories();
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fta-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RootDirectory_is_always_in_AllowedDirectories()
    {
        var root = NewTempDir();
        FileToolsConfiguration.RootDirectory = root;

        Assert.Contains(Path.GetFullPath(root), FileToolsConfiguration.AllowedDirectories);
    }

    [Fact]
    public void IsAllowed_accepts_a_path_inside_the_root()
    {
        var root = NewTempDir();
        FileToolsConfiguration.RootDirectory = root;

        var inside = Path.Combine(root, "subdir", "file.txt");

        Assert.True(FileToolsConfiguration.IsAllowed(inside));
    }

    [Fact]
    public void IsAllowed_rejects_a_sibling_directory_whose_name_prefixes_the_root()
    {
        // "root2" starting with "root" must not satisfy a check against "root": the separator
        // padding in IsWithin is exactly what prevents that string-prefix false positive.
        var root = NewTempDir();
        FileToolsConfiguration.RootDirectory = root;

        var sibling = root + "2" + Path.DirectorySeparatorChar + "file.txt";

        Assert.False(FileToolsConfiguration.IsAllowed(sibling));
    }

    [Fact]
    public void IsAllowed_rejects_a_directory_never_added()
    {
        FileToolsConfiguration.RootDirectory = NewTempDir();
        var untouched = NewTempDir();

        Assert.False(FileToolsConfiguration.IsAllowed(Path.Combine(untouched, "secret.txt")));
    }

    [Fact]
    public void AddAllowedDirectory_grants_access_without_changing_the_root()
    {
        var root = NewTempDir();
        var extra = NewTempDir();
        FileToolsConfiguration.RootDirectory = root;

        var added = FileToolsConfiguration.AddAllowedDirectory(extra);

        Assert.True(added);
        Assert.True(FileToolsConfiguration.IsAllowed(Path.Combine(extra, "file.txt")));
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(FileToolsConfiguration.RootDirectory));
    }

    [Fact]
    public void AddAllowedDirectory_reports_a_duplicate_instead_of_adding_it_twice()
    {
        var extra = NewTempDir();
        FileToolsConfiguration.RootDirectory = NewTempDir();

        Assert.True(FileToolsConfiguration.AddAllowedDirectory(extra));
        Assert.False(FileToolsConfiguration.AddAllowedDirectory(extra));
        Assert.Single(FileToolsConfiguration.AllowedDirectories, d =>
            string.Equals(d, Path.GetFullPath(extra), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SetRootDirectory_keeps_the_previous_root_reachable()
    {
        var firstRoot = NewTempDir();
        var secondRoot = NewTempDir();
        FileToolsConfiguration.RootDirectory = firstRoot;

        var kept = FileToolsConfiguration.SetRootDirectory(secondRoot);

        Assert.Equal(Path.GetFullPath(firstRoot), kept);
        Assert.True(FileToolsConfiguration.IsAllowed(Path.Combine(firstRoot, "old.txt")));
        Assert.True(FileToolsConfiguration.IsAllowed(Path.Combine(secondRoot, "new.txt")));
        Assert.Equal(Path.GetFullPath(secondRoot), Path.GetFullPath(FileToolsConfiguration.RootDirectory));
    }

    [Fact]
    public void SetRootDirectory_does_not_duplicate_a_root_that_was_already_added_manually()
    {
        var firstRoot = NewTempDir();
        var secondRoot = NewTempDir();
        FileToolsConfiguration.RootDirectory = firstRoot;
        FileToolsConfiguration.AddAllowedDirectory(secondRoot);

        FileToolsConfiguration.SetRootDirectory(secondRoot);

        Assert.Single(FileToolsConfiguration.AllowedDirectories, d =>
            string.Equals(d, Path.GetFullPath(secondRoot), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClearAdditionalAllowedDirectories_leaves_only_the_root()
    {
        var root = NewTempDir();
        FileToolsConfiguration.RootDirectory = root;
        FileToolsConfiguration.AddAllowedDirectory(NewTempDir());
        FileToolsConfiguration.AddAllowedDirectory(NewTempDir());

        FileToolsConfiguration.ClearAdditionalAllowedDirectories();

        Assert.Equal([Path.GetFullPath(root)], FileToolsConfiguration.AllowedDirectories);
    }
}
