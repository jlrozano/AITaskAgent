using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Enrichment;

/// <summary>
/// Sección <c>JavaAnalyzer</c>: el analizador Java nativo (<c>javac</c> ejecutado como proceso aparte). La JVM, los lotes y
/// las rutas dependen de la máquina y del tamaño del repositorio, así que todo se ajusta por configuración (entorno, user
/// secrets o línea de comandos) sin tocar el fichero del sample.
/// </summary>
public sealed record JavaAnalyzerOptions
{
    public const string SectionName = "JavaAnalyzer";

    public bool Enabled { get; init; } = true;

    /// <summary>Carpeta de un JDK 17 o superior (la que contiene <c>bin/java</c> y <c>release</c>). Vacía = sin JDK.</summary>
    public string JdkPath { get; init; } = "";

    /// <summary>Opciones de la JVM, una por elemento.</summary>
    public IReadOnlyList<string> JvmOptions { get; init; } = ["-Xmx8g", "-XX:+UseG1GC"];

    /// <summary>Ficheros por lote; 0 = un solo lote.</summary>
    public int BatchSize { get; init; }

    /// <summary>Lotes que se analizan a la vez; la memoria se multiplica por este valor.</summary>
    public int MaxParallelBatches { get; init; } = 1;

    /// <summary>
    /// Jars que el código usa pero que no viajan con él (servlet-api, jsp-api…), como ficheros o carpetas de jars. Sin
    /// valores por defecto: la pasada informa de los paquetes que faltan.
    /// </summary>
    public IReadOnlyList<string> ProvidedClasspath { get; init; } = [];

    /// <summary>Raíces de test adicionales, relativas al repositorio; se suman a las del <c>pom.xml</c>.</summary>
    public IReadOnlyList<string> TestRoots { get; init; } = [];

    /// <summary>Codificación de los fuentes que no son UTF-8 válido. Vacía = la del <c>pom.xml</c> raíz, o UTF-8.</summary>
    public string DefaultEncoding { get; init; } = "";

    /// <summary>Límite del proceso Java, en segundos; al superarlo se mata. 0 = sin límite.</summary>
    public int TimeoutSeconds { get; init; } = 900;

    /// <summary>
    /// Límite del paso <c>LanguageEnricherStep</c> entero (proceso Java, escritura en el grafo y plugin Struts/JSP), en
    /// segundos. 0 = sin límite. Tiene que quedar por encima de <see cref="TimeoutSeconds"/> más
    /// <see cref="StepTimeoutMarginSeconds"/>; si no, el paso avisa al arrancar.
    /// </summary>
    public int StepTimeoutSeconds { get; init; } = 3600;

    /// <summary>
    /// Conserva <c>java-analysis.jsonl</c> tras aplicarlo al grafo, para depurar el analizador. Por defecto se borra al
    /// terminar bien la escritura: en el repositorio objetivo pesa del orden de 1 GB y no lo vuelve a leer nadie.
    /// </summary>
    public bool KeepIntermediate { get; init; }

    /// <summary>Margen para la escritura en el grafo y el plugin Struts/JSP que se espera entre los dos límites.</summary>
    public const int StepTimeoutMarginSeconds = 300;

    /// <summary>
    /// <c>Timeout</c> del paso. Sin límite es <see cref="Timeout.InfiniteTimeSpan"/> y no null: con null el pipeline aplica
    /// su timeout por defecto de paso.
    /// </summary>
    public TimeSpan StepTimeout => StepTimeoutSeconds > 0 ? TimeSpan.FromSeconds(StepTimeoutSeconds) : Timeout.InfiniteTimeSpan;

    public static JavaAnalyzerOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new JavaAnalyzerOptions();
        return new JavaAnalyzerOptions
        {
            Enabled = section.GetValue<bool?>(nameof(Enabled)) ?? defaults.Enabled,
            JdkPath = section[nameof(JdkPath)] ?? defaults.JdkPath,
            JvmOptions = ReadList(section, nameof(JvmOptions)) ?? defaults.JvmOptions,
            BatchSize = section.GetValue<int?>(nameof(BatchSize)) ?? defaults.BatchSize,
            MaxParallelBatches = section.GetValue<int?>(nameof(MaxParallelBatches)) ?? defaults.MaxParallelBatches,
            ProvidedClasspath = ReadList(section, nameof(ProvidedClasspath)) ?? defaults.ProvidedClasspath,
            TestRoots = ReadList(section, nameof(TestRoots)) ?? defaults.TestRoots,
            DefaultEncoding = section[nameof(DefaultEncoding)] ?? defaults.DefaultEncoding,
            TimeoutSeconds = section.GetValue<int?>(nameof(TimeoutSeconds)) ?? defaults.TimeoutSeconds,
            StepTimeoutSeconds = section.GetValue<int?>(nameof(StepTimeoutSeconds)) ?? defaults.StepTimeoutSeconds,
            KeepIntermediate = section.GetValue<bool?>(nameof(KeepIntermediate)) ?? defaults.KeepIntermediate
        };
    }

    /// <summary>
    /// Lista de la configuración, o null si la clave no tiene elementos. Una lista vacía en JSON no llega a
    /// <see cref="IConfiguration"/>, así que no se distingue de una clave ausente y se aplica el valor por defecto.
    /// </summary>
    internal static IReadOnlyList<string>? ReadList(IConfigurationSection section, string key)
    {
        var values = section.GetSection(key).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
        return values.Count > 0 ? values : null;
    }
}
