using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// Ficheros de muestra de cada familia de §7.2, generados en código (plan v2 §11): nunca binarios hechos a mano. Los
/// usan los tests de Core y de la web y, con <c>V2_SAMPLES_DIR</c>, la exportación para la verificación real.
/// </summary>
public static class SampleFiles
{
    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    public static byte[] Markdown() => Utf8("# Guía de pagos\n\nIntro.\n\n## Validación\n\nLa clase `PaymentValidator` valida.\n\n### Detalle\n\nMás texto.\n");

    public static byte[] Csv() => Utf8("id;nombre;nota\n1;\"Pérez; Juan\";\"dice \"\"hola\"\"\"\n2;Ana;ok\n");

    public static byte[] InvalidJson() => Utf8("{ \"a\": 1, ");

    public static byte[] Html() => Utf8("<html><head><title>Página de prueba</title><script>fetch('/api/workspace',{method:'PUT'})</script></head>" +
        "<body><h1>Título HTML</h1><p>Párrafo con <b>negrita</b>.</p><img src=x onerror=\"alert(1)\"><style>p{}</style></body></html>");

    public static byte[] Svg() => Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><title>Diagrama</title><desc>Flujo de pedidos</desc>" +
        "<text x=\"1\" y=\"1\">Pedido</text><text><tspan>Factura</tspan></text></svg>");

    public static byte[] Docx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            static W.Paragraph Para(string text, string? style = null, bool bullet = false)
            {
                var p = new W.Paragraph();
                if (style != null || bullet)
                {
                    var props = new W.ParagraphProperties();
                    if (style != null)
                        props.ParagraphStyleId = new W.ParagraphStyleId { Val = style };
                    if (bullet)
                        props.NumberingProperties = new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = 1 });
                    p.AppendChild(props);
                }
                p.AppendChild(new W.Run(new W.Text(text)));
                return p;
            }
            static W.TableCell Cell(string text) => new(new W.Paragraph(new W.Run(new W.Text(text))));

            main.Document = new W.Document(new W.Body(
                Para("Especificación API", "Heading1"),
                Para("Texto de introducción."),
                Para("Requisitos", "Heading2"),
                Para("Primer requisito", bullet: true),
                Para("Segundo requisito", bullet: true),
                new W.Table(
                    new W.TableRow(Cell("Campo"), Cell("Tipo")),
                    new W.TableRow(Cell("importe"), Cell("decimal")))));
        }
        return ms.ToArray();
    }

    public static byte[] Xlsx()
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new S.Workbook();
            var shared = wb.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new S.SharedStringTable(
                new S.SharedStringItem(new S.Text("Concepto")),
                new S.SharedStringItem(new S.Text("Importe")),
                new S.SharedStringItem(new S.Text("Fecha")),
                new S.SharedStringItem(new S.Text("Alquiler")));
            var styles = wb.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(
                new S.Fonts(new S.Font()), new S.Fills(new S.Fill()), new S.Borders(new S.Border()),
                new S.CellFormats(new S.CellFormat(), new S.CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }));

            var sheetPart = wb.AddNewPart<WorksheetPart>();
            S.Cell Shared(string r, int i) => new() { CellReference = r, DataType = S.CellValues.SharedString, CellValue = new S.CellValue(i.ToString()) };
            sheetPart.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(Shared("A1", 0), Shared("B1", 1), Shared("C1", 2)) { RowIndex = 1 },
                new S.Row(
                    Shared("A2", 3),
                    new S.Cell { CellReference = "B2", CellValue = new S.CellValue("1250.5") },
                    new S.Cell { CellReference = "C2", StyleIndex = 1, CellValue = new S.CellValue("45000") }) { RowIndex = 2 },
                new S.Row(
                    new S.Cell { CellReference = "A3", DataType = S.CellValues.Boolean, CellValue = new S.CellValue("1") },
                    new S.Cell { CellReference = "B3", CellFormula = new S.CellFormula("B2*2"), CellValue = new S.CellValue("2501") }) { RowIndex = 3 }));
            wb.Workbook.AppendChild(new S.Sheets(new S.Sheet { Id = wb.GetIdOfPart(sheetPart), SheetId = 1, Name = "Gastos" }));
        }
        return ms.ToArray();
    }

    public static byte[] Pptx()
    {
        using var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var pres = doc.AddPresentationPart();
            pres.Presentation = new P.Presentation();
            var ids = new P.SlideIdList();
            uint next = 256;
            foreach (var (title, body, notes) in new[] { ("Arquitectura", "Tres capas", "Explicar el flujo"), ("Riesgos", "Deuda técnica", (string?)null) })
            {
                var slidePart = pres.AddNewPart<SlidePart>();
                slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                    Shape(1, title, P.PlaceholderValues.Title),
                    Shape(2, body, null))));
                if (notes != null)
                {
                    var notesPart = slidePart.AddNewPart<NotesSlidePart>();
                    notesPart.NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(Shape(3, notes, P.PlaceholderValues.Body))));
                }
                ids.AppendChild(new P.SlideId { Id = next++, RelationshipId = pres.GetIdOfPart(slidePart) });
            }
            pres.Presentation.AppendChild(ids);
        }
        return ms.ToArray();

        static P.Shape Shape(uint id, string text, P.PlaceholderValues? placeholder)
        {
            var app = new P.ApplicationNonVisualDrawingProperties();
            if (placeholder is { } ph)
                app.PlaceholderShape = new P.PlaceholderShape { Type = ph };
            return new P.Shape(
                new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = id, Name = $"s{id}" }, new P.NonVisualShapeDrawingProperties(), app),
                new P.ShapeProperties(),
                new P.TextBody(new A.BodyProperties(), new A.Paragraph(new A.Run(new A.Text(text)))));
        }
    }

    public static byte[] Pdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Primera pagina del contrato", 12, new PdfPoint(40, 750), font);
        builder.AddPage(PageSize.A4).AddText("Segunda pagina con anexos", 12, new PdfPoint(40, 750), font);
        return builder.Build();
    }

    /// <summary>PNG de <paramref name="width"/>×<paramref name="height"/>: mitad izquierda roja y derecha azul (la prueba de §2.1).</summary>
    public static byte[] Png(int width = 16, int height = 8)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var red = x < width / 2;
                raw.Write([(byte)(red ? 255 : 0), 0, (byte)(red ? 0 : 255)]);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(z);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    public static byte[] GifSignature() => Utf8("GIF89a").Concat(new byte[16]).ToArray();
    public static byte[] JpegSignature() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat(new byte[16]).ToArray();
    public static byte[] WebpSignature() => Utf8("RIFF").Concat(new byte[4]).Concat(Utf8("WEBPVP8 ")).Concat(new byte[8]).ToArray();
    public static byte[] Executable() => Utf8("MZ").Concat(new byte[64]).ToArray();

    /// <summary>ZIP con la estructura de un DOCX y una entrada de <paramref name="entryBytes"/> descomprimidos (para el límite anti-bomba).</summary>
    public static byte[] ZipWithLargeEntry(int entryBytes)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry("word/document.xml");
            using var stream = zip.CreateEntry("word/big.bin", CompressionLevel.Optimal).Open();
            stream.Write(new byte[entryBytes]);
        }
        return ms.ToArray();
    }

    /// <summary>Todas las familias: nombre de fichero y bytes.</summary>
    public static IReadOnlyList<(string FileName, byte[] Bytes)> All() =>
    [
        ("guia.md", Markdown()),
        ("notas.txt", Utf8("Primer párrafo.\n\nSegundo párrafo.\n")),
        ("Pagos.cs", Utf8("namespace Demo;\npublic class Pagos { }\n")),
        ("config.json", Utf8("{\"a\":1,\"b\":[1,2]}")),
        ("roto.json", InvalidJson()),
        ("datos.csv", Csv()),
        ("pagina.html", Html()),
        ("contrato.pdf", Pdf()),
        ("especificacion.docx", Docx()),
        ("gastos.xlsx", Xlsx()),
        ("presentacion.pptx", Pptx()),
        ("dos-colores.png", Png()),
        ("diagrama.svg", Svg())
    ];

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        png.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, (int)Crc32([.. typeBytes, .. data]));
        png.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
