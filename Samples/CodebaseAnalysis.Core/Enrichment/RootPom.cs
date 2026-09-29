using System.Xml;
using System.Xml.Linq;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>
/// Lectura mínima del <c>pom.xml</c> de la raíz del repositorio: solo los valores que cambian el análisis. Sin DTD ni red,
/// e ignorando el espacio de nombres de Maven, porque hay pom antiguos que no lo declaran.
/// </summary>
internal static class RootPom
{
    public static XElement? Load(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "pom.xml");
        if (!File.Exists(path))
            return null;

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            // Un pom mal formado no debe parar el análisis: se comporta como si no hubiera pom.
            return null;
        }
    }

    /// <summary>Texto del elemento que sigue la ruta de nombres locales desde la raíz, o null.</summary>
    public static string? Value(XElement? root, params string[] localNames)
    {
        var current = root;
        foreach (var name in localNames)
        {
            current = current?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            if (current == null)
                return null;
        }

        var value = current?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
