using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodebaseAnalysis.Core.Enrichment;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Huellas SHA-256 (hexadecimal en minúsculas) para saber qué ha cambiado entre dos indexaciones sin comparar código. Se
/// calculan sobre los tokens de Roslyn sin trivia (sin espacios, saltos de línea ni comentarios) y sobre firmas del
/// compilador, en un orden fijo: dan lo mismo en cada pasada y en cada máquina mientras el código no cambie.
///
///   - <c>body_hash</c> (método o constructor con cuerpo): los tokens del cuerpo, del cuerpo de expresión y del inicializador
///     <c>: base(..)</c>/<c>: this(..)</c>. Reformatear o comentar no lo cambia; tocar una sentencia, sí.
///   - <c>contract_hash</c> (todo método con nodo, también los abstractos y los de interfaz): la firma completa (accesibilidad,
///     modificadores, tipo de retorno, parámetros con nombre, <c>ref</c>/<c>out</c>, valores por defecto, restricciones
///     genéricas) más <c>throws</c> (de <see cref="ThrowsEnricher"/>, que va antes).
///   - <c>type_hash</c> (Class, Interface, Enum): la cabecera del tipo (firma, bases, interfaces, atributos) y las huellas
///     ordenadas de sus miembros declarados (firma y tokens de la declaración; un tipo anidado aporta su <c>type_hash</c>).
///     Cambia con cualquier cambio dentro del tipo, incluidas las partes de un <c>partial</c> en otros ficheros.
///
/// Las sobrecargas comparten nodo en cbm: la huella del nodo combina las de todas, ordenadas por su firma.
/// </summary>
public sealed class HashEnricher : IGraphEnricher
{
    public string Name  => "HashEnricher";
    public int    Order => 13;

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints
                         | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers
                       | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeRef
                       | SymbolDisplayMemberOptions.IncludeExplicitInterface | SymbolDisplayMemberOptions.IncludeConstantValue,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword | SymbolDisplayKindOptions.IncludeTypeKeyword,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue
                          | SymbolDisplayParameterOptions.IncludeExtensionThis | SymbolDisplayParameterOptions.IncludeOptionalBrackets,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var graph = ctx.Repository;
        await graph.ExecuteAsync($"""
            UPDATE nodes SET properties = json_remove(properties, '$.body_hash', '$.contract_hash', '$.type_hash')
            WHERE project = @project AND label IN ('Method','Function','Class','Interface','Enum')
              AND (json_type(properties, '$.contract_hash') IS NOT NULL OR json_type(properties, '$.type_hash') IS NOT NULL)
              AND {LanguageScope.NotJava()}
            """, new { project = ctx.Project });

        var symbols = await ctx.GetGraphSymbolsAsync();
        var throwsByNode = new Dictionary<long, string>();
        foreach (var row in await graph.QueryAsync($"""
            SELECT id, properties FROM nodes
            WHERE project = @project AND label IN ('Method','Function') AND {LanguageScope.NotJava()}
              AND json_valid(properties) AND json_type(properties, '$.throws') = 'array'
            """, new { project = ctx.Project }))
        {
            var throws = EnrichmentMarks.ParseProperties((string)row.properties)["throws"] as JsonArray ?? [];
            throwsByNode[(long)row.id] = string.Join(",", throws.Select(t => (string?)t).OfType<string>().Order(StringComparer.Ordinal));
        }

        int bodies = 0, contracts = 0;
        foreach (var (nodeId, methods) in symbols.MethodsByNode.OrderBy(m => m.Key))
        {
            var ordered = methods
                .Select(m => (Method: m, Signature: m.ToDisplayString(SignatureFormat)))
                .DistinctBy(m => m.Signature)
                .OrderBy(m => m.Signature, StringComparer.Ordinal)
                .ToList();
            var bodyParts = ordered.Select(m => Body(m.Method)).OfType<string>().ToList();
            var patch = new Dictionary<string, object?>
            {
                ["contract_hash"] = Sha256(string.Join("\n", ordered.Select(m => m.Signature))
                                           + "\nthrows:" + throwsByNode.GetValueOrDefault(nodeId, ""))
            };
            if (bodyParts.Count > 0)
            {
                patch["body_hash"] = Sha256(string.Join("\n--\n", bodyParts));
                bodies++;
            }
            await graph.PatchNodePropertiesByIdAsync(nodeId, patch);
            ctx.RecordPatchHit(Name);
            contracts++;
        }

        var typeHashes = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
        int types = 0;
        foreach (var (nodeId, type) in symbols.TypesByNode.OrderBy(t => t.Key))
        {
            await graph.PatchNodePropertiesByIdAsync(nodeId, new Dictionary<string, object?> { ["type_hash"] = TypeHash(type, typeHashes) });
            ctx.RecordPatchHit(Name);
            types++;
        }

        ctx.EnricherDetails["hashes"] = new { methods = contracts, with_body = bodies, types };
        progress?.Report($"Hashes: {contracts} method contract(s), {bodies} body hash(es), {types} type hash(es).");
    }

    /// <summary>Tokens del cuerpo del método (y del inicializador de un constructor); null si no tiene cuerpo.</summary>
    private static string? Body(IMethodSymbol method)
    {
        var parts = new List<string>();
        foreach (var reference in method.DeclaringSyntaxReferences.OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(r => r.Span.Start))
        {
            if (reference.GetSyntax() is not BaseMethodDeclarationSyntax declaration) continue;
            if (declaration is ConstructorDeclarationSyntax { Initializer: { } initializer }) parts.Add(Tokens(initializer));
            if (declaration.Body is { } block) parts.Add(Tokens(block));
            if (declaration.ExpressionBody is { } expression) parts.Add(Tokens(expression));
        }
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string TypeHash(INamedTypeSymbol type, Dictionary<INamedTypeSymbol, string> cache)
    {
        if (cache.TryGetValue(type, out var cached)) return cached;

        var header = new StringBuilder()
            .Append(type.TypeKind).Append(' ').Append(type.ToDisplayString(SignatureFormat))
            .Append(type.IsAbstract ? " abstract" : "").Append(type.IsSealed ? " sealed" : "").Append(type.IsStatic ? " static" : "")
            .Append("\nbase:").Append(type.BaseType?.ToDisplayString(SignatureFormat))
            .Append("\ninterfaces:").Append(string.Join(",", type.Interfaces.Select(i => i.ToDisplayString(SignatureFormat)).Order(StringComparer.Ordinal)))
            .Append("\nattributes:").Append(string.Join(",", type.GetAttributes().Select(a => a.ToString()).Order(StringComparer.Ordinal)));
        if (type.EnumUnderlyingType is { } underlying) header.Append("\nenum:").Append(underlying.ToDisplayString());

        var members = new List<string>();
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member.DeclaringSyntaxReferences.Length == 0) continue;
            if (member is INamedTypeSymbol nested)
            {
                members.Add(TypeHash(nested, cache));
                continue;
            }
            // Los accessors de una propiedad o un evento van con su miembro, cuya declaración ya los incluye.
            if (member is IMethodSymbol { AssociatedSymbol: not null }) continue;
            var declarations = member.DeclaringSyntaxReferences
                .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(r => r.Span.Start)
                .Select(r => Tokens(r.GetSyntax()));
            members.Add(Sha256(member.Kind + " " + member.ToDisplayString(SignatureFormat) + "\n" + string.Join("\n", declarations)));
        }

        var hash = Sha256(header + "\nmembers:\n" + string.Join("\n", members.Order(StringComparer.Ordinal)));
        cache[type] = hash;
        return hash;
    }

    /// <summary>Los tokens sin trivia, separados por un espacio; los saltos de línea dentro de un literal, normalizados.</summary>
    internal static string Tokens(SyntaxNode node) =>
        string.Join(" ", node.DescendantTokens().Select(t => t.Text.Replace("\r\n", "\n", StringComparison.Ordinal)));

    internal static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
