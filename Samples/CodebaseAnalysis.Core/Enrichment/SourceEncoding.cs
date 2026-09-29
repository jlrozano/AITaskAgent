using AITaskAgent.FileTools;
using System.Text;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>Codificación por defecto resuelta: la usa .NET y se pasa por nombre al analizador Java.</summary>
/// <param name="Encoding">La codificación para .NET.</param>
/// <param name="Name">Nombre que entienden .NET y Java (p. ej. <c>iso-8859-1</c>).</param>
/// <param name="Origin">De dónde sale, para los mensajes: configuración, <c>pom.xml</c> o valor por defecto.</param>
public sealed record DefaultSourceEncoding(Encoding Encoding, string Name, string Origin);

/// <summary>
/// Regla de codificación de los fuentes (P6): fichero a fichero, UTF-8 si los bytes son UTF-8 válido (con o sin BOM) y,
/// si no, la codificación por defecto. La de defecto es <c>JavaAnalyzer:DefaultEncoding</c>; si está vacía, la
/// <c>project.build.sourceEncoding</c> del <c>pom.xml</c> raíz; si no hay, UTF-8. Es la regla del analizador Java y la
/// de las JSP que no declaran <c>pageEncoding</c> ni <c>charset</c>. La decodificación es la de
/// <see cref="TextFileEncoding"/> (FileTools), la misma que aplican <c>view_file</c>, <c>grep_search</c> y el visor de fuentes
/// (auditoría H-8), que además reconoce los BOM de UTF-16 y UTF-32.
/// </summary>
public static class SourceEncoding
{

    /// <summary>Nombres de Java que .NET no conoce.</summary>
    private static readonly Dictionary<string, string> JavaAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ISO8859_1"] = "iso-8859-1",
        ["ISO8859-1"] = "iso-8859-1",
        ["ISO8859_15"] = "iso-8859-15",
        ["UTF8"] = "utf-8",
        ["Cp1252"] = "windows-1252",
        ["Cp1250"] = "windows-1250",
        ["Cp850"] = "ibm850"
    };

    static SourceEncoding()
    {
        // windows-125x y las páginas de IBM no vienen registradas en .NET por defecto.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <exception cref="InvalidOperationException">Si la codificación configurada no existe: es un error de configuración.</exception>
    public static DefaultSourceEncoding ResolveDefault(string repositoryRoot, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var encoding = TryGetEncoding(configured)
                ?? throw new InvalidOperationException($"JavaAnalyzer:DefaultEncoding: codificación desconocida '{configured}'.");
            return new DefaultSourceEncoding(encoding, encoding.WebName, "JavaAnalyzer:DefaultEncoding");
        }

        var fromPom = RootPom.Value(RootPom.Load(repositoryRoot), "properties", "project.build.sourceEncoding");
        if (fromPom != null)
        {
            if (TryGetEncoding(fromPom) is { } pomEncoding)
                return new DefaultSourceEncoding(pomEncoding, pomEncoding.WebName, "pom.xml");
            return new DefaultSourceEncoding(Encoding.UTF8, Encoding.UTF8.WebName, $"UTF-8 (el pom.xml declara '{fromPom}', que no se reconoce)");
        }

        return new DefaultSourceEncoding(Encoding.UTF8, Encoding.UTF8.WebName, "UTF-8 por defecto");
    }

    /// <summary>Codificación por nombre de .NET o de Java, o null si no existe.</summary>
    public static Encoding? TryGetEncoding(string name)
    {
        var key = name.Trim();
        if (JavaAliases.TryGetValue(key, out var alias))
            key = alias;
        try
        {
            return Encoding.GetEncoding(key);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True si los bytes son UTF-8 estricto (el BOM, si lo hay, cuenta como UTF-8).</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes) => TextFileEncoding.IsValidUtf8(bytes);

    /// <summary>
    /// Decodifica según la regla P6; el BOM no llega al texto. Con BOM manda el BOM aunque haya algún byte inválido, que se
    /// sustituye en lugar de fallar.
    /// </summary>
    public static (string Text, Encoding Encoding) Decode(byte[] bytes, Encoding defaultEncoding) =>
        TextFileEncoding.Decode(bytes, defaultEncoding);

    /// <summary>
    /// Codificación con la que las tools y el visor leen los ficheros que no son UTF-8 válido (auditoría H-8): la de
    /// <see cref="ResolveDefault"/> salvo que sea UTF-8, que por definición no los decodifica y dejaría U+FFFD en cada tilde;
    /// entonces Latin-1, que no pierde ningún byte y es lo que ya hacía el visor. Una codificación configurada que no existe
    /// también da Latin-1: ese error de configuración ya lo avisa el analizador Java.
    /// </summary>
    public static Encoding ResolveReadFallback(string repositoryRoot, string? configured)
    {
        DefaultSourceEncoding resolved;
        try
        {
            resolved = ResolveDefault(repositoryRoot, configured);
        }
        catch (InvalidOperationException)
        {
            return Encoding.Latin1;
        }
        return resolved.Encoding.CodePage == Encoding.UTF8.CodePage ? Encoding.Latin1 : resolved.Encoding;
    }

    public static (string Text, Encoding Encoding) ReadFile(string path, Encoding defaultEncoding) =>
        Decode(File.ReadAllBytes(path), defaultEncoding);
}
