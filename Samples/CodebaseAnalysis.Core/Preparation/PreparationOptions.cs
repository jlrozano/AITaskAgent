using System.Globalization;
using CodebaseAnalysis.Core.Models;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>Cómo se preparan los datos de los auditores (<c>CodebaseAnalysis:Preparation:Mode</c>).</summary>
public enum PreparationMode
{
    /// <summary>Inventario, mapa de dominios, plan de cobertura y presupuesto de tiempo, y los informes de dominio investigan por subsistema.</summary>
    Subsystems,

    /// <summary>Sin pasos de preparación: el pipeline queda como antes de existir esta sección.</summary>
    Off
}

/// <summary>
/// Pesos con los que <c>CoveragePlanningStep</c> puntúa cada subsistema (<c>CodebaseAnalysis:Preparation:Weights</c>). Son
/// relativos entre sí: lo que importa es su proporción, no su valor absoluto. Un peso 0 anula esa señal.
/// </summary>
public sealed record SubsystemScoreWeights
{
    /// <summary>Centralidad del subsistema en el grafo (cuánto lo llama el resto).</summary>
    public double Centrality { get; init; } = 3;

    /// <summary>Número y variedad de puntos de entrada (acciones, <c>main</c>, procesos planificados, consumidores).</summary>
    public double EntryPoints { get; init; } = 2;

    /// <summary>Tablas distintas que tocan sus clases.</summary>
    public double Tables { get; init; } = 2;

    /// <summary>Estados o enumerados propios.</summary>
    public double States { get; init; } = 1;

    /// <summary>Integraciones externas.</summary>
    public double Integrations { get; init; } = 1;

    /// <summary>Riesgo de uso dinámico (reflexión, instanciación por nombre): el grafo ve menos de lo que hay.</summary>
    public double DynamicUseRisk { get; init; } = 1;
}

/// <summary>
/// Énfasis con que un informe lee las señales de <see cref="SubsystemScoreWeights"/> (<c>CodebaseAnalysis:Preparation:ReportProfiles:&lt;informe&gt;</c>).
/// Cada valor es un <b>factor</b> que multiplica el peso base de su señal: 1 la deja como está, 2 la duplica y 0 la anula para ese
/// informe. Así los pesos base siguen siendo el único sitio donde se ajusta cuánto vale cada señal, y un perfil solo dice qué
/// señales importan más a un informe que a otro.
/// </summary>
public sealed record SubsystemScoreProfile
{
    public double Centrality { get; init; } = 1;
    public double EntryPoints { get; init; } = 1;
    public double Tables { get; init; } = 1;
    public double States { get; init; } = 1;
    public double Integrations { get; init; } = 1;
    public double DynamicUseRisk { get; init; } = 1;
}

/// <summary>
/// Los perfiles de puntuación de los cuatro informes en modo B (<see cref="CoveragePlanner.ModeBReports"/>). Un informe sin perfil
/// (<see cref="Of"/>) se puntúa con los pesos base. Es un registro con una propiedad por informe, y no un diccionario, para que dos
/// opciones iguales sean iguales por valor y para que la configuración enlace por nombre de informe.
/// </summary>
/// <remarks>
/// Los valores por defecto salen de lo que cada informe necesita ver, no de ningún repositorio concreto: el funcional y el de flujos
/// leen entradas, estados y tablas (qué hace el sistema, por dónde se entra y qué guarda); el de errores, integraciones, uso
/// dinámico y centralidad (dónde falla lo que el grafo no ve y lo que muchos dependen); el de arquitectura, centralidad
/// (la infraestructura compartida que casi nunca es capacidad de negocio es justo lo que le interesa).
/// </remarks>
public sealed record ReportScoreProfiles
{
    public SubsystemScoreProfile AnalisisFuncional { get; init; } = new()
    { Centrality = 0.5, EntryPoints = 2.5, Tables = 1.5, States = 3, Integrations = 0.5, DynamicUseRisk = 0.5 };

    public SubsystemScoreProfile FlujosEndToEnd { get; init; } = new()
    { Centrality = 0.5, EntryPoints = 3, Tables = 1.5, States = 2, Integrations = 1, DynamicUseRisk = 0.5 };

    public SubsystemScoreProfile ErroresYBugs { get; init; } = new()
    { Centrality = 1, EntryPoints = 0.5, Tables = 0.5, States = 0.5, Integrations = 3, DynamicUseRisk = 3 };

    public SubsystemScoreProfile ArquitecturaAcoplamiento { get; init; } = new()
    { Centrality = 1.5, EntryPoints = 0.5, Tables = 1, States = 0.5, Integrations = 2, DynamicUseRisk = 0.5 };

    /// <summary>Todos los factores a 1: cada informe se puntúa con los pesos base, como antes de existir los perfiles.</summary>
    public static ReportScoreProfiles Neutral { get; } = new()
    {
        AnalisisFuncional = new(), FlujosEndToEnd = new(), ErroresYBugs = new(), ArquitecturaAcoplamiento = new()
    };

    /// <summary>El perfil del informe; uno neutro (factores a 1) si no es de los de modo B.</summary>
    public SubsystemScoreProfile Of(ReportType report) => report switch
    {
        ReportType.AnalisisFuncional => AnalisisFuncional,
        ReportType.FlujosEndToEnd => FlujosEndToEnd,
        ReportType.ErroresYBugs => ErroresYBugs,
        ReportType.ArquitecturaAcoplamiento => ArquitecturaAcoplamiento,
        _ => new SubsystemScoreProfile()
    };
}

/// <summary>
/// Sección <c>CodebaseAnalysis:Preparation</c>: cómo se descubren y reparten los subsistemas y cuánto se investiga de cada
/// uno. Los valores por defecto de <see cref="MaxSubsystemsPerReport"/> y <see cref="MaxParallelInvestigations"/> los aceptó
/// el usuario el 2026-10-04 (D9); el umbral de fusión y el tamaño mínimo son estimaciones que se calibran con la medición
/// sobre el repositorio grande (pregunta abierta 1 de la spec).
/// </summary>
/// <remarks>
/// Un valor que no se entiende o que no tiene sentido (un entero no positivo donde hace falta uno positivo, un umbral fuera de
/// 0-1, un modo desconocido) cae al valor por defecto y deja un texto en <see cref="Warnings"/>, que el primer paso que usa
/// las opciones emite como aviso: una errata en la configuración no debe parar un análisis.
/// </remarks>
public sealed record PreparationOptions
{
    public const string SectionName = "CodebaseAnalysis:Preparation";

    /// <summary>Extensiones que cuentan como código para las líneas de producción (en minúsculas, con punto).</summary>
    public static readonly IReadOnlyList<string> DefaultCountedExtensions = [".java", ".cs", ".jsp", ".js", ".ts"];

    /// <summary>
    /// Tokens genéricos de nombres de paquete, clase y tabla que no distinguen una capacidad de otra (patrones técnicos, no
    /// de negocio). Se excluyen de la fusión por vocabulario; ningún término de dominio va aquí.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultStopTokens =
    [
        "impl", "dao", "action", "actions", "util", "utils", "service", "services", "manager", "bean", "beans", "helper",
        "factory", "base", "abstract", "test", "tests", "exception", "dto", "vo", "form", "forms", "command", "controller",
        "handler", "model", "entity", "repository", "repo", "common", "core", "main", "java", "src", "com", "org", "net",
        "get", "set", "is", "new", "default", "generic", "config", "configuration"
    ];

    public PreparationMode Mode { get; init; } = PreparationMode.Subsystems;

    /// <summary>Subsistemas elegidos por informe en modo B (K).</summary>
    public int MaxSubsystemsPerReport { get; init; } = 8;

    /// <summary>Investigaciones de subsistema que corren a la vez dentro de un informe en modo B.</summary>
    public int MaxParallelInvestigations { get; init; } = 4;

    /// <summary>
    /// Fracción (0-1) de los ficheros de cada una de dos comunidades del grafo que tienen que llevar un mismo término de sus
    /// nombres para fundirlas por vocabulario (<see cref="VocabularyMerger"/>). Más bajo funde capacidades distintas; más alto
    /// reproduce el problema del árbol de paquetes.
    /// </summary>
    public double VocabularyMergeThreshold { get; init; } = 0.5;

    /// <summary>
    /// Fracción de los ficheros de producción que, como mucho, puede tener un subsistema (con un suelo de
    /// <see cref="MinSubsystemCap"/> ficheros): uno mayor se divide en sus comunidades internas, y un término de vocabulario que
    /// llevan más ficheros que eso no puede definir un subsistema (es un paraguas, no una capacidad) y no funde nada.
    /// </summary>
    public double MaxSubsystemShare { get; init; } = 0.05;

    /// <summary>
    /// Peso, en aristas medias (como <see cref="AffinityGraph.TermWeightFactor"/> para los nombres de clase), con que un tipo se une a cada término
    /// de la <b>ruta de su carpeta</b> (<c>.../notificacion/dao</c> → <c>notificacion</c>) en el grafo de afinidad, además de los de su nombre. Con el
    /// mismo filtro de coherencia que los nombres de clase: el término solo cuenta si al menos la mitad de los tipos que lo llevan se relacionan
    /// entre sí, y solo para los términos que además son nombre coherente de alguna clase (una capa como <c>web/</c> no es una capacidad). Une a una
    /// capacidad los tipos cuyo nombre no la dice (<c>ConsultaDAO</c> bajo <c>notificacion/</c>). 0 lo desactiva. El 1,25 por defecto sale de
    /// medir sobre un repositorio grande (2026-10-05): subió la concentración media de los 30 términos de ruta más frecuentes en su subsistema
    /// dominante de 0,29 a 0,32 sin empeorar el resto, y los valores de 0,75 a 1,6 dieron resultados parecidos (más arriba el reparto se vuelve inestable).
    /// </summary>
    public double PathTermWeightFactor { get; init; } = DefaultPathTermWeightFactor;

    /// <summary>Valor por defecto de <see cref="PathTermWeightFactor"/>.</summary>
    public const double DefaultPathTermWeightFactor = 1.25;

    /// <summary>
    /// Comunidades distintas (la propia incluida) desde las que, como mínimo, tienen que depender de un fichero para que sea infraestructura
    /// compartida (<see cref="SharedInfrastructure"/>); con <see cref="SharedMinConsumerShare"/> fija el umbral efectivo (el mayor de los dos).
    /// 0 desactiva el subsistema compartido: la infraestructura queda repartida entre las capacidades que la usan. El valor por defecto es 3: en la
    /// medición (2026-10-05) agrupó el 1 % de los ficheros, casi todo utilidades, excepciones, constantes y
    /// superclases, por los que pasaba el 68 % del peso de las dependencias entre subsistemas, sin cambiar la concentración de términos de ruta.
    /// </summary>
    public int SharedMinConsumers { get; init; } = DefaultSharedMinConsumers;

    /// <summary>Valor por defecto de <see cref="SharedMinConsumers"/>.</summary>
    public const int DefaultSharedMinConsumers = 3;

    /// <summary>
    /// Fracción (0-1) de las comunidades del repositorio desde las que tienen que depender de un fichero para que sea compartido: en un
    /// repositorio grande, depender de tres capacidades no basta para ser infraestructura. El umbral efectivo es el mayor entre esta fracción de
    /// las comunidades y <see cref="SharedMinConsumers"/>.
    /// </summary>
    public double SharedMinConsumerShare { get; init; } = DefaultSharedMinConsumerShare;

    /// <summary>Valor por defecto de <see cref="SharedMinConsumerShare"/>.</summary>
    public const double DefaultSharedMinConsumerShare = 0.2;

    /// <summary>Suelo del tamaño máximo de un subsistema: en un repositorio pequeño no se divide nada por debajo de esto.</summary>
    public const int MinSubsystemCap = 40;

    /// <summary>Ficheros de producción por debajo de los cuales un subsistema se absorbe en el más cercano o en <c>unassigned</c>.</summary>
    public int MinSubsystemFiles { get; init; } = 5;

    /// <summary>Subsistemas fuera del top K que se muestrean (en un orden estable, no aleatorio) como «cola larga» de cada informe.</summary>
    public int LongTailSize { get; init; } = 3;

    /// <summary>
    /// Tope de consultas a herramientas de la <b>consolidación</b> de un informe en modo B (la llamada del auditor que escribe el informe
    /// tras las investigaciones). Las notas ya traen lo investigado de cada subsistema, así que la consolidación solo debería comprobar
    /// alguna cita dudosa o un hueco concreto: sin tope, en la medición real (2026-10-04) rehacía la investigación con 26-48 llamadas y era
    /// la causa principal del sobrecoste. <b>Por defecto 0</b> hasta medirlo con un informe controlado: acotarla a 4 era una mejora que no se
    /// probó con el LLM real antes del primer análisis real, y los informes salieron más flojos. Al agotarlo, el auditor redacta con lo consultado (<c>ToolLimitFinalAnswerPrompt</c>). 0 = sin
    /// acotar: usa el mismo tope que el auditor (<c>CodebaseAnalysis:Auditors:MaxToolIterations</c>); uno mayor que el del auditor no lo sube. El
    /// auditor en modo A, con <c>Mode = Off</c> o sin subsistemas elegidos no se acota nunca.
    /// </summary>
    public int ConsolidationMaxToolIterations { get; init; } = 0;

    /// <summary>Pesos base de las señales de la puntuación de un subsistema (<see cref="CoveragePlan.Scores"/>).</summary>
    public SubsystemScoreWeights Weights { get; init; } = new();

    /// <summary>
    /// Qué señales importan más a cada informe en modo B: factores sobre <see cref="Weights"/> (ver <see cref="ReportScoreProfiles"/>).
    /// Sin ellos, los cuatro informes elegían los mismos K subsistemas con una puntuación casi toda centralidad, que favorece la
    /// infraestructura compartida sobre las capacidades de negocio.
    /// </summary>
    public ReportScoreProfiles ReportProfiles { get; init; } = new();

    /// <summary>Extensiones de los ficheros cuyas líneas se cuentan como producción.</summary>
    public IReadOnlyList<string> CountedExtensions { get; init; } = DefaultCountedExtensions;

    public IReadOnlyList<string> StopTokens { get; init; } = DefaultStopTokens;

    /// <summary>Lo que no se pudo leer de la configuración y se sustituyó por su valor por defecto, para avisar.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static PreparationOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new PreparationOptions();
        var warnings = new List<string>();

        var mode = defaults.Mode;
        if (section[nameof(Mode)] is { Length: > 0 } rawMode)
        {
            if (Enum.TryParse<PreparationMode>(rawMode.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                mode = parsed;
            else
                warnings.Add($"{SectionName}:{nameof(Mode)}: '{rawMode}' no es un modo conocido (Subsystems u Off); se usa {defaults.Mode}.");
        }

        var weightsSection = section.GetSection(nameof(Weights));
        var defaultWeights = defaults.Weights;

        return new PreparationOptions
        {
            Mode = mode,
            MaxSubsystemsPerReport = Positive(section, nameof(MaxSubsystemsPerReport), defaults.MaxSubsystemsPerReport, warnings),
            MaxParallelInvestigations = Positive(section, nameof(MaxParallelInvestigations), defaults.MaxParallelInvestigations, warnings),
            VocabularyMergeThreshold = Fraction(section, nameof(VocabularyMergeThreshold), defaults.VocabularyMergeThreshold, warnings),
            MinSubsystemFiles = Positive(section, nameof(MinSubsystemFiles), defaults.MinSubsystemFiles, warnings),
            MaxSubsystemShare = Fraction(section, nameof(MaxSubsystemShare), defaults.MaxSubsystemShare, warnings),
            PathTermWeightFactor = NonNegative(section, nameof(PathTermWeightFactor), defaults.PathTermWeightFactor, warnings),
            SharedMinConsumers = NonNegative(section, nameof(SharedMinConsumers), defaults.SharedMinConsumers, warnings),
            SharedMinConsumerShare = Fraction(section, nameof(SharedMinConsumerShare), defaults.SharedMinConsumerShare, warnings),
            LongTailSize = NonNegative(section, nameof(LongTailSize), defaults.LongTailSize, warnings),
            ConsolidationMaxToolIterations = NonNegative(section, nameof(ConsolidationMaxToolIterations), defaults.ConsolidationMaxToolIterations, warnings),
            Weights = new SubsystemScoreWeights
            {
                Centrality = NonNegative(weightsSection, nameof(SubsystemScoreWeights.Centrality), defaultWeights.Centrality, warnings),
                EntryPoints = NonNegative(weightsSection, nameof(SubsystemScoreWeights.EntryPoints), defaultWeights.EntryPoints, warnings),
                Tables = NonNegative(weightsSection, nameof(SubsystemScoreWeights.Tables), defaultWeights.Tables, warnings),
                States = NonNegative(weightsSection, nameof(SubsystemScoreWeights.States), defaultWeights.States, warnings),
                Integrations = NonNegative(weightsSection, nameof(SubsystemScoreWeights.Integrations), defaultWeights.Integrations, warnings),
                DynamicUseRisk = NonNegative(weightsSection, nameof(SubsystemScoreWeights.DynamicUseRisk), defaultWeights.DynamicUseRisk, warnings)
            },
            ReportProfiles = ReadProfiles(section.GetSection(nameof(ReportProfiles)), defaults.ReportProfiles, warnings),
            CountedExtensions = Words(section, nameof(CountedExtensions), defaults.CountedExtensions, NormalizeExtension),
            StopTokens = Words(section, nameof(StopTokens), defaults.StopTokens, word => word.Trim().ToLowerInvariant()),
            Warnings = warnings
        };
    }

    /// <summary>
    /// Los perfiles de <c>ReportProfiles</c>: cada factor que falta o no se entiende queda como en <paramref name="defaults"/>, y una
    /// clave que no es un informe en modo B se avisa y se ignora.
    /// </summary>
    private static ReportScoreProfiles ReadProfiles(IConfigurationSection section, ReportScoreProfiles defaults, List<string> warnings)
    {
        foreach (var child in section.GetChildren())
        {
            if (!CoveragePlanner.ModeBReports.Any(r => string.Equals(r.ToString(), child.Key, StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"{child.Path}: '{child.Key}' no es un informe que investigue por subsistema ({string.Join(", ", CoveragePlanner.ModeBReports)}); se ignora.");
        }

        return new ReportScoreProfiles
        {
            AnalisisFuncional = ReadProfile(section.GetSection(nameof(ReportType.AnalisisFuncional)), defaults.AnalisisFuncional, warnings),
            FlujosEndToEnd = ReadProfile(section.GetSection(nameof(ReportType.FlujosEndToEnd)), defaults.FlujosEndToEnd, warnings),
            ErroresYBugs = ReadProfile(section.GetSection(nameof(ReportType.ErroresYBugs)), defaults.ErroresYBugs, warnings),
            ArquitecturaAcoplamiento = ReadProfile(section.GetSection(nameof(ReportType.ArquitecturaAcoplamiento)), defaults.ArquitecturaAcoplamiento, warnings)
        };
    }

    private static SubsystemScoreProfile ReadProfile(IConfigurationSection section, SubsystemScoreProfile defaults, List<string> warnings) =>
        new()
        {
            Centrality = NonNegative(section, nameof(SubsystemScoreProfile.Centrality), defaults.Centrality, warnings),
            EntryPoints = NonNegative(section, nameof(SubsystemScoreProfile.EntryPoints), defaults.EntryPoints, warnings),
            Tables = NonNegative(section, nameof(SubsystemScoreProfile.Tables), defaults.Tables, warnings),
            States = NonNegative(section, nameof(SubsystemScoreProfile.States), defaults.States, warnings),
            Integrations = NonNegative(section, nameof(SubsystemScoreProfile.Integrations), defaults.Integrations, warnings),
            DynamicUseRisk = NonNegative(section, nameof(SubsystemScoreProfile.DynamicUseRisk), defaults.DynamicUseRisk, warnings)
        };

    /// <summary>Las de la configuración del <paramref name="serviceProvider"/>, o las de por defecto si no la hay.</summary>
    public static PreparationOptions FromServices(IServiceProvider? serviceProvider) =>
        serviceProvider?.GetService(typeof(IConfiguration)) is IConfiguration configuration
            ? FromConfiguration(configuration)
            : new PreparationOptions();

    internal static int Positive(IConfigurationSection section, string key, int fallback, List<string> warnings)
    {
        if (section[key] is not { Length: > 0 } raw)
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
            return value;
        warnings.Add($"{section.Path}:{key}: '{raw}' no es un entero positivo; se usa {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    internal static int NonNegative(IConfigurationSection section, string key, int fallback, List<string> warnings)
    {
        if (section[key] is not { Length: > 0 } raw)
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
            return value;
        warnings.Add($"{section.Path}:{key}: '{raw}' no es un entero mayor o igual que 0; se usa {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    internal static double NonNegative(IConfigurationSection section, string key, double fallback, List<string> warnings)
    {
        if (section[key] is not { Length: > 0 } raw)
            return fallback;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0)
            return value;
        warnings.Add($"{section.Path}:{key}: '{raw}' no es un número mayor o igual que 0; se usa {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    private static double Fraction(IConfigurationSection section, string key, double fallback, List<string> warnings)
    {
        if (section[key] is not { Length: > 0 } raw)
            return fallback;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value is >= 0 and <= 1)
            return value;
        warnings.Add($"{section.Path}:{key}: '{raw}' no es un número entre 0 y 1; se usa {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    private static string NormalizeExtension(string extension)
    {
        var value = extension.Trim().ToLowerInvariant();
        return value.StartsWith('.') ? value : "." + value;
    }

    /// <summary>
    /// Lista de la configuración (distinta, normalizada y sin vacíos), o <paramref name="fallback"/> si la clave no tiene
    /// elementos (un <c>[]</c> de JSON no deja ninguna clave: se queda con los de por defecto).
    /// </summary>
    private static IReadOnlyList<string> Words(IConfigurationSection section, string key, IReadOnlyList<string> fallback, Func<string, string> normalize)
    {
        var child = section.GetSection(key);
        if (!child.Exists())
            return fallback;
        return
        [
            .. child.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => normalize(v!)).Distinct(StringComparer.Ordinal)
        ];
    }
}
