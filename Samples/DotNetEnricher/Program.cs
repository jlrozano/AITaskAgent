// DotNetEnricher: enriquece con Roslyn el grafo SQLite de codebase-memory-mcp de una solución .NET.
//
// Es un ejecutable aparte y no una librería de la app a propósito; la app (CodebaseAnalysis.Core,
// DotNetEnricherStep) lo lanza como proceso, igual que al analizador Java:
//   1. Un MSBuild colgado en la carga de la solución no se puede cortar desde dentro del host web: el timeout del
//      paso solo deja de esperar. En un proceso aparte, el timeout mata el proceso y su árbol.
//   2. MSBuildLocator tiene que registrarse antes de que se cargue ningún tipo de MSBuild, y en un proceso que ya ha
//      cargado otras cosas (el backend web, los tests) no se controla qué se ha cargado antes.
//   3. La solución compilada entera (compilaciones, modelos semánticos) se quedaría en la memoria del backend; al
//      salir el proceso se libera toda.
//
// Códigos de salida (ExitCodes): 0 correcto, 2 correcto con avisos (algún enriquecedor con más de un 5 % de patches sin
// nodo), 1 argumentos o entradas que no sirven (sin .sln, sin BD, grafo vacío), 3 la solución no se pudo cargar o no
// tiene ningún proyecto C# compilable, 4 fallo durante el enriquecimiento (la transacción se deshace y el grafo queda
// como estaba). El original devolvía 0 aunque faltaran la solución o la BD.

using System.CommandLine;
using System.Text;
using DotNetEnricher;
using Microsoft.Build.Locator;

Console.OutputEncoding = Encoding.UTF8;

// ─── MSBuild debe registrarse ANTES de cargar cualquier tipo de MSBuild ──────
// Los tipos de Microsoft.CodeAnalysis.MSBuild solo aparecen en SolutionLoader, que se JIT-compila después.
VisualStudioInstance? msbuild;
try
{
    msbuild = MSBuildLocator.RegisterDefaults();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"ERROR: no se encontró una instalación de MSBuild (SDK de .NET): {ex.Message}");
    return ExitCodes.LoadFailed;
}

var solutionOpt = new Option<FileInfo>("--solution") { Description = "Ruta al archivo .sln o .slnx", Required = true };
var dbOpt       = new Option<FileInfo>("--db") { Description = "Ruta a la copia SQLite del grafo de codebase-memory-mcp", Required = true };
var repoRootOpt = new Option<DirectoryInfo?>("--repo-root")
{
    Description = "Raíz del repositorio analizado, la que usó codebase-memory-mcp (por defecto, la carpeta con .git que contiene la solución, o la de la solución)"
};
var projectOpt  = new Option<string?>("--project")
{
    Description = "Nombre del proyecto en el grafo (por defecto, el mayoritario en la tabla nodes)"
};
var fingerprintOpt = new Option<string?>("--config-fingerprint")
{
    Description = "Huella de configuración para enrichment_runs (la calcula la app; por defecto, solución y proyecto)"
};
var verboseOpt  = new Option<bool>("--verbose") { Description = "Mostrar progreso detallado" };
var restoreOpt  = new Option<string>("--restore")
{
    Description = "Restauración de paquetes NuGet antes de cargar la solución: auto (solo si falta algún project.assets.json), always/siempre o never/nunca",
    DefaultValueFactory = _ => "auto"
};
var restoreTimeoutOpt = new Option<int>("--restore-timeout")
{
    Description = "Límite de dotnet restore, en segundos (0 = sin límite); al superarlo se mata y se sigue sin paquetes",
    DefaultValueFactory = _ => 300
};
var cacheDirOpt = new Option<DirectoryInfo?>("--cache-dir")
{
    Description = "Carpeta (fuera del repositorio) donde va la salida de la restauración (project.assets.json); por defecto %LOCALAPPDATA%/DotNetEnricher/restore/<hash de la solución>"
};

var enrichCmd = new Command("enrich", "Enriquece el grafo con metadatos de Roslyn")
{
    solutionOpt, dbOpt, repoRootOpt, projectOpt, fingerprintOpt, verboseOpt, restoreOpt, restoreTimeoutOpt, cacheDirOpt
};

enrichCmd.SetAction((parseResult, ct) => EnrichCommand.RunAsync(new EnrichOptions
{
    Solution          = parseResult.GetValue(solutionOpt)!,
    Database          = parseResult.GetValue(dbOpt)!,
    RepoRoot          = parseResult.GetValue(repoRootOpt),
    Project           = parseResult.GetValue(projectOpt),
    ConfigFingerprint = parseResult.GetValue(fingerprintOpt),
    Verbose           = parseResult.GetValue(verboseOpt),
    MsBuildVersion    = msbuild.Version?.ToString(),
    MsBuildPath       = msbuild.MSBuildPath,
    Restore           = parseResult.GetValue(restoreOpt)!,
    RestoreTimeoutSeconds = parseResult.GetValue(restoreTimeoutOpt),
    RestoreCacheDirectory = parseResult.GetValue(cacheDirOpt)
}, Console.Out, Console.Error, ct));

var root = new RootCommand("DotNetEnricher -- Enriquece el grafo codebase-memory-mcp con Roslyn")
{
    enrichCmd
};

return await root.Parse(args).InvokeAsync();
