using AITaskAgent.Core.Models;
using AITaskAgent.FileTools;
using AITaskAgent.FileTools.Tools;
using AITaskAgent.LLM.Tools.Abstractions;
using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Support;
using CodebaseAnalysis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Rutas de lectura adicionales del chat: validación, persistencia, y el límite real que ponen las tools de FileTools. La
/// configuración de FileTools es estática, así que va en la colección sin paralelismo.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ReadRootsTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;
    private readonly string _repo;
    private readonly string _extra;
    private readonly KnowledgePaths _paths;
    private readonly List<string> _junctions = [];

    public ReadRootsTests()
    {
        _repo = Path.Combine(_dir.Path, "repo");
        _extra = Path.Combine(_dir.Path, "docs-externos");
        Directory.CreateDirectory(Path.Combine(_repo, ".codebase"));
        Directory.CreateDirectory(_extra);
        File.WriteAllText(Path.Combine(_extra, "notas.md"), "# Notas\nclave-secreta-del-modelo");
        FileToolsConfiguration.RootDirectory = _repo;
        FileToolsConfiguration.ClearAdditionalAllowedDirectories();
        _paths = new KnowledgePaths(_repo, Path.Combine(_repo, "reports"));
    }

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        FileToolsConfiguration.ClearAdditionalAllowedDirectories();
        // Primero los junctions, sin recursión: así el borrado del temporal no puede seguirlos hasta su destino.
        foreach (var junction in _junctions.Where(Directory.Exists))
            Directory.Delete(junction, recursive: false);
        _dir.Dispose();
    }

    private static ReadRootException Rejected(Func<string> act) => Assert.Throws<ReadRootException>(act);

    [Fact]
    public void Validate_accepts_an_existing_absolute_directory_and_returns_it_canonical()
    {
        Assert.Equal(_extra, AllowedReadRoots.Validate(_extra + Path.DirectorySeparatorChar, _paths, allowUnc: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(@"docs\relativa")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"\\servidor\recurso")]
    [InlineData(@"\\?\C:\Temp")]
    public void Validate_rejects_the_paths_that_are_not_a_safe_absolute_directory(string? path)
    {
        Rejected(() => AllowedReadRoots.Validate(path, _paths, allowUnc: false));
    }

    [Fact]
    public void Validate_rejects_a_missing_path_a_file_and_parent_traversal()
    {
        Rejected(() => AllowedReadRoots.Validate(Path.Combine(_dir.Path, "no-existe"), _paths, false));
        Rejected(() => AllowedReadRoots.Validate(Path.Combine(_extra, "notas.md"), _paths, false));
        var traversal = Rejected(() => AllowedReadRoots.Validate(Path.Combine(_extra, "..", "docs-externos"), _paths, false));
        Assert.Contains("..", traversal.Message);
    }

    [Fact]
    public void Validate_rejects_the_whole_user_profile_but_not_a_subfolder_of_it()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Rejected(() => AllowedReadRoots.Validate(profile, _paths, false));
        Rejected(() => AllowedReadRoots.Validate(Path.GetDirectoryName(profile), _paths, false));
        // El temporal de los tests cuelga del perfil y sí vale.
        Assert.Equal(_extra, AllowedReadRoots.Validate(_extra, _paths, false));
    }

    [Fact]
    public void Validate_rejects_the_codebase_folder_the_repository_its_parents_and_what_is_already_inside_it()
    {
        Rejected(() => AllowedReadRoots.Validate(Path.Combine(_repo, ".codebase"), _paths, false));
        Directory.CreateDirectory(Path.Combine(_repo, ".codebase", "chat"));
        Rejected(() => AllowedReadRoots.Validate(Path.Combine(_repo, ".codebase", "chat"), _paths, false));
        Rejected(() => AllowedReadRoots.Validate(_repo, _paths, false));
        Rejected(() => AllowedReadRoots.Validate(_dir.Path, _paths, false));
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        Rejected(() => AllowedReadRoots.Validate(Path.Combine(_repo, "src"), _paths, false));
    }

    [Fact]
    public void Validate_rejects_a_junction_whose_target_is_forbidden()
    {
        var link = Path.Combine(_dir.Path, "enlace-a-windows");
        if (!TryCreateJunction(link, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            return; // sin permiso para crear junctions o fuera de Windows: no hay nada que probar

        Rejected(() => AllowedReadRoots.Validate(link, _paths, false));
    }

    [Fact]
    public void Revalidate_drops_the_roots_that_stopped_being_valid()
    {
        var gone = Path.Combine(_dir.Path, "borrada");
        Directory.CreateDirectory(gone);
        Directory.Delete(gone);
        var rejected = new List<string>();

        var valid = AllowedReadRoots.Revalidate([_extra, gone, _extra], _paths, false, (path, _) => rejected.Add(path));

        Assert.Equal([_extra], valid);
        Assert.Equal([gone], rejected);
    }

    [Fact]
    public void Store_adds_persists_ignores_duplicates_and_removes()
    {
        var store = new ReadRootsStore(_paths);
        Assert.Empty(store.List());

        Assert.Equal(_extra, store.Add(_extra));
        store.Add(_extra.ToUpperInvariant());
        Assert.Equal([_extra], new ReadRootsStore(_paths).List());
        Assert.True(File.Exists(_paths.ChatReadRootsFile));

        Assert.True(store.Remove(_extra + Path.DirectorySeparatorChar));
        Assert.False(store.Remove(_extra));
        Assert.Empty(new ReadRootsStore(_paths).List());
    }

    [Fact]
    public void Store_does_not_save_an_invalid_path_and_survives_a_corrupt_file()
    {
        var store = new ReadRootsStore(_paths);
        Rejected(() => store.Add(@"C:\Windows"));
        Assert.False(File.Exists(_paths.ChatReadRootsFile));

        File.WriteAllText(_paths.ChatReadRootsFile, "{ esto no es json");
        Assert.Empty(store.List());
        Assert.Equal(_extra, store.Add(_extra));
    }

    [Fact]
    public async Task The_file_tools_read_the_extra_root_only_after_it_is_applied_and_never_beyond_it()
    {
        var absolute = _extra.Replace("\\", "\\\\");
        var context = new PipelineContext();
        var list = new ListDirTool();
        var view = new ViewFileTool();
        var notas = Path.Combine(_extra, "notas.md").Replace("\\", "\\\\");

        // Sin añadir la ruta, el chat no la lee.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => list.ExecuteAsync($$"""{"DirectoryPath": "{{absolute}}"}""", context, "t", NullLogger.Instance));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.ExecuteAsync($$"""{"AbsolutePath": "{{notas}}"}""", context, "t", NullLogger.Instance));

        AllowedReadRoots.Apply([_extra]);
        Assert.Contains("notas.md", await list.ExecuteAsync($$"""{"DirectoryPath": "{{absolute}}"}""", context, "t", NullLogger.Instance));
        Assert.Contains("clave-secreta-del-modelo", await view.ExecuteAsync($$"""{"AbsolutePath": "{{notas}}"}""", context, "t", NullLogger.Instance));
        var grep = await new GrepSearchTool().ExecuteAsync($$"""{"Query": "clave-secreta", "SearchPath": "{{absolute}}"}""", context, "t", NullLogger.Instance);
        Assert.Contains("notas.md", grep);

        // Ni un hermano de la raíz, ni el padre con "..", ni la lectura sigue un junction que salga de ella.
        var sibling = Path.Combine(_dir.Path, "otro");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "x.txt"), "fuera");
        var siblingFile = Path.Combine(sibling, "x.txt").Replace("\\", "\\\\");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.ExecuteAsync($$"""{"AbsolutePath": "{{siblingFile}}"}""", context, "t", NullLogger.Instance));
        var dotdot = (_extra + @"\..\otro\x.txt").Replace("\\", "\\\\");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.ExecuteAsync($$"""{"AbsolutePath": "{{dotdot}}"}""", context, "t", NullLogger.Instance));

        var escape = Path.Combine(_extra, "salida");
        if (TryCreateJunction(escape, sibling))
        {
            var viaLink = Path.Combine(escape, "x.txt").Replace("\\", "\\\\");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => view.ExecuteAsync($$"""{"AbsolutePath": "{{viaLink}}"}""", context, "t", NullLogger.Instance));
        }
    }

    [Fact]
    public void Apply_replaces_the_previous_extra_roots_and_keeps_the_repository()
    {
        var second = Path.Combine(_dir.Path, "segunda");
        Directory.CreateDirectory(second);

        AllowedReadRoots.Apply([_extra, second]);
        Assert.Equal([_repo, _extra, second], FileToolsConfiguration.AllowedDirectories);

        AllowedReadRoots.Apply([second]);
        Assert.Equal([_repo, second], FileToolsConfiguration.AllowedDirectories);

        AllowedReadRoots.Apply([]);
        Assert.Equal([_repo], FileToolsConfiguration.AllowedDirectories);
    }

    [Fact]
    public void The_file_tools_say_in_their_description_which_roots_are_allowed_and_the_rest_are_untouched()
    {
        var tools = new ITool[] { new ListDirTool(), new FindByNameTool(), new GrepSearchTool(), new ViewFileTool(), new ViewFileOutlineTool(), new CreateReportTool() };

        var annotated = ReadRootsAwareTool.Annotate(tools, _repo, [_extra]).ToList();

        foreach (var tool in annotated.Take(5))
        {
            Assert.Contains(_repo, tool.Description);
            Assert.Contains(_extra, tool.Description);
            Assert.Contains(_extra, tool.GetDefinition().Description);
            Assert.True(tool.IsReadOnly);
        }
        Assert.Same(tools[5], annotated[5]);
        Assert.Equal(tools[0].Name, annotated[0].Name);
    }

    [Fact]
    public async Task The_chat_prompt_lists_the_extra_roots_as_read_only_context()
    {
        var workspace = new ChatWorkspace(_repo, Path.Combine(_repo, ".codebase", "codebase.db"), Path.Combine(_repo, "reports")) { ReadRoots = [_extra] };
        var withRoots = await ChatPromptBuilder.BuildAsync(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), null, workspace);
        var without = await ChatPromptBuilder.BuildAsync(AnalysisPipelineFactory.CreateDefaultTemplateProvider(), null, workspace with { ReadRoots = [] });

        Assert.Contains("### Rutas autorizadas de solo lectura", withRoots);
        Assert.Contains($"`{_extra}`", withRoots);
        Assert.DoesNotContain("### Rutas autorizadas de solo lectura", without);
    }

    private bool TryCreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            return false;
        _junctions.Add(link);
        return true;
    }
}
