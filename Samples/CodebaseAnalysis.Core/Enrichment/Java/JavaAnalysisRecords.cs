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

    /// <summary><c>javadoc</c> o <c>comment</c> (el comentario pegado a la declaración); null sin <see cref="Doc"/> (schema 3).</summary>
    [JsonPropertyName("docSource")]
    public string? DocSource { get; init; }

    /// <summary>FQN del tipo con nombre que lo contiene; null en los de primer nivel (schema 3).</summary>
    [JsonPropertyName("outer")]
    public string? Outer { get; init; }

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

    /// <summary><c>javadoc</c> o <c>comment</c>; null sin <see cref="Doc"/> (schema 3).</summary>
    [JsonPropertyName("docSource")]
    public string? DocSource { get; init; }

    [JsonPropertyName("cyclomatic")]
    public int? Cyclomatic { get; init; }

    /// <summary>Sobrescribe o implementa algún método, también del JDK o de un jar (schema 3).</summary>
    [JsonPropertyName("isOverride")]
    public bool IsOverride { get; init; }

    /// <summary>
    /// <c>key</c> de los métodos del proyecto que sobrescribe o implementa (<c>Elements.overrides</c>), el más cercano por cada
    /// camino de supertipos (schema 3).
    /// </summary>
    [JsonPropertyName("overrides")]
    public IReadOnlyList<string> Overrides { get; init; } = [];
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
/// <c>"k":"field"</c> (schema 2): un campo o una constante de enum declarados en un tipo con nombre. <c>int a, b;</c> da dos
/// registros en la misma línea.
/// </summary>
public sealed record JavaFieldRecord : JavaAnalysisRecord
{
    /// <summary>Anidamiento del tipo propietario.</summary>
    [JsonPropertyName("chain")]
    public IReadOnlyList<string> Chain { get; init; } = [];

    [JsonPropertyName("ownerFqn")]
    public string OwnerFqn { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("end")]
    public int End { get; init; }

    /// <summary>Tipo declarado, borrado.</summary>
    [JsonPropertyName("typeFqn")]
    public string? TypeFqn { get; init; }

    [JsonPropertyName("access")]
    public string? Access { get; init; }

    [JsonPropertyName("static")]
    public bool Static { get; init; }

    [JsonPropertyName("final")]
    public bool Final { get; init; }

    [JsonPropertyName("enumConstant")]
    public bool EnumConstant { get; init; }

    [JsonPropertyName("doc")]
    public string? Doc { get; init; }

    /// <summary><c>javadoc</c> o <c>comment</c>; null sin <see cref="Doc"/> (schema 3).</summary>
    [JsonPropertyName("docSource")]
    public string? DocSource { get; init; }

    /// <summary><c>ownerFqn#nombre</c>: casa por igualdad con el <c>to</c> y el <c>fromField</c> de los <c>ref</c>.</summary>
    [JsonIgnore]
    public string Key => $"{OwnerFqn}#{Name}";
}

/// <summary>
/// <c>"k":"ref"</c> (schema 2): referencias de un origen a un tipo o a un campo del proyecto, agregadas por fichero y por
/// (<c>from</c>, <c>fromField</c>, <c>fromType</c>, <c>to</c>, <c>kind</c>, <c>access</c>). Con <c>from</c> y
/// <c>fromField</c> a null el origen es el tipo (<c>fromType</c>): un bloque <c>static</c> o de instancia, o las
/// anotaciones o los argumentos de tipo del propio tipo.
/// </summary>
public sealed record JavaRefRecord : JavaAnalysisRecord
{
    /// <summary><c>key</c> del método que contiene la referencia; null fuera de un método.</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("fromType")]
    public string FromType { get; init; } = "";

    /// <summary><c>ownerFqn#campo</c> si la referencia está en el tipo declarado o el inicializador de un campo.</summary>
    [JsonPropertyName("fromField")]
    public string? FromField { get; init; }

    /// <summary>
    /// FQN del tipo o <c>ownerFqn#campo</c>. Un destino declarado en una clase anónima o local lleva el nombre binario
    /// (<c>a.B$1Local</c>, <c>a.B$1#campo</c>) y nunca tiene nodo.
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; init; } = "";

    /// <summary><c>type</c> o <c>field</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = JavaRefKinds.Type;

    /// <summary><c>read</c>, <c>write</c> o <c>readwrite</c> en los campos; null en los tipos.</summary>
    [JsonPropertyName("access")]
    public string? Access { get; init; }

    /// <summary>Contextos del uso (<c>param</c>, <c>local</c>, <c>new_implicit</c>…); solo en los de tipo.</summary>
    [JsonPropertyName("contexts")]
    public IReadOnlyList<string> Contexts { get; init; } = [];

    [JsonPropertyName("origin")]
    public string Origin { get; init; } = JavaOrigins.Project;

    /// <summary>Referencias agregadas en el registro.</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; } = 1;

    /// <summary>Líneas únicas, ordenadas.</summary>
    [JsonPropertyName("lines")]
    public IReadOnlyList<int> Lines { get; init; } = [];
}

/// <summary>Valores de <c>kind</c> y <c>access</c> en los registros <c>ref</c>.</summary>
public static class JavaRefKinds
{
    public const string Type = "type";
    public const string Field = "field";
    public const string Read = "read";
    public const string Write = "write";
    public const string ReadWrite = "readwrite";
}

/// <summary>
/// <c>"k":"issue"</c> (schema 3): un patrón de error que el compilador o el AST atribuido permiten decidir. El origen es el método
/// (<c>from</c>), el campo (<c>fromField</c>) o el tipo (<c>fromType</c>), el primero que no sea null.
/// </summary>
public sealed record JavaIssueRecord : JavaAnalysisRecord
{
    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("fromField")]
    public string? FromField { get; init; }

    [JsonPropertyName("fromType")]
    public string FromType { get; init; } = "";

    /// <summary><c>empty-catch</c>, <c>string-identity</c>, <c>undisposed-resource</c>, <c>null-return-deref</c> u <c>obsolete-api</c>.</summary>
    [JsonPropertyName("rule")]
    public string Rule { get; init; } = "";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("line")]
    public int Line { get; init; }
}

/// <summary>Una marca TODO/FIXME/HACK de un comentario.</summary>
public sealed record JavaTodoMarker
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";
}

/// <summary>Una racha de código comentado: líneas de <c>start_line</c> a <c>end_line</c> y la primera línea de texto.</summary>
public sealed record JavaCommentedCode
{
    [JsonPropertyName("start_line")]
    public int StartLine { get; init; }

    [JsonPropertyName("end_line")]
    public int EndLine { get; init; }

    [JsonPropertyName("lines")]
    public int Lines { get; init; }

    [JsonPropertyName("preview")]
    public string Preview { get; init; } = "";
}

/// <summary>
/// <c>"k":"comment"</c> (schema 3): marcas y código comentado de un propietario en un fichero. Un comentario va al método que lo
/// contiene (o al que sigue si está justo encima), si no al tipo y, si no, al fichero.
/// </summary>
public sealed record JavaCommentRecord : JavaAnalysisRecord
{
    /// <summary><c>method</c>, <c>type</c> o <c>file</c>.</summary>
    [JsonPropertyName("owner")]
    public string Owner { get; init; } = "";

    /// <summary><c>key</c> del método o FQN del tipo; null con <c>file</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("todos")]
    public IReadOnlyList<JavaTodoMarker> Todos { get; init; } = [];

    [JsonPropertyName("code")]
    public IReadOnlyList<JavaCommentedCode> Code { get; init; } = [];
}

/// <summary>
/// <c>"k":"const"</c> (analizador 4, mismo schema 3): un valor que el compilador resuelve como constante. <c>secret</c> es una
/// credencial escrita en claro (el valor llega ya ENMASCARADO: el secreto no sale nunca del analizador) y <c>reflection</c> es el
/// nombre de clase que se pasa a <c>Class.forName</c> o <c>loadClass</c>, con <c>*</c> por cada tramo no constante. El origen es el
/// método (<c>from</c>), el campo (<c>fromField</c>) o el tipo (<c>fromType</c>), el primero que no sea null.
/// </summary>
public sealed record JavaConstantRecord : JavaAnalysisRecord
{
    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("fromField")]
    public string? FromField { get; init; }

    [JsonPropertyName("fromType")]
    public string FromType { get; init; } = "";

    /// <summary><see cref="JavaConstantKinds.Secret"/> o <see cref="JavaConstantKinds.Reflection"/>.</summary>
    [JsonPropertyName("what")]
    public string What { get; init; } = "";

    /// <summary>En un secreto, <c>identifier</c>, <c>call-argument</c> o <c>connection-string</c>; en la reflexión, <c>Class.forName</c> o <c>loadClass</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>El identificador (o el método o la clave) con nombre de credencial; null en la reflexión.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary>El valor enmascarado (<c>Or*** (11 caracteres)</c>) o el patrón de nombre de clase (<c>com.acme.export.*Impl</c>).</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = "";
}

/// <summary>Valores de <c>what</c> en los registros <c>const</c>.</summary>
public static class JavaConstantKinds
{
    public const string Secret = "secret";
    public const string Reflection = "reflection";
}

/// <summary>Valores de <c>owner</c> en los registros <c>comment</c>.</summary>
public static class JavaCommentOwners
{
    public const string Method = "method";
    public const string Type = "type";
    public const string File = "file";
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

    /// <summary>Totales de las referencias a tipos y campos (schema 2); null en un resultado de schema 1.</summary>
    [JsonPropertyName("refs")]
    public JavaRefsSummary? Refs { get; init; }

    /// <summary>Registros <c>field</c> emitidos (schema 2).</summary>
    [JsonPropertyName("fields")]
    public long Fields { get; init; }

    /// <summary>Registros <c>issue</c> emitidos (schema 3).</summary>
    [JsonPropertyName("issues")]
    public long Issues { get; init; }

    /// <summary>Marcas TODO/FIXME/HACK encontradas (schema 3).</summary>
    [JsonPropertyName("todoMarkers")]
    public long TodoMarkers { get; init; }

    /// <summary>Rachas de código comentado encontradas (schema 3).</summary>
    [JsonPropertyName("commentedCodeBlocks")]
    public long CommentedCodeBlocks { get; init; }

    /// <summary>Credenciales en claro resueltas como constantes (analizador 4).</summary>
    [JsonPropertyName("secrets")]
    public long Secrets { get; init; }

    /// <summary>Nombres de clase resueltos en <c>Class.forName</c> y <c>loadClass</c> (analizador 4).</summary>
    [JsonPropertyName("reflectionTargets")]
    public long ReflectionTargets { get; init; }
}

/// <summary><c>summary.refs</c>: las referencias por origen, también las que no llegan a registro <c>ref</c>.</summary>
public sealed record JavaRefsSummary
{
    /// <summary>Referencias a tipos por origen: <c>project</c>, <c>jdk</c>, <c>jar</c>, <c>unresolved</c>.</summary>
    [JsonPropertyName("type")]
    public IReadOnlyDictionary<string, long> Type { get; init; } = new Dictionary<string, long>();

    /// <summary>Referencias a campos por origen.</summary>
    [JsonPropertyName("field")]
    public IReadOnlyDictionary<string, long> Field { get; init; } = new Dictionary<string, long>();

    /// <summary>Contextos de las referencias de tipo del proyecto, por ocurrencia.</summary>
    [JsonPropertyName("contexts")]
    public IReadOnlyDictionary<string, long> Contexts { get; init; } = new Dictionary<string, long>();

    /// <summary>Registros <c>ref</c> emitidos.</summary>
    [JsonPropertyName("records")]
    public long Records { get; init; }

    /// <summary>Referencias de un tipo a sí mismo, que no se emiten.</summary>
    [JsonPropertyName("selfExcluded")]
    public long SelfExcluded { get; init; }

    [JsonPropertyName("unresolvedByReason")]
    public IReadOnlyDictionary<string, long> UnresolvedByReason { get; init; } = new Dictionary<string, long>();
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
    /// <summary>
    /// Última versión del contrato que entiende este lector (<c>"schema"</c> del registro <c>summary</c>). También lee las
    /// anteriores desde <see cref="MinSupportedSchema"/>: el schema 1 no trae registros <c>field</c> ni <c>ref</c> y el 2 no trae
    /// <c>issue</c> ni <c>comment</c>, <c>overrides</c> ni <c>docSource</c>.
    /// </summary>
    public const int SupportedSchema = 3;

    /// <inheritdoc cref="SupportedSchema"/>
    public const int MinSupportedSchema = 1;

    /// <summary>Si el lector y el escritor entienden un resultado de esa versión del contrato.</summary>
    public static bool IsSupportedSchema(int schema) => schema is >= MinSupportedSchema and <= SupportedSchema;

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
            "field" => root.Deserialize<JavaFieldRecord>(Options),
            "ref" => root.Deserialize<JavaRefRecord>(Options),
            "issue" => root.Deserialize<JavaIssueRecord>(Options),
            "comment" => root.Deserialize<JavaCommentRecord>(Options),
            "const" => root.Deserialize<JavaConstantRecord>(Options),
            "diag" => root.Deserialize<JavaDiagnosticRecord>(Options),
            "summary" => root.Deserialize<JavaSummaryRecord>(Options),
            _ => null
        };
    }
}
