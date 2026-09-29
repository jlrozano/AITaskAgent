using System.Reflection;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Versión de la app para la procedencia de los informes y de <c>_run_meta.json</c>: la <c>InformationalVersion</c> de
/// <c>CodebaseAnalysis.Core</c>, que el SDK compone con la versión del proyecto y el commit de git en el que se compiló
/// (<c>1.0.0+6322741…</c>), con el hash acortado a 7 caracteres. No refleja cambios sin commitear.
/// </summary>
public static class AppVersion
{
    public static string? Current { get; } = Read();

    private static string? Read()
    {
        var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return null;

        var plus = informational.IndexOf('+');
        return plus >= 0 && informational.Length - plus - 1 > 7 ? informational[..(plus + 8)] : informational;
    }
}
