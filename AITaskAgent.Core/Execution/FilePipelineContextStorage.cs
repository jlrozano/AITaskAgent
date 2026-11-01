using AITaskAgent.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace AITaskAgent.Core.Execution;

/// <summary>
/// Persists pipeline contexts as JSON files, one per <see cref="PipelineContext.CorrelationId"/>, in
/// one folder, so a suspended run survives a restart: another process (another
/// <c>IServiceProvider</c>) loads it and resumes with fast-forward.
/// </summary>
/// <remarks>
/// <para>What is stored, what is not and which values are supported is the contract of
/// <see cref="PipelineContextSerializer"/>. A value that cannot be stored makes
/// <see cref="SaveAsync"/> throw <see cref="NotSupportedException"/> naming the entry, and leaves any
/// previous file of that id untouched.</para>
/// <para>Writes are atomic: the document goes to a temporary file in the same folder that then
/// replaces the old one, so a crash mid-write never leaves a half-written context.</para>
/// <para>File names never come straight from the id: lower-case letters, digits and <c>_</c> are kept
/// and every other character (<c>.</c>, <c>/</c>, <c>\</c>, <c>:</c>, upper case...) is escaped, so an
/// id cannot leave the folder, hit a reserved name or collide with another on a case-insensitive file
/// system; the <c>ctx-</c> prefix keeps names like <c>CON</c> away, and ids too long for a file name are hashed. The id is also
/// stored inside the file and checked on load.</para>
/// <para>Loading builds the live context (event channel, services) with the context factory given to
/// the constructor (by default the registered <see cref="PipelineContextFactory"/>) and writes the
/// stored data onto it.</para>
/// </remarks>
public sealed class FilePipelineContextStorage : IPipelineContextStorage
{
    private const string FilePrefix = "ctx-";
    private const string FileExtension = ".json";
    private const int MaxEncodedLength = 150;

    private readonly Func<PipelineContext> _newContext;

    /// <summary>Creates a storage over <paramref name="directory"/> (created if missing).</summary>
    /// <param name="directory">Folder of the context files; a relative path is resolved against the current directory.</param>
    /// <param name="newContext">
    /// Builds the fresh context a loaded one is restored onto. Null uses the registered
    /// <see cref="PipelineContextFactory"/> (<see cref="PipelineContextFactory.Create"/>).
    /// </param>
    public FilePipelineContextStorage(string directory, Func<PipelineContext>? newContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
        _newContext = newContext ?? (() => PipelineContextFactory.Create());
    }

    /// <summary>Absolute path of the folder holding the context files.</summary>
    public string Directory { get; }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">A value or result of the context cannot be stored.</exception>
    public async Task SaveAsync(string correlationId, PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string path = GetFilePath(correlationId);

        // The file is keyed by the id the caller gives; the document must carry that same id.
        PipelineContext keyed = context.CorrelationId == correlationId ? context : context with { CorrelationId = correlationId };
        string json = PipelineContextSerializer.Serialize(keyed);

        string temp = Path.Combine(Directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<PipelineContext?> LoadAsync(string correlationId, CancellationToken cancellationToken)
    {
        string path = GetFilePath(correlationId);
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken);
        PipelineContext context = PipelineContextSerializer.Deserialize(json, _newContext());

        return context.CorrelationId == correlationId
            ? context
            : throw new InvalidOperationException(
                $"The file '{path}' holds the context '{context.CorrelationId}', not '{correlationId}'.");
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string correlationId, CancellationToken cancellationToken)
    {
        string path = GetFilePath(correlationId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>Deletes only the context files of this storage (<c>ctx-*.json</c>), not other files in the folder.</remarks>
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        foreach (string file in System.IO.Directory.GetFiles(Directory, $"{FilePrefix}*{FileExtension}"))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    /// <summary>Full path of the file for <paramref name="correlationId"/>, always inside <see cref="Directory"/>.</summary>
    public string GetFilePath(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return Path.Combine(Directory, FilePrefix + EncodeFileName(correlationId) + FileExtension);
    }

    /// <summary>
    /// Injective, file-system-safe name, also on case-insensitive file systems: <c>[a-z0-9_]</c> are
    /// kept and every other character (upper-case letters and <c>-</c> included) becomes <c>-</c>
    /// plus its UTF-8 bytes in lower-case hex. Ids whose name would be too long become <c>-h</c> plus
    /// their SHA-256, which no escape can produce (<c>h</c> is not a hex digit).
    /// </summary>
    private static string EncodeFileName(string id)
    {
        StringBuilder sb = new(id.Length);
        foreach (Rune rune in id.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterLower((char)rune.Value) || char.IsAsciiDigit((char)rune.Value) || rune.Value == '_'))
            {
                sb.Append((char)rune.Value);
                continue;
            }

            Span<byte> bytes = stackalloc byte[4];
            int count = rune.EncodeToUtf8(bytes);
            foreach (byte b in bytes[..count])
            {
                sb.Append('-').Append(b.ToString("x2"));
            }
        }

        return sb.Length <= MaxEncodedLength
            ? sb.ToString()
            : "-h" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }
}
