using CodebaseAnalysis.Core.Reports;

namespace CodebaseAnalysis.Core.Documents;

/// <summary>
/// Valida el nombre que manda el cliente en <c>X-File-Name</c> (plan v2 §5.1, §5.6). El nombre nunca llega al disco
/// (el documento se guarda por id), pero se muestra y decide la familia: tiene que ser un nombre de fichero simple, sin
/// rutas, controles ni nombres reservados de Windows, y con extensión.
/// </summary>
public static class FileNameSanitizer
{
    public const int MaxLength = 255;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Decodifica (<c>encodeURIComponent</c>) y valida. Devuelve el nombre, su extensión en minúsculas con punto y el
    /// título por defecto (el nombre sin extensión).
    /// </summary>
    /// <exception cref="KnowledgeOperationException"><c>invalid_file_name</c>.</exception>
    public static (string FileName, string Extension, string Title) Validate(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            throw Invalid("Falta la cabecera X-File-Name con el nombre del fichero.");

        string name;
        try
        {
            name = Uri.UnescapeDataString(encoded.Trim());
        }
        catch (UriFormatException)
        {
            throw Invalid("X-File-Name no está codificado con encodeURIComponent.");
        }

        if (name.Length == 0 || name.Length > MaxLength)
            throw Invalid($"El nombre del fichero tiene que tener entre 1 y {MaxLength} caracteres.");
        if (name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            throw Invalid($"El nombre '{Printable(name)}' contiene caracteres no permitidos (rutas, controles o * ? \" < > | :).");
        if (name is "." or ".." || name.Contains("..", StringComparison.Ordinal) && name.Trim('.').Length == 0)
            throw Invalid($"'{Printable(name)}' no es un nombre de fichero.");
        if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
            throw Invalid($"El nombre '{name}' no puede empezar por espacio ni terminar en punto o espacio.");

        var stem = name.Split('.')[0];
        if (Reserved.Contains(stem.TrimEnd()))
            throw Invalid($"'{name}' usa un nombre reservado de Windows.");

        var extension = Path.GetExtension(name).ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(name).Trim();
        if (extension.Length <= 1 || title.Length == 0)
            throw Invalid($"El fichero '{name}' no tiene extensión o solo tiene extensión.");

        return (name, extension, title);
    }

    private static string Printable(string name) => new(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private static KnowledgeOperationException Invalid(string message) => new(KnowledgeErrorCodes.InvalidFileName, message);
}
