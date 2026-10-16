using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// APIs sensibles por método, resueltas por símbolo (el tipo que declara el método llamado, sus bases y sus interfaces), no
/// por texto: un <c>File</c> propio no cuenta y un <c>SqlCommand</c> cuenta por derivar de <c>DbCommand</c>. En cada nodo de
/// método o constructor con alguna escribe:
///
///   - <c>sensitive_apis</c>: <c>[{category, api, line}]</c>, ordenada por línea, con <c>api</c> = <c>{tipo}.{método}</c> o
///     <c>new {tipo}</c>.
///   - <c>sensitive_categories</c>: las categorías, sin repetir.
///
/// Categorías: <c>file-io</c> (<c>File</c>, <c>Directory</c>, <c>FileInfo</c>/<c>DirectoryInfo</c>, <c>new FileStream</c>,
/// <c>new StreamReader/StreamWriter</c> sobre una ruta), <c>process</c> (<c>Process.Start</c>/<c>Kill</c>/<c>GetProcesses*</c>),
/// <c>network</c> (<c>HttpClient</c>, <c>Socket</c>, <c>TcpClient</c>/<c>UdpClient</c>/<c>TcpListener</c>, <c>WebClient</c>,
/// <c>WebRequest.Create</c>, <c>Dns</c>), <c>deserialization</c> (<c>BinaryFormatter</c> y los formateadores inseguros, YamlDotNet
/// <c>Deserialize</c>, y Newtonsoft con <c>TypeNameHandling</c> distinto de <c>None</c>: la asignación, y las llamadas a
/// <c>Deserialize*</c> del mismo método), <c>reflection</c> (<c>Activator.CreateInstance</c>, <c>Type.GetType</c>,
/// <c>Type.InvokeMember</c>, <c>Assembly.Load*</c>, <c>MethodBase.Invoke</c>), <c>dynamic-code</c> (<c>Expression.Compile</c>,
/// <c>System.Reflection.Emit</c>, <c>Compilation.Emit</c>, <c>CSharpScript</c>, <c>CodeDomProvider.CompileAssembly*</c>),
/// <c>environment</c> (<c>Environment.Get/SetEnvironmentVariable(s)</c>, <c>ExpandEnvironmentVariables</c>) y <c>sql</c>
/// (<c>DbCommand</c>/<c>IDbCommand</c> <c>Execute*</c> y su creación, Dapper <c>Query*</c>/<c>Execute*</c>, EF Core
/// <c>FromSql*</c>/<c>ExecuteSql*</c>/<c>SqlQuery*</c>).
///
/// Cuenta todo el cuerpo, lambdas y funciones locales incluidas (lo que lanza el método aunque se ejecute después). El código
/// fuera de un método con nodo (accessors de propiedades, inicializadores) no se atribuye.
/// </summary>
public sealed class SensitiveApiEnricher : IGraphEnricher
{
    public string Name  => "SensitiveApiEnricher";
    public int    Order => 9;

    private delegate bool Applies(IMethodSymbol method, bool creation);

    private sealed record Rule(string Category, string Type, Applies Applies);

    private static readonly Applies AnyUse = (_, _) => true;
    private static readonly Applies Calls = (_, creation) => !creation;
    private static Applies Named(params string[] names) => (m, creation) => !creation && names.Contains(m.Name);
    private static Applies Prefixed(params string[] prefixes) =>
        (m, creation) => !creation && prefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal));

    private static readonly Rule[] Rules =
    [
        new("file-io", "System.IO.File", Calls),
        new("file-io", "System.IO.Directory", Calls),
        new("file-io", "System.IO.FileInfo", Calls),
        new("file-io", "System.IO.DirectoryInfo", Calls),
        new("file-io", "System.IO.FileStream", (_, creation) => creation),
        new("file-io", "System.IO.StreamReader", (m, creation) => creation && m.Parameters.FirstOrDefault()?.Type.SpecialType == SpecialType.System_String),
        new("file-io", "System.IO.StreamWriter", (m, creation) => creation && m.Parameters.FirstOrDefault()?.Type.SpecialType == SpecialType.System_String),

        new("process", "System.Diagnostics.Process", Named("Start", "Kill", "GetProcesses", "GetProcessById", "GetProcessesByName")),

        new("network", "System.Net.Http.HttpClient", AnyUse),
        new("network", "System.Net.Sockets.Socket", AnyUse),
        new("network", "System.Net.Sockets.TcpClient", AnyUse),
        new("network", "System.Net.Sockets.UdpClient", AnyUse),
        new("network", "System.Net.Sockets.TcpListener", AnyUse),
        new("network", "System.Net.WebClient", AnyUse),
        new("network", "System.Net.WebRequest", Prefixed("Create")),
        new("network", "System.Net.Dns", Calls),

        new("deserialization", "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter", AnyUse),
        new("deserialization", "System.Runtime.Serialization.Formatters.Soap.SoapFormatter", AnyUse),
        new("deserialization", "System.Runtime.Serialization.NetDataContractSerializer", AnyUse),
        new("deserialization", "System.Web.UI.LosFormatter", AnyUse),
        new("deserialization", "System.Web.UI.ObjectStateFormatter", AnyUse),
        new("deserialization", "YamlDotNet.Serialization.IDeserializer", Named("Deserialize")),
        new("deserialization", "YamlDotNet.Serialization.Deserializer", Named("Deserialize")),

        new("reflection", "System.Activator", Prefixed("CreateInstance")),
        new("reflection", "System.Type", (m, creation) => !creation && ((m.Name == "GetType" && m.IsStatic) || m.Name == "InvokeMember")),
        new("reflection", "System.Reflection.Assembly", Named("Load", "LoadFrom", "LoadFile", "UnsafeLoadFrom")),
        new("reflection", "System.Reflection.MethodBase", Named("Invoke")),

        new("dynamic-code", "System.Linq.Expressions.LambdaExpression", Named("Compile")),
        new("dynamic-code", "Microsoft.CodeAnalysis.Compilation", Named("Emit")),
        new("dynamic-code", "Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript", Calls),
        new("dynamic-code", "System.CodeDom.Compiler.CodeDomProvider", Prefixed("CompileAssembly")),

        new("environment", "System.Environment", Named("GetEnvironmentVariable", "GetEnvironmentVariables", "SetEnvironmentVariable", "ExpandEnvironmentVariables")),

        new("sql", "System.Data.Common.DbCommand", (m, creation) => creation || m.Name.StartsWith("Execute", StringComparison.Ordinal)),
        new("sql", "System.Data.IDbCommand", Prefixed("Execute")),
        new("sql", "Dapper.SqlMapper", Prefixed("Query", "Execute")),
        new("sql", "Microsoft.EntityFrameworkCore.RelationalQueryableExtensions", Prefixed("FromSql")),
        new("sql", "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", Prefixed("ExecuteSql", "SqlQuery"))
    ];

    /// <summary>Espacio de nombres cuyo uso entero es generación de código.</summary>
    private const string EmitNamespace = "System.Reflection.Emit.";

    private const string NewtonsoftNamespace = "Newtonsoft.Json.";

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        await ctx.Repository.ExecuteAsync($"""
            UPDATE nodes SET properties = json_remove(properties, '$.sensitive_apis', '$.sensitive_categories')
            WHERE project = @project AND label IN ('Method','Function') AND json_type(properties, '$.sensitive_apis') IS NOT NULL
              AND {LanguageScope.NotJava()}
            """, new { project = ctx.Project });

        var found = new Dictionary<long, HashSet<(int Line, string Category, string Api)>>();
        foreach (var body in await MethodBodies.LoadAsync(ctx, Name))
        {
            var hits = Analyze(body).ToList();
            if (hits.Count == 0) continue;
            if (!found.TryGetValue(body.NodeId, out var set)) found[body.NodeId] = set = [];
            set.UnionWith(hits);
        }

        foreach (var (nodeId, hits) in found.OrderBy(f => f.Key))
        {
            var ordered = hits.OrderBy(h => h.Line).ThenBy(h => h.Category, StringComparer.Ordinal).ThenBy(h => h.Api, StringComparer.Ordinal).ToList();
            await ctx.Repository.PatchNodePropertiesByIdAsync(nodeId, new Dictionary<string, object?>
            {
                ["sensitive_apis"] = ordered.Select(h => new Dictionary<string, object?> { ["category"] = h.Category, ["api"] = h.Api, ["line"] = h.Line }).ToList(),
                ["sensitive_categories"] = ordered.Select(h => h.Category).Distinct().Order(StringComparer.Ordinal).ToArray()
            });
            ctx.RecordPatchHit(Name);
        }

        var byCategory = found.Values.SelectMany(h => h.Select(x => x.Category).Distinct())
            .GroupBy(c => c).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        ctx.EnricherDetails["sensitive_apis"] = new { methods = found.Count, methods_by_category = byCategory };
        progress?.Report($"Sensitive APIs: {found.Count} method(s) ({string.Join(", ", byCategory.Select(kv => $"{kv.Key}={kv.Value}"))}).");
    }

    private static IEnumerable<(int Line, string Category, string Api)> Analyze(MethodBody body)
    {
        var model = body.Model;
        var newtonsoftDeserialize = new List<(int Line, string Api)>();
        var typeNameHandling = false;

        foreach (var node in body.Declaration.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                    when MethodBodies.MethodOf(node, model) is { } method:
                {
                    var creation = node is BaseObjectCreationExpressionSyntax;
                    var definition = (method.ReducedFrom ?? method).OriginalDefinition;
                    if (definition.ContainingType is not { } owner) break;
                    var ownerName = MethodBodies.FullName(owner);
                    var api = creation ? $"new {ownerName}" : $"{ownerName}.{definition.Name}";
                    if (Match(definition, owner, creation) is { } category)
                        yield return (body.LineOf(node), category, api);
                    else if (!creation && ownerName.StartsWith(NewtonsoftNamespace, StringComparison.Ordinal)
                             && definition.Name.StartsWith("Deserialize", StringComparison.Ordinal))
                        newtonsoftDeserialize.Add((body.LineOf(node), api));
                    break;
                }
                case AssignmentExpressionSyntax assignment
                    when model.GetSymbolInfo(assignment.Left).Symbol is IPropertySymbol { Name: "TypeNameHandling", ContainingType: { } settings }
                         && MethodBodies.FullName(settings).StartsWith(NewtonsoftNamespace, StringComparison.Ordinal)
                         && !IsNone(model.GetOperation(assignment.Right)):
                    typeNameHandling = true;
                    yield return (body.LineOf(assignment), "deserialization", $"{MethodBodies.FullName(settings)}.TypeNameHandling");
                    break;
            }
        }

        if (typeNameHandling)
            foreach (var (line, api) in newtonsoftDeserialize)
                yield return (line, "deserialization", api);
    }

    /// <summary><c>TypeNameHandling.None</c> (0) como constante; cualquier otro valor, o uno no constante, cuenta.</summary>
    private static bool IsNone(IOperation? value)
    {
        while (value is IConversionOperation conversion) value = conversion.Operand;
        return value?.ConstantValue is { HasValue: true, Value: var v } && Convert.ToInt64(v) == 0;
    }

    private static string? Match(IMethodSymbol method, INamedTypeSymbol owner, bool creation)
    {
        foreach (var type in MethodBodies.SelfBasesAndInterfaces(owner))
        {
            var name = MethodBodies.FullName(type);
            if (name.StartsWith(EmitNamespace, StringComparison.Ordinal)) return "dynamic-code";
            foreach (var rule in Rules)
                if (rule.Type == name && rule.Applies(method, creation))
                    return rule.Category;
        }
        return null;
    }
}
