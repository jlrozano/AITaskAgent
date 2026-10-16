using CodebaseAnalysis.Core.Enrichment;

namespace DotNetEnricher.Tests;

/// <summary>
/// La restauración de paquetes antes de cargar la solución: cuándo se hace, que la salida va a una caché fuera del repositorio
/// y que un fallo no aborta. Los tests con restauración real lanzan <c>dotnet restore</c> sobre proyectos sin paquetes (no
/// necesitan red) o contra un origen local vacío.
/// </summary>
public sealed class RestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dne-restore-" + Guid.NewGuid().ToString("N")[..8]);

    public RestoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Repo => Path.Combine(_root, "repo");
    private string Cache => Path.Combine(_root, "cache");

    private string WriteProject(string name, string packages = "")
    {
        var dir = Path.Combine(Repo, "src", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>{{packages}}</ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "C.cs"), "namespace N; public class C { }");
        return Path.Combine(dir, name + ".csproj");
    }

    private string WriteSolution(params string[] projects)
    {
        var path = Path.Combine(Repo, "app.slnx");
        File.WriteAllText(path, "<Solution>" + string.Concat(projects.Select(p =>
            $"<Project Path=\"{Path.GetRelativePath(Repo, p).Replace('\\', '/')}\" />")) + "</Solution>");
        return path;
    }

    private static string[] Listing(string dir) =>
        Directory.Exists(dir)
            ? [.. Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p)).Order(StringComparer.Ordinal)]
            : [];

    private static void WriteAssets(string project)
    {
        var obj = Path.Combine(Path.GetDirectoryName(project)!, "obj");
        Directory.CreateDirectory(obj);
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), "{}");
    }

    // ─── Decisión ───────────────────────────────────────────────────────────────

    [Fact]
    public void Auto_does_not_restore_when_every_project_already_has_its_assets_file()
    {
        var a = WriteProject("A");
        var b = WriteProject("B");
        WriteAssets(a);
        WriteAssets(b);

        Assert.False(NuGetRestore.NeedsRestore(DotNetRestoreMode.Auto, NuGetRestore.FindProjects(Repo)));
    }

    [Fact]
    public void Auto_restores_when_any_project_lacks_its_assets_file()
    {
        var a = WriteProject("A");
        WriteProject("B");
        WriteAssets(a);

        Assert.True(NuGetRestore.NeedsRestore(DotNetRestoreMode.Auto, NuGetRestore.FindProjects(Repo)));
    }

    [Fact]
    public void Always_restores_even_with_assets_and_never_does_not_restore_without_them()
    {
        var a = WriteProject("A");
        WriteAssets(a);
        var projects = NuGetRestore.FindProjects(Repo);

        Assert.True(NuGetRestore.NeedsRestore(DotNetRestoreMode.Always, projects));
        Assert.False(NuGetRestore.NeedsRestore(DotNetRestoreMode.Never, projects));
        Assert.False(NuGetRestore.NeedsRestore(DotNetRestoreMode.Always, []));
    }

    [Fact]
    public void Projects_inside_bin_obj_and_the_pipeline_folders_are_not_listed()
    {
        WriteProject("A");
        foreach (var skipped in new[] { "bin", "obj", ".codebase", "node_modules" })
        {
            var dir = Path.Combine(Repo, skipped, "X");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "X.csproj"), "<Project />");
        }

        var projects = NuGetRestore.FindProjects(Repo);

        Assert.Equal(["A.csproj"], projects.Select(Path.GetFileName));
    }

    // ─── Restauración real ──────────────────────────────────────────────────────

    [Fact]
    public async Task Restoring_a_clean_repository_writes_the_assets_to_the_cache_and_leaves_the_repository_intact()
    {
        var project = WriteProject("A");
        var solution = WriteSolution(project, WriteProject("B"));
        var before = Listing(Repo);
        var stamp = File.GetLastWriteTimeUtc(project);

        var log = new StringWriter();
        var outcome = await NuGetRestore.PrepareAsync(DotNetRestoreMode.Auto, solution, Repo, Cache, 240, null, log, CancellationToken.None);

        Assert.True(outcome.Attempted, log.ToString());
        Assert.True(outcome.Succeeded, log + outcome.Message);
        Assert.True(outcome.Redirect);
        Assert.Equal(before, Listing(Repo));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(project));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(Cache, "pkg"), "project.assets.json", SearchOption.AllDirectories).Count());
        Assert.Empty(Directory.EnumerateDirectories(Repo, "obj", SearchOption.AllDirectories));
        // Las mismas propiedades que la restauración: la carga de la solución lee las mismas rutas.
        Assert.Equal(NuGetRestore.GlobalProperties(Cache), outcome.Properties);
    }

    [Fact]
    public async Task Existing_assets_are_used_without_restoring_in_auto_mode()
    {
        var project = WriteProject("A");
        WriteAssets(project);
        var solution = WriteSolution(project);
        var before = Listing(Repo);

        var outcome = await NuGetRestore.PrepareAsync(DotNetRestoreMode.Auto, solution, Repo, Cache, 240, null, TextWriter.Null, CancellationToken.None);

        Assert.False(outcome.Attempted);
        Assert.False(outcome.Redirect);
        // Los assets se leen del repositorio, pero obj/ (lo que escribe la carga) va a la caché.
        Assert.Equal(NuGetRestore.GlobalProperties(Cache, redirectRestore: false), outcome.Properties);
        Assert.False(outcome.Properties.ContainsKey(NuGetRestore.RestoreRootProperty));
        Assert.False(Directory.Exists(Path.Combine(Cache, "pkg")));
        Assert.Equal(before, Listing(Repo));
    }

    [Fact]
    public async Task Never_does_not_touch_anything()
    {
        var solution = WriteSolution(WriteProject("A"));

        var outcome = await NuGetRestore.PrepareAsync(DotNetRestoreMode.Never, solution, Repo, Cache, 240, null, TextWriter.Null, CancellationToken.None);

        Assert.False(outcome.Attempted);
        Assert.False(outcome.Redirect);
        Assert.Equal(NuGetRestore.GlobalProperties(Cache, redirectRestore: false), outcome.Properties);
        Assert.False(Directory.Exists(Path.Combine(Cache, "pkg")));
    }

    [Fact]
    public async Task A_restore_that_cannot_reach_its_packages_reports_a_warning_and_does_not_throw_or_write_in_the_repository()
    {
        var project = WriteProject("A", "<PackageReference Include=\"Paquete.Que.No.Existe\" Version=\"1.0.0\" />");
        var solution = WriteSolution(project);
        // Sin acceso a feeds (entorno corporativo): el único origen es una carpeta local vacía.
        var emptyFeed = Path.Combine(_root, "feed");
        Directory.CreateDirectory(emptyFeed);
        File.WriteAllText(Path.Combine(Repo, "nuget.config"),
            $"<configuration><packageSources><clear /><add key=\"vacio\" value=\"{emptyFeed}\" /></packageSources></configuration>");
        var before = Listing(Repo);

        var outcome = await NuGetRestore.PrepareAsync(DotNetRestoreMode.Auto, solution, Repo, Cache, 240, null, TextWriter.Null, CancellationToken.None);

        Assert.True(outcome.Attempted);
        Assert.False(outcome.Succeeded);
        Assert.Contains("restauración", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Listing(Repo));
    }

    [Fact]
    public async Task A_restore_that_exceeds_its_timeout_is_killed_and_the_step_goes_on_without_packages()
    {
        var solution = WriteSolution(WriteProject("A"));
        var before = Listing(Repo);

        var outcome = await NuGetRestore.PrepareAsync(DotNetRestoreMode.Auto, solution, Repo, Cache, 1, null, TextWriter.Null, CancellationToken.None);

        Assert.True(outcome.Attempted);
        Assert.False(outcome.Succeeded);
        Assert.Contains("límite", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Listing(Repo));
    }
}
