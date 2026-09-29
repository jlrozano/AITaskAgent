namespace CodebaseAnalysis.Core.Chat;

/// <summary>
/// Una conversación del chat, persistida en <c>&lt;carpeta de la BD&gt;/chat/&lt;id&gt;.json</c> (plan §4.5).
/// </summary>
public sealed class ChatSession
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string RepositoryPath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ChatTurn> Turns { get; set; } = [];
}

/// <summary>Un turno: la pregunta, la respuesta final y lo que costó obtenerla.</summary>
public sealed class ChatTurn
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset AskedAt { get; set; }
    public string Question { get; set; } = string.Empty;
    public string? Answer { get; set; }

    /// <summary>Uno de <see cref="ChatTurnStatus"/>.</summary>
    public string Status { get; set; } = ChatTurnStatus.Completed;

    public string? Error { get; set; }
    public string? Model { get; set; }
    public int? Tokens { get; set; }
    public decimal? CostUsd { get; set; }
    public long? DurationMs { get; set; }

    /// <summary>El <c>FinishReason</c> de la respuesta final; <c>Length</c> indica que se cortó.</summary>
    public string? FinishReason { get; set; }

    public List<ChatToolCall> ToolCalls { get; set; } = [];

    /// <summary>
    /// Informes que escribió el turno, en orden (plan v2, decisión 0-9); null si no escribió ninguno. Es aditivo, así
    /// que <see cref="ChatSession.SchemaVersion"/> sigue en 1.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatTurnArtifact>? Artifacts { get; set; }

    /// <summary>
    /// Afirmaciones marcadas (<c>[V]</c>, <c>[D]</c>, <c>[I]</c>) y referencias de la respuesta, contrastadas con lo que
    /// leyeron las tools del turno (<see cref="ClaimAuditor"/>). Solo en turnos completados; null en los anteriores a la
    /// auditoría. Aditivo: <see cref="ChatSession.SchemaVersion"/> sigue en 1.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ChatTurnAudit? Audit { get; set; }
}

/// <summary>Resultado de <see cref="ClaimAuditor"/> sobre una respuesta.</summary>
public sealed class ChatTurnAudit
{
    /// <summary>Afirmaciones en el orden en que aparecen sus marcas fuera de bloques y spans de código.</summary>
    public List<ChatClaim> Claims { get; set; } = [];

    /// <summary>Cada referencia distinta de la respuesta (por su texto), esté o no dentro de una afirmación.</summary>
    public List<ChatReference> References { get; set; } = [];
}

/// <summary>Una afirmación marcada por el modelo.</summary>
public sealed class ChatClaim
{
    /// <summary>La marca: uno de <see cref="ChatClaimKinds"/>.</summary>
    public string Kind { get; set; } = ChatClaimKinds.Inferred;

    /// <summary>Uno de <see cref="ChatClaimStatus"/>.</summary>
    public string Status { get; set; } = ChatClaimStatus.Inferred;

    /// <summary>Por qué quedó <see cref="ChatClaimStatus.Unchecked"/>; null en otro caso.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>Textos de las referencias de la afirmación (las claves de <see cref="ChatTurnAudit.References"/>).</summary>
    public List<string> References { get; set; } = [];
}

/// <summary>Una referencia citada en la respuesta: código (<c>ruta:línea</c>) o fuente (<c>informe › sección</c>).</summary>
public sealed class ChatReference
{
    /// <summary>Texto literal del span de código de la respuesta; la UI lo usa como clave.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary><c>code</c> o <c>source</c>.</summary>
    public string Kind { get; set; } = ChatReferenceKinds.Code;

    /// <summary>
    /// Código: ruta relativa a la raíz del repositorio con <c>/</c>. Fuente: el id del informe (<c>05_x.md</c>,
    /// <c>custom/x.md</c>) o <c>doc/&lt;id&gt;</c>, que es como los abre el visor; si no se reconoce, el nombre citado.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? StartLine { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? EndLine { get; set; }

    /// <summary>Uno de <see cref="ChatReferenceStatus"/>.</summary>
    public string Status { get; set; } = ChatReferenceStatus.Missing;
}

public static class ChatClaimKinds
{
    public const string Verified = "verified";
    public const string Documented = "documented";
    public const string Inferred = "inferred";
}

public static class ChatClaimStatus
{
    public const string Verified = "verified";
    public const string Documented = "documented";
    public const string Inferred = "inferred";

    /// <summary>Marcada <c>[V]</c> o <c>[D]</c> pero sin una referencia que lo respalde en este turno.</summary>
    public const string Unchecked = "unchecked";
}

public static class ChatReferenceKinds
{
    public const string Code = "code";
    public const string Source = "source";
}

public static class ChatReferenceStatus
{
    /// <summary>Existe y el turno leyó todas las líneas citadas (o el informe o documento).</summary>
    public const string Ok = "ok";

    /// <summary>El turno leyó solo parte de las líneas citadas.</summary>
    public const string Partial = "partial";

    /// <summary>Existe, pero el turno no lo leyó.</summary>
    public const string NotRead = "not_read";

    /// <summary>El fichero existe, pero las líneas citadas no.</summary>
    public const string OutOfRange = "out_of_range";

    /// <summary>No existe (o queda fuera del repositorio).</summary>
    public const string Missing = "missing";
}

/// <summary>Una tool que el modelo usó en el turno. El resultado de la tool no se guarda.</summary>
public sealed class ChatToolCall
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Argumentos JSON, truncados a <see cref="ChatSessionStore.MaxStoredArgumentsChars"/>.</summary>
    public string? Arguments { get; set; }

    public bool? Success { get; set; }
    public long? DurationMs { get; set; }
}

/// <summary>Resumen de una sesión para el listado.</summary>
public sealed record ChatSessionSummary(
    string Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int TurnCount);

public static class ChatTurnStatus
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
