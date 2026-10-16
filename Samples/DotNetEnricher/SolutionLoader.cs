using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotNetEnricher;

/// <summary>Compilaciones y proyectos C# de la solución, con los avisos de la carga.</summary>
public sealed record LoadedSolution(
    IReadOnlyList<Compilation> Compilations,
    IReadOnlyList<CsProjectInfo> Projects,
    IReadOnlyList<string> Diagnostics);

/// <summary>Carga la solución con <see cref="MSBuildWorkspace"/>. Solo se usa desde el CLI, con MSBuildLocator ya registrado.</summary>
internal static class SolutionLoader
{
    /// <summary>Ensamblados que delatan un proyecto de test: con Microsoft.NET.Test.Sdk el proyecto es un Exe, y no es un host.</summary>
    private static readonly string[] TestFrameworkAssemblies =
        ["xunit.core", "xunit.v3.core", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework", "MSTest.TestFramework", "TUnit.Core"];

    /// <remarks>
    /// Propiedades globales de MSBuild (las de <see cref="NuGetRestore.GlobalProperties"/> tras una restauración): hacen que la
    /// carga lea los <c>project.assets.json</c> de la caché y no los (inexistentes) del repositorio. Null o vacío = tal cual.
    /// </remarks>
    public static async Task<LoadedSolution> LoadAsync(
        string solutionPath, string repoRoot, TextWriter log, CancellationToken ct, IReadOnlyDictionary<string, string>? properties = null)
    {
        var diagnostics = new List<string>();
        using var workspace = properties is { Count: > 0 }
            ? MSBuildWorkspace.Create(new Dictionary<string, string>(properties))
            : MSBuildWorkspace.Create();
        // Los avisos de carga van siempre al log, no solo con --verbose: un proyecto que no carga sus documentos es la
        // causa habitual de un enriquecimiento vacío, y sin ellos no hay forma de verlo.
        using var failedHandler = workspace.RegisterWorkspaceFailedHandler(e =>
        {
            var line = $"{e.Diagnostic.Kind}: {e.Diagnostic.Message}";
            lock (diagnostics) diagnostics.Add(line);
            lock (log) log.WriteLine($"  ! {line}");
        });

        var sln = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

        var compilations = new List<Compilation>();
        var projects = new List<CsProjectInfo>();
        // Un proyecto multi-target aparece una vez por framework con el mismo .csproj: basta con el primero.
        foreach (var proj in sln.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null)
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()))
        {
            Compilation? compilation;
            try
            {
                compilation = await proj.GetCompilationAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var line = $"Failure: no se pudo compilar '{proj.Name}': {ex.Message}";
                diagnostics.Add(line);
                log.WriteLine($"  ! {line}");
                continue;
            }
            if (compilation is null) continue;

            compilations.Add(compilation);
            var directory = Path.GetRelativePath(repoRoot, Path.GetDirectoryName(proj.FilePath!)!).Replace('\\', '/');
            projects.Add(new CsProjectInfo(
                Name: Path.GetFileNameWithoutExtension(proj.FilePath!),
                RelativeDirectory: directory == "." ? "" : directory,
                IsExecutable: compilation.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication or OutputKind.WindowsRuntimeApplication,
                IsTest: compilation.ReferencedAssemblyNames.Any(a => TestFrameworkAssemblies.Contains(a.Name, StringComparer.OrdinalIgnoreCase)),
                ProjectReferences: DeclaredProjectReferences(sln, proj),
                Packages: DeclaredPackages(proj.FilePath!)));
        }

        return new LoadedSolution(compilations, projects, diagnostics);
    }

    /// <summary>Nombres de los proyectos de la solución que este proyecto referencia con <c>ProjectReference</c>.</summary>
    private static IReadOnlyList<string> DeclaredProjectReferences(Solution solution, Project project) =>
        [.. project.ProjectReferences
            .Select(r => solution.GetProject(r.ProjectId)?.FilePath)
            .Where(p => p is not null)
            .Select(p => Path.GetFileNameWithoutExtension(p!))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Paquetes NuGet que declara el .csproj (<c>PackageReference Include</c>). Se lee el XML y no la evaluación de MSBuild:
    /// bastan los nombres, y un fichero ilegible solo deja la lista vacía.
    /// </summary>
    private static IReadOnlyList<string> DeclaredPackages(string projectFile)
    {
        try
        {
            return [.. System.Xml.Linq.XDocument.Load(projectFile).Descendants("PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
