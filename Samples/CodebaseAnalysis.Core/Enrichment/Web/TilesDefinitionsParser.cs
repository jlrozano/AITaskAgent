using System.Xml;
using System.Xml.Linq;

namespace CodebaseAnalysis.Core.Enrichment.Web;

/// <summary>
/// Un <c>&lt;put&gt;</c> de una definición de Tiles. <c>Value</c> es el atributo <c>value</c> o <c>content</c>, o el texto
/// del elemento (null si no tiene); <c>Type</c>, el atributo <c>type</c> (<c>string</c>, <c>page</c>, <c>template</c>,
/// <c>definition</c>), si lo tiene.
/// </summary>
public sealed record TilesPut(string Name, string? Value, string? Type, int Line);

/// <summary>
/// Un <c>&lt;definition&gt;</c> de Tiles 1.x. <c>Template</c> es el atributo <c>path</c> (o sus alias antiguos <c>page</c>
/// y <c>template</c>): la JSP plantilla.
/// </summary>
public sealed record TilesDefinition(string Name, string? Template, string? Extends, IReadOnlyList<TilesPut> Puts, int Line);

/// <summary>Las definiciones de un fichero de Tiles, con su ruta relativa (con <c>/</c>).</summary>
public sealed record TilesDefinitionsDocument(string ConfigFile, IReadOnlyList<TilesDefinition> Definitions);

/// <summary>
/// Lector de ficheros <c>&lt;tiles-definitions&gt;</c> de Tiles 1.x (P14), con la misma carga segura que
/// <see cref="StrutsConfigParser"/>: sin DTD ni red, codificación de la declaración XML y líneas de <see cref="IXmlLineInfo"/>.
/// </summary>
public static class TilesDefinitionsParser
{
    /// <exception cref="XmlException">Si el XML está mal formado.</exception>
    /// <exception cref="IOException">Si no se puede leer.</exception>
    public static TilesDefinitionsDocument ParseFile(string relativePath, string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        return Parse(relativePath, stream);
    }

    /// <exception cref="XmlException">Si el XML está mal formado.</exception>
    public static TilesDefinitionsDocument Parse(string relativePath, Stream stream)
    {
        var document = SafeXml.Load(stream);
        var root = document.Root ?? throw new XmlException("El documento no tiene elemento raíz.");

        var definitions = new List<TilesDefinition>();
        foreach (var definition in SafeXml.Children(root, "definition"))
        {
            var name = SafeXml.Attribute(definition, "name");
            if (name == null)
                continue;

            var puts = new List<TilesPut>();
            foreach (var put in SafeXml.Children(definition, "put"))
            {
                var putName = SafeXml.Attribute(put, "name");
                if (putName == null)
                    continue;
                var body = put.Value.Trim();
                var value = SafeXml.Attribute(put, "value") ?? SafeXml.Attribute(put, "content") ?? (body.Length > 0 ? body : null);
                puts.Add(new TilesPut(putName, value, SafeXml.Attribute(put, "type"), SafeXml.Line(put)));
            }

            definitions.Add(new TilesDefinition(
                name,
                SafeXml.Attribute(definition, "path") ?? SafeXml.Attribute(definition, "page") ?? SafeXml.Attribute(definition, "template"),
                SafeXml.Attribute(definition, "extends"),
                puts,
                SafeXml.Line(definition)));
        }

        return new TilesDefinitionsDocument(relativePath, definitions);
    }
}
