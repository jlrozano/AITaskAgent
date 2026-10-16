using AITaskAgent.FileTools;
using CodebaseAnalysis.Core.Knowledge;

namespace CodebaseAnalysis.Core.Support;

/// <summary>La ruta de lectura adicional no es válida; el mensaje dice el motivo y se muestra tal cual al usuario.</summary>
public sealed class ReadRootException(string message) : Exception(message);

/// <summary>
/// Rutas de solo lectura, además del repositorio, que el chat puede recorrer con sus tools de ficheros (<c>list_dir</c>,
/// <c>find_by_name</c>, <c>grep_search</c>, <c>view_file</c>, <c>view_file_outline</c>, <c>view_code_item</c>). Nada de aquí
/// indexa, copia ni escribe: solo valida la ruta y la suma a las carpetas permitidas de <see cref="FileToolsConfiguration"/>,
/// que en cada llamada resuelve enlaces y junctions y rechaza lo que salga de ellas.
/// </summary>
public static class AllowedReadRoots
{
    private static readonly object ApplyGate = new();

    /// <summary>
    /// La ruta canónica (<see cref="Path.GetFullPath(string)"/>, sin separador final). Rechaza lo que no sea una ruta
    /// absoluta a un directorio existente, las UNC (salvo <paramref name="allowUnc"/>), las raíces de unidad, las carpetas
    /// del sistema, el perfil del usuario entero (y sus padres), la <c>.codebase</c> del repositorio y cualquier carpeta
    /// que la contenga (el propio repositorio y sus padres), lo que ya cuelga del repositorio, y un enlace cuyo destino
    /// caiga en alguno de esos casos.
    /// </summary>
    /// <exception cref="ReadRootException">Con el motivo.</exception>
    public static string Validate(string? path, KnowledgePaths paths, bool allowUnc)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ReadRootException("Falta la ruta.");
        path = path.Trim();
        if (path.Length > 1024 || path.Contains('\0'))
            throw new ReadRootException("La ruta no es válida.");
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ReadRootException("No se admiten rutas de dispositivo (con prefijo \\\\?\\ o \\\\.\\).");
        if (!Path.IsPathFullyQualified(path))
            throw new ReadRootException("La ruta tiene que ser absoluta (por ejemplo C:\\documentos).");
        if (path.Split('\\', '/').Any(segment => segment == ".."))
            throw new ReadRootException("La ruta no puede contener '..'.");
        if ((path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) && !allowUnc)
            throw new ReadRootException("Las rutas de red (UNC) no están permitidas. Se activan con CodebaseAnalysis:Chat:AllowUncPaths.");

        string full;
        try
        {
            full = Trim(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ReadRootException("La ruta no es válida.");
        }

        RejectForbidden(full, paths);
        if (!Directory.Exists(full))
            throw new ReadRootException("La ruta no existe o no es un directorio.");

        // Un enlace en cualquier punto de la ruta vale lo que valga su destino real.
        var real = ResolveRealPath(full);
        if (!real.Equals(full, StringComparison.OrdinalIgnoreCase))
            RejectForbidden(real, paths);
        return full;
    }

    /// <summary>
    /// Las rutas que valen ahora de <paramref name="candidates"/>: se vuelven a validar porque el disco cambia (una carpeta
    /// borrada, o sustituida por un junction a otro sitio) desde que se añadieron. Las que no valen se descartan y se avisan
    /// por <paramref name="onRejected"/>.
    /// </summary>
    public static IReadOnlyList<string> Revalidate(
        IEnumerable<string> candidates, KnowledgePaths paths, bool allowUnc, Action<string, string>? onRejected = null)
    {
        var valid = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                var full = Validate(candidate, paths, allowUnc);
                if (!valid.Contains(full, StringComparer.OrdinalIgnoreCase))
                    valid.Add(full);
            }
            catch (ReadRootException ex)
            {
                onRejected?.Invoke(candidate, ex.Message);
            }
        }
        return valid;
    }

    /// <summary>
    /// Deja en <see cref="FileToolsConfiguration"/> exactamente <paramref name="roots"/> como carpetas adicionales (más el
    /// repositorio, que es su raíz). Solo vacía las anteriores si alguna sobra, para no abrir una ventana sin ellas a otro
    /// turno que esté leyendo. Llamar solo con el chat reservado: un análisis en curso comparte esta configuración estática.
    /// </summary>
    public static void Apply(IReadOnlyList<string> roots)
    {
        lock (ApplyGate)
        {
            var repository = Path.GetFullPath(FileToolsConfiguration.RootDirectory);
            var current = FileToolsConfiguration.AllowedDirectories
                .Where(d => !d.Equals(repository, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var desired = roots.Select(Path.GetFullPath).ToList();

            if (current.Any(c => !desired.Contains(c, StringComparer.OrdinalIgnoreCase)))
                FileToolsConfiguration.ClearAdditionalAllowedDirectories();
            foreach (var root in desired)
                FileToolsConfiguration.AddAllowedDirectory(root);
        }
    }

    /// <summary>True si <paramref name="path"/> es <paramref name="root"/> o cuelga de ella (comparación por segmentos).</summary>
    public static bool IsSameOrInside(string path, string root)
    {
        path = Trim(path);
        root = Trim(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectForbidden(string full, KnowledgePaths paths)
    {
        var root = Path.GetPathRoot(full);
        if (root != null && Trim(root).Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new ReadRootException("No se puede usar la raíz de una unidad.");

        foreach (var system in SystemFolders())
        {
            if (IsSameOrInside(full, system))
                throw new ReadRootException($"No se puede usar una carpeta del sistema ('{system}').");
        }

        // El perfil entero (o C:\Users) daría acceso a claves, tokens y credenciales de otras aplicaciones; una subcarpeta sí vale.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && IsSameOrInside(Trim(Path.GetFullPath(profile)), full))
            throw new ReadRootException("No se puede usar el perfil del usuario entero: indica una subcarpeta.");

        var codebase = Trim(Path.GetFullPath(paths.CodebaseRoot));
        if (IsSameOrInside(full, codebase))
            throw new ReadRootException("No se puede usar la carpeta .codebase del repositorio.");
        if (IsSameOrInside(codebase, full))
            throw new ReadRootException("Esa carpeta contiene el repositorio y su .codebase: indica una subcarpeta que no lo contenga.");
        if (IsSameOrInside(full, Trim(Path.GetFullPath(paths.RepositoryPath))))
            throw new ReadRootException("Esa carpeta ya está dentro del repositorio: el chat ya puede leerla.");
    }

    private static IEnumerable<string> SystemFolders()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path))
                yield return Trim(Path.GetFullPath(path));
        }
    }

    /// <summary>La ruta con los enlaces de cualquier nivel resueltos (la misma pasada que hace FileTools en cada llamada).</summary>
    private static string ResolveRealPath(string full)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            current = current.Length == 0 ? segment : Path.Combine(current, segment);
            if (!Directory.Exists(current))
                break;
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = target.FullName;
        }
        return Trim(current);
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
}
