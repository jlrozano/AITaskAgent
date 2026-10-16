using System.Text;
using CodebaseAnalysis.Core.Enrichment;
using CodebaseAnalysis.Core.Enrichment.Java;

namespace CodebaseAnalysis.Core.Tests.Enrichment;

/// <summary>
/// Un repositorio Java pequeño con un caso positivo y otro negativo por cada regla de constantes resueltas (pieza 1 de la tanda 5):
/// credenciales en claro (<c>static final</c>, concatenación de constantes, argumentos de conexión) y el argumento de
/// <c>Class.forName</c>. Se analiza una sola vez con el JDK de las pruebas.
/// </summary>
public sealed class JavaConstantsFixture : IAsyncLifetime
{
    public const string DbConfigPath = "src/t/DbConfig.java";
    public const string FactoryPath = "src/t/Factory.java";

    private TempDirectory? _temp;

    public IReadOnlyList<JavaAnalysisRecord> Records { get; private set; } = [];

    public IReadOnlyList<JavaConstantRecord> Constants => [.. Records.OfType<JavaConstantRecord>()];

    public IEnumerable<JavaConstantRecord> Secrets(string? from = null) =>
        Constants.Where(c => c.What == JavaConstantKinds.Secret && (from == null || c.From?.StartsWith("t.DbConfig#" + from + "(", StringComparison.Ordinal) == true));

    public IEnumerable<JavaConstantRecord> Reflection(string method) =>
        Constants.Where(c => c.What == JavaConstantKinds.Reflection && c.From?.StartsWith("t.Factory#" + method + "(", StringComparison.Ordinal) == true);

    public int LineOf(string path, string fragment)
    {
        var lines = Sources[path].Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(fragment, StringComparison.Ordinal))
                return i + 1;
        }
        throw new ArgumentException($"'{fragment}' no está en {path}.");
    }

    public static IReadOnlyDictionary<string, string> Sources { get; } = new Dictionary<string, string>
    {
        [DbConfigPath] = """
            package t;

            import java.sql.Connection;
            import java.sql.DriverManager;
            import java.sql.SQLException;
            import java.util.Properties;

            public final class DbConfig {
                private static final String URL = "jdbc:oracle:thin:@dbhost01:1521:ORDERS";
                private static final String USER = "orders_app";
                private static final String PASSWORD = "Ord3rs#2019";
                private static final String PREFIX = "jdbc:sqlserver://h;user=app;";
                private static final String CONNECTION = PREFIX + "password=Sup3rSecret";
                private static final String API_KEY = "ak-" + "1234567890";
                private static final String USER_SECRETS_ID = "6c1b6b8e-2f3d-4f43-9d11-0a1f2b3c4d5e";
                private static final String TOKEN_URL = "https://login.example.com/token";
                private static final String PASSWORD_PLACEHOLDER_ = "${DB_PASSWORD}";
                private static final String HIDDEN_PASSWORD = "********";
                private static final String DRIVER = "oracle.jdbc.OracleDriver";
                private String password = "field-initial";

                void literalLocal() { String pwd = "local-pwd-1"; }
                void assignment() { this.password = "assigned-9"; }
                void connectionArgument() throws SQLException { DriverManager.getConnection(URL, USER, "arg-pass-7"); }
                void connectionConstant() throws SQLException { DriverManager.getConnection(URL, USER, PASSWORD); }
                void connectionString() throws SQLException { DriverManager.getConnection("jdbc:x://h;pwd=InUrl#1;db=a"); }
                void properties() { Properties p = new Properties(); p.setProperty("db.password", "prop-secret"); p.setProperty("db.user", "orders"); }
                void setter(Holder h) { h.setPassword("via-setter"); }
                String ternary(boolean flag, String other) { return flag ? "Server=a;Password=Tern4ry#1" : other; }

                void fromParameter(String password) { String copy = password; }
                void placeholder() { String token = "${TOKEN}"; }
                void maskedValue() { String secret = "xxxxxxxx"; }
                void notACredential() { String name = "plain-text"; String greeting = "hello world"; }
                void shortValue() { String pwd = "ab"; }
                void nonConstant(String suffix) { String token = "abc" + suffix; }
                void referenceName() { String tokenUrl = "https://t.example/token"; String passwordFile = "pw.txt"; }
                void keyNotCredential() { Properties p = new Properties(); p.setProperty("db.user", "orders-user"); }
                void setterNotCredential(Holder h) { h.setName("bobby"); }

                static class Holder {
                    void setPassword(String v) { }
                    void setName(String v) { }
                }
            }
            """,
        [FactoryPath] = """
            package t;

            public class Factory {
                private static final String PKG = "com.acme.orders.export.";
                private static final String CLASS_NAME = "com.acme.orders.util.LegacyPrinter";

                Object byPattern(String name) throws Exception { return Class.forName(PKG + name + "Impl").newInstance(); }
                Object fixedLiteral() throws Exception { return Class.forName("com.acme.orders.util.Fixed").newInstance(); }
                Object fixedConstant() throws Exception { return Class.forName(CLASS_NAME).newInstance(); }
                Object concatenatedConstants() throws Exception { return Class.forName(PKG + "Csv" + "Exporter"); }
                Object viaLoader(ClassLoader cl, String n) throws Exception { return cl.loadClass(PKG + n); }
                Object nested() throws Exception { return Class.forName("com.acme.Outer$Inner"); }

                Object fullyDynamic(String name) throws Exception { return Class.forName(name); }
                Object tooShort(String name) throws Exception { return Class.forName("a." + name); }
                Object otherForName() { return Integer.valueOf(forName("x")); }
                int forName(String s) { return 1; }
                Object typeLiteral() throws Exception { return Class.forName(String.class.getName()); }
            }
            """
    };

    public async Task InitializeAsync()
    {
        if (TestJdk.Info == null)
            return;

        _temp = new TempDirectory();
        var root = Path.Combine(_temp.Path, "repo");
        foreach (var (path, text) in Sources)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, new UTF8Encoding(false).GetBytes(text.ReplaceLineEndings("\n")));
        }

        var scan = RepositoryScan.Scan(root, []);
        var run = await JavaAnalyzerRunner.RunAsync(new JavaAnalyzerRunRequest
        {
            Scan = scan,
            Options = new JavaAnalyzerOptions { JdkPath = TestJdk.Info.JdkPath, JvmOptions = ["-Xmx1g"] },
            Jdk = TestJdk.Info,
            Classpath = new JavaClasspath([], []),
            DefaultEncoding = "UTF-8",
            WorkingDirectory = Path.Combine(_temp.Path, "work")
        }, null, null, CancellationToken.None);
        Records = [.. JavaAnalysisReader.Read(run.ResultPath)];
    }

    public Task DisposeAsync()
    {
        _temp?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Registros <c>const</c> de <c>JavaAnalyzer.java</c>: credenciales con el valor enmascarado y argumentos de reflexión resueltos.</summary>
public class JavaConstantsTests(JavaConstantsFixture f) : IClassFixture<JavaConstantsFixture>
{
    private IEnumerable<string> SecretNames(string method) => f.Secrets(method).Select(s => s.Name ?? "").Order(StringComparer.Ordinal);

    [JdkFact]
    public void The_fixture_compiles_without_errors_and_the_summary_counts_the_constants()
    {
        Assert.DoesNotContain(f.Records, r => r is JavaDiagnosticRecord);
        var summary = f.Records.OfType<JavaSummaryRecord>().Single();
        Assert.Equal("4", summary.AnalyzerVersion);
        Assert.Equal(f.Constants.Count(c => c.What == JavaConstantKinds.Secret), summary.Secrets);
        Assert.Equal(f.Constants.Count(c => c.What == JavaConstantKinds.Reflection), summary.ReflectionTargets);
    }

    [JdkFact]
    public void Static_final_constants_with_a_credential_name_are_found_even_when_they_are_concatenations()
    {
        var fields = f.Constants.Where(c => c.What == JavaConstantKinds.Secret && c.From == null && c.FromField != null)
            .ToDictionary(c => c.Name + "/" + c.Kind, c => c);

        var password = fields["PASSWORD/identifier"];
        Assert.Equal("Or*** (11 caracteres)", password.Value);
        Assert.Equal(f.LineOf(JavaConstantsFixture.DbConfigPath, "PASSWORD = \"Ord3rs#2019\""), password.Line);
        Assert.Equal("t.DbConfig#PASSWORD", password.FromField);
        // «ak-» + «1234567890» es una concatenación de constantes: la resuelve el compilador.
        Assert.Equal("ak*** (13 caracteres)", fields["API_KEY/identifier"].Value);
        // PREFIX + "password=..." es una cadena de conexión con contraseña en una constante con nombre sin credencial.
        Assert.Equal("Su*** (11 caracteres)", fields["CONNECTION/connection-string"].Value);
        Assert.Equal("fi*** (13 caracteres)", fields["password/identifier"].Value);
    }

    [JdkFact]
    public void Secrets_never_leave_the_analyzer_in_clear()
    {
        var everything = string.Join('\n', f.Constants.Select(c => c.Value));
        foreach (var secret in new[] { "Ord3rs#2019", "Sup3rSecret", "1234567890", "local-pwd-1", "assigned-9", "arg-pass-7", "InUrl#1", "prop-secret", "via-setter", "Tern4ry#1" })
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
    }

    [JdkFact]
    public void Locals_assignments_and_call_arguments_that_carry_a_credential_are_found()
    {
        Assert.Equal(["pwd"], SecretNames("literalLocal"));
        Assert.Equal(["password"], SecretNames("assignment"));
        Assert.Equal(["getConnection"], SecretNames("connectionArgument"));
        Assert.Equal(["db.password"], SecretNames("properties"));
        Assert.Equal(["setPassword"], SecretNames("setter"));
        Assert.Equal("ar*** (10 caracteres)", Assert.Single(f.Secrets("connectionArgument")).Value);

        // Una cadena de conexión en la rama de un ternario no tiene declaración ni argumento: la recoge el literal.
        var ternary = Assert.Single(f.Secrets("ternary"));
        Assert.Equal(("connection-string", "Te*** (9 caracteres)", null), (ternary.Kind, ternary.Value, ternary.Name));

        var inUrl = Assert.Single(f.Secrets("connectionString"));
        Assert.Equal("connection-string", inUrl.Kind);
        Assert.Equal("In*** (7 caracteres)", inUrl.Value);
    }

    [JdkFact]
    public void A_credential_constant_passed_by_name_is_reported_once_at_its_declaration()
    {
        Assert.Empty(f.Secrets("connectionConstant"));
    }

    [JdkFact]
    public void References_placeholders_masked_values_short_values_and_non_constants_are_not_reported_as_secrets()
    {
        foreach (var method in new[] { "fromParameter", "placeholder", "maskedValue", "notACredential", "shortValue", "nonConstant", "keyNotCredential", "setterNotCredential" })
            Assert.Empty(f.Secrets(method));

        // Los nombres de referencia (tokenUrl, passwordFile) llegan al escritor, que los descarta con NamesAReference.
        Assert.Equal(["passwordFile", "tokenUrl"], SecretNames("referenceName"));
        var fieldsWithoutCredentialName = f.Constants.Where(c => c.What == JavaConstantKinds.Secret && c.FromField != null).Select(c => c.Name).ToList();
        Assert.DoesNotContain("URL", fieldsWithoutCredentialName);
        Assert.DoesNotContain("USER", fieldsWithoutCredentialName);
        Assert.DoesNotContain("DRIVER", fieldsWithoutCredentialName);
        Assert.DoesNotContain("PASSWORD_PLACEHOLDER_", fieldsWithoutCredentialName);
        Assert.DoesNotContain("HIDDEN_PASSWORD", fieldsWithoutCredentialName);
    }

    [JdkFact]
    public void Class_forName_arguments_are_resolved_with_a_star_for_what_is_not_constant()
    {
        // La trampa *ExporterImpl: PKG constante + nombre que llega de fuera + "Impl".
        var pattern = Assert.Single(f.Reflection("byPattern"));
        Assert.Equal("com.acme.orders.export.*Impl", pattern.Value);
        Assert.Equal("Class.forName", pattern.Kind);
        Assert.Equal(f.LineOf(JavaConstantsFixture.FactoryPath, "Class.forName(PKG + name"), pattern.Line);

        Assert.Equal("com.acme.orders.util.Fixed", Assert.Single(f.Reflection("fixedLiteral")).Value);
        Assert.Equal("com.acme.orders.util.LegacyPrinter", Assert.Single(f.Reflection("fixedConstant")).Value);
        Assert.Equal("com.acme.orders.export.CsvExporter", Assert.Single(f.Reflection("concatenatedConstants")).Value);
        var loader = Assert.Single(f.Reflection("viaLoader"));
        Assert.Equal(("com.acme.orders.export.*", "loadClass"), (loader.Value, loader.Kind));
        Assert.Equal("com.acme.Outer.Inner", Assert.Single(f.Reflection("nested")).Value);
    }

    [JdkFact]
    public void A_fully_dynamic_or_too_short_class_name_or_another_forName_is_not_a_target()
    {
        Assert.Empty(f.Reflection("fullyDynamic"));
        Assert.Empty(f.Reflection("tooShort"));
        Assert.Empty(f.Reflection("otherForName"));
        Assert.Empty(f.Reflection("typeLiteral"));
    }
}
