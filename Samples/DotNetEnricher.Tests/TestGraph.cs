using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;

namespace DotNetEnricher.Tests;

/// <summary>
/// Un repositorio de prueba: ficheros C# en una carpeta temporal, compilados con Roslyn sin MSBuild, y una BD SQLite con
/// el esquema de codebase-memory-mcp 0.6 cuyos nodos imitan los de cbm (Class/Interface/Enum/Method con el qualified_name
/// por ruta de fichero, DEFINES_METHOD y las aristas CALLS/USAGE que añada cada test). El nombre del proyecto lleva un
/// punto, como el que cbm deriva de una ruta <c>C:\Users\nombre.apellido\…</c>.
/// </summary>
internal sealed class TestGraph : IDisposable
{
    public const string DefaultProject = "C-Users-ana.lopez-repos-sample";

    /// <summary>Tipos mínimos de ASP.NET Core, DI y hosting. Van en un fichero fuera del repositorio: el filtro de cbm los excluye.</summary>
    public const string FrameworkStubs = """
        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }
            public static class ServiceCollectionServiceExtensions
            {
                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection s) where TImplementation : TService => s;
                public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection s) where TImplementation : TService => s;
                public static IServiceCollection AddTransient<TService>(this IServiceCollection s) => s;
                public static IServiceCollection AddScoped<TService>(this IServiceCollection s) => s;
                public static IServiceCollection AddScoped<TService>(this IServiceCollection s, System.Func<System.IServiceProvider, TService> factory) => s;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection s) => s;
                public static IServiceCollection AddHostedService<TService>(this IServiceCollection s) => s;
            }
            public static class ServiceProviderServiceExtensions
            {
                public static T GetRequiredService<T>(this System.IServiceProvider p) where T : notnull => default!;
                public static T? GetService<T>(this System.IServiceProvider p) => default;
            }
        }
        namespace Microsoft.Extensions.Hosting
        {
            public abstract class BackgroundService
            {
                protected abstract System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken);
            }
        }
        namespace Newtonsoft.Json
        {
            public enum TypeNameHandling { None, Objects, All }
            public sealed class JsonSerializerSettings { public TypeNameHandling TypeNameHandling { get; set; } }
            public static class JsonConvert { public static T? DeserializeObject<T>(string value, JsonSerializerSettings? settings = null) => default; }
        }
        namespace Microsoft.AspNetCore.Mvc
        {
            public class ControllerBase { }
            public sealed class ApiControllerAttribute : System.Attribute { }
            public sealed class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
            public sealed class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } }
            public sealed class HttpPostAttribute : System.Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
            public sealed class FromBodyAttribute : System.Attribute { }
        }
        namespace Microsoft.AspNetCore.Authorization
        {
            public sealed class AuthorizeAttribute : System.Attribute { public string? Roles { get; set; } }
            public sealed class AllowAnonymousAttribute : System.Attribute { }
            public sealed class AuthorizationOptions { public object? FallbackPolicy { get; set; } }
        }
        namespace Microsoft.AspNetCore.Routing
        {
            public interface IEndpointRouteBuilder { }
            public sealed class RouteGroupBuilder : IEndpointRouteBuilder { }
            public static class EndpointExtensions
            {
                public static RouteGroupBuilder MapGroup(this IEndpointRouteBuilder b, string prefix) => new();
                public static IEndpointRouteBuilder MapGet(this IEndpointRouteBuilder b, string pattern, System.Delegate handler) => b;
                public static IEndpointRouteBuilder MapPost(this IEndpointRouteBuilder b, string pattern, System.Delegate handler) => b;
                public static T RequireAuthorization<T>(this T b) where T : IEndpointRouteBuilder => b;
                public static T AllowAnonymous<T>(this T b) where T : IEndpointRouteBuilder => b;
            }
        }
        """;

    private const string Schema = """
        CREATE TABLE projects (name TEXT PRIMARY KEY, indexed_at TEXT NOT NULL, root_path TEXT NOT NULL);
        CREATE TABLE nodes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project TEXT NOT NULL REFERENCES projects(name) ON DELETE CASCADE,
            label TEXT NOT NULL, name TEXT NOT NULL, qualified_name TEXT NOT NULL,
            file_path TEXT DEFAULT '', start_line INTEGER DEFAULT 0, end_line INTEGER DEFAULT 0,
            properties TEXT DEFAULT '{}', UNIQUE(project, qualified_name));
        CREATE INDEX idx_nodes_label ON nodes(project, label);
        CREATE INDEX idx_nodes_file ON nodes(project, file_path);
        CREATE TABLE edges (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project TEXT NOT NULL REFERENCES projects(name) ON DELETE CASCADE,
            source_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
            target_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
            type TEXT NOT NULL, properties TEXT DEFAULT '{}',
            url_path_gen TEXT GENERATED ALWAYS AS (json_extract(properties,'$.url_path')),
            UNIQUE(source_id, target_id, type));
        CREATE INDEX idx_edges_type ON edges(project, type);
        CREATE TABLE node_vectors (node_id INTEGER PRIMARY KEY, project TEXT NOT NULL, vector BLOB NOT NULL);
        CREATE TABLE token_vectors (id INTEGER PRIMARY KEY, project TEXT NOT NULL, token TEXT NOT NULL, vector BLOB NOT NULL, idf INTEGER NOT NULL);
        CREATE VIRTUAL TABLE nodes_fts USING fts5(name, qualified_name, label, file_path, content='', tokenize='unicode61 remove_diacritics 2');
        """;

    private readonly List<(string RelativePath, string Source)> _files = [];
    private readonly List<CsProjectInfo> _projects = [];

    public TestGraph(string project = DefaultProject)
    {
        Project = project;
        Root = Path.Combine(Path.GetTempPath(), "dne-tests", Guid.NewGuid().ToString("N"));
        RepoRoot = Path.Combine(Root, "repo");
        Directory.CreateDirectory(RepoRoot);
        DatabasePath = Path.Combine(Root, "codebase.db");
    }

    public string Project { get; }
    public string Root { get; }
    public string RepoRoot { get; }
    public string DatabasePath { get; }
    public IReadOnlyList<Compilation> Compilations { get; private set; } = [];
    public IReadOnlyList<CsProjectInfo> CsProjects => _projects;

    /// <summary>Declara un proyecto C#: sus ficheros son los que se añadan con <see cref="AddFile"/> bajo su carpeta.</summary>
    public TestGraph AddProject(string name, string relativeDirectory, bool isExecutable = false, bool isTest = false,
        string[]? projectReferences = null, string[]? packages = null)
    {
        _projects.Add(new CsProjectInfo(name, relativeDirectory, isExecutable, isTest, projectReferences, packages));
        return this;
    }

    public TestGraph AddFile(string relativePath, string source)
    {
        _files.Add((relativePath, source));
        var full = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, source);
        return this;
    }

    /// <summary>
    /// Compila (una compilación por proyecto declarado, o una sola) y crea la BD con los nodos «de cbm». Con
    /// <paramref name="allowErrors"/>, el código puede no compilar (un paquete NuGet sin restaurar).
    /// </summary>
    public async Task<TestGraph> BuildAsync(bool allowErrors = false)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll"
                or "System.Collections.dll" or "System.Linq.dll" or "System.Threading.Tasks.dll" or "System.ComponentModel.Annotations.dll" or "System.ComponentModel.dll"
                // APIs sensibles y de concurrencia (paquete B1b).
                or "System.Diagnostics.Process.dll" or "System.ComponentModel.Primitives.dll" or "System.Net.Http.dll" or "System.Net.Primitives.dll"
                or "System.Linq.Expressions.dll" or "System.Data.Common.dll" or "System.Collections.Concurrent.dll" or "System.Threading.dll"
                or "System.Threading.Tasks.Parallel.dll" or "System.Private.Uri.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        var stubs = CSharpSyntaxTree.ParseText(FrameworkStubs, path: Path.Combine(Root, "stubs", "Framework.cs"));
        var parse = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse);

        // Una sola compilación con todo el repositorio: los proyectos declarados solo dan la carpeta de cada uno
        // (EnrichmentContext.CsProjects), que es lo que miran los enriquecedores; así se usan tipos entre proyectos
        // sin montar referencias.
        var trees = _files.Select(f => CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(f.Source, System.Text.Encoding.UTF8), parse,
            path: Path.Combine(RepoRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        var compilation = CSharpCompilation.Create("Sample", [stubs, .. trees], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0 && !allowErrors)
            throw new InvalidOperationException("El código de prueba no compila:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        Compilations = [compilation];

        await CreateDatabaseAsync();
        return this;
    }

    private async Task CreateDatabaseAsync()
    {
        using var conn = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await conn.OpenAsync();
        await ExecAsync(conn, Schema);
        await ExecAsync(conn, "INSERT INTO projects(name, indexed_at, root_path) VALUES (@p, '2026-09-30T10:00:00Z', @r)",
            ("@p", Project), ("@r", RepoRoot.Replace('\\', '/')));

        var filter = new CbmExclusionFilter(RepoRoot);
        foreach (var compilation in Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = compilation.GetSemanticModel(tree);
                var rel = SymbolQn.Relativize(tree.FilePath, RepoRoot);
                await InsertNodeAsync(conn, "File", Path.GetFileName(tree.FilePath), SymbolQn.ForFile(tree.FilePath, Project, RepoRoot)!, rel, await tree.GetRootAsync(), new JsonObject());
                foreach (var decl in (await tree.GetRootAsync()).DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type) continue;
                    var label = type.TypeKind switch { TypeKind.Interface => "Interface", TypeKind.Enum => "Enum", _ => "Class" };
                    var typeQn = SymbolQn.ForType(type, Project, RepoRoot)!;
                    var typeId = await InsertNodeAsync(conn, label, type.Name, typeQn, rel, decl, new JsonObject());
                    // Como cbm: métodos ordinarios, constructores declarados (con el nombre de la clase) e implementaciones
                    // explícitas de interfaz (con el nombre del miembro), y los tipos anidados con toda la cadena en el QN.
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>()
                        .Where(m => GraphMethods.HasNode(m) && m.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree == tree))
                    {
                        var qn = SymbolQn.ForMethod(method, Project, RepoRoot)!;
                        var syntax = method.DeclaringSyntaxReferences[0].GetSyntax();
                        var span = syntax.GetLocation().GetLineSpan();
                        var methodId = await InsertNodeAsync(conn, "Method", SymbolQn.GraphName(method),
                            qn, rel, syntax, new JsonObject { ["lines"] = span.EndLinePosition.Line - span.StartLinePosition.Line + 1 });
                        await InsertEdgeAsync(conn, typeId, methodId, "DEFINES_METHOD");
                    }
                }
            }
        }
    }

    private async Task<long> InsertNodeAsync(SqliteConnection conn, string label, string name, string qn, string rel, SyntaxNode syntax, JsonObject properties)
    {
        var span = syntax.GetLocation().GetLineSpan();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO nodes(project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES (@p, @l, @n, @q, @f, @s, @e, @props);
            SELECT id FROM nodes WHERE project = @p AND qualified_name = @q;
            """;
        cmd.Parameters.AddWithValue("@p", Project);
        cmd.Parameters.AddWithValue("@l", label);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@q", qn);
        cmd.Parameters.AddWithValue("@f", rel);
        cmd.Parameters.AddWithValue("@s", span.StartLinePosition.Line + 1);
        cmd.Parameters.AddWithValue("@e", span.EndLinePosition.Line + 1);
        cmd.Parameters.AddWithValue("@props", properties.ToJsonString());
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task InsertEdgeAsync(SqliteConnection conn, long source, long target, string type)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO edges(project, source_id, target_id, type) VALUES (@p, @s, @t, @y)";
        cmd.Parameters.AddWithValue("@p", Project);
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@t", target);
        cmd.Parameters.AddWithValue("@y", type);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Arista «de cbm» entre dos nodos dados por el final de su qualified_name (p. ej. <c>OrderService.Create</c>).</summary>
    public async Task AddEdgeAsync(string sourceQnSuffix, string targetQnSuffix, string type)
    {
        using var conn = Open();
        await InsertEdgeAsync(conn, await IdAsync(conn, sourceQnSuffix), await IdAsync(conn, targetQnSuffix), type);
    }

    /// <summary>
    /// Un nodo que escribió otro enriquecedor (Java en un repositorio con los dos lenguajes): fichero <c>.java</c> y las
    /// propiedades que ese enriquecedor deja. Se inserta después de <see cref="BuildAsync"/>, antes de enriquecer.
    /// </summary>
    public async Task<long> AddNodeAsync(string label, string name, string qualifiedName, string filePath, JsonObject properties)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes(project, label, name, qualified_name, file_path, start_line, end_line, properties)
            VALUES (@p, @l, @n, @q, @f, 1, 5, @props);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@p", Project);
        cmd.Parameters.AddWithValue("@l", label);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@q", qualifiedName);
        cmd.Parameters.AddWithValue("@f", filePath);
        cmd.Parameters.AddWithValue("@props", properties.ToJsonString());
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        conn.Open();
        return conn;
    }

    /// <summary>Ejecuta la pasada completa, como el CLI tras cargar la solución.</summary>
    public Task<EnrichmentResult> EnrichAsync(TextWriter? log = null) =>
        EnrichmentPipeline.RunAsync(Request(), log ?? TextWriter.Null, new ListProgress());

    public EnrichmentRequest Request() => new()
    {
        DatabasePath = DatabasePath,
        RepoRoot = RepoRoot,
        Compilations = Compilations,
        CsProjects = CsProjects,
        SolutionPath = "sample.slnx"
    };

    /// <summary>Propiedades del nodo cuyo qualified_name termina en <paramref name="qnSuffix"/> (debe haber uno).</summary>
    public JsonObject Props(string qnSuffix) => (JsonObject)JsonNode.Parse(Scalar<string>(
        "SELECT properties FROM nodes WHERE qualified_name = @q OR qualified_name LIKE '%.' || @q", ("@q", qnSuffix)))!;

    public JsonObject? PropsOrNull(string qualifiedName)
    {
        var raw = Scalar<string?>("SELECT properties FROM nodes WHERE qualified_name = @q", ("@q", qualifiedName));
        return raw is null ? null : (JsonObject)JsonNode.Parse(raw)!;
    }

    /// <summary>Aristas de un tipo como (qn origen, qn destino, propiedades), con los qualified_names completos.</summary>
    public List<(string Source, string Target, JsonObject Properties)> Edges(string type)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.qualified_name, t.qualified_name, e.properties FROM edges e
            JOIN nodes s ON s.id = e.source_id JOIN nodes t ON t.id = e.target_id
            WHERE e.type = @t ORDER BY e.id
            """;
        cmd.Parameters.AddWithValue("@t", type);
        var result = new List<(string, string, JsonObject)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1), (JsonObject)JsonNode.Parse(reader.GetString(2))!));
        return result;
    }

    public T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    /// <summary>QN que cbm daría a un tipo o método del fichero <paramref name="relativeFile"/> (sin .cs).</summary>
    public string Qn(string relativeFile, string member) =>
        $"{Project}.{relativeFile[..^3].Replace('/', '.')}.{member}";

    private static async Task<long> IdAsync(SqliteConnection conn, string qnSuffix)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM nodes WHERE qualified_name LIKE '%.' || @q";
        cmd.Parameters.AddWithValue("@q", qnSuffix);
        return (long)((await cmd.ExecuteScalarAsync()) ?? throw new InvalidOperationException($"No hay nodo '*.{qnSuffix}'."));
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Progreso síncrono que guarda los mensajes.</summary>
    public sealed class ListProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) { lock (Messages) Messages.Add(value); }
    }
}
