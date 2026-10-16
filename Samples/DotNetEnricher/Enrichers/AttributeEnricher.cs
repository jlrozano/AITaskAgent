using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// F5.2 — Atributos vía Roslyn. Materializa <c>attributes[]</c> = [{name, args[], named_args{}}] en cada
/// nodo Type (class/struct/record/interface/enum) y Method/Function, desde <c>symbol.GetAttributes()</c>.
/// Habilita búsqueda por atributo (<c>[Obsolete]</c>, <c>[Serializable]</c>, <c>[ApiController]</c>,
/// custom…) vía <c>graph_domain(view=attributes)</c> / <c>graph_search(mode=attribute)</c>.
///
/// <para><b>Order 2</b> (tras <see cref="SemanticMethodEnricher"/>, que crea los nodos Interface
/// faltantes; registrado DESPUÉS en el CLI para respetar el orden estable ante el empate de Order).
/// No crea nodos: solo parchea por QN sobre los que ya existen.</para>
///
/// <para><b>Idempotente:</b> resetea <c>$.attributes</c> al inicio porque el patch es merge-only.</para>
///
/// Convenciones: <c>name</c> = nombre del atributo SIN el sufijo "Attribute" (p.ej. <c>Obsolete</c>,
/// <c>JsonPropertyName</c>). <c>args</c>/<c>named_args</c> usan <see cref="TypedConstant.ToCSharpString"/>
/// (renderiza strings, enums, <c>typeof</c>, arrays). Sobrecargas de método (nodo name-based) → unión de
/// atributos de todas las sobrecargas, deduplicada.
/// </summary>
public class AttributeEnricher : IGraphEnricher
{
    public string Name  => "AttributeEnricher";
    public int    Order => 2;

    // Atributos sintetizados por el compilador que ensucian la señal (no escritos en fuente de forma útil).
    private static readonly HashSet<string> NoiseAttributes = new(StringComparer.Ordinal)
    {
        "NullableAttribute", "NullableContextAttribute", "NativeIntegerAttribute",
        "TupleElementNamesAttribute", "DynamicAttribute", "IsReadOnlyAttribute",
        "IsByRefLikeAttribute", "EmbeddedAttribute"
    };

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        int typesPatched = 0, methodsPatched = 0, attrInstances = 0;

        // Reset idempotente (el patch es merge-only): quita $.attributes de los labels C# que lo llevan.
        await ctx.Repository.ExecuteAsync(
            "UPDATE nodes SET properties=json_remove(properties,'$.attributes') " +
            "WHERE project=@project AND label IN ('Class','Interface','Enum','Method','Function')",
            new { project = ctx.Project });

        foreach (var compilation in ctx.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = compilation.GetSemanticModel(tree);
                var root  = await tree.GetRootAsync();

                // ── Tipos: class/struct/record/interface + enum (todos son BaseTypeDeclarationSyntax) ──
                foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol) continue;

                    var typeAttrs = ExtractAttributes(typeSymbol);
                    if (typeAttrs.Count > 0)
                    {
                        var qn = SymbolQn.ForType(typeSymbol, ctx.Project, ctx.RepoRoot);
                        if (qn is not null && await PatchAttrsAsync(ctx, qn, typeAttrs))
                        {
                            typesPatched++;
                            attrInstances += typeAttrs.Count;
                        }
                    }

                    // ── Métodos: nodo name-based → agrupar sobrecargas y unir sus atributos ──
                    // Por QN, con constructores e implementaciones explícitas (GraphMethods).
                    foreach (var group in typeSymbol.GetMembers().OfType<IMethodSymbol>()
                        .Where(GraphMethods.HasNode)
                        .GroupBy(m => SymbolQn.ForMethod(m, ctx.Project, ctx.RepoRoot) ?? m.Name, StringComparer.Ordinal))
                    {
                        var merged = MergeAttributes(group);
                        if (merged.Count == 0) continue;
                        var qn = SymbolQn.ForMethod(group.First(), ctx.Project, ctx.RepoRoot);
                        if (qn is not null && await PatchAttrsAsync(ctx, qn, merged))
                        {
                            methodsPatched++;
                            attrInstances += merged.Count;
                        }
                    }
                }
            }
        }

        progress?.Report($"Attributes: {typesPatched} types + {methodsPatched} methods patched ({attrInstances} attribute instances).");
    }

    // ── Patch de un array attributes[] sobre un nodo localizado por QN ──────────────
    private async Task<bool> PatchAttrsAsync(EnrichmentContext ctx, string qn, List<Dictionary<string, object?>> attrs)
    {
        var patched = await ctx.Repository.PatchNodePropertiesAsync(ctx.Project, qn,
            new Dictionary<string, object?> { ["attributes"] = attrs });
        if (patched == 0) { ctx.RecordPatchMiss(Name, qn); return false; }
        ctx.RecordPatchHit(Name);
        return true;
    }

    // ── Unión deduplicada de atributos de un grupo de sobrecargas ──────────────────
    private static List<Dictionary<string, object?>> MergeAttributes(IEnumerable<IMethodSymbol> overloads)
    {
        var result = new List<Dictionary<string, object?>>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in overloads)
            foreach (var entry in ExtractAttributes(m))
                if (seen.Add(AttrKey(entry)))
                    result.Add(entry);
        return result;
    }

    // ── Extrae los atributos de fuente de un símbolo → [{name, [args], [named_args]}] ──
    public static List<Dictionary<string, object?>> ExtractAttributes(ISymbol symbol)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var a in symbol.GetAttributes())
        {
            var cls = a.AttributeClass;
            if (cls is null || NoiseAttributes.Contains(cls.Name)) continue;

            var entry = new Dictionary<string, object?> { ["name"] = StripAttributeSuffix(cls.Name) };

            if (a.ConstructorArguments.Length > 0)
                entry["args"] = a.ConstructorArguments.Select(FormatTypedConstant).ToList();

            if (a.NamedArguments.Length > 0)
                entry["named_args"] = a.NamedArguments.ToDictionary(na => na.Key, na => FormatTypedConstant(na.Value), StringComparer.Ordinal);

            list.Add(entry);
        }
        return list;
    }

    /// <summary>
    /// Renderiza un <see cref="TypedConstant"/> a su forma C# de fuente ("foo", true, 'a',
    /// <c>StringComparison.Ordinal</c>, <c>typeof(X)</c>, <c>{1, 2}</c>). Reemplaza a
    /// <c>TypedConstant.ToCSharpString()</c>, que no es público en este Roslyn.
    /// </summary>
    public static string FormatTypedConstant(TypedConstant c)
    {
        if (c.IsNull) return "null";
        return c.Kind switch
        {
            TypedConstantKind.Array => "{" + string.Join(", ", c.Values.Select(FormatTypedConstant)) + "}",
            TypedConstantKind.Type  => c.Value is ITypeSymbol t ? $"typeof({t.ToDisplayString()})" : "typeof(?)",
            TypedConstantKind.Enum  => FormatEnumConstant(c),
            _                       => FormatPrimitive(c.Value),
        };
    }

    private static string FormatPrimitive(object? v) => v switch
    {
        null      => "null",
        string s  => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        bool b    => b ? "true" : "false",
        char ch   => $"'{ch}'",
        _         => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "null",
    };

    private static string FormatEnumConstant(TypedConstant c)
    {
        if (c.Type is INamedTypeSymbol e && c.Value is not null)
        {
            foreach (var f in e.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                if (Equals(f.ConstantValue, c.Value)) return $"{e.Name}.{f.Name}";
            return $"{e.Name}.{c.Value}";
        }
        return c.Value?.ToString() ?? "null";
    }

    private static string StripAttributeSuffix(string name) =>
        name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;

    // Clave de deduplicación estable (name + args + named_args ordenados).
    private static string AttrKey(Dictionary<string, object?> e)
    {
        var args  = e.TryGetValue("args", out var a) && a is List<string> la ? string.Join(",", la) : "";
        var named = e.TryGetValue("named_args", out var n) && n is Dictionary<string, string> dn
            ? string.Join(",", dn.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        return $"{e["name"]}({args})[{named}]";
    }
}
