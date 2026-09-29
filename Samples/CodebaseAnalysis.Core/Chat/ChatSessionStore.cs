using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Chat;

/// <summary>
/// Sesiones de chat en disco: un JSON por sesión en <see cref="Directory"/>, escrito de forma atómica
/// (fichero temporal en la misma carpeta y <c>File.Move</c> con sobrescritura).
/// </summary>
public sealed partial class ChatSessionStore
{
    /// <summary>Tope de los argumentos de una tool call guardados en el turno.</summary>
    public const int MaxStoredArgumentsChars = 2000;

    /// <summary>Longitud del título por defecto, tomado de la primera pregunta.</summary>
    public const int DefaultTitleLength = 80;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Las preguntas y respuestas van en español: sin esto, cada tilde se guarda como \u00XX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;

    public ChatSessionStore(string directory, TimeProvider? clock = null)
    {
        Directory = Path.GetFullPath(directory);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Carpeta de las sesiones.</summary>
    public string Directory { get; }

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdRegex().IsMatch(id);

    /// <summary>
    /// Ruta del fichero de una sesión, o null si el id no es válido o la ruta resultante saldría de
    /// <see cref="Directory"/> (la misma defensa que <c>FileConversationStorage.GetFilePath</c>).
    /// </summary>
    public string? GetFilePath(string? id)
    {
        if (!IsValidId(id))
            return null;

        var path = Path.GetFullPath(Path.Combine(Directory, id + ".json"));
        var root = Directory.EndsWith(Path.DirectorySeparatorChar) ? Directory : Directory + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>Id nuevo: <c>yyyyMMdd-HHmmss-xxxx</c> con 4 caracteres de <c>[a-z0-9]</c>.</summary>
    public string NewId()
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
            if (!File.Exists(GetFilePath(id)))
                return id;
        }
    }

    public ChatSession Create(string repositoryPath, string? title = null)
    {
        var now = _clock.GetUtcNow();
        var session = new ChatSession
        {
            Id = NewId(),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            RepositoryPath = repositoryPath,
            CreatedAt = now,
            UpdatedAt = now
        };
        Save(session);
        return session;
    }

    /// <summary>La sesión, o null si no existe, el id no es válido o el fichero está dañado.</summary>
    public ChatSession? TryGet(string? id) => TryGet(id, out _);

    /// <summary>
    /// La sesión, o null. <paramref name="corrupt"/> es true si el fichero existe pero no es una sesión válida:
    /// igual que en <see cref="List"/>, que lo omite, se trata como inexistente.
    /// </summary>
    public ChatSession? TryGet(string? id, out bool corrupt)
    {
        corrupt = false;
        var path = GetFilePath(id);
        if (path == null)
            return null;

        lock (_lock)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                var session = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(path), JsonOptions);
                corrupt = session == null;
                return session;
            }
            catch (JsonException)
            {
                corrupt = true;
                return null;
            }
        }
    }

    /// <summary>True si hay fichero para la sesión, aunque esté dañado (para poder borrarlo).</summary>
    public bool Exists(string? id)
    {
        var path = GetFilePath(id);
        if (path == null)
            return false;

        lock (_lock)
        {
            return File.Exists(path);
        }
    }

    /// <summary>Mensaje de error para una sesión que no se ha podido cargar.</summary>
    public static string NotFoundMessage(string? id, bool corrupt) => corrupt
        ? $"El fichero de la sesión '{id}' está dañado y no se puede leer."
        : $"No existe la sesión '{id}'.";

    /// <summary>Sesiones, la más reciente primero. Un fichero ilegible se omite.</summary>
    public IReadOnlyList<ChatSessionSummary> List()
    {
        var result = new List<ChatSessionSummary>();
        if (!System.IO.Directory.Exists(Directory))
            return result;

        lock (_lock)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                if (!IsValidId(Path.GetFileNameWithoutExtension(file)))
                    continue;

                try
                {
                    var session = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(file), JsonOptions);
                    if (session != null)
                        result.Add(new ChatSessionSummary(session.Id, session.Title, session.CreatedAt, session.UpdatedAt, session.Turns.Count));
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                }
            }
        }

        return result.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <summary>Guarda la sesión de forma atómica.</summary>
    public void Save(ChatSession session)
    {
        var path = GetFilePath(session.Id)
            ?? throw new ArgumentException($"Id de sesión no válido: '{session.Id}'.", nameof(session));

        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = Path.Combine(Directory, $".{session.Id}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
        }
    }

    public bool Delete(string? id)
    {
        var path = GetFilePath(id);
        if (path == null)
            return false;

        lock (_lock)
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
    }

    /// <summary>
    /// Pares pregunta/respuesta de los últimos <paramref name="maxTurns"/> turnos <c>completed</c>: el contexto
    /// del modelo en el turno siguiente. Los turnos fallidos o cancelados no entran.
    /// </summary>
    public static IReadOnlyList<(string Question, string Answer)> BuildHistory(ChatSession session, int maxTurns)
    {
        var completed = session.Turns
            .Where(t => t.Status == ChatTurnStatus.Completed && !string.IsNullOrEmpty(t.Answer))
            .Select(t => (t.Question, t.Answer!))
            .ToList();

        return maxTurns <= 0 ? [] : completed.Skip(Math.Max(0, completed.Count - maxTurns)).ToList();
    }

    /// <summary>Título por defecto: los primeros <see cref="DefaultTitleLength"/> caracteres de la pregunta.</summary>
    public static string DefaultTitle(string question)
    {
        var singleLine = question.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= DefaultTitleLength ? singleLine : singleLine[..DefaultTitleLength];
    }
}
