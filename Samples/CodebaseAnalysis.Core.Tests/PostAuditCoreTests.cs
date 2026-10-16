using CodebaseAnalysis.Core.Chat;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Steps;
using CodebaseAnalysis.Core.Support;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodebaseAnalysis.Core.Tests;

public sealed class HardcodedSecretScannerTests
{
    private static async Task<List<JsonElement>> ScanLinesAsync(params string[] lines)
    {
        using var dir = new TempDirectory();
        File.WriteAllLines(Path.Combine(dir.Path, "Sample.txt"), lines);
        var json = await HardcodedSecretScanner.ScanAsync(["Sample.txt"], dir.Path, CancellationToken.None);
        return JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [Theory]
    [InlineData("Private Const PASSWORD As String = \"Sup3rClave\"", "PASSWORD")]
    [InlineData("var pwd = \"Sup3rClave\";", "pwd")]
    [InlineData("api_key = 'abcd1234efgh'", "api_key")]
    [InlineData("\"ApiKey\": \"abcd1234efgh\"", "ApiKey")]
    [InlineData("access_token := \"abcd1234efgh\"", "access_token")]
    [InlineData("public const string ClientSecret = \"abcd1234efgh\";", "ClientSecret")]
    [InlineData("authTokenValue = \"abcd1234efgh\"", "authTokenValue")]
    [InlineData("cn = \"Server=db;Database=x;UID=sa;PWD=Sup3rClave;\"", "PWD")]
    [InlineData("\"Data Source=db;User Id=sa;Password=Sup3rClave\"", "Password")]
    public async Task Real_credentials_are_reported_with_the_value_masked(string line, string identifier)
    {
        var finding = Assert.Single(await ScanLinesAsync("x = 1", line));

        Assert.Equal(2, finding.GetProperty("start_line").GetInt32());
        Assert.Equal(identifier, finding.GetProperty("identifier").GetString());
        Assert.DoesNotContain("Sup3rClave", finding.GetProperty("evidence").GetString());
        Assert.DoesNotContain("abcd1234efgh", finding.GetProperty("evidence").GetString());
    }

    [Theory]
    [InlineData("public const string UserSecretsId = \"3e60cd17-5eb3-409f-b631-3ab8497c947d\";")]
    [InlineData("SECRET_ID = \"3e60cd17-5eb3-409f-b631-3ab8497c947d\"")]
    [InlineData("var passwordFileName = \"credenciales.txt\";")]
    [InlineData("\"TokenUrl\": \"https://login.example/token\"")]
    [InlineData("token_endpoint = \"https://login.example/token\"")]
    [InlineData("SecretProvider = \"AzureKeyVault\"")]
    [InlineData("passwordLength: \"12\"")]
    [InlineData("apiKeyHeader = \"X-Api-Key\"")]
    [InlineData("SecretURLPath = \"/secrets/app\"")]
    [InlineData("password = \"${DB_PASSWORD}\"")]
    public async Task Identifiers_that_name_a_reference_are_not_credentials(string line)
    {
        Assert.Empty(await ScanLinesAsync(line));
    }

    [Theory]
    [InlineData("UserSecretsId", true)]
    [InlineData("PASSWORD_ID", true)]
    [InlineData("tokenType", true)]
    [InlineData("SecretURLPath", true)]
    [InlineData("ApiKey", false)]
    [InlineData("api_key", false)]
    [InlineData("ClientSecret", false)]
    [InlineData("password", false)]
    [InlineData("PWD", false)]
    [InlineData("Id", false)]
    public void NamesAReference_looks_only_at_the_last_segment(string identifier, bool expected)
    {
        Assert.Equal(expected, HardcodedSecretScanner.NamesAReference(identifier));
    }
}

public sealed class ReportMarkdownTests
{
    private const string Matrix = """
        # Informe de errores

        ## Matriz de criticidad

        | ID | Severidad | Tipo | Evidencia |
        |---|:---:|---|---|
        | SEC-001 | **P0** | Credencial en claro | `a.bas:6` |
        | ERR-001 | P1 | Inyección SQL | `b.bas:10` |
        | ERR-002 | P2 | Bucle frágil | `b.bas:30` |
        | ERR-003 | 🔴 P1 | Errores silenciados | `c.bas:5` |
        | ERR-004 | P3 | Typo | `c.bas:9` |

        ## Plan

        | ID | Prioridad | Fase |
        |---|---|---|
        | SEC-01 | P0 | 1 |
        | ERR-1 | P1 | 1 |

        ```markdown
        | ID | Severidad |
        |---|---|
        | FAKE-001 | P0 |
        ```

        <!-- analysis-meta: health=Red; critical=6 -->
        """;

    [Fact]
    public void Critical_findings_are_counted_from_the_severity_tables_once_per_id()
    {
        // SEC-001/SEC-01 y ERR-001/ERR-1 son el mismo hallazgo; la tabla dentro del bloque de código no cuenta.
        Assert.Equal(3, ReportMarkdown.CountCriticalFindings(Matrix));
    }

    [Fact]
    public void Critical_findings_with_the_report_prefix_are_counted_once_per_id()
    {
        // 05-SEC-001 y 05-SEC-1 son el mismo; 05-SEC-001 y 07-SEC-001 no.
        var table = "| ID | Severidad |\n|---|---|\n| 05-SEC-001 | P0 |\n| 05-SEC-1 | P0 |\n| 07-SEC-001 | P1 |\n| 05-ERR-002 | P2 |\n";
        Assert.Equal(2, ReportMarkdown.CountCriticalFindings(table));
    }

    [Fact]
    public void Without_a_severity_table_there_is_no_count()
    {
        Assert.Null(ReportMarkdown.CountCriticalFindings("# X\n\n| ID | Impacto |\n|---|---|\n| A-001 | Alto |\n"));
    }

    [Fact]
    public void A_table_without_ids_counts_its_critical_rows()
    {
        Assert.Equal(2, ReportMarkdown.CountCriticalFindings("| Hallazgo x | Criticidad |\n|---|---|\n| a | P0 |\n| b | P1/P2 |\n| c | P2 |\n"));
    }

    [Fact]
    public void SetCriticalInMeta_rewrites_the_last_meta_line_and_keeps_the_rest()
    {
        var fixedText = ReportMarkdown.SetCriticalInMeta(Matrix, 3);

        Assert.EndsWith("<!-- analysis-meta: health=Red; critical=3 -->", fixedText.TrimEnd());
        var report = new SingleReportResult { MarkdownContent = fixedText };
        report.PopulateDerivedMetadata();
        Assert.Equal(3, report.CriticalFindings);
        Assert.Equal("Red", report.QualityHealth);
        Assert.Equal("<!-- analysis-meta: critical=0 -->", ReportMarkdown.SetCriticalInMeta("# X", 0).Trim().Split('\n')[^1]);
    }

    [Theory]
    [InlineData("# `00_resumen_ejecutivo.md`\n\n# Resumen ejecutivo\n\nTexto", "# Resumen ejecutivo\n\nTexto")]
    [InlineData("\n# 00_resumen_ejecutivo.md\n# Resumen\nTexto", "\n# Resumen\nTexto")]
    [InlineData("# informe.md\n\nTexto sin otro título", "# informe.md\n\nTexto sin otro título")]
    [InlineData("# Resumen ejecutivo\n\n# Otro", "# Resumen ejecutivo\n\n# Otro")]
    public void A_leading_file_name_heading_is_removed_only_when_a_real_title_follows(string input, string expected)
    {
        Assert.Equal(expected, ReportMarkdown.RemoveFileNameHeading(input));
    }

    [Fact]
    public void The_finding_index_lists_each_identifier_once_with_its_severity_and_type()
    {
        var markdown = string.Join("\n",
            "# X", "",
            "| ID | Severidad | Tipo | Evidencia |", "|---|---|---|---|",
            "| `05-SEC-001` | **P1** | Inyección | a.cs:1 |", "| 05-ERR-002 | P2 | Concurrencia | b.cs:2 |", "",
            "| ID | Severidad |", "|---|---|", "| 05-SEC-001 | P1 |", "",
            "| Otro | Dato |", "|---|---|", "| x | y |");
        Assert.Equal(["05-SEC-001 · P1 · Inyección", "05-ERR-002 · P2 · Concurrencia"], ReportMarkdown.FindingIndex(markdown));
        Assert.Empty(ReportMarkdown.FindingIndex("# Sin tablas"));
    }

    public static TheoryData<string[], string[]> PrefaceCases => new()
    {
        { ["La evidencia confirma el punto de entrada.", "# Informe", "", "Texto"], ["# Informe", "", "Texto"] },
        { ["Frase uno.", "", "Frase dos.", "", "# Informe", "Texto"], ["# Informe", "Texto"] },
        { ["# Informe", "Texto"], ["# Informe", "Texto"] },
        { ["## Sección sin título", "Texto", "# Después"], ["## Sección sin título", "Texto", "# Después"] },
        { ["| a | b |", "|---|---|", "# Informe"], ["| a | b |", "|---|---|", "# Informe"] },
        { ["```", "# no es título", "```", "# Informe"], ["```", "# no es título", "```", "# Informe"] },
        { ["Sin ningún título", "Texto"], ["Sin ningún título", "Texto"] }
    };

    [Theory]
    [MemberData(nameof(PrefaceCases))]
    public void Prose_before_the_title_is_removed_only_when_it_is_plain_text(string[] input, string[] expected)
    {
        Assert.Equal(string.Join("\n", expected), ReportMarkdown.RemoveTextBeforeTitle(string.Join("\n", input)));
    }
}

public sealed class TotalLocTests
{
    private static async Task<int> LocAsync(params (string Label, string Name, string File, int Start, int End, string Properties)[] nodes)
    {
        using var dir = new TempDirectory();
        var db = Path.Combine(dir.Path, "codebase.db");
        await GraphSchema.EnsureAsync(db, CancellationToken.None);
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        await conn.OpenAsync();
        foreach (var n in nodes)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO nodes (label, name, qualified_name, file, start_line, end_line, properties) VALUES ($l, $n, $q, $f, $s, $e, $p);";
            cmd.Parameters.AddWithValue("$l", n.Label);
            cmd.Parameters.AddWithValue("$n", n.Name);
            cmd.Parameters.AddWithValue("$q", $"{n.File}:{n.Label}:{n.Name}");
            cmd.Parameters.AddWithValue("$f", n.File);
            cmd.Parameters.AddWithValue("$s", n.Start);
            cmd.Parameters.AddWithValue("$e", n.End);
            cmd.Parameters.AddWithValue("$p", n.Properties);
            await cmd.ExecuteNonQueryAsync();
        }
        return await SqlFacetExtractorStep.ComputeTotalLocAsync(conn, CancellationToken.None);
    }

    [Fact]
    public async Task A_csharp_graph_without_loc_total_counts_the_line_span_of_its_code_files()
    {
        // Lo que deja codebase-memory en C#: File sin líneas, Module con el rango del fichero y tipos sin loc_total.
        var loc = await LocAsync(
            ("File", "A.cs", "A.cs", 0, 0, "{\"extension\":\".cs\"}"),
            ("Module", "A.cs", "A.cs", 1, 120, "{\"lines\":0}"),
            ("Class", "A", "A.cs", 5, 118, "{\"lines\":0}"),
            ("Method", "Run", "A.cs", 10, 40, "{\"lines\":31}"),
            ("File", "B.cs", "B.cs", 0, 0, "{}"),
            ("Class", "B", "B.cs", 3, 30, "{}"),
            // Markdown y JSON tienen contenedor pero no son código.
            ("Module", "README.md", "README.md", 1, 300, "{}"),
            ("Section", "Intro", "README.md", 1, 20, "{}"),
            ("Module", "appsettings.json", "appsettings.json", 1, 80, "{}"),
            ("Variable", "Logging", "appsettings.json", 2, 5, "{}"));

        Assert.Equal(120 + 30, loc);
    }

    [Fact]
    public async Task Loc_total_wins_over_line_spans()
    {
        var loc = await LocAsync(
            ("Module", "M1", "modules/M1.bas", 1, 371, "{\"loc_total\":300}"),
            ("Sub", "S", "modules/M1.bas", 10, 20, "{}"),
            ("Class", "C", "src/C.java", 1, 500, "{\"loc_total\":200}"),
            ("Interface", "I", "src/C.java", 501, 520, "{\"loc_total\":20}"));

        Assert.Equal(300 + 220, loc);
    }
}

public sealed class ChatSessionCorruptionTests
{
    [Fact]
    public void A_corrupt_session_file_reads_as_missing_but_exists()
    {
        using var dir = new TempDirectory();
        var store = new ChatSessionStore(dir.Path);
        var session = store.Create("C:/repo");
        File.WriteAllText(store.GetFilePath(session.Id)!, "{ roto");

        Assert.Null(store.TryGet(session.Id));
        Assert.Null(store.TryGet(session.Id, out var corrupt));
        Assert.True(corrupt);
        Assert.True(store.Exists(session.Id));
        Assert.Contains("dañado", ChatSessionStore.NotFoundMessage(session.Id, corrupt));
        Assert.True(store.Delete(session.Id));
        Assert.False(store.Exists(session.Id));
    }
}
