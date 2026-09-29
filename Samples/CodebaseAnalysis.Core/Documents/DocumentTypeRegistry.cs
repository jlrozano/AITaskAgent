namespace CodebaseAnalysis.Core.Documents;

/// <summary>Familias de documentos admitidas (plan v2 §7.2), tal como salen en la API (<c>family</c>).</summary>
public static class DocumentFamilies
{
    public const string Markdown = "markdown";
    public const string Text = "text";
    public const string Code = "code";
    public const string Structured = "structured";
    public const string Csv = "csv";
    public const string Html = "html";
    public const string Pdf = "pdf";
    public const string Docx = "docx";
    public const string Xlsx = "xlsx";
    public const string Pptx = "pptx";
    public const string Image = "image";
    public const string Svg = "svg";
}

/// <summary>
/// Visor que usa el cliente para una familia (<c>viewer</c> en la API). Cinco valores, uno por componente:
/// <list type="bullet">
/// <item><c>markdown</c>: <c>MarkdownView</c> sobre <c>/text</c>. Familias markdown, code y structured (su
/// <c>content.md</c> es un bloque cercado con lenguaje, que se resalta), csv, docx, xlsx y pptx.</item>
/// <item><c>text</c>: <c>ExtractedTextView</c> (preformateado) sobre <c>/text</c>. Familia text.</item>
/// <item><c>pdf</c>: <c>iframe</c> sin sandbox sobre <c>/raw</c> y pestaña «Texto extraído» con <c>/text</c>.</item>
/// <item><c>html</c>: <c>iframe sandbox=""</c> sobre <c>/raw</c> y pestaña «Texto extraído».</item>
/// <item><c>image</c>: <c>img</c> sobre <c>/raw</c> y pestaña «Texto extraído». Familias image (con «Describir») y svg
/// (sin «Describir»: el SVG no se envía al modelo como imagen).</item>
/// </list>
/// Las pestañas «Vista» y «Texto extraído» solo existen con <c>pdf</c>, <c>html</c> e <c>image</c>, que es cuando difieren.
/// </summary>
public static class DocumentViewers
{
    public const string Markdown = "markdown";
    public const string Text = "text";
    public const string Pdf = "pdf";
    public const string Html = "html";
    public const string Image = "image";
}

/// <summary>Una familia: extensiones (en minúsculas, con punto), tamaño máximo del original y visor.</summary>
public sealed record DocumentTypeInfo(string Family, IReadOnlyList<string> Extensions, long MaxBytes, string Viewer);

/// <summary>
/// Tabla de tipos admitidos (plan v2 §7.2). Es contrato: la expone <c>/api/meta</c> (el cliente filtra por extensión
/// y tamaño antes de subir) y la aplica la subida, que además valida por contenido (V2-C). Los máximos son
/// constantes y no configuración porque los tests fijan su comportamiento. Cualquier otra extensión es
/// <c>unsupported_type</c>.
/// </summary>
public static class DocumentTypeRegistry
{
    private const long MiB = 1024 * 1024;

    public static IReadOnlyList<DocumentTypeInfo> All { get; } =
    [
        new(DocumentFamilies.Markdown, [".md", ".markdown"], 5 * MiB, DocumentViewers.Markdown),
        new(DocumentFamilies.Text, [".txt", ".log"], 5 * MiB, DocumentViewers.Text),
        new(DocumentFamilies.Code,
        [
            ".cs", ".vb", ".bas", ".cls", ".frm", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".java", ".kt", ".go", ".rs",
            ".c", ".h", ".cpp", ".hpp", ".sql", ".ps1", ".psm1", ".sh", ".bat", ".rb", ".php", ".swift", ".scala", ".css",
            ".scss", ".razor", ".cshtml", ".xaml", ".csproj", ".props", ".targets", ".gradle", ".toml", ".ini", ".cfg"
        ], 5 * MiB, DocumentViewers.Markdown),
        new(DocumentFamilies.Structured, [".json", ".yaml", ".yml", ".xml"], 5 * MiB, DocumentViewers.Markdown),
        new(DocumentFamilies.Csv, [".csv", ".tsv"], 10 * MiB, DocumentViewers.Markdown),
        new(DocumentFamilies.Html, [".html", ".htm"], 5 * MiB, DocumentViewers.Html),
        new(DocumentFamilies.Pdf, [".pdf"], 25 * MiB, DocumentViewers.Pdf),
        new(DocumentFamilies.Docx, [".docx"], 25 * MiB, DocumentViewers.Markdown),
        new(DocumentFamilies.Xlsx, [".xlsx"], 25 * MiB, DocumentViewers.Markdown),
        new(DocumentFamilies.Pptx, [".pptx"], 25 * MiB, DocumentViewers.Markdown),
        new(DocumentFamilies.Image, [".png", ".jpg", ".jpeg", ".gif", ".webp"], 10 * MiB, DocumentViewers.Image),
        new(DocumentFamilies.Svg, [".svg"], 2 * MiB, DocumentViewers.Image)
    ];

    /// <summary>El mayor de los máximos por familia (25 MiB): el límite de cuerpo de <c>POST /api/documents</c>.</summary>
    public static long MaxFileBytes { get; } = All.Max(t => t.MaxBytes);

    /// <summary>Familia de una extensión (con punto, sin distinguir mayúsculas); false si no se admite.</summary>
    public static bool TryGetByExtension(string? extension, out DocumentTypeInfo type)
    {
        type = All.FirstOrDefault(t => t.Extensions.Contains(extension ?? string.Empty, StringComparer.OrdinalIgnoreCase))!;
        return type != null;
    }
}
