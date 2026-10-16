using CodebaseAnalysis.Core.Support;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="SqlTableScanner"/>: el escáner determinista de nombres de tabla en literales SQL de <c>.java</c> y <c>.cs</c> que alimenta
/// <c>inventory.persistence.tables</c> y <c>Metrics.Tables</c> cuando el grafo no trae nodos <c>Table</c> (los DAO consultan con SQL en
/// cadenas). Sin disco salvo los tests de <see cref="SqlTableScanner.Scan"/>, y sin nombres de dominio: las tablas de los ejemplos son genéricas.
/// </summary>
public sealed class SqlTableScannerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string[] Names(string code, bool java = true) =>
        [.. SqlTableScanner.ScanText("src/A." + (java ? "java" : "cs"), code, java).Select(r => r.Name)];

    private static IReadOnlyList<SqlTableReference> Refs(string code, bool java = true) =>
        SqlTableScanner.ScanText("src/A." + (java ? "java" : "cs"), code, java);

    // ───────────────────────────── Sentencias ─────────────────────────────

    [Fact]
    public void It_reads_the_tables_of_select_insert_update_and_delete_literals()
    {
        var code = """
            class Dao {
                void run() {
                    stmt("SELECT id, name FROM customer WHERE id = ?");
                    stmt("INSERT INTO invoice (number, customer_id) VALUES (?, ?)");
                    stmt("UPDATE reminder SET sent = 1 WHERE id = ?");
                    stmt("DELETE FROM payment WHERE id = ?");
                }
            }
            """;

        var refs = Refs(code);

        Assert.Equal(["customer", "invoice", "reminder", "payment"], refs.Select(r => r.Name));
        Assert.Equal([3, 4, 5, 6], refs.Select(r => r.Line));
        Assert.Equal(["SELECT", "INSERT", "UPDATE", "DELETE"], refs.Select(r => r.Statement));
        Assert.All(refs, r => Assert.Null(r.Schema));
    }

    [Fact]
    public void It_reads_joins_lists_of_tables_merge_and_ddl()
    {
        var code = """
            class Dao {
                String a = "SELECT o.id FROM orders o INNER JOIN lines l ON l.order_id = o.id LEFT OUTER JOIN products p ON p.id = l.product_id";
                String b = "SELECT * FROM stock s, warehouse w WHERE s.w_id = w.id";
                String c = "MERGE INTO balance b USING incoming i ON (b.id = i.id) WHEN MATCHED THEN UPDATE SET b.v = i.v";
                String d = "TRUNCATE TABLE staging";
                String e = "CREATE TABLE IF NOT EXISTS audit_log (id INT)";
                String f = "SELECT * FROM (SELECT id FROM inner_t) x WHERE x.id IN (SELECT ref FROM other_t)";
            }
            """;

        Assert.Equal(
            ["orders", "lines", "products", "stock", "warehouse", "balance", "staging", "audit_log", "inner_t", "other_t"],
            Names(code));
    }

    [Fact]
    public void A_schema_prefix_is_kept_apart_from_the_table_and_quoted_names_are_read()
    {
        var code = """
            class Dao {
                String a = "SELECT * FROM APP.ORDERS o JOIN app.\"Customers\" c ON c.id = o.cid";
                String b = "INSERT INTO db1.dbo.Audit (a) VALUES (?)";
                String c = "SELECT * FROM `ledger` l";
            }
            """;

        var refs = Refs(code);

        Assert.Equal([("ORDERS", "APP"), ("Customers", "app"), ("Audit", "dbo"), ("ledger", null)], refs.Select(r => (r.Name, r.Schema)));
    }

    [Fact]
    public void A_statement_spread_over_lines_and_concatenations_is_one_statement()
    {
        var code = """"
            class Dao {
                String sql = "SELECT i.id, i.number "
                    + "FROM invoice i "
                    + "JOIN customer c "
                    + "  ON c.id = i.customer_id "
                    + "WHERE i.paid = 0";
                String block = """
                    SELECT *
                      FROM shipment
                     WHERE sent = 0
                    """;
            }
            """";

        var refs = Refs(code);

        Assert.Equal(["invoice", "customer", "shipment"], refs.Select(r => r.Name));
        // La línea es la del primer fragmento de la sentencia.
        Assert.Equal([2, 2, 7], refs.Select(r => r.Line));
    }

    [Fact]
    public void Constants_of_the_same_file_are_resolved_in_a_concatenation()
    {
        var code = """
            class Dao {
                private static final String COLUMNS = "SELECT id, sku FROM product";
                private static final String TABLE = "ledger";
                void a() { stmt(COLUMNS + " WHERE id = ?"); }
                void b() { stmt("SELECT * FROM " + TABLE + " WHERE id = ?"); }
                void c() { stmt("SELECT * FROM " + other() + " WHERE id = ?"); }
                void d() { stmt(COLUMNS + " JOIN price pr ON pr.product_id = id"); }
            }
            """;

        var names = Names(code);

        // El literal de la constante, su uso con una cola que añade un JOIN, y la constante dentro de la sentencia; lo que no se resuelve se omite.
        Assert.Contains("product", names);
        Assert.Contains("ledger", names);
        Assert.Contains("price", names);
        Assert.DoesNotContain("other", names);
        Assert.DoesNotContain("TABLE", names);
    }

    [Fact]
    public void Csharp_verbatim_interpolated_and_const_strings_are_read()
    {
        var code = """"
            class Repo
            {
                private const string Select = "SELECT Id, Name FROM Product";
                string a = $"SELECT * FROM {schema}.Orders WHERE Id = {id}";
                string b = @"SELECT *
                               FROM ""Customer"" c
                              WHERE c.Id = @id";
                string c = Select + " WHERE Active = 1";
                string d = """
                    DELETE FROM Cart WHERE Id = @id
                    """;
            }
            """";

        Assert.Equal(["Product", "Orders", "Customer", "Product", "Cart"], Names(code, java: false));
    }

    // ───────────────────────────── Lo que no es SQL ─────────────────────────────

    [Fact]
    public void Comments_are_ignored()
    {
        var code = """
            class Dao {
                // SELECT * FROM old_table
                /* INSERT INTO removed (a) VALUES (?)
                   UPDATE legacy SET x = 1 */
                String live = "SELECT * FROM current_t";
                /** Ejecuta "DELETE FROM javadoc_t" */
                String url = "http://example.com/a//b"; // SELECT * FROM after_line
            }
            """;

        Assert.Equal(["current_t"], Names(code));
    }

    [Fact]
    public void Strings_that_are_not_sql_do_not_give_tables()
    {
        var code = """
            class Ui {
                String a = "Select an option from the list below";
                String b = "Delete from the list";
                String c = "Update from remote server finished";
                String d = "Results from database";
                String e = "SELECT EXTRACT(YEAR FROM created) FROM events";
                String f = "SELECT SUBSTRING(code FROM 2) FROM items";
                String g = "No hay datos para mostrar";
                String h = "INSERT";
                String i = "from customer";
            }
            """;

        // Solo las tablas de verdad: `FROM` dentro de EXTRACT/SUBSTRING no es una tabla.
        Assert.Equal(["events", "items"], Names(code));
    }

    [Fact]
    public void A_name_that_continues_outside_the_literal_is_not_a_table()
    {
        var code = """
            class Dao {
                void a() { sql.append("INSERT INTO APP."); sql.append(Tables.LEDGER); }
                void b() { stmt("SELECT * FROM " + Tables.LEDGER + " WHERE id = ?"); }
                void c() { stmt("SELECT * FROM " + schema + ".ledger l WHERE id = ?"); }
                void d() { stmt("SELECT * FROM " + table + " WHERE id = ?"); }
            }
            """;

        var refs = Refs(code);

        // El esquema colgando no es una tabla; un miembro de otra clase no se resuelve; una variable en el esquema deja la tabla; una variable sola no es nada.
        Assert.Equal([("ledger", (string?)null)], refs.Select(r => (r.Name, r.Schema)));
    }

    [Fact]
    public void A_source_that_cannot_be_parsed_gives_what_it_can_and_never_throws()
    {
        var code = "class X { String a = \"SELECT * FROM first_t\"; String b = \"unterminated FROM nothing\n String c = \"SELECT * FROM second_t\"; char q = '\"'; }";

        var names = Names(code);

        Assert.Contains("first_t", names);
        Assert.Contains("second_t", names);
        Assert.Empty(Names(string.Empty));
        Assert.Empty(Names("\"\"\" SELECT * FROM never_closed"));
    }

    // ───────────────────────────── Escaneo de ficheros ─────────────────────────────

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_temp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Scan_associates_each_table_to_its_file_in_a_deterministic_order_and_counts_what_it_read()
    {
        Write("src/b/Two.java", "class Two { String s = \"SELECT * FROM shared_t JOIN only_two ON 1=1\"; }");
        Write("src/a/One.java", "class One { String s = \"UPDATE shared_t SET a = 1\"; }");
        Write("src/a/Three.cs", "class Three { string s = \"DELETE FROM only_three\"; }");
        Write("src/a/Ignored.txt", "SELECT * FROM ignored_t");
        var files = new[] { "src/b/Two.java", "src/a/One.java", "src/a/Three.cs", "src/a/Ignored.txt", "src/a/Missing.java" };

        var scan = SqlTableScanner.Scan(_temp.Path, files, CancellationToken.None);
        var again = SqlTableScanner.Scan(_temp.Path, files.Reverse(), CancellationToken.None);

        Assert.Equal(3, scan.FilesScanned);
        Assert.Equal(1, scan.FilesSkipped);
        Assert.Null(scan.StoppedBecause);
        Assert.Equal(
            [("src/a/One.java", "shared_t"), ("src/a/Three.cs", "only_three"), ("src/b/Two.java", "shared_t"), ("src/b/Two.java", "only_two")],
            scan.References.Select(r => (r.File, r.Name)));
        Assert.Equal(scan.References.Select(r => (r.File, r.Line, r.Name)), again.References.Select(r => (r.File, r.Line, r.Name)));
        Assert.Equal(["only_three", "only_two", "shared_t"], scan.TablesByFile().SelectMany(f => f.Value).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["shared_t"], scan.TablesByFile()["src/a/One.java"]);
        var shared = scan.Tables.Single(t => t.Name == "shared_t");
        Assert.Equal(2, shared.References);
        Assert.Equal(2, shared.Files);
    }

    [Fact]
    public void Tables_that_differ_only_in_case_or_schema_are_one_table()
    {
        Write("src/One.java", "class One { String a = \"SELECT * FROM Ledger\"; String b = \"SELECT * FROM APP.LEDGER\"; String c = \"SELECT * FROM ledger\"; }");

        var scan = SqlTableScanner.Scan(_temp.Path, ["src/One.java"], CancellationToken.None);

        var table = Assert.Single(scan.Tables);
        Assert.Equal(3, table.References);
        // El nombre y el esquema son los de la primera aparición que lo trae.
        Assert.Equal("Ledger", table.Name);
        Assert.Equal("APP", table.Schema);
    }

    [Fact]
    public void An_unreadable_or_huge_file_is_skipped_and_the_rest_are_read()
    {
        Write("src/Good.java", "class G { String s = \"SELECT * FROM good_t\"; }");
        Write("src/Huge.java", "// " + new string('x', SqlTableScanner.MaxFileBytes + 10) + "\nclass H { String s = \"SELECT * FROM huge_t\"; }");

        var scan = SqlTableScanner.Scan(_temp.Path, ["src/Huge.java", "src/Good.java"], CancellationToken.None);

        Assert.Equal(["good_t"], scan.References.Select(r => r.Name));
        Assert.Equal(1, scan.FilesScanned);
        Assert.Equal(1, scan.FilesSkipped);
    }

    [Fact]
    public void The_scan_stops_at_the_file_limit_and_says_so()
    {
        for (var i = 0; i < 5; i++)
            Write($"src/F{i}.java", $"class F{i} {{ String s = \"SELECT * FROM t{i}\"; }}");

        var scan = SqlTableScanner.Scan(_temp.Path, Enumerable.Range(0, 5).Select(i => $"src/F{i}.java"), CancellationToken.None, maxFiles: 3);

        Assert.Equal(3, scan.FilesScanned);
        Assert.Equal(SqlTableScan.StoppedMaxFiles, scan.StoppedBecause);
        Assert.Equal(["t0", "t1", "t2"], scan.References.Select(r => r.Name));
    }

    [Fact]
    public void The_scan_stops_at_the_time_budget_and_says_so()
    {
        for (var i = 0; i < 5; i++)
            Write($"src/F{i}.java", $"class F{i} {{ String s = \"SELECT * FROM t{i}\"; }}");

        var scan = SqlTableScanner.Scan(_temp.Path, Enumerable.Range(0, 5).Select(i => $"src/F{i}.java"), CancellationToken.None, timeBudget: TimeSpan.Zero);

        Assert.Equal(SqlTableScan.StoppedTimeBudget, scan.StoppedBecause);
        Assert.True(scan.FilesScanned < 5);
    }

    [Fact]
    public void Cancellation_propagates()
    {
        Write("src/F.java", "class F { String s = \"SELECT * FROM t\"; }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => SqlTableScanner.Scan(_temp.Path, ["src/F.java"], cts.Token));
    }

    [Fact]
    public void A_latin1_file_is_read_like_the_tools_do()
    {
        var path = Path.Combine(_temp.Path, "src", "Latin.java");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes("class L { String s = \"SELECT descripción FROM pedido\"; }"));

        var scan = SqlTableScanner.Scan(_temp.Path, ["src/Latin.java"], CancellationToken.None);

        Assert.Equal(["pedido"], scan.References.Select(r => r.Name));
    }
}
