using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// <c>[Fact]</c> que necesita un JDK 17 o superior (P15). Lo busca en la variable de entorno
/// <see cref="TestJdk.EnvironmentVariable"/> y, si no está, en <c>JavaAnalyzer:JdkPath</c> del
/// <c>appsettings.analysis.json</c> de la salida. Sin JDK válido el test sale como omitido con el motivo, sin paquetes
/// de xUnit adicionales.
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
            + "(o JavaAnalyzer:JdkPath en appsettings.analysis.json)";

        var candidate = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var origin = EnvironmentVariable;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var settings = Path.Combine(AppContext.BaseDirectory, "appsettings.analysis.json");
            if (File.Exists(settings))
                candidate = JavaAnalyzerOptions.FromConfiguration(new ConfigurationBuilder().AddJsonFile(settings).Build()).JdkPath;
            origin = "JavaAnalyzer:JdkPath";
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return (null, hint + ".");

        try
        {
            return (JavaAnalyzerRunner.ResolveJdk(candidate), "");
        }
        catch (JavaAnalyzerException ex)
        {
            return (null, $"{hint}; el de {origin} no vale: {ex.Message}");
        }
    }
}
