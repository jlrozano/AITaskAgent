using System.Xml;
using System.Xml.Linq;

namespace CodebaseAnalysis.Core.Enrichment.Web;

/// <summary>
/// Un <c>&lt;forward&gt;</c> de Struts, local de una acción o global: nombre lógico (<c>mapping.findForward(name)</c>),
/// destino tal cual (JSP de contexto, acción <c>.do</c> o nombre de definición de Tiles), <c>redirect</c> y línea del
/// elemento en el fichero.
/// </summary>
public sealed record StrutsForward(string Name, string Path, bool Redirect, int Line);

/// <summary>
/// Un <c>&lt;action&gt;</c> de <c>action-mappings</c>, con sus forwards locales en orden de documento. <c>Forward</c> e
/// <c>Include</c> son los atributos de una acción que solo reenvía o incluye, si los tiene.
/// </summary>
public sealed record StrutsAction(
    string Path,
    string? Type,
    string? FormBean,
    string? Scope,
    string? Input,
    string? Parameter,
    string? Forward,
    string? Include,
    int Line,
    IReadOnlyList<StrutsForward> Forwards);

/// <summary>
/// Lo que el plugin usa de un <c>struts-config*.xml</c>: su ruta relativa (con <c>/</c>), los <c>form-bean</c> (nombre →
/// clase), los forwards globales y las acciones.
/// </summary>
public sealed record StrutsConfigDocument(
    string ConfigFile,
    IReadOnlyDictionary<string, string> FormBeans,
    IReadOnlyList<StrutsForward> GlobalForwards,
    IReadOnlyList<StrutsAction> Actions);

/// <summary>
/// Lector de <c>struts-config</c> de Struts 1.x (P14). No valida contra la DTD ni la descarga: <c>DtdProcessing.Ignore</c> y
/// sin <c>XmlResolver</c>, así que un <c>DOCTYPE</c> con URL no toca la red. La codificación sale de la declaración XML
/// (ISO-8859-1 en los proyectos antiguos). Las líneas salen de <see cref="IXmlLineInfo"/>.
/// </summary>
public static class StrutsConfigParser
{
    /// <exception cref="XmlException">Si el XML está mal formado.</exception>
    /// <exception cref="IOException">Si no se puede leer.</exception>
    public static StrutsConfigDocument ParseFile(string relativePath, string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        return Parse(relativePath, stream);
    }

    /// <exception cref="XmlException">Si el XML está mal formado.</exception>
    public static StrutsConfigDocument Parse(string relativePath, Stream stream)
    {
        var document = SafeXml.Load(stream);
        var root = document.Root ?? throw new XmlException("El documento no tiene elemento raíz.");

        var formBeans = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bean in SafeXml.Children(root, "form-beans").SelectMany(e => SafeXml.Children(e, "form-bean")))
        {
            var name = SafeXml.Attribute(bean, "name");
            var type = SafeXml.Attribute(bean, "type");
            if (name != null && type != null)
                formBeans.TryAdd(name, type);
        }

        var globalForwards = SafeXml.Children(root, "global-forwards")
            .SelectMany(e => SafeXml.Children(e, "forward"))
            .Select(ReadForward)
            .OfType<StrutsForward>()
            .ToList();

        var actions = new List<StrutsAction>();
        foreach (var action in SafeXml.Children(root, "action-mappings").SelectMany(e => SafeXml.Children(e, "action")))
        {
            var path = SafeXml.Attribute(action, "path");
            if (path == null)
                continue;
            actions.Add(new StrutsAction(
                path,
                SafeXml.Attribute(action, "type"),
                SafeXml.Attribute(action, "name"),
                SafeXml.Attribute(action, "scope"),
                SafeXml.Attribute(action, "input"),
                SafeXml.Attribute(action, "parameter"),
                SafeXml.Attribute(action, "forward"),
                SafeXml.Attribute(action, "include"),
                SafeXml.Line(action),
                SafeXml.Children(action, "forward").Select(ReadForward).OfType<StrutsForward>().ToList()));
        }

        return new StrutsConfigDocument(relativePath, formBeans, globalForwards, actions);
    }

    private static StrutsForward? ReadForward(XElement forward)
    {
        var name = SafeXml.Attribute(forward, "name");
        var path = SafeXml.Attribute(forward, "path");
        if (name == null || path == null)
            return null;
        var redirect = string.Equals(SafeXml.Attribute(forward, "redirect"), "true", StringComparison.OrdinalIgnoreCase);
        return new StrutsForward(name, path, redirect, SafeXml.Line(forward));
    }
}

/// <summary>Carga de XML sin red ni DTD y acceso por nombre local, compartida por los lectores de Struts y de Tiles.</summary>
internal static class SafeXml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    public static XDocument Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, Settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    /// <summary>Hijos directos por nombre local: los ficheros de Struts y Tiles no usan espacio de nombres, pero no se exige.</summary>
    public static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    /// <summary>Valor del atributo recortado, o null si falta o está vacío.</summary>
    public static string? Attribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static int Line(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
}
