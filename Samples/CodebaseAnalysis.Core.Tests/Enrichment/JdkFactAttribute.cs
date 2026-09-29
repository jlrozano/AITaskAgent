using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;
using CodebaseAnalysis.Core.Hosting;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// <c>[Fact]</c> que necesita un JDK 17 o superior (P15). Lo busca, por este orden, en la variable de entorno
/// <see cref="TestJdk.EnvironmentVariable"/>, en <c>JavaAnalyzer:JdkPath</c> del <c>appsettings.analysis.json</c> de la
/// salida, en la misma clave de los user secrets de la app (<see cref="AnalysisConfiguration.UserSecretsId"/>) y en el
/// primer JDK 17 o superior de <c>%LOCALAPPDATA%\Programs\jdk-*</c>, el más reciente por nombre. Sin JDK válido el
/// test sale como omitido con el motivo, sin paquetes de xUnit adicionales.
/// </summary>
public sealed class JdkFactAttribute : FactAttribute
{
    public JdkFactAttribute()
    {
        if (TestJdk.Info == null)
            Skip = TestJdk.SkipReason;
    }
}

/// <summary>El JDK de los tests, resuelto una vez por proceso.</summary>
internal static class TestJdk
{
    public const string EnvironmentVariable = "CODEBASE_ANALYSIS_TEST_JDK";

    private static readonly Lazy<(JdkInfo? Info, string Reason)> Resolved = new(Resolve);

    public static JdkInfo? Info => Resolved.Value.Info;

    public static string SkipReason => Resolved.Value.Reason;

    private static (JdkInfo?, string) Resolve()
    {
        const string hint = "Sin JDK: define CODEBASE_ANALYSIS_TEST_JDK con la carpeta de un JDK 17 o superior "
            + "(o JavaAnalyzer:JdkPath en appsettings.analysis.json o en los user secrets, o instala uno en %LOCALAPPDATA%\\Programs\\jdk-*)";

        // La variable de entorno y las dos configuraciones mandan: si nombran un JDK que no vale, se dice y no se busca otro.
        foreach (var (origin, candidate) in ConfiguredCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            try
            {
                return (JavaAnalyzerRunner.ResolveJdk(candidate), "");
            }
            catch (JavaAnalyzerException ex)
            {
                return (null, $"{hint}; el de {origin} no vale: {ex.Message}");
            }
        }

        foreach (var installed in InstalledJdks())
        {
            try
            {
                return (JavaAnalyzerRunner.ResolveJdk(installed), "");
            }
            catch (JavaAnalyzerException)
            {
                // Un JDK anterior al 17 o una carpeta incompleta: se prueba el siguiente.
            }
        }

        return (null, hint + ".");
    }

    private static IEnumerable<(string Origin, string? Path)> ConfiguredCandidates()
    {
        yield return (EnvironmentVariable, Environment.GetEnvironmentVariable(EnvironmentVariable));

        var settings = Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json");
        if (File.Exists(settings))
            yield return ("JavaAnalyzer:JdkPath", JavaAnalyzerOptions.FromConfiguration(new ConfigurationBuilder().AddJsonFile(settings).Build()).JdkPath);

        yield return ("JavaAnalyzer:JdkPath de los user secrets",
            JavaAnalyzerOptions.FromConfiguration(new ConfigurationBuilder().AddUserSecrets(AnalysisConfiguration.UserSecretsId).Build()).JdkPath);
    }

    /// <summary>Las carpetas <c>%LOCALAPPDATA%\Programs\jdk-*</c>, de la más reciente a la más antigua por nombre.</summary>
    private static IEnumerable<string> InstalledJdks()
    {
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        return Directory.Exists(programs)
            ? Directory.EnumerateDirectories(programs, "jdk-*").OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            : [];
    }
}
