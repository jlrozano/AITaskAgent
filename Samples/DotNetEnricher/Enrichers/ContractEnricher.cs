using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Contratos y documentación (Fase 5), con Roslyn. Order 12: tras Project(8) para poder agregar
/// <c>doc_coverage</c> por CsProject. Complementa a <see cref="SemanticMethodEnricher"/> (que ya pone
/// <c>docstring</c>=summary) añadiendo:
///   - Método: <c>doc_params</c> (name→desc de los <c>&lt;param&gt;</c>) y <c>doc_returns</c> (<c>&lt;returns&gt;</c>).
///   - Class/record/struct: <c>public_props</c> = [{name,type,nullable,default,doc}] de propiedades/campos públicos.
///   - Enum: <c>values</c> = [{name,value,doc}].
///   - CsProject: <c>doc_coverage</c> (fracción de métodos con docstring) + documented/total.
/// Todo por PatchNodePropertiesAsync (merge). Las colecciones se recalculan y sobreescriben cada corrida.
/// </summary>
public class ContractEnricher : IGraphEnricher
{
    public string Name  => "ContractEnricher";
    public int    Order => 12;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        int methodsDoc = 0, typesContract = 0, enumsContract = 0;

        // Reset de doc_params/doc_returns (idempotencia en re-runs sin reindex): solo se escriben cuando
        // existen, así que un método que pierde sus <param> conservaría el valor viejo sin este reset.
        // (public_props/values/doc_coverage se reescriben siempre, no necesitan reset.)
        await ctx.Repository.ExecuteAsync(
            "UPDATE nodes SET properties=json_remove(properties,'$.doc_params','$.doc_returns','$.exceptions','$.overloads','$.overload_count') WHERE project=@project AND label IN ('Method','Function')",
            new { project = ctx.Project });

        foreach (var compilation in ctx.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = compilation.GetSemanticModel(tree);
                var root  = await tree.GetRootAsync();

                // Tipos con miembros: class / struct / record (interface no lleva public_props de datos).
                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol) continue;

                    if (typeSymbol.TypeKind is TypeKind.Class or TypeKind.Struct)
                        typesContract += await PatchTypeContractAsync(typeSymbol, ctx) ? 1 : 0;

                    // Agrupar por nodo: el nodo de método es name-based (una clave por nombre, para no
                    // romper las aristas de cbm), así que las sobrecargas se materializan como overloads[]. Se agrupa por
                    // el QN y no por el nombre de Roslyn: los constructores (.ctor) y las implementaciones explícitas
                    // (Ns.IFoo.Bar) caen en su nodo de cbm (GraphMethods); antes se quedaban sin contrato.
                    foreach (var group in typeSymbol.GetMembers().OfType<IMethodSymbol>()
                        .Where(GraphMethods.HasNode)
                        .GroupBy(m => SymbolQn.ForMethod(m, ctx.Project, ctx.RepoRoot) ?? m.Name, StringComparer.Ordinal))
                    {
                        methodsDoc += await PatchMethodContractAsync(group.ToList(), ctx) ? 1 : 0;
                    }
                }

                // Enums (no son TypeDeclarationSyntax).
                foreach (var enumDecl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(enumDecl) is not INamedTypeSymbol enumSymbol) continue;
                    enumsContract += await PatchEnumContractAsync(enumSymbol, ctx) ? 1 : 0;
                }
            }
        }

        progress?.Report($"Contract: {methodsDoc} methods with param/return docs, {typesContract} type contracts, {enumsContract} enum contracts.");

        await PatchDocCoverageAsync(ctx, progress);
    }

    // ── Método: overloads[] + doc_params/doc_returns/exceptions ──────────────────
    // Todas las sobrecargas de un nombre comparten el nodo (QN name-based). Se materializa un array
    // overloads[] con {signature, return_type_fqn, accessibility, [is_static], [doc_params], [doc_returns],
    // [exceptions]} y, por retrocompatibilidad, las claves top-level del primer overload documentado.
    private async Task<bool> PatchMethodContractAsync(List<IMethodSymbol> overloadSet, EnrichmentContext ctx)
    {
        var qn = SymbolQn.ForMethod(overloadSet[0], ctx.Project, ctx.RepoRoot);
        if (qn is null) return false;

        var overloads = new List<Dictionary<string, object?>>();
        Dictionary<string, string>? primaryParams = null;
        string? primaryReturns = null;
        List<Dictionary<string, object?>>? primaryExceptions = null;

        foreach (var m in overloadSet.OrderBy(m => m.Parameters.Length))
        {
            var doc = ParseMethodDoc(m.GetDocumentationCommentXml());
            var entry = new Dictionary<string, object?>
            {
                ["signature"]       = BuildSignature(m),
                ["return_type_fqn"] = m.ReturnType.ToDisplayString(),
                ["accessibility"]   = m.DeclaredAccessibility.ToString().ToLowerInvariant(),
            };
            if (m.IsStatic)                 entry["is_static"]   = true;
            if (doc.Params is not null)     entry["doc_params"]  = doc.Params;
            if (doc.Returns is not null)    entry["doc_returns"] = doc.Returns;
            if (doc.Exceptions is not null) entry["exceptions"]  = doc.Exceptions;
            overloads.Add(entry);

            primaryParams     ??= doc.Params;
            primaryReturns    ??= doc.Returns;
            primaryExceptions ??= doc.Exceptions;
        }

        var patch = new Dictionary<string, object?>
        {
            ["overloads"]      = overloads,
            ["overload_count"] = overloads.Count,
        };
        if (primaryParams is not null)     patch["doc_params"]  = primaryParams;
        if (primaryReturns is not null)    patch["doc_returns"] = primaryReturns;
        if (primaryExceptions is not null) patch["exceptions"]  = primaryExceptions;

        var patched = await ctx.Repository.PatchNodePropertiesAsync(ctx.Project, qn, patch);
        if (patched == 0) { ctx.RecordPatchMiss(Name, qn); return false; }
        ctx.RecordPatchHit(Name);
        return true;
    }

    // ── Class/struct/record: public_props ────────────────────────────────────────
    private async Task<bool> PatchTypeContractAsync(INamedTypeSymbol type, EnrichmentContext ctx)
    {
        var qn = SymbolQn.ForType(type, ctx.Project, ctx.RepoRoot);
        if (qn is null) return false;

        var props = new List<Dictionary<string, object?>>();
        foreach (var member in type.GetMembers())
        {
            string? name = null, typeName = null, def = null, doc = null;
            bool nullable = false, isStatic = false;

            // Se incluyen miembros estáticos (p.ej. singletons como `Engine`) con flag is_static.
            if (member is IPropertySymbol p && p.DeclaredAccessibility == Accessibility.Public && !p.IsIndexer)
            {
                name = p.Name; typeName = p.Type.ToDisplayString();
                nullable = IsNullable(p.Type);
                doc = ExtractSummary(p.GetDocumentationCommentXml());
                def = ExtractInitializer(p.DeclaringSyntaxReferences);
                isStatic = p.IsStatic;
            }
            else if (member is IFieldSymbol f && f.DeclaredAccessibility == Accessibility.Public
                     && !f.IsConst && !f.IsImplicitlyDeclared)
            {
                name = f.Name; typeName = f.Type.ToDisplayString();
                nullable = IsNullable(f.Type);
                doc = ExtractSummary(f.GetDocumentationCommentXml());
                def = ExtractInitializer(f.DeclaringSyntaxReferences);
                isStatic = f.IsStatic;
            }
            else continue;

            var entry = new Dictionary<string, object?> { ["name"] = name, ["type"] = typeName, ["nullable"] = nullable };
            if (isStatic)        entry["is_static"] = true;
            if (def is not null) entry["default"]   = def;
            if (doc is not null) entry["doc"]       = doc;
            AddPropAttributes(member, entry);   // F5.2: validaciones DataAnnotations + serialización JSON
            props.Add(entry);
        }

        // Se escribe siempre (incluso []) para que un tipo que pierde propiedades no conserve un valor viejo.
        var patched = await ctx.Repository.PatchNodePropertiesAsync(ctx.Project, qn,
            new Dictionary<string, object?> { ["public_props"] = props });
        if (patched == 0) { ctx.RecordPatchMiss(Name, qn); return false; }
        ctx.RecordPatchHit(Name);
        return true;
    }

    // ── Enum: values ──────────────────────────────────────────────────────────────
    private async Task<bool> PatchEnumContractAsync(INamedTypeSymbol enumSymbol, EnrichmentContext ctx)
    {
        var qn = SymbolQn.ForType(enumSymbol, ctx.Project, ctx.RepoRoot);
        if (qn is null) return false;

        var values = new List<Dictionary<string, object?>>();
        foreach (var f in enumSymbol.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
        {
            // ulong > long.MaxValue (flags de 64 bits, 1UL<<63) haría lanzar a Convert.ToInt64 → bit-cast seguro.
            long value = f.ConstantValue is ulong u ? unchecked((long)u) : Convert.ToInt64(f.ConstantValue);
            var entry = new Dictionary<string, object?> { ["name"] = f.Name, ["value"] = value };
            var doc = ExtractSummary(f.GetDocumentationCommentXml());
            if (doc is not null) entry["doc"] = doc;
            values.Add(entry);
        }

        var patched = await ctx.Repository.PatchNodePropertiesAsync(ctx.Project, qn,
            new Dictionary<string, object?> { ["values"] = values });
        if (patched == 0) { ctx.RecordPatchMiss(Name, qn); return false; }
        ctx.RecordPatchHit(Name);
        return true;
    }

    // ── doc_coverage por CsProject ──────────────────────────────────────────────
    private static async Task PatchDocCoverageAsync(EnrichmentContext ctx, IProgress<string>? progress)
    {
        var db = ctx.Repository;

        var rows = (await db.QueryAsync("""
            SELECT file_path,
                   CASE WHEN json_extract(properties,'$.docstring') IS NOT NULL
                             AND trim(json_extract(properties,'$.docstring')) <> '' THEN 1 ELSE 0 END AS documented
            FROM nodes
            WHERE project=@project AND label IN ('Method','Class','Interface')
              AND qualified_name NOT LIKE '\_%' ESCAPE '\'
              AND qualified_name NOT LIKE '__route__%'
            """, new { project = ctx.Project })).ToList();

        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        var doc   = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var proj = ctx.CsProjectOf((string?)r.file_path)?.Name;
            if (string.IsNullOrEmpty(proj)) continue;
            total[proj] = total.GetValueOrDefault(proj) + 1;
            if ((long)r.documented == 1) doc[proj] = doc.GetValueOrDefault(proj) + 1;
        }

        int patched = 0;
        var csProjects = await db.QueryAsync("SELECT id, name FROM nodes WHERE project=@project AND label='CsProject'", new { project = ctx.Project });
        foreach (var cp in csProjects)
        {
            string name = (string)cp.name;
            int t = total.GetValueOrDefault(name), d = doc.GetValueOrDefault(name);
            double coverage = t == 0 ? 0.0 : Math.Round((double)d / t, 3);
            await ctx.Repository.PatchNodePropertiesByIdAsync((long)cp.id, new Dictionary<string, object?>
            {
                ["doc_coverage"]    = coverage,
                ["documented_defs"] = d,
                ["total_defs"]      = t
            });
            patched++;
        }
        progress?.Report($"Contract: doc_coverage on {patched} CsProjects.");
    }

    // ── F5.2: validaciones DataAnnotations + serialización JSON en una public_prop ──
    // Lee los atributos directamente de Roslyn (sin depender del AttributeEnricher). Materializa:
    //   validations[] = [{name, [args]}] para los validadores de System.ComponentModel.DataAnnotations;
    //   json_name / json_ignore para System.Text.Json y Newtonsoft.Json.
    private static void AddPropAttributes(ISymbol member, Dictionary<string, object?> entry)
    {
        List<Dictionary<string, object?>>? validations = null;
        foreach (var a in member.GetAttributes())
        {
            var n = a.AttributeClass?.Name;
            if (n is null) continue;
            switch (n)
            {
                // Validadores sin argumentos relevantes.
                case "RequiredAttribute":
                case "KeyAttribute":
                case "EmailAddressAttribute":
                case "PhoneAttribute":
                case "UrlAttribute":
                case "CreditCardAttribute":
                    (validations ??= new()).Add(new() { ["name"] = Strip(n) });
                    break;
                // Validadores con argumentos (longitud/rango/patrón).
                case "MaxLengthAttribute":
                case "MinLengthAttribute":
                case "StringLengthAttribute":
                case "RangeAttribute":
                case "RegularExpressionAttribute":
                case "CompareAttribute":
                    var v = new Dictionary<string, object?> { ["name"] = Strip(n) };
                    if (a.ConstructorArguments.Length > 0)
                        v["args"] = a.ConstructorArguments.Select(AttributeEnricher.FormatTypedConstant).ToList();
                    (validations ??= new()).Add(v);
                    break;
                // Serialización — System.Text.Json.
                case "JsonPropertyNameAttribute":
                    if (a.ConstructorArguments.FirstOrDefault().Value is string stj) entry["json_name"] = stj;
                    break;
                // Serialización — Newtonsoft.Json.
                case "JsonPropertyAttribute":
                    var jn = a.ConstructorArguments.FirstOrDefault().Value as string
                          ?? a.NamedArguments.FirstOrDefault(x => x.Key == "PropertyName").Value.Value as string;
                    if (jn is not null) entry["json_name"] = jn;
                    break;
                case "JsonIgnoreAttribute":
                    // STJ admite [JsonIgnore(Condition=...)]: Always/(sin Condition)→siempre ignora (true);
                    // Never→se serializa (no marcar); WhenWritingNull/Default→condicional (guardar el nombre).
                    // Newtonsoft [JsonIgnore] no tiene Condition → siempre ignora.
                    var cond = a.NamedArguments.FirstOrDefault(x => x.Key == "Condition").Value;
                    if (cond.Kind == TypedConstantKind.Enum && cond.Value is not null)
                    {
                        var full = AttributeEnricher.FormatTypedConstant(cond);              // JsonIgnoreCondition.WhenWritingNull
                        var condName = full.Contains('.') ? full[(full.LastIndexOf('.') + 1)..] : full;
                        if (condName == "Never") break;
                        entry["json_ignore"] = condName == "Always" ? (object)true : condName;
                    }
                    else entry["json_ignore"] = true;
                    break;
            }
        }
        if (validations is not null) entry["validations"] = validations;
    }

    private static string Strip(string name) =>
        name.Length > 9 && name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^9] : name;

    // ── Helpers ──────────────────────────────────────────────────────────────────
    private static bool IsNullable(ITypeSymbol t) =>
        t.NullableAnnotation == NullableAnnotation.Annotated ||
        (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T });

    private static string? ExtractInitializer(ImmutableArray<SyntaxReference> refs)
    {
        // Best-effort: initializer sintáctico de una propiedad/campo (= <valor>).
        foreach (var r in refs)
        {
            var eq = r.GetSyntax() switch
            {
                PropertyDeclarationSyntax pd => pd.Initializer,
                VariableDeclaratorSyntax  vd => vd.Initializer,
                _                            => null
            };
            if (eq?.Value is { } v) return v.ToString();
        }
        return null;
    }

    private sealed record MethodDoc(
        Dictionary<string, string>? Params, string? Returns, List<Dictionary<string, object?>>? Exceptions);

    private static MethodDoc ParseMethodDoc(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return new(null, null, null);
        try
        {
            var root = XElement.Parse(xml);
            Dictionary<string, string>? ps = null;
            foreach (var pe in root.Descendants("param"))
            {
                var n = pe.Attribute("name")?.Value;
                var v = Normalize(FlattenXml(pe));
                if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(v)) continue;
                (ps ??= new())[n] = v;
            }
            var ret = root.Descendants("returns").Select(e => Normalize(FlattenXml(e)))
                          .FirstOrDefault(s => !string.IsNullOrEmpty(s));
            List<Dictionary<string, object?>>? exs = null;
            foreach (var ee in root.Descendants("exception"))
            {
                var cref = ee.Attribute("cref")?.Value;
                if (string.IsNullOrEmpty(cref)) continue;
                var entry = new Dictionary<string, object?> { ["type"] = CrefName(cref) };
                var d = Normalize(FlattenXml(ee));
                if (!string.IsNullOrEmpty(d)) entry["doc"] = d;
                (exs ??= new()).Add(entry);
            }
            return new(ps, string.IsNullOrEmpty(ret) ? null : ret, exs);
        }
        catch { return new(null, null, null); }
    }

    private static string? ExtractSummary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var root = XElement.Parse(xml);
            var s = root.Name.LocalName == "summary" ? root : root.Descendants("summary").FirstOrDefault();
            var text = s is null ? null : Normalize(FlattenXml(s));
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch { return null; }
    }

    // Renderiza el contenido mixto de un elemento XML-doc a texto, resolviendo <see cref>/<paramref>/
    // <typeparamref>/<see langword> — que XElement.Value descarta, dejando huecos (p.ej. "represented as a .").
    private static string FlattenXml(XElement el)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var node in el.Nodes())
        {
            if (node is XText t) { sb.Append(t.Value); continue; }
            if (node is not XElement e) continue;
            switch (e.Name.LocalName)
            {
                case "see":
                case "seealso":
                    var cref = e.Attribute("cref")?.Value;
                    var lang = e.Attribute("langword")?.Value;
                    if (!string.IsNullOrEmpty(cref))      sb.Append(CrefName(cref));
                    else if (!string.IsNullOrEmpty(lang)) sb.Append(lang);
                    else                                  sb.Append(FlattenXml(e));
                    break;
                case "paramref":
                case "typeparamref":
                    sb.Append(e.Attribute("name")?.Value ?? FlattenXml(e));
                    break;
                default:
                    sb.Append(FlattenXml(e));
                    break;
            }
        }
        return sb.ToString();
    }

    // "T:Namespace.Type" / "M:Namespace.Type.Method(args)" → último segmento (nombre de tipo/miembro).
    private static string CrefName(string cref)
    {
        var s = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        var dot = s.LastIndexOf('.');
        return dot >= 0 ? s[(dot + 1)..] : s;
    }

    private static string BuildSignature(IMethodSymbol m)
    {
        var tp = m.TypeParameters.Length > 0
            ? "<" + string.Join(", ", m.TypeParameters.Select(t => t.Name)) + ">" : "";
        return $"{tp}({string.Join(", ", m.Parameters.Select(FormatParam))})";
    }

    private static string FormatParam(IParameterSymbol p)
    {
        var mod = p.IsParams ? "params "
            : p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };
        var def = p.HasExplicitDefaultValue ? " = " + FormatDefault(p.ExplicitDefaultValue) : "";
        return $"{mod}{p.Type.ToDisplayString()} {p.Name}{def}";
    }

    private static string FormatDefault(object? v) => v switch
    {
        null     => "null",
        string s => $"\"{s}\"",
        bool b   => b ? "true" : "false",
        _        => v.ToString() ?? "null"
    };

    private static string Normalize(string s) =>
        string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim()).Where(l => l.Length > 0)).Trim();
}
