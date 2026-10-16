namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>
/// Cuándo restaura el enriquecedor Roslyn los paquetes NuGet de la solución (<c>DotNetEnricher:Restore</c>). Sin
/// <c>project.assets.json</c> Roslyn no resuelve los paquetes (p. ej. <c>SqlConnection</c>) y se pierden reglas que dependen de
/// los tipos. El CLI enlaza este fichero: el valor viaja como texto en <c>--restore</c>.
/// </summary>
public enum DotNetRestoreMode
{
    /// <summary>Restaura solo si algún proyecto no tiene su <c>project.assets.json</c>; si los tiene todos, usa los del repositorio.</summary>
    Auto,

    /// <summary>Restaura siempre, aunque ya haya <c>project.assets.json</c> en el repositorio.</summary>
    Always,

    /// <summary>No restaura nunca: se carga la solución tal como está.</summary>
    Never
}

public static class DotNetRestoreModes
{
    /// <summary>Acepta <c>auto</c>, <c>always</c>/<c>siempre</c> y <c>never</c>/<c>nunca</c> (sin distinguir mayúsculas); vacío = <see cref="DotNetRestoreMode.Auto"/>.</summary>
    public static bool TryParse(string? raw, out DotNetRestoreMode mode)
    {
        mode = DotNetRestoreMode.Auto;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                return true;
            case "always" or "siempre":
                mode = DotNetRestoreMode.Always;
                return true;
            case "never" or "nunca":
                mode = DotNetRestoreMode.Never;
                return true;
            default:
                return false;
        }
    }

    /// <summary>El valor que se pasa a <c>--restore</c>.</summary>
    public static string ToArgument(DotNetRestoreMode mode) => mode switch
    {
        DotNetRestoreMode.Always => "always",
        DotNetRestoreMode.Never => "never",
        _ => "auto"
    };
}
