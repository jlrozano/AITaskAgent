using CodebaseAnalysis.Core.Reports;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace CodebaseAnalysis.Core.Documents;

/// <summary>Límites de los ZIP de Office antes de abrirlos con OpenXml (plan v2 §7.2): anti-bomba.</summary>
public sealed record ZipLimits(long MaxUncompressedBytes = 200L * 1024 * 1024, int MaxEntries = 10_000)
{
    public static ZipLimits Default { get; } = new();
}

/// <summary>Resultado de validar por contenido: el tipo MIME detectado y, en las familias de texto, el texto decodificado.</summary>
public sealed record SniffResult(string MediaType, string? Text, IReadOnlyList<string> Warnings);

/// <summary>
/// Valida que el contenido casa con la familia declarada por la extensión (plan v2 §7.2, V2-D5): firmas binarias,
/// estructura de los ZIP de Office con límites anti-bomba, XML sin DTD en SVG y texto en UTF-8 estricto (o UTF-16 con
/// BOM) sin NUL. Un desajuste es <c>content_mismatch</c> con lo declarado y lo detectado.
/// </summary>
public static class ContentSniffer
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool IsTextFamily(string family) => family is DocumentFamilies.Markdown or DocumentFamilies.Text or DocumentFamilies.Code
        or DocumentFamilies.Structured or DocumentFamilies.Csv or DocumentFamilies.Html or DocumentFamilies.Svg;

    /// <summary>Valida <paramref name="bytes"/> para la familia de <paramref name="extension"/>.</summary>
    /// <exception cref="KnowledgeOperationException"><c>content_mismatch</c>.</exception>
    public static SniffResult Sniff(byte[] bytes, string extension, DocumentTypeInfo type, ZipLimits? zipLimits = null)
    {
        extension = extension.ToLowerInvariant();
        switch (type.Family)
        {
            case DocumentFamilies.Pdf:
                if (IndexOf(bytes, "%PDF-"u8, 1024) < 0)
                    throw Mismatch(extension, "PDF", Detect(bytes));
                return new SniffResult("application/pdf", null, []);

            case DocumentFamilies.Image:
                var detected = DetectImage(bytes);
                var expected = extension switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                if (detected != expected)
                    throw Mismatch(extension, expected, detected ?? Detect(bytes));
                return new SniffResult(detected, null, []);

            case DocumentFamilies.Docx:
            case DocumentFamilies.Xlsx:
            case DocumentFamilies.Pptx:
                CheckOfficeZip(bytes, extension, type.Family, zipLimits ?? ZipLimits.Default);
                return new SniffResult(OfficeMediaType(type.Family), null, []);
        }

        var text = DecodeText(bytes, extension);
        var warnings = new List<string>();
        var mediaType = TextMediaType(type.Family, extension);
        if (type.Family == DocumentFamilies.Svg)
            CheckSvg(text, extension);
        return new SniffResult(mediaType, text, warnings);
    }

    /// <summary>Texto en UTF-8 estricto (con o sin BOM) o UTF-16 con BOM, sin NUL en los primeros 8 KB.</summary>
    public static string DecodeText(byte[] bytes, string extension)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
        {
            var encoding = bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            var utf16 = encoding.GetString(bytes, 2, bytes.Length - 2);
            if (utf16.AsSpan(0, Math.Min(utf16.Length, 4096)).Contains('\0'))
                throw Mismatch(extension, "texto", "binario (NUL)");
            return utf16;
        }

        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        if (Array.IndexOf(bytes, (byte)0, start, Math.Min(8192, bytes.Length - start)) >= 0)
            throw Mismatch(extension, "texto", Detect(bytes));

        try
        {
            return StrictUtf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            throw Mismatch(extension, "texto UTF-8", Detect(bytes) is "binario" ? "texto en otra codificación o binario" : Detect(bytes));
        }
    }

    /// <summary>Nombre de lo que parece el contenido, para el mensaje de <c>content_mismatch</c>.</summary>
    public static string Detect(byte[] bytes)
    {
        if (StartsWith(bytes, "MZ"u8)) return "ejecutable (MZ)";
        if (StartsWith(bytes, "PK\u0003\u0004"u8)) return "ZIP";
        if (IndexOf(bytes, "%PDF-"u8, 1024) >= 0) return "PDF";
        if (DetectImage(bytes) is { } image) return image;
        if (StartsWith(bytes, "\u007FELF"u8)) return "ejecutable (ELF)";
        if (StartsWith(bytes, [0xD0, 0xCF, 0x11, 0xE0])) return "Office binario antiguo (OLE)";
        return Array.IndexOf(bytes, (byte)0, 0, Math.Min(8192, bytes.Length)) >= 0 ? "binario" : "texto";
    }

    private static string? DetectImage(byte[] bytes)
    {
        if (StartsWith(bytes, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (StartsWith(bytes, [0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (StartsWith(bytes, "GIF87a"u8) || StartsWith(bytes, "GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && StartsWith(bytes, "RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    private static void CheckOfficeZip(byte[] bytes, string extension, string family, ZipLimits limits)
    {
        if (!StartsWith(bytes, "PK\u0003\u0004"u8))
            throw Mismatch(extension, "documento Office (ZIP)", Detect(bytes));

        var mainPart = family switch
        {
            DocumentFamilies.Docx => "word/document.xml",
            DocumentFamilies.Xlsx => "xl/workbook.xml",
            _ => "ppt/presentation.xml"
        };

        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > limits.MaxEntries)
                throw new KnowledgeOperationException(KnowledgeErrorCodes.ContentMismatch,
                    $"El fichero '{extension}' es un ZIP con {zip.Entries.Count} entradas (máximo {limits.MaxEntries}): se rechaza por seguridad.");

            var declared = zip.Entries.Sum(e => e.Length);
            if (declared > limits.MaxUncompressedBytes)
                throw new KnowledgeOperationException(KnowledgeErrorCodes.ContentMismatch,
                    $"El fichero '{extension}' declara {declared / (1024 * 1024)} MB descomprimidos (máximo {limits.MaxUncompressedBytes / (1024 * 1024)} MB): se rechaza por seguridad.");

            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!names.Contains("[Content_Types].xml") || !names.Contains(mainPart))
                throw Mismatch(extension, $"documento Office con {mainPart}", "otro ZIP");
        }
        catch (InvalidDataException)
        {
            throw Mismatch(extension, "documento Office (ZIP)", "ZIP dañado");
        }
    }

    private static void CheckSvg(string text, string extension)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (!string.Equals(reader.LocalName, "svg", StringComparison.Ordinal))
                        throw Mismatch(extension, "SVG", $"XML con raíz <{reader.LocalName}>");
                    return;
                }
            }
            throw Mismatch(extension, "SVG", "XML sin elementos");
        }
        catch (XmlException ex)
        {
            throw Mismatch(extension, "SVG (XML sin DTD)", $"XML no válido: {ex.Message}");
        }
    }

    public static string OfficeMediaType(string family) => family switch
    {
        DocumentFamilies.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        DocumentFamilies.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    private static string TextMediaType(string family, string extension) => family switch
    {
        DocumentFamilies.Markdown => "text/markdown",
        DocumentFamilies.Html => "text/html",
        DocumentFamilies.Svg => "image/svg+xml",
        DocumentFamilies.Csv => extension == ".tsv" ? "text/tab-separated-values" : "text/csv",
        DocumentFamilies.Structured => extension switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            _ => "application/yaml"
        },
        _ => "text/plain"
    };

    private static KnowledgeOperationException Mismatch(string extension, string declared, string detected) =>
        new(KnowledgeErrorCodes.ContentMismatch,
            $"El contenido no corresponde a la extensión '{extension}': declarado {declared}, detectado {detected}.");

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) => bytes.AsSpan().StartsWith(prefix);

    private static int IndexOf(byte[] bytes, ReadOnlySpan<byte> value, int within) =>
        bytes.AsSpan(0, Math.Min(bytes.Length, within)).IndexOf(value);
}
