using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodebaseAnalysis.Core.Enrichment.Java;

/// <summary>
/// Un registro de <c>java-analysis.jsonl</c>, el resultado del analizador Java (P5). Cada línea es un objeto JSON con la
/// clase de registro en <c>"k"</c>; las rutas son relativas a la raíz del repositorio y con <c>/</c>.
/// </summary>
public abstract record JavaAnalysisRecord
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";
}

/// <summary><c>"k":"file"</c>: un fichero analizado.</summary>
public sealed record JavaFileRecord : JavaAnalysisRecord
{
    [JsonPropertyName("encoding")]
    public string? Encoding { get; init; }

    [JsonPropertyName("errors")]
    public int Errors { get; init; }
}

/// <summary>Referencia a un tipo con su origen (<see cref="JavaOrigins"/>).</summary>
public sealed record JavaTypeReference
{
    [JsonPropertyName("fqn")]
    public string Fqn { get; init; } = "";

    [JsonPropertyName("origin")]
    public string Origin { get; init; } = JavaOrigins.Unresolved;
}

/// <summary><c>"k":"type"</c>: una declaración de tipo con nombre (no se emiten las anónimas ni las locales).</summary>
public sealed record JavaTypeRecord : JavaAnalysisRecord
{
    /// <summary>Anidamiento de tipos con nombre desde el de primer nivel: <c>["B","Inner"]</c>.</summary>
    [JsonPropertyName("chain")]
    public IReadOnlyList<string> Chain { get; init; } = [];

    [JsonPropertyName("fqn")]
    public string Fqn { get; init; } = "";

    /// <summary><c>class</c>, <c>interface</c>, <c>enum</c> o <c>annotation</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "class";

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("end")]
    public int End { get; init; }

    [JsonPropertyName("access")]
    public string? Access { get; init; }

    [JsonPropertyName("abstract")]
    public bool Abstract { get; init; }

    [JsonPropertyName("annotations")]
    public IReadOnlyList<string> Annotations { get; init; } = [];

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("super")]
    public JavaTypeReference? Super { get; init; }

    /// <summary>Interfaces implementadas (en una interfaz, las que extiende).</summary>
    [JsonPropertyName("interfaces")]
    public IReadOnlyList<JavaTypeReference> Interfaces { get; init; } = [];
}

/// <summary>
/// <c>"k":"method"</c>: un método o constructor declarado en un tipo con nombre. En un constructor, <c>name</c> y la
/// <c>key</c> llevan el nombre simple de la clase (<c>a.B#B(int)</c>) y <c>ret</c>/<c>retSimple</c> son null; los
/// constructores implícitos no se emiten.
/// </summary>
public sealed record JavaMethodRecord : JavaAnalysisRecord
{
    /// <summary>Anidamiento del tipo propietario.</summary>
    [JsonPropertyName("chain")]
    public IReadOnlyList<string> Chain { get; init; } = [];

    [JsonPropertyName("ownerFqn")]
    public string OwnerFqn { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("ctor")]
    public bool Ctor { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("end")]
    public int End { get; init; }

    /// <summary><c>ownerFqn#nombre(FQN borrados)</c>: casa por igualdad con el <c>to</c> de las llamadas.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = "";

    [JsonPropertyName("params")]
    public IReadOnlyList<string> Params { get; init; } = [];

    [JsonPropertyName("paramSimple")]
    public IReadOnlyList<string> ParamSimple { get; init; } = [];

    [JsonPropertyName("paramNames")]
    public IReadOnlyList<string> ParamNames { get; init; } = [];

    [JsonPropertyName("ret")]
    public string? Ret { get; init; }

    [JsonPropertyName("retSimple")]
    public string? RetSimple { get; init; }

    [JsonPropertyName("access")]
    public string? Access { get; init; }

    [JsonPropertyName("static")]
    public bool Static { get; init; }

    [JsonPropertyName("abstract")]
    public bool Abstract { get; init; }

    [JsonPropertyName("annotations")]
    public IReadOnlyList<string> Annotations { get; init; } = [];

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    [JsonPropertyName("cyclomatic")]
    public int? Cyclomatic { get; init; }
}

/// <summary><c>"k":"call"</c>: una invocación o creación (<c>new</c>) con su destino resuelto por <c>javac</c>.</summary>
public sealed record JavaCallRecord : JavaAnalysisRecord
{
    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary><c>key</c> del método que llama; null fuera de un método (inicializadores).</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("fromType")]
    public string FromType { get; init; } = "";

    /// <summary>
    /// <c>key</c> del destino; null si <c>javac</c> no dio un método (<c>reason</c> lleva el kind, p. ej. <c>CLASS</c>). Un
    /// destino en una clase anónima o local lleva el nombre binario (<c>a.B$1Local#go()</c>) y nunca tiene nodo.
    /// </summary>
    [JsonPropertyName("to")]
    public string? To { get; init; }

    [JsonPropertyName("origin")]
    public string Origin { get; init; } = JavaOrigins.Unresolved;

    /// <summary><c>call</c> o <c>new</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "call";

    /// <summary>Motivo de una llamada sin resolver (<c>null-element</c>, <c>error-owner</c>…).</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// <c>"k":"diag"</c>: un error de compilación. <c>path</c> y <c>line</c> pueden venir a null; un fichero ilegible da
/// <c>diag</c> sin registro <c>file</c>.
/// </summary>
public sealed record JavaDiagnosticRecord : JavaAnalysisRecord
{
    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("msg")]
    public string Message { get; init; } = "";
}

/// <summary><c>"k":"summary"</c>: último registro, con la versión del contrato y los totales de la pasada.</summary>
public sealed record JavaSummaryRecord : JavaAnalysisRecord
{
    [JsonPropertyName("schema")]
    public int Schema { get; init; }

    [JsonPropertyName("analyzerVersion")]
    public string? AnalyzerVersion { get; init; }

    [JsonPropertyName("javaVersion")]
    public string? JavaVersion { get; init; }

    [JsonPropertyName("files")]
    public int Files { get; init; }

    [JsonPropertyName("parseMs")]
    public long ParseMs { get; init; }

    [JsonPropertyName("analyzeMs")]
    public long AnalyzeMs { get; init; }

    [JsonPropertyName("scanMs")]
    public long ScanMs { get; init; }

    [JsonPropertyName("heapPeakMb")]
    public long HeapPeakMb { get; init; }

    [JsonPropertyName("batches")]
    public int Batches { get; init; }

    /// <summary>Duración total del proceso Java, en ms.</summary>
    [JsonPropertyName("wallMs")]
    public long WallMs { get; init; }

    /// <summary>Llamadas por origen: <c>project</c>, <c>jdk</c>, <c>jar</c>, <c>unresolved</c>.</summary>
    [JsonPropertyName("calls")]
    public IReadOnlyDictionary<string, long> Calls { get; init; } = new Dictionary<string, long>();

    [JsonPropertyName("unresolvedByReason")]
    public IReadOnlyDictionary<string, long> UnresolvedByReason { get; init; } = new Dictionary<string, long>();

    /// <summary>Paquetes de <c>package X does not exist</c>: los jars que faltan en el classpath.</summary>
    [JsonPropertyName("missingPackages")]
    public IReadOnlyList<string> MissingPackages { get; init; } = [];

    /// <summary>Diagnósticos por tipo: <c>error</c>, <c>warning</c>.</summary>
    [JsonPropertyName("diagnostics")]
    public IReadOnlyDictionary<string, long> Diagnostics { get; init; } = new Dictionary<string, long>();
}

/// <summary>Valores de <c>origin</c> en tipos y llamadas.</summary>
public static class JavaOrigins
{
    public const string Project = "project";
    public const string Jdk = "jdk";
    public const string Jar = "jar";
    public const string Unresolved = "unresolved";
}

/// <summary>
/// Lector en streaming de <c>java-analysis.jsonl</c>. Las clases de registro desconocidas se ignoran (un analizador más
/// nuevo puede añadir alguna); una línea que no es JSON o un registro conocido mal formado es un error con su línea.
/// </summary>
public static class JavaAnalysisReader
{
    /// <summary>Versión del contrato que entiende este lector (<c>"schema"</c> del registro <c>summary</c>).</summary>
    public const int SupportedSchema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <exception cref="InvalidDataException">Si una línea no se puede leer; el mensaje lleva fichero y línea.</exception>
    public static IEnumerable<JavaAnalysisRecord> Read(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            JavaAnalysisRecord? record;
            try
            {
                record = Parse(line);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new InvalidDataException($"{path}:{lineNumber}: registro del analizador Java ilegible: {ex.Message}", ex);
            }
            if (record != null)
                yield return record;
        }
    }

    /// <summary>Un registro a partir de una línea; null si está vacía o su clase no se conoce.</summary>
    public static JavaAnalysisRecord? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("k", out var kind) || kind.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("falta la clave \"k\".");

        return kind.GetString() switch
        {
            "file" => root.Deserialize<JavaFileRecord>(Options),
            "type" => root.Deserialize<JavaTypeRecord>(Options),
            "method" => root.Deserialize<JavaMethodRecord>(Options),
            "call" => root.Deserialize<JavaCallRecord>(Options),
            "diag" => root.Deserialize<JavaDiagnosticRecord>(Options),
            "summary" => root.Deserialize<JavaSummaryRecord>(Options),
            _ => null
        };
    }
}
