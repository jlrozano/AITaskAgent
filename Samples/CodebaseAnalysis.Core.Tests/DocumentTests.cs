using AITaskAgent.LLM.Abstractions;
using AITaskAgent.LLM.Constants;
using AITaskAgent.LLM.Models;
using CodebaseAnalysis.Core.Documents;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Reports;
using CodebaseAnalysis.Core.Steps;
using Newtonsoft.Json.Linq;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>V2-C (plan v2 §7): validación, extracción por familia, almacén, descripción de imágenes y faceta de los auditores.</summary>
public sealed class DocumentTests
{
    private static (DocumentStore Store, KnowledgePaths Paths) NewStore(TempDirectory dir, DocumentOptions? options = null, ZipLimits? zip = null)
    {
        var paths = new KnowledgePaths(dir.Path, Path.Combine(dir.Path, "reports"));
        return (new DocumentStore(paths, options, zipLimits: zip), paths);
    }

    private static Task<DocumentRecord> Upload(DocumentStore store, string name, byte[] bytes) =>
        store.UploadAsync(new MemoryStream(bytes), Uri.EscapeDataString(name), CancellationToken.None);

    [Theory]
    [InlineData("informe.pdf", ".pdf", "informe")]
    [InlineData("Especificaci%C3%B3n%20API.PDF", ".pdf", "Especificación API")]
    public void Valid_names_are_decoded_with_their_extension(string encoded, string extension, string title)
    {
        var (_, ext, t) = FileNameSanitizer.Validate(encoded);
        Assert.Equal((extension, title), (ext, t));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("..%2Fsecreto.md")]
    [InlineData("a%5Cb.md")]
    [InlineData("CON.txt")]
    [InlineData("com1.md")]
    [InlineData("sin-extension")]
    [InlineData(".md")]
    [InlineData("nota.md.")]
    [InlineData("a%00b.md")]
    [InlineData("tab%09.md")]
    public void Invalid_names_are_invalid_file_name(string? encoded) =>
        Assert.Equal("invalid_file_name", Assert.Throws<KnowledgeOperationException>(() => FileNameSanitizer.Validate(encoded)).Code);

    [Fact]
    public async Task Every_family_is_uploaded_validated_and_extracted()
    {
        using var dir = new TempDirectory();
        var (store, paths) = NewStore(dir);
        var results = new Dictionary<string, DocumentRecord>();
        foreach (var (name, bytes) in SampleFiles.All())
            results[name] = await Upload(store, name, bytes);

        Assert.Equal(paths.CountDocuments(), store.List().Count);
        Assert.Equal(SampleFiles.All().Count, store.List().Count);
        Assert.All(results.Values, r => Assert.Matches("^[0-9]{8}-[0-9]{6}-[a-z0-9]{4}$", r.Id));

        string Content(string name) => store.ReadContent(results[name].Id);

        var md = results["guia.md"];
        Assert.Equal(("markdown", "text/markdown", "markdown", "ok"), (md.Family, md.MediaType, md.Viewer, md.Extraction.Status));
        Assert.Equal(["Guía de pagos", "Validación", "Detalle"], md.Sections.Select(s => s.Heading));
        Assert.Equal([1, 2, 3], md.Sections.Select(s => s.Level));

        Assert.Equal("text", results["notas.txt"].Viewer);
        Assert.StartsWith("```csharp\nnamespace Demo;", Content("Pagos.cs"));
        Assert.Contains("\"b\": [", Content("config.json"));
        Assert.Contains(results["roto.json"].Extraction.Warnings, w => w.Contains("JSON no válido"));

        var csv = Content("datos.csv");
        Assert.Contains("| id | nombre | nota |", csv);
        Assert.Contains("| 1 | Pérez; Juan | dice \"hola\" |", csv);

        var html = Content("pagina.html");
        Assert.Contains("# Título HTML", html);
        Assert.Contains("**negrita**", html);
        Assert.DoesNotContain("fetch", html);
        Assert.DoesNotContain("onerror", html);

        var pdf = results["contrato.pdf"];
        Assert.Equal((2, "pdf"), (pdf.Extraction.PageCount, pdf.Viewer));
        Assert.Equal(["Página 1", "Página 2"], pdf.Sections.Select(s => s.Heading));
        Assert.Contains("Segunda pagina con anexos", Content("contrato.pdf"));

        var docx = Content("especificacion.docx");
        Assert.Contains("# Especificación API", docx);
        Assert.Contains("## Requisitos", docx);
        Assert.Contains("Primer requisito", docx);
        Assert.Contains("importe", docx);

        var xlsx = results["gastos.xlsx"];
        Assert.Equal(1, xlsx.Extraction.PageCount);
        var sheet = Content("gastos.xlsx");
        Assert.Contains("## Hoja: Gastos", sheet);
        Assert.Contains("| Concepto | Importe | Fecha |", sheet);
        Assert.Contains("| Alquiler | 1250.5 | 2023-03-15 |", sheet);
        Assert.Contains("| VERDADERO | 2501 |", sheet);

        var pptx = results["presentacion.pptx"];
        Assert.Equal(2, pptx.Extraction.PageCount);
        Assert.Equal(["Diapositiva 1 — Arquitectura", "Diapositiva 2 — Riesgos"], pptx.Sections.Select(s => s.Heading));
        Assert.Contains("> Notas: Explicar el flujo", Content("presentacion.pptx"));

        var png = results["dos-colores.png"];
        Assert.Equal(("image", "image/png", "image", "pending_description"), (png.Family, png.MediaType, png.Viewer, png.Extraction.Status));
        Assert.Equal(string.Empty, Content("dos-colores.png"));

        var svg = Content("diagrama.svg");
        Assert.Equal("image/svg+xml", results["diagrama.svg"].MediaType);
        Assert.Contains("# Diagrama", svg);
        Assert.Contains("- Factura", svg);

        // El original se guarda como original.bin, con sus bytes.
        Assert.Equal(SampleFiles.Pdf().Length, new FileInfo(store.OriginalPath(pdf.Id)).Length);
        Assert.Empty(Directory.GetFiles(paths.IncomingDocumentsPath));
    }

    [Theory]
    [InlineData("foto.png", "text", "content_mismatch")]
    [InlineData("foto.png", "mz", "content_mismatch")]
    [InlineData("doc.pdf", "mz", "content_mismatch")]
    [InlineData("nota.md", "binary", "content_mismatch")]
    [InlineData("hoja.docx", "text", "content_mismatch")]
    [InlineData("imagen.jpg", "png", "content_mismatch")]
    [InlineData("viejo.doc", "text", "unsupported_type")]
    [InlineData("setup.exe", "mz", "unsupported_type")]
    [InlineData("paquete.zip", "text", "unsupported_type")]
    [InlineData("nota.md", "empty", "empty_file")]
    public async Task Rejections_have_their_codes(string name, string content, string code)
    {
        using var dir = new TempDirectory();
        var (store, _) = NewStore(dir);
        var bytes = content switch
        {
            "text" => SampleFiles.Utf8("hola"),
            "mz" => SampleFiles.Executable(),
            "binary" => [0x41, 0x00, 0x42, 0xFF],
            "png" => SampleFiles.Png(),
            _ => []
        };

        var ex = await Assert.ThrowsAsync<KnowledgeOperationException>(() => Upload(store, name, bytes));
        Assert.Equal(code, ex.Code);
        Assert.Empty(store.List());
        if (name.EndsWith(".doc"))
            Assert.Contains(".docx", ex.Message);
    }

    [Fact]
    public void Image_signatures_are_detected_and_webp_gif_jpeg_pass()
    {
        DocumentTypeRegistry.TryGetByExtension(".png", out var image);
        Assert.Equal("image/gif", ContentSniffer.Sniff(SampleFiles.GifSignature(), ".gif", image).MediaType);
        Assert.Equal("image/jpeg", ContentSniffer.Sniff(SampleFiles.JpegSignature(), ".jpeg", image).MediaType);
        Assert.Equal("image/webp", ContentSniffer.Sniff(SampleFiles.WebpSignature(), ".webp", image).MediaType);
    }

    [Fact]
    public async Task Zip_bombs_duplicates_sizes_and_limits_are_rejected()
    {
        using var dir = new TempDirectory();
        var (store, _) = NewStore(dir, new DocumentOptions { MaxDocuments = 2 }, new ZipLimits(MaxUncompressedBytes: 1000));

        var bomb = await Assert.ThrowsAsync<KnowledgeOperationException>(() => Upload(store, "bomba.docx", SampleFiles.ZipWithLargeEntry(5000)));
        Assert.Equal("content_mismatch", bomb.Code);
        Assert.Contains("descomprimidos", bomb.Message);

        var first = await Upload(store, "a.md", SampleFiles.Markdown());
        var duplicate = await Assert.ThrowsAsync<KnowledgeOperationException>(() => Upload(store, "otra.md", SampleFiles.Markdown()));
        Assert.Equal(("duplicate_document", first.Id), (duplicate.Code, duplicate.DocumentId));

        var tooLarge = await Assert.ThrowsAsync<KnowledgeOperationException>(() => Upload(store, "grande.md", new byte[5 * 1024 * 1024 + 1]));
        Assert.Equal("file_too_large", tooLarge.Code);

        await Upload(store, "b.md", SampleFiles.Utf8("# B"));
        Assert.Equal("document_limit_reached", (await Assert.ThrowsAsync<KnowledgeOperationException>(() => Upload(store, "c.md", SampleFiles.Utf8("# C")))).Code);
    }

    [Fact]
    public async Task Patch_delete_and_description_update_the_record()
    {
        using var dir = new TempDirectory();
        var (store, _) = NewStore(dir);
        var image = await Upload(store, "dos-colores.png", SampleFiles.Png());

        var patched = store.Patch(image.Id, "  Captura  ", false);
        Assert.Equal(("Captura", false), (patched.Title, patched.UseInAnalysis));
        Assert.Equal("invalid_request", Assert.Throws<KnowledgeOperationException>(() => store.Patch(image.Id, " ", null)).Code);

        var described = store.SaveDescription(image.Id, "Mitad roja y mitad azul.", "gpt-5.4");
        Assert.Equal("ok", described.Extraction.Status);
        Assert.Equal("gpt-5.4", described.Description!.Model);
        Assert.Contains("Descripción generada por gpt-5.4", store.ReadContent(image.Id));
        Assert.Contains("puede contener errores", store.ReadContent(image.Id));

        Assert.True(store.Delete(image.Id));
        Assert.Null(store.TryGet(image.Id));
    }

    [Fact]
    public async Task Image_describer_sends_a_native_message_with_image_url_and_detail_high()
    {
        var llm = new CapturingLlmService("Mitad izquierda roja, mitad derecha azul.");
        var role = new AITaskAgent.LLM.Configuration.LlmRole
        {
            Provider = new AITaskAgent.LLM.Configuration.LlmProvider { Service = llm, BaseUrl = "http://fake.invalid/", ApiKey = "k" },
            Model = "vision-model"
        };
        var describer = new ImageDescriber(role, AnalysisPipelineFactory.CreateDefaultTemplateProvider());

        var text = await describer.DescribeAsync(SampleFiles.Png(), "image/png", "¿Qué colores hay?", CancellationToken.None);

        Assert.Equal("Mitad izquierda roja, mitad derecha azul.", text);
        var message = Assert.Single(llm.Last!.Conversation.History.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal("OpenAI", message.Native!.Provider);
        var content = (JArray)((JObject)message.Native.Value)["content"]!;
        Assert.Equal("text", (string?)content[0]["type"]);
        Assert.Contains("¿Qué colores hay?", (string?)content[0]["text"]);
        Assert.Equal("high", (string?)content[1]["image_url"]!["detail"]);
        Assert.StartsWith("data:image/png;base64,", (string?)content[1]["image_url"]!["url"]);
        Assert.False(llm.Last.UseSlidingWindow);
        Assert.Equal("vision-model", llm.Last.Model);

        var big = await Assert.ThrowsAsync<KnowledgeOperationException>(() =>
            describer.DescribeAsync(new byte[ImageDescriber.MaxImageBytes + 1], "image/png", null, CancellationToken.None));
        Assert.Equal("image_description_unavailable", big.Code);
    }

    [Fact]
    public async Task Provided_documentation_facet_indexes_documents_and_neutralizes_includes()
    {
        using var dir = new TempDirectory();
        var (store, paths) = NewStore(dir);
        Assert.Equal("[]", await ProvidedDocumentationFacet.BuildAsync(paths, null, 8000, CancellationToken.None));

        await Upload(store, "guia.md", SampleFiles.Utf8("# Guía\n\nIncluye @[secreto.md] y más.\n"));
        var hidden = await Upload(store, "oculto.md", SampleFiles.Utf8("# Oculto"));
        store.Patch(hidden.Id, null, false);

        var facet = await ProvidedDocumentationFacet.BuildAsync(paths, null, 8000, CancellationToken.None);
        Assert.Contains("\"title\":\"guia\"", facet);
        Assert.DoesNotContain("@[", facet, StringComparison.Ordinal);
        Assert.Contains("@​[secreto.md]", facet);
        Assert.DoesNotContain("oculto", facet);
    }

    [Fact]
    public void Auditor_prompts_include_the_block_and_without_documents_only_add_the_rule()
    {
        var provider = AnalysisPipelineFactory.CreateDefaultTemplateProvider();
        var templatesDir = Path.Combine(AppContext.BaseDirectory, "templates");
        var rule = provider.Render("_provided_documentation", new Dictionary<string, object?> { ["provided_documentation"] = "[]" })!;
        foreach (var prompt in Directory.GetFiles(templatesDir, "report_*_prompt.md").Where(f => !f.Contains("sintesis")))
        {
            var name = Path.GetFileNameWithoutExtension(prompt);
            var withDocs = provider.Render(name, new Dictionary<string, object?> { ["provided_documentation"] = "[{\"id\":\"doc-1\"}]" })!;
            Assert.Contains("{\"id\":\"doc-1\"}", withDocs);
            Assert.Contains("Documentación aportada por el usuario", withDocs);
            Assert.DoesNotContain("{{ provided_documentation }}", withDocs);

            // Sin documentos, el prompt es el de antes más la regla con el bloque vacío.
            var withoutDocs = provider.Render(name, new Dictionary<string, object?> { ["provided_documentation"] = "[]" })!;
            Assert.Contains(rule.Trim(), withoutDocs);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(prompt), "_provided_documentation"));
        }
    }

    private sealed class CapturingLlmService(string answer) : ILlmService
    {
        public LlmRequest? Last { get; private set; }
        public string Name => "capturing";

        public Task<LlmResult> InvokeAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(LlmResult.Success([new LlmResponse { Content = answer, FinishReason = FinishReason.Stop }], model: request.Model));
        }

        public IAsyncEnumerable<LlmStreamChunk> InvokeStreamingAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int EstimateTokenCount(string text) => text.Length / 4;
        public int GetMaxContextTokens(string? model = null) => 1_000_000;
    }
}

/// <summary>Escribe las muestras en <c>V2_SAMPLES_DIR</c> para la verificación real (omitido sin la variable).</summary>
public sealed class SampleFilesExport
{
    [Fact]
    public void Export_samples_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("V2_SAMPLES_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in SampleFiles.All())
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
    }
}
