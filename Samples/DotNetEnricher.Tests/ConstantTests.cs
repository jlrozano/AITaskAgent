using System.Text.Json.Nodes;

namespace DotNetEnricher.Tests;

/// <summary>
/// Constantes que resuelve el compilador (pieza 1 de la tanda 5): <c>hardcoded_secrets</c> con el valor enmascarado y
/// <c>reflection_targets</c>. Cada regla con un caso positivo y otro negativo, sobre una solución pequeña y el esquema de cbm.
/// </summary>
public sealed class ConstantTests
{
    private static async Task<TestGraph> BuildAsync(params (string Path, string Source)[] files)
    {
        var graph = new TestGraph().AddProject("App", "src/App");
        foreach (var (path, source) in files) graph.AddFile("src/App/" + path, source);
        await graph.BuildAsync();
        return graph;
    }

    private static JsonObject[] Secrets(JsonObject? props) =>
        props?["hardcoded_secrets"]?.AsArray().Select(s => (JsonObject)s!).ToArray() ?? [];

    private static string[] Targets(JsonObject? props) =>
        props?["reflection_targets"]?.AsArray().Select(t => (string)t!).ToArray() ?? [];

    private const string SecretsSource = """
        using System;
        using System.Collections.Generic;
        namespace App;

        public sealed class Connection
        {
            public Connection(string connectionString) { }
            public string Password { get; set; } = "";
            public void SetPassword(string value) { }
        }

        public sealed class Positive
        {
            private const string Prefix = "Server=db01;User Id=app;";
            private const string ConnectionString = Prefix + "Password=Orders#2024!Prod";
            private const string ApiKey = "sk-live-12345678";
            private const string Token = Prefix + "abc";

            public void Open()
            {
                var c = new Connection(ConnectionString);
                c.Password = "Hunter2xx";
                c.SetPassword("letmein-1");
                var settings = new Dictionary<string, string>();
                settings.Add("db.password", "s3cr3t-value");
                var direct = new Connection("Server=a;Pwd=Direct#9;Database=x");
                Login(user: "bob", password: "named-secret");
                var cs = $"Server=a;{ApiKey}";
            }

            private void Login(string user, string password) { }

            public string Ternary(string configured) =>
                string.IsNullOrEmpty(configured) ? "Server=db;User Id=app;Password=Tern4ry#1;Database=x" : configured;
        }

        public sealed class Negative
        {
            private const string UserSecretsId = "6c1b6b8e-2f3d-4f43-9d11-0a1f2b3c4d5e";
            private const string TokenUrl = "https://login.example.com/token";
            private const string PasswordFileName = "password.txt";
            private const string Placeholder = "${DB_PASSWORD}";
            private const string Password = "${DB_PASSWORD}";
            private const string Masked = "********";
            private const string Greeting = "hello world";

            public void Run(string password, string token)
            {
                var c = new Connection("Server=a;Password=${DB_PASSWORD};Database=x");
                c.Password = password;
                c.SetPassword(token);
                var dynamicName = "Server=a;Password=" + password;
                var plain = "no credentials here";
            }
        }
        """;

    [Fact]
    public async Task Constants_that_name_a_credential_are_written_masked_on_the_node_that_contains_them()
    {
        using var graph = await BuildAsync(("Secrets.cs", SecretsSource));
        await graph.EnrichAsync();

        // Campos const (con concatenación de constantes y cadena de conexión): van al nodo del campo.
        JsonObject[] Field(string name) => Secrets(graph.PropsOrNull(graph.Qn("src/App/Secrets.cs", "Positive." + name)));
        Assert.Contains(Field("ApiKey"), s => (string?)s["kind"] == "identifier" && (string?)s["name"] == "ApiKey" && (string?)s["preview_masked"] == "sk*** (16 caracteres)");
        Assert.Contains(Field("ConnectionString"), s => (string?)s["kind"] == "connection-string" && (string?)s["name"] == "ConnectionString"
            && (string?)s["preview_masked"] == "Or*** (16 caracteres)");
        Assert.Contains(Field("Token"), s => (string?)s["name"] == "Token" && (string?)s["kind"] == "identifier");
        Assert.Empty(Field("Prefix"));

        // En el método: asignación, SetPassword, par clave/valor, cadena de conexión directa y argumento con nombre.
        var open = Secrets(graph.PropsOrNull(graph.Qn("src/App/Secrets.cs", "Positive.Open")));
        Assert.Contains(open, s => (string?)s["name"] == "Password" && (string?)s["kind"] == "identifier" && (string?)s["preview_masked"] == "Hu*** (9 caracteres)");
        Assert.Contains(open, s => (string?)s["kind"] == "call-argument" && (string?)s["name"] == "SetPassword");
        Assert.Contains(open, s => (string?)s["kind"] == "call-argument" && (string?)s["name"] == "db.password");
        Assert.Contains(open, s => (string?)s["kind"] == "connection-string" && (string?)s["preview_masked"] == "Di*** (8 caracteres)");
        Assert.Contains(open, s => (string?)s["kind"] == "call-argument" && (string?)s["name"] == "password" && (string?)s["preview_masked"] == "na*** (12 caracteres)");
        Assert.All(open, s => Assert.True(s["line"]!.GetValue<int>() > 0));

        // La cadena de conexión de una rama de un ternario (la de CustomerRepository.OpenConnection) no tiene declaración ni argumento.
        var ternary = Assert.Single(Secrets(graph.PropsOrNull(graph.Qn("src/App/Secrets.cs", "Positive.Ternary"))));
        Assert.Equal(("connection-string", "Te*** (9 caracteres)"), ((string?)ternary["kind"], (string?)ternary["preview_masked"]));

        // El secreto completo no sale en ningún sitio del grafo.
        var everything = graph.Scalar<string>("SELECT group_concat(properties, ' ') FROM nodes");
        foreach (var secret in new[] { "Tern4ry#1", "Orders#2024!Prod", "sk-live-12345678", "Hunter2xx", "letmein-1", "s3cr3t-value", "Direct#9", "named-secret" })
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task References_placeholders_masked_values_and_non_constants_are_not_secrets()
    {
        using var graph = await BuildAsync(("Secrets.cs", SecretsSource));
        await graph.EnrichAsync();

        Assert.Empty(Secrets(graph.PropsOrNull(graph.Qn("src/App/Secrets.cs", "Negative"))));
        Assert.Empty(Secrets(graph.PropsOrNull(graph.Qn("src/App/Secrets.cs", "Negative.Run"))));
        // Ni sus campos (UserSecretsId, TokenUrl, PasswordFileName, Password con marcador de posición…).
        Assert.Equal(0, graph.Scalar<int>("SELECT COUNT(1) FROM nodes WHERE qualified_name LIKE '%Secrets.Negative%' AND properties LIKE '%hardcoded_secrets%'"));
    }

    private const string ReflectionSource = """
        using System;
        using System.Reflection;
        namespace App.Notifications;

        public interface INotifier { }
        public sealed class EmailNotifier : INotifier { }
        public sealed class SmsNotifier : INotifier { }

        public sealed class Factory
        {
            private const string TypePrefix = "App.Notifications.";

            public INotifier Create(string channel)
            {
                var name = char.ToUpperInvariant(channel[0]) + channel[1..] + "Notifier";
                return (INotifier)Activator.CreateInstance(Type.GetType(TypePrefix + name)!)!;
            }

            public object Fixed() => Type.GetType("App.Notifications.EmailNotifier")!;

            public object Interpolated(string kind) => Activator.CreateInstance("App", $"App.Notifications.{kind}Notifier")!;

            public object FromAssembly(Assembly assembly, string kind) => assembly.GetType(TypePrefix + kind + "Handler")!;

            public object Unknown(string typeName) => Type.GetType(typeName)!;

            public object TooShort(string kind) => Type.GetType("A." + kind)!;

            public object Generic() => Activator.CreateInstance(typeof(EmailNotifier))!;
        }
        """;

    [Fact]
    public async Task Type_names_passed_to_reflection_are_resolved_with_a_star_for_what_is_not_constant()
    {
        using var graph = await BuildAsync(("Reflection.cs", ReflectionSource));
        await graph.EnrichAsync();
        string[] On(string member) => Targets(graph.PropsOrNull(graph.Qn("src/App/Reflection.cs", "Factory." + member)));

        Assert.Equal(["App.Notifications.*"], On("Create"));
        Assert.Equal(["App.Notifications.EmailNotifier"], On("Fixed"));
        Assert.Equal(["App.Notifications.*Notifier"], On("Interpolated"));
        Assert.Equal(["App.Notifications.*Handler"], On("FromAssembly"));
    }

    [Fact]
    public async Task A_type_name_with_no_fixed_part_a_short_prefix_or_a_typeof_is_not_a_target()
    {
        using var graph = await BuildAsync(("Reflection.cs", ReflectionSource));
        await graph.EnrichAsync();

        Assert.Empty(Targets(graph.PropsOrNull(graph.Qn("src/App/Reflection.cs", "Factory.Unknown"))));
        Assert.Empty(Targets(graph.PropsOrNull(graph.Qn("src/App/Reflection.cs", "Factory.TooShort"))));
        Assert.Empty(Targets(graph.PropsOrNull(graph.Qn("src/App/Reflection.cs", "Factory.Generic"))));
    }

    [Fact]
    public async Task The_classes_a_reflection_target_can_name_are_alive_and_the_rest_stay_dead_code_candidates()
    {
        using var graph = await BuildAsync(("Reflection.cs", ReflectionSource), ("Other.cs", "namespace App.Other;\npublic sealed class NeverCreated { }\n"));
        await graph.EnrichAsync();
        JsonObject Class(string name) => graph.PropsOrNull(graph.Qn(name == "NeverCreated" ? "src/App/Other.cs" : "src/App/Reflection.cs", name))!;

        foreach (var name in new[] { "EmailNotifier", "SmsNotifier" })
        {
            Assert.True((bool)Class(name)["is_reachable"]!, name);
            Assert.Null(Class(name)["dead_code_candidate"]);
            Assert.Equal("ReflectionTargetSuppressor", (string?)Class(name)["dead_code_suppressed_by"]);
        }
        Assert.True((bool)Class("NeverCreated")["dead_code_candidate"]!);
    }

    [Fact]
    public async Task A_second_pass_does_not_keep_what_the_code_no_longer_has()
    {
        using var graph = await BuildAsync(("Secrets.cs", SecretsSource));
        await graph.EnrichAsync();
        await graph.EnrichAsync();

        var open = Secrets(graph.PropsOrNull(graph.Qn("src/App/Secrets.cs", "Positive.Open")));
        Assert.Equal(open.Length, open.Select(s => s.ToJsonString()).Distinct().Count());
    }
}
