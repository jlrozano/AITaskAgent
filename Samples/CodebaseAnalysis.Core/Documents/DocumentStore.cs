using CodebaseAnalysis.Core.Documents.Extraction;
using CodebaseAnalysis.Core.Knowledge;
using CodebaseAnalysis.Core.Reports;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Documents;

/// <summary>
/// Documentos aportados (plan v2 §7.1): una carpeta por documento en <c>&lt;repo&gt;/.codebase/documents/&lt;id&gt;/</c> con
/// <c>original.bin</c>, <c>content.md</c> y <c>document.json</c>, sin manifiesto global. La subida valida nombre, tipo,
/// tamaño, límites, duplicado y contenido, escribe a <c>.incoming/</c>, extrae y solo entonces mueve a su carpeta.
/// </summary>
public sealed class DocumentStore(KnowledgePaths paths, DocumentOptions? options = null, TimeProvider? clock = null, ZipLimits? zipLimits = null)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Una escritura de documentos a la vez en el proceso: el límite, el duplicado y el id se comprueban juntos.</summary>
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private static readonly HashSet<string> LegacyOffice = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".xls", ".ppt" };

    private static readonly HashSet<string> ExecutablesAndArchives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".com", ".scr", ".cpl", ".sys", ".so", ".dylib", ".app", ".apk", ".jar", ".war",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".iso"
    };

    private readonly DocumentOptions _options = options ?? new DocumentOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ZipLimits _zipLimits = zipLimits ?? ZipLimits.Default;

    /// <summary>
    /// Un cerrojo por carpeta de documento: <see cref="Patch"/> y <see cref="SaveDescription"/> leen y reescriben
    /// <c>document.json</c> enteros y, sin él, uno pisaría el cambio del otro.
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> RecordLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extractor y plazo de la subida; los tests ponen uno lento para probar que el plazo corta.</summary>
    internal Func<ExtractionInput, CancellationToken, ExtractionResult>? ExtractorOverride { get; init; }

    internal TimeSpan ExtractionTimeout { get; init; } = DocumentExtractor.Timeout;

    public KnowledgePaths Paths { get; } = paths;

    /// <summary>Documentos presentes, el más reciente primero. Da el mismo número que <see cref="KnowledgePaths.CountDocuments"/>.</summary>
    public IReadOnlyList<DocumentRecord> List()
    {
        if (!Directory.Exists(Paths.DocumentsPath))
            return [];

        var result = new List<DocumentRecord>();
        foreach (var dir in Directory.EnumerateDirectories(Paths.DocumentsPath))
        {
            var id = Path.GetFileName(dir);
            if (SourceRef.DocumentIdPattern().IsMatch(id) && TryGet(id) is { } record)
                result.Add(record);
        }
        return result.OrderByDescending(r => r.UploadedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>El documento, o null si el id no es válido, no existe o su <c>document.json</c> está dañado.</summary>
    public DocumentRecord? TryGet(string? id)
    {
        if (id == null || !SourceRef.DocumentIdPattern().IsMatch(id))
            return null;

        var file = Path.Combine(Folder(id), KnowledgePaths.DocumentMetadataFileName);
        if (!File.Exists(file))
            return null;

        try
        {
            return JsonSerializer.Deserialize<DocumentRecord>(File.ReadAllText(file), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary><c>content.md</c> del documento (lo que lee el LLM); vacío si no tiene.</summary>
    public string ReadContent(string id)
    {
        var file = Path.Combine(Folder(id), KnowledgePaths.DocumentContentFileName);
        return File.Exists(file) ? File.ReadAllText(file) : string.Empty;
    }

    public string ContentPath(string id) => Path.Combine(Folder(id), KnowledgePaths.DocumentContentFileName);

    public string OriginalPath(string id) => Path.Combine(Folder(id), KnowledgePaths.DocumentOriginalFileName);

    public long TotalBytes() => List().Sum(r => r.SizeBytes);

    /// <summary>
    /// Sube un documento. <paramref name="encodedFileName"/> es la cabecera <c>X-File-Name</c> tal cual.
    /// </summary>
    /// <exception cref="KnowledgeOperationException">Con los códigos de §5.1 (nombre, tipo, tamaño, vacío, límite, duplicado, contenido).</exception>
    public async Task<DocumentRecord> UploadAsync(Stream body, string? encodedFileName, CancellationToken cancellationToken)
    {
        var (fileName, extension, title) = FileNameSanitizer.Validate(encodedFileName);
        var type = ResolveType(extension);
        var bytes = await ReadLimitedAsync(body, type, cancellationToken);
        if (bytes.Length == 0)
            throw new KnowledgeOperationException(KnowledgeErrorCodes.EmptyFile, $"El fichero '{fileName}' está vacío.");

        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            var existing = List();
            if (existing.Count >= _options.MaxDocuments)
            {
                throw new KnowledgeOperationException(KnowledgeErrorCodes.DocumentLimitReached,
                    $"Se ha alcanzado el máximo de {_options.MaxDocuments} documentos en este repositorio.");
            }
            if (existing.Sum(r => r.SizeBytes) + bytes.LongLength > _options.MaxTotalBytes)
            {
                throw new KnowledgeOperationException(KnowledgeErrorCodes.DocumentLimitReached,
                    $"Con este fichero se superarían los {_options.MaxTotalMegabytes} MB de documentos del repositorio.");
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (existing.FirstOrDefault(r => r.Sha256 == sha) is { } duplicate)
                throw new KnowledgeOperationException(KnowledgeErrorCodes.DuplicateDocument, $"'{fileName}' ya estaba subido como '{duplicate.Title}'.", duplicate.Id);

            var sniff = ContentSniffer.Sniff(bytes, extension, type, _zipLimits);

            Directory.CreateDirectory(Paths.IncomingDocumentsPath);
            var incoming = Path.Combine(Paths.IncomingDocumentsPath, $"{Guid.NewGuid():N}{extension}");
            string? folder = null;
            try
            {
                await File.WriteAllBytesAsync(incoming, bytes, cancellationToken);
                var extractionInput = new ExtractionInput(incoming, sniff.Text, type, extension, title);
                var extraction = ExtractorOverride == null
                    ? await DocumentExtractor.ExtractAsync(extractionInput, cancellationToken)
                    : await DocumentExtractor.ExtractAsync(extractionInput, ExtractorOverride, ExtractionTimeout, cancellationToken);
                if (sniff.Warnings.Count > 0)
                    extraction = extraction with { Warnings = [.. extraction.Warnings, .. sniff.Warnings] };

                var id = NewId();
                folder = Folder(id);
                Directory.CreateDirectory(folder);
                // El original sale de los bytes y no de mover el temporal: si la extracción agotó el plazo, el extractor
                // puede seguir con el temporal abierto y moverlo fallaría.
                await File.WriteAllBytesAsync(OriginalPath(id), bytes, cancellationToken);
                await File.WriteAllTextAsync(ContentPath(id), extraction.Content, cancellationToken);

                var record = new DocumentRecord
                {
                    Id = id,
                    Title = title,
                    OriginalFileName = fileName,
                    Extension = extension,
                    Family = type.Family,
                    MediaType = sniff.MediaType,
                    Viewer = type.Viewer,
                    SizeBytes = bytes.LongLength,
                    Sha256 = sha,
                    UploadedAt = _clock.GetUtcNow(),
                    UseInAnalysis = _options.IncludeInAnalysisByDefault,
                    Extraction = extraction.ToInfo(),
                    Sections = extraction.Sections
                };
                Save(record);
                return record;
            }
            catch
            {
                if (folder != null && Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
                throw;
            }
            finally
            {
                TryDelete(incoming);
            }
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>Cambia título o <c>useInAnalysis</c>; null deja el campo igual.</summary>
    /// <exception cref="KnowledgeOperationException"><c>not_found</c> o <c>invalid_request</c> (título vacío o de más de 200).</exception>
    public DocumentRecord Patch(string id, string? title, bool? useInAnalysis)
    {
        if (title != null)
        {
            title = title.Trim();
            if (title.Length is 0 or > 200)
                throw new KnowledgeOperationException(KnowledgeErrorCodes.InvalidRequest, "El título tiene que tener entre 1 y 200 caracteres.");
        }

        lock (RecordLock(id))
        {
            var record = TryGet(id) ?? throw NotFound(id);
            var updated = record with
            {
                Title = title ?? record.Title,
                UseInAnalysis = useInAnalysis ?? record.UseInAnalysis
            };
            Save(updated);
            return updated;
        }
    }

    /// <summary>Borra la carpeta del documento; false si no existía.</summary>
    public bool Delete(string id)
    {
        if (TryGet(id) == null)
            return false;
        Directory.Delete(Folder(id), recursive: true);
        return true;
    }

    /// <summary>
    /// Guarda la descripción de una imagen como su <c>content.md</c>, con la cabecera que avisa de que es generada
    /// (plan v2 §7.4), y pasa la extracción a <c>ok</c>.
    /// </summary>
    public DocumentRecord SaveDescription(string id, string description, string model)
    {
        lock (RecordLock(id))
        {
            var record = TryGet(id) ?? throw NotFound(id);
            var now = _clock.GetUtcNow();
            var content = $"# {record.Title}\n\n> Descripción generada por {model} el {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC; puede contener errores.\n\n{description.Replace("\r\n", "\n").Trim()}\n";
            File.WriteAllText(ContentPath(id), content);
            var sections = MarkdownSections.Parse(content, record.Title);
            var updated = record with
            {
                Extraction = new DocumentExtractionInfo(ExtractionStatuses.Ok, content.Length, sections.Count, null, false, []),
                Sections = sections,
                Description = new DocumentDescriptionInfo(model, now)
            };
            Save(updated);
            return updated;
        }
    }

    private object RecordLock(string id) => RecordLocks.GetOrAdd(Path.GetFullPath(Paths.DocumentsPath) + "|" + id, _ => new object());

    /// <summary>Borrado de un temporal sin fallar: tras agotar el plazo, el extractor puede tenerlo abierto todavía.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Save(DocumentRecord record) =>
        ReportHistoryStore.WriteAtomic(Path.Combine(Folder(record.Id), KnowledgePaths.DocumentMetadataFileName),
            JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions));

    private string Folder(string id) => Paths.DocumentFolder(SourceRef.Document(id));

    private static DocumentTypeInfo ResolveType(string extension)
    {
        if (DocumentTypeRegistry.TryGetByExtension(extension, out var type))
            return type;

        var message = LegacyOffice.Contains(extension)
            ? $"Los ficheros '{extension}' (Office binario antiguo) no se admiten: guárdalo como .docx, .xlsx o .pptx."
            : ExecutablesAndArchives.Contains(extension)
                ? $"Los ejecutables y los comprimidos ('{extension}') no se admiten."
                : $"El tipo '{extension}' no se admite. Tipos admitidos: {string.Join(" ", DocumentTypeRegistry.All.SelectMany(t => t.Extensions))}.";
        throw new KnowledgeOperationException(KnowledgeErrorCodes.UnsupportedType, message);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream body, DocumentTypeInfo type, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > type.MaxBytes)
            {
                throw new KnowledgeOperationException(KnowledgeErrorCodes.FileTooLarge,
                    $"El fichero supera el máximo de {type.MaxBytes / (1024 * 1024)} MB para '{type.Family}'.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private string NewId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var now = _clock.GetUtcNow();
        while (true)
        {
            var suffix = string.Create(4, 0, (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            });
            var id = $"{now:yyyyMMdd-HHmmss}-{suffix}";
            if (!Directory.Exists(Folder(id)))
                return id;
        }
    }

    private static KnowledgeOperationException NotFound(string id) =>
        new(KnowledgeErrorCodes.NotFound, $"No existe el documento '{id}'.");
}
