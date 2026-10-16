using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetEnricher.Enrichers;

/// <summary>
/// Nodos <c>Route</c> + aristas <c>HANDLES</c> para la superficie HTTP (ASP.NET controllers +
/// Minimal APIs). v2 (Fase 2): resuelve prefijos de <c>MapGroup</c> (incl. anidados), captura
/// <c>MapMethods</c>/<c>MapFallback</c>/<c>MapHealthChecks</c>, controllers sin <c>[Route]</c> de
/// clase (por convención), y materializa auth (<c>[Authorize]</c>/<c>[AllowAnonymous]</c>/
/// <c>.RequireAuthorization()</c>) y contrato (<c>[ProducesResponseType]</c>/<c>[Consumes]</c>/
/// <c>[Produces]</c>/<c>[FromBody]</c>) como propiedades del nodo Route y de la arista HANDLES.
/// El diagnóstico de rutas huérfanas (Route sin HANDLES) vive aquí (genérico), no en ConfigBinding.
/// </summary>
public class RouteEnricher : IGraphEnricher
{
    public string Name  => "RouteEnricher";
    public int    Order => 4;

    private static readonly Dictionary<string, string> HttpMethodAttrToMethod = new()
    {
        ["HttpGetAttribute"]    = "GET",
        ["HttpPostAttribute"]   = "POST",
        ["HttpPutAttribute"]    = "PUT",
        ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatchAttribute"]  = "PATCH"
    };

    private static readonly Dictionary<string, string> MapMethodToHttp = new()
    {
        ["MapGet"]    = "GET",
        ["MapPost"]   = "POST",
        ["MapPut"]    = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"]  = "PATCH"
    };

    // ─── Tipos auxiliares ───────────────────────────────────────────────────────

    /// <summary>Auth resuelta de atributos/cadena. <c>Required</c>: true=Authorize, false=AllowAnonymous, null=no declarado.</summary>
    private sealed record AuthInfo(bool? Required, string[]? Schemes, string[]? Roles, string[]? Policies, bool Inferred = false)
    {
        public static readonly AuthInfo None = new(null, null, null, null);
    }

    private sealed record ContractInfo(
        int[]? ResponseCodes, string? ResponseTypeFqn, string? RequestTypeFqn,
        string? Consumes, string? Produces);

    private sealed record GroupInfo(string Prefix, bool Unresolved, AuthInfo Auth);

    // ─── Entry point ────────────────────────────────────────────────────────────

    /// <summary>La solución protege todos los endpoints a nivel global (política por defecto, filtro, grupo de controllers): no se infiere «sin autenticación».</summary>
    private string? _globalAuthorization;

    public async Task EnrichAsync(EnrichmentContext ctx, IProgress<string>? progress = null)
    {
        var filter = new CbmExclusionFilter(ctx.RepoRoot);
        int controllerRoutes = 0, minimalApiRoutes = 0;
        _globalAuthorization = await DetectGlobalAuthorizationAsync(ctx.Compilations, filter);

        foreach (var compilation in ctx.Compilations)
        {
            var enrichedAt = DateTime.UtcNow.ToString("O");
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                var model = compilation.GetSemanticModel(tree);
                var root  = await tree.GetRootAsync();

                controllerRoutes += await ProcessControllersAsync(root, model, enrichedAt, ctx);
                minimalApiRoutes += await ProcessMinimalApisAsync(root, model, enrichedAt, ctx);
            }
        }

        progress?.Report($"RouteEnricher: {controllerRoutes} controller routes, {minimalApiRoutes} minimal API routes"
            + (_globalAuthorization is null ? "" : $"; global authorization ({_globalAuthorization}): undeclared auth is not inferred as false"));
        await ReportOrphanRoutesAsync(ctx.Repository, ctx.Project, progress);
    }

    // ─── Controllers (MVC) ──────────────────────────────────────────────────────

    private async Task<int> ProcessControllersAsync(
        SyntaxNode root, SemanticModel model, string enrichedAt, EnrichmentContext ctx)
    {
        int count = 0;
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var cls in classes)
        {
            if (model.GetDeclaredSymbol(cls) is not INamedTypeSymbol typeSymbol)
                continue;

            var routeAttr = typeSymbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "RouteAttribute");

            // ¿Es un controller? Con [Route] de clase, o bien [ApiController]/hereda ControllerBase
            // (convención: sin [Route], las rutas salen del template de cada [HttpX]).
            bool isApiController = typeSymbol.GetAttributes()
                .Any(a => a.AttributeClass?.Name == "ApiControllerAttribute");
            bool derivesController = DerivesFromController(typeSymbol);

            string? basePath;
            bool convention;
            if (routeAttr is not null)
            {
                basePath = routeAttr.ConstructorArguments.FirstOrDefault().Value as string;
                if (string.IsNullOrWhiteSpace(basePath)) continue;
                basePath   = ReplaceControllerToken(basePath, typeSymbol.Name);
                convention = false;
            }
            else if (isApiController || derivesController)
            {
                basePath   = "";       // ruta = template del [HttpX] tal cual
                convention = true;
            }
            else
            {
                continue;              // no es un controller HTTP
            }

            var classAuth = ClassAuth(typeSymbol);

            foreach (var member in typeSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                var httpAttrs = member.GetAttributes()
                    .Where(a => a.AttributeClass?.Name is string name && HttpMethodAttrToMethod.ContainsKey(name))
                    .ToList();

                if (httpAttrs.Count == 0)
                {
                    // Método con [Route] pero sin [HttpX] → verbo ANY (poco frecuente).
                    var methodRouteAttr = member.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name == "RouteAttribute");
                    if (methodRouteAttr is null) continue;

                    var mp       = methodRouteAttr.ConstructorArguments.FirstOrDefault().Value as string;
                    var fullPath = CombinePaths(basePath, mp);
                    var (anyName, anyOrder) = ExtractRouteNaming(methodRouteAttr);
                    if (await CreateRouteNode(member, fullPath, "ANY", cls, enrichedAt, ctx,
                            InferUnprotected(MergeAuth(classAuth, ExtractAuthFromAttributes(member))),
                            ExtractContract(member), convention, anyName, anyOrder))
                        count++;
                    continue;
                }

                var methodAuth = InferUnprotected(MergeAuth(classAuth, ExtractAuthFromAttributes(member)));
                var contract   = ExtractContract(member);

                foreach (var httpAttr in httpAttrs)
                {
                    var httpMethod = HttpMethodAttrToMethod[httpAttr.AttributeClass!.Name];
                    var methodPath = httpAttr.ConstructorArguments.FirstOrDefault().Value as string;
                    var fullPath   = CombinePaths(basePath, methodPath);
                    var (routeName, routeOrder) = ExtractRouteNaming(httpAttr);

                    if (await CreateRouteNode(member, fullPath, httpMethod, cls, enrichedAt, ctx,
                            methodAuth, contract, convention, routeName, routeOrder))
                        count++;
                }
            }
        }

        return count;
    }

    private async Task<bool> CreateRouteNode(
        IMethodSymbol method, string fullPath, string httpMethod,
        ClassDeclarationSyntax cls, string enrichedAt, EnrichmentContext ctx,
        AuthInfo auth, ContractInfo contract, bool convention,
        string? routeName = null, int? routeOrder = null)
    {
        var graph   = ctx.Repository;
        var project = ctx.Project;

        var methodQn     = SymbolQn.ForMethod(method, project, ctx.RepoRoot);
        if (methodQn is null) return false;

        var methodNodeId = await graph.GetNodeIdAsync(project, methodQn);
        var filePath     = SymbolQn.Relativize(cls.SyntaxTree.FilePath, ctx.RepoRoot);
        var line         = cls.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        var fullPathWithSlash = fullPath.StartsWith('/') ? fullPath : '/' + fullPath;
        var routeQn = $"__route__{httpMethod}__{fullPathWithSlash}";

        var props = BaseProps(httpMethod, enrichedAt);
        props["controller"] = method.ContainingType?.Name;
        if (convention) props["route_convention"] = true;
        if (routeName is not null)  props["route_name"]  = routeName;   // F5.2: [HttpX(Name="...")]/[Route(Name=...)]
        if (routeOrder is not null) props["route_order"] = routeOrder;  // F5.2: [HttpX(Order=n)]
        AddAuthProps(props, auth);
        AddContractProps(props, contract);

        var routeNodeId = await graph.UpsertNodeAsync(project, "Route", fullPathWithSlash, routeQn, filePath, line, props);

        if (methodNodeId.HasValue)
        {
            var edgeProps = BaseProps(httpMethod, enrichedAt);
            edgeProps["route_path"] = fullPathWithSlash;
            if (routeName is not null)  edgeProps["route_name"]  = routeName;
            if (routeOrder is not null) edgeProps["route_order"] = routeOrder;
            AddAuthProps(edgeProps, auth);
            AddContractProps(edgeProps, contract);
            await graph.UpsertEdgeAsync(project, methodNodeId.Value, routeNodeId, "HANDLES", edgeProps);
        }

        // Patch al método handler: útil para inspect_node del propio método.
        var methodPatch = new Dictionary<string, object?>
        {
            ["route_path"]   = fullPathWithSlash,
            ["route_method"] = httpMethod
        };
        if (auth.Required is bool req) methodPatch["auth_required"] = req;
        if (auth.Schemes is not null)  methodPatch["auth_schemes"]  = auth.Schemes;

        var patched = await graph.PatchNodePropertiesAsync(project, methodQn, methodPatch);
        if (patched == 0) ctx.RecordPatchMiss(Name, methodQn);
        else              ctx.RecordPatchHit(Name);

        return true;
    }

    // ─── Minimal APIs (incl. MapGroup) ──────────────────────────────────────────

    private async Task<int> ProcessMinimalApisAsync(
        SyntaxNode root, SemanticModel model, string enrichedAt, EnrichmentContext ctx)
    {
        var graph   = ctx.Repository;
        var project = ctx.Project;
        int count = 0;

        var groups = BuildGroupMap(root, model);

        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            var mapName = ma.Name.Identifier.Text;

            bool isVerbMap  = MapMethodToHttp.ContainsKey(mapName);
            bool isMethods  = mapName == "MapMethods";
            bool isFallback = mapName == "MapFallback";
            bool isHealth   = mapName == "MapHealthChecks";
            if (!isVerbMap && !isMethods && !isFallback && !isHealth) continue;

            var args = inv.ArgumentList.Arguments;

            // Prefijo + auth heredados del receiver (grupo) y de la cadena de esta invocación
            // (p.ej. app.MapPost("/x", h).RequireAuthorization() — auth colgada de la propia ruta).
            var (prefix, unresolvedPrefix, groupAuth) = ResolveReceiver(ma.Expression, model, groups);
            var chainAuth = ExtractChainAuth(OutermostInvocation(inv), model);
            // Un receptor que es un grupo que no se ve aquí (llega por parámetro o lo devuelve otro método) puede traer su
            // propia autorización; y el health check es público a propósito. En los demás casos, sin autorización declarada, el
            // endpoint queda abierto.
            var auth      = MergeAuth(groupAuth, chainAuth);
            if (!isHealth && !IsUnresolvedGroupReceiver(ma.Expression, model, groups))
                auth = InferUnprotected(auth);

            // Patrón de ruta (primer arg string), salvo MapFallback(handler) sin patrón.
            string? rawPath;
            List<string> verbs;

            if (isFallback)
            {
                rawPath = args.Count >= 2 ? GetStringConstant(args[0].Expression, model) : null;
                rawPath ??= "{*path}";
                verbs = new() { "ANY" };
            }
            else if (isHealth)
            {
                rawPath = args.Count >= 1 ? GetStringConstant(args[0].Expression, model) : null;
                if (rawPath is null) continue;
                verbs = new() { "GET" };
            }
            else if (isMethods)
            {
                // MapMethods(pattern, methods, handler)
                if (args.Count < 2) continue;
                rawPath = GetStringConstant(args[0].Expression, model);
                if (rawPath is null) continue;
                verbs = ExtractVerbList(args[1].Expression, model);
                if (verbs.Count == 0) continue;
            }
            else // isVerbMap
            {
                if (args.Count == 0) continue;
                rawPath = GetStringConstant(args[0].Expression, model);
                if (rawPath is null) continue;
                verbs = new() { MapMethodToHttp[mapName] };
            }

            var fullPath = CombineGroupPath(prefix, rawPath!);
            var filePath = SymbolQn.Relativize(inv.SyntaxTree.FilePath, ctx.RepoRoot);
            var line     = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            // Handler (último arg): si es un method group first-party resoluble, enlazar con
            // HANDLES (habilita que FlowEnricher camine el endpoint). Las lambdas inline quedan
            // como Route huérfano — es el caso que el diagnóstico de huérfanas describe.
            var handlerNodeId = await ResolveMinimalApiHandlerNodeAsync(inv, isHealth, model, ctx);

            foreach (var verb in verbs)
            {
                var routeQn = $"__route__{verb}__{fullPath}";
                var props   = BaseProps(verb, enrichedAt);
                if (!string.IsNullOrEmpty(prefix)) props["group_prefix"] = "/" + prefix.Trim('/');
                if (unresolvedPrefix) props["group_prefix_unresolved"] = true;
                if (isFallback) props["is_fallback"] = true;
                AddAuthProps(props, auth);

                var routeNodeId = await graph.UpsertNodeAsync(project, "Route", fullPath, routeQn, filePath, line, props);
                count++;

                if (handlerNodeId is long hid)
                {
                    var edgeProps = BaseProps(verb, enrichedAt);
                    edgeProps["route_path"] = fullPath;
                    AddAuthProps(edgeProps, auth);
                    await graph.UpsertEdgeAsync(project, hid, routeNodeId, "HANDLES", edgeProps);
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Resuelve el argumento handler (último) de un <c>Map*</c> minimal-API a su nodo Method del grafo,
    /// o <c>null</c> si es una lambda inline, un <c>MapHealthChecks</c> (sin método propio) o no es
    /// first-party. Habilita la arista HANDLES para method groups (p.ej. <c>MapPost("/x", HandleAsync)</c>).
    /// </summary>
    private static async Task<long?> ResolveMinimalApiHandlerNodeAsync(
        InvocationExpressionSyntax inv, bool isHealth, SemanticModel model, EnrichmentContext ctx)
    {
        if (isHealth) return null;
        var args = inv.ArgumentList.Arguments;
        if (args.Count == 0) return null;

        var handlerExpr = args[^1].Expression;
        if (handlerExpr is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            return null;

        var info   = model.GetSymbolInfo(handlerExpr);
        var method = info.Symbol as IMethodSymbol
                     ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (method is null) return null;

        var qn = SymbolQn.ForMethod(method, ctx.Project, ctx.RepoRoot);
        if (qn is null) return null;

        return await ctx.Repository.GetNodeIdAsync(ctx.Project, qn);
    }

    /// <summary>
    /// Mapa símbolo-de-variable → grupo (prefijo compuesto + auth), resolviendo <c>MapGroup</c>
    /// anidados de forma recursiva con memoización y guarda de ciclos.
    /// </summary>
    private static Dictionary<ISymbol, GroupInfo> BuildGroupMap(SyntaxNode root, SemanticModel model)
    {
        var raw = new Dictionary<ISymbol, (InvocationExpressionSyntax mg, ExpressionSyntax init)>(SymbolEqualityComparer.Default);
        foreach (var vd in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (vd.Initializer?.Value is not ExpressionSyntax init) continue;
            var mg = FindMapGroupInvocation(init);
            if (mg is null) continue;
            if (model.GetDeclaredSymbol(vd) is ILocalSymbol sym)
                raw[sym] = (mg, init);
        }

        var resolved = new Dictionary<ISymbol, GroupInfo>(SymbolEqualityComparer.Default);

        GroupInfo Resolve(ISymbol s, HashSet<ISymbol> stack)
        {
            if (resolved.TryGetValue(s, out var done)) return done;
            if (!raw.TryGetValue(s, out var entry) || !stack.Add(s))
                return new GroupInfo("", false, AuthInfo.None);

            var mg        = entry.mg;
            var prefixArg = mg.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            var ownPrefix = prefixArg is null ? null : GetStringConstant(prefixArg, model);

            // receiver del propio MapGroup (para anidados)
            var receiver     = (mg.Expression as MemberAccessExpressionSyntax)?.Expression;
            var parent       = ResolveExprToGroup(receiver, model, raw, resolved, stack, Resolve);
            var chainAuth    = ExtractChainAuth(entry.init, model);
            var combinedAuth = MergeAuth(parent.Auth, chainAuth);

            bool unresolved = ownPrefix is null || parent.Unresolved;
            var full = CombineGroupPath(parent.Prefix, ownPrefix ?? "");

            var info = new GroupInfo(full.Trim('/'), unresolved, combinedAuth);
            resolved[s] = info;
            return info;
        }

        foreach (var key in raw.Keys.ToList())
            if (!resolved.ContainsKey(key))
                Resolve(key, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

        return resolved;
    }

    // Resuelve una expresión-receiver a su grupo (por variable conocida o MapGroup inline).
    private static GroupInfo ResolveExprToGroup(
        ExpressionSyntax? receiver, SemanticModel model,
        Dictionary<ISymbol, (InvocationExpressionSyntax, ExpressionSyntax)> raw,
        Dictionary<ISymbol, GroupInfo> resolved,
        HashSet<ISymbol> stack,
        Func<ISymbol, HashSet<ISymbol>, GroupInfo> resolve)
    {
        if (receiver is IdentifierNameSyntax id &&
            model.GetSymbolInfo(id).Symbol is ISymbol sym && raw.ContainsKey(sym))
            return resolve(sym, stack);
        return new GroupInfo("", false, AuthInfo.None);
    }

    // Para invocaciones MapX: (prefijo, unresolved, auth) del receiver. Cubre grupos ligados a una
    // variable (vía `groups`) y el patrón fluido inline `app.MapGroup("/x").MapGet(..)` sin variable.
    private static (string prefix, bool unresolved, AuthInfo auth) ResolveReceiver(
        ExpressionSyntax? receiver, SemanticModel model, Dictionary<ISymbol, GroupInfo> groups)
    {
        if (receiver is IdentifierNameSyntax id &&
            model.GetSymbolInfo(id).Symbol is ISymbol sym && groups.TryGetValue(sym, out var g))
            return (g.Prefix, g.Unresolved, g.Auth);

        // Inline: el receiver es él mismo una cadena que contiene MapGroup(..).
        var mg = receiver is null ? null : FindMapGroupInvocation(receiver);
        if (mg is not null)
        {
            var prefixArg = mg.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            var ownPrefix = prefixArg is null ? null : GetStringConstant(prefixArg, model);
            var inner     = (mg.Expression as MemberAccessExpressionSyntax)?.Expression;

            var (parentPrefix, parentUnresolved, parentAuth) = ResolveReceiver(inner, model, groups);
            var auth = MergeAuth(parentAuth, ExtractChainAuth(receiver, model));
            var full = CombineGroupPath(parentPrefix, ownPrefix ?? "").Trim('/');
            return (full, ownPrefix is null || parentUnresolved, auth);
        }

        return ("", false, AuthInfo.None);
    }

    /// <summary>
    /// Dada una invocación dentro de una cadena fluida <c>a.M1(..).M2(..).M3(..)</c>, sube hasta la
    /// invocación más externa de la cadena (para poder inspeccionar métodos encadenados posteriores
    /// como <c>.RequireAuthorization()</c> que cuelgan de una ruta Map individual).
    /// </summary>
    private static ExpressionSyntax OutermostInvocation(InvocationExpressionSyntax inv)
    {
        ExpressionSyntax cur = inv;
        while (cur.Parent is MemberAccessExpressionSyntax ma && ma.Expression == cur &&
               ma.Parent is InvocationExpressionSyntax outer)
            cur = outer;
        return cur;
    }

    /// <summary>Recorre una cadena <c>x.MapGroup(..).Y(..).Z(..)</c> y devuelve la invocación MapGroup, o null.</summary>
    private static InvocationExpressionSyntax? FindMapGroupInvocation(ExpressionSyntax expr)
    {
        var cur = expr;
        while (cur is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
        {
            if (ma.Name.Identifier.Text == "MapGroup") return inv;
            cur = ma.Expression;
        }
        return null;
    }

    /// <summary>Auth declarada por <c>.RequireAuthorization(..)</c> / <c>.AllowAnonymous()</c> en una cadena de invocaciones.</summary>
    private static AuthInfo ExtractChainAuth(ExpressionSyntax? expr, SemanticModel model)
    {
        bool? required = null;
        var policies = new List<string>();
        var cur = expr;
        while (cur is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma)
        {
            var name = ma.Name.Identifier.Text;
            if (name == "RequireAuthorization")
            {
                if (required != false) required = true;
                foreach (var arg in inv.ArgumentList.Arguments)
                    if (GetStringConstant(arg.Expression, model) is string p)
                        policies.Add(p);
            }
            else if (name == "AllowAnonymous")
            {
                required = false;
            }
            cur = ma.Expression;
        }
        return new AuthInfo(required, null, null, policies.Count > 0 ? policies.Distinct().ToArray() : null);
    }

    // ─── Extracción de auth / contrato desde atributos ──────────────────────────

    private static AuthInfo ExtractAuthFromAttributes(ISymbol symbol)
    {
        bool? required = null;
        var schemes = new List<string>();
        var roles = new List<string>();
        var policies = new List<string>();

        foreach (var attr in symbol.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "AllowAnonymousAttribute":
                    required = false;
                    break;
                case "AuthorizeAttribute":
                    if (required != false) required = true;
                    if (attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Kind == TypedConstantKind.Primitive &&
                        attr.ConstructorArguments[0].Value is string pol && !string.IsNullOrWhiteSpace(pol))
                        policies.Add(pol);
                    foreach (var na in attr.NamedArguments)
                    {
                        if (na.Value.Kind != TypedConstantKind.Primitive ||
                            na.Value.Value is not string s || string.IsNullOrWhiteSpace(s)) continue;
                        switch (na.Key)
                        {
                            case "AuthenticationSchemes": AddCsv(schemes, s); break;
                            case "Roles":                 AddCsv(roles, s);   break;
                            case "Policy":                policies.Add(s);    break;
                        }
                    }
                    break;
            }
        }

        return new AuthInfo(
            required,
            schemes.Count  > 0 ? schemes.Distinct().ToArray()  : null,
            roles.Count    > 0 ? roles.Distinct().ToArray()    : null,
            policies.Count > 0 ? policies.Distinct().ToArray() : null);
    }

    private static ContractInfo ExtractContract(IMethodSymbol method)
    {
        var codes = new List<int>();
        string? responseType = null, consumes = null, produces = null;

        foreach (var attr in method.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "ProducesResponseTypeAttribute":
                {
                    int? code = null; string? type = null;
                    foreach (var ca in attr.ConstructorArguments)
                    {
                        if (ca.Kind == TypedConstantKind.Type && ca.Value is INamedTypeSymbol t)
                            type = t.ToDisplayString();
                        else if (ca.Kind == TypedConstantKind.Primitive && ca.Value is int c)
                            code = c;
                    }
                    foreach (var na in attr.NamedArguments)
                    {
                        if (na.Key == "StatusCode" && na.Value.Kind == TypedConstantKind.Primitive && na.Value.Value is int c)
                            code = c;
                        else if (na.Key == "Type" && na.Value.Kind == TypedConstantKind.Type && na.Value.Value is INamedTypeSymbol t)
                            type = t.ToDisplayString();
                    }
                    if (code.HasValue) codes.Add(code.Value);
                    if (type is not null && responseType is null) responseType = type;
                    break;
                }
                case "ConsumesAttribute":
                    consumes ??= FirstStringArg(attr);
                    break;
                case "ProducesAttribute":
                    produces ??= FirstStringArg(attr);
                    break;
            }
        }

        string? requestType = null;
        foreach (var p in method.Parameters)
            if (p.GetAttributes().Any(a => a.AttributeClass?.Name == "FromBodyAttribute"))
            {
                requestType = p.Type.ToDisplayString();
                break;
            }

        return new ContractInfo(
            codes.Count > 0 ? codes.Distinct().OrderBy(x => x).ToArray() : null,
            responseType, requestType, consumes, produces);
    }

    // ─── Materialización de propiedades ─────────────────────────────────────────

    private static Dictionary<string, object?> BaseProps(string httpMethod, string enrichedAt) => new()
    {
        ["http_method"]    = httpMethod,
        ["origin"]         = "enricher",
        ["enriched_by"]    = new[] { "RouteEnricher" },
        ["enriched_at"]    = enrichedAt,
        ["presentational"] = true
    };

    private static void AddAuthProps(Dictionary<string, object?> props, AuthInfo auth)
    {
        if (auth.Required is bool req) props["auth_required"] = req;
        if (auth.Inferred)             props["auth_inferred"] = true;
        if (auth.Schemes  is not null) props["auth_schemes"]  = auth.Schemes;
        if (auth.Roles    is not null) props["auth_roles"]    = auth.Roles;
        if (auth.Policies is not null) props["auth_policies"] = auth.Policies;
    }

    private static void AddContractProps(Dictionary<string, object?> props, ContractInfo c)
    {
        if (c.ResponseCodes   is not null) props["response_codes"]   = c.ResponseCodes;
        if (c.ResponseTypeFqn is not null) props["response_type_fqn"] = c.ResponseTypeFqn;
        if (c.RequestTypeFqn  is not null) props["request_type_fqn"]  = c.RequestTypeFqn;
        if (c.Consumes        is not null) props["consumes"]          = c.Consumes;
        if (c.Produces        is not null) props["produces"]          = c.Produces;
    }

    // ─── Helpers de merge / parsing ─────────────────────────────────────────────

    /// <summary>
    /// Sin ninguna autorización declarada (atributo, cadena de <c>RequireAuthorization</c>, grupo) y sin protección
    /// global, el endpoint es público: <c>auth_required = false</c> con <c>auth_inferred = true</c>. Distingue «no declara
    /// nada» (antes, sin propiedad) de <c>[AllowAnonymous]</c>, que es un false declarado y no lleva <c>auth_inferred</c>.
    /// </summary>
    private AuthInfo InferUnprotected(AuthInfo auth) =>
        auth.Required is null && _globalAuthorization is null ? auth with { Required = false, Inferred = true } : auth;

    /// <summary>Un receptor de tipo grupo (<c>RouteGroupBuilder</c>) que no se resuelve a un <c>MapGroup</c> visible.</summary>
    private static bool IsUnresolvedGroupReceiver(ExpressionSyntax? receiver, SemanticModel model, Dictionary<ISymbol, GroupInfo> groups)
    {
        if (receiver is null || FindMapGroupInvocation(receiver) is not null) return false;
        if (receiver is IdentifierNameSyntax id && model.GetSymbolInfo(id).Symbol is ISymbol sym && groups.ContainsKey(sym)) return false;
        var type = model.GetTypeInfo(receiver).Type;
        return type is not null && (type.Name.Contains("GroupBuilder", StringComparison.Ordinal) || type.Name == "IEndpointConventionBuilder");
    }

    /// <summary>La autorización de la clase y de sus bases (<c>[Authorize]</c> se hereda): de la más base a la más derivada.</summary>
    private static AuthInfo ClassAuth(INamedTypeSymbol type)
    {
        var auth = AuthInfo.None;
        var chain = new Stack<INamedTypeSymbol>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            chain.Push(t);
        while (chain.Count > 0)
            auth = MergeAuth(auth, ExtractAuthFromAttributes(chain.Pop()));
        return auth;
    }

    /// <summary>
    /// Protección a nivel de toda la aplicación, que hace que un endpoint sin atributo no esté abierto: una
    /// <c>FallbackPolicy</c> (o <c>SetFallbackPolicy</c>), un <c>AuthorizeFilter</c> global o un
    /// <c>.RequireAuthorization()</c> colgado de <c>MapControllers()</c>/<c>MapRazorPages()</c>. Devuelve qué encontró, o null.
    /// </summary>
    private static async Task<string?> DetectGlobalAuthorizationAsync(IEnumerable<Compilation> compilations, CbmExclusionFilter filter)
    {
        foreach (var compilation in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees.Where(t => !filter.ShouldExclude(t.FilePath)))
            {
                foreach (var node in (await tree.GetRootAsync()).DescendantNodes())
                {
                    switch (node)
                    {
                        case IdentifierNameSyntax { Identifier.ValueText: "FallbackPolicy" or "SetFallbackPolicy" or "AuthorizeFilter" } id:
                            return id.Identifier.ValueText;
                        case GenericNameSyntax { Identifier.ValueText: "AuthorizeFilter" }:
                            return "AuthorizeFilter";
                        case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "RequireAuthorization" } member }
                            when ChainContains(member.Expression, "MapControllers", "MapControllerRoute", "MapDefaultControllerRoute", "MapRazorPages"):
                            return "RequireAuthorization on the controllers";
                    }
                }
            }
        }
        return null;
    }

    private static bool ChainContains(ExpressionSyntax? expression, params string[] names)
    {
        var cur = expression;
        while (cur is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma })
        {
            if (names.Contains(ma.Name.Identifier.ValueText)) return true;
            cur = ma.Expression;
        }
        return false;
    }

    private static AuthInfo MergeAuth(AuthInfo baseAuth, AuthInfo overrideAuth)
    {
        // AllowAnonymous (Required=false) del override anula por completo.
        if (overrideAuth.Required == false)
            return AuthInfo.None with { Required = false };

        bool? required = overrideAuth.Required ?? baseAuth.Required;
        return new AuthInfo(
            required,
            Union(baseAuth.Schemes,  overrideAuth.Schemes),
            Union(baseAuth.Roles,    overrideAuth.Roles),
            Union(baseAuth.Policies, overrideAuth.Policies));
    }

    private static string[]? Union(string[]? a, string[]? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Concat(b).Distinct().ToArray();
    }

    /// <summary>
    /// F5.2: named args <c>Name</c>/<c>Order</c> de un atributo de ruta (<c>[HttpGet(Name="...", Order=n)]</c>,
    /// <c>[Route(..., Name="...")]</c>). Devuelve (null, null) si no están presentes.
    /// </summary>
    private static (string? Name, int? Order) ExtractRouteNaming(AttributeData attr)
    {
        string? name = null; int? order = null;
        foreach (var na in attr.NamedArguments)
        {
            if (na.Key == "Name"  && na.Value.Value is string s) name  = s;
            else if (na.Key == "Order" && na.Value.Value is int o) order = o;
        }
        return (name, order);
    }

    /// <summary>Primer argumento string de un atributo, tolerando <c>params string[]</c> (usa <c>.Values</c> en arrays).</summary>
    private static string? FirstStringArg(AttributeData attr)
    {
        foreach (var ca in attr.ConstructorArguments)
        {
            if (ca.Kind == TypedConstantKind.Primitive && ca.Value is string s)
                return s;
            if (ca.Kind == TypedConstantKind.Array)
                foreach (var el in ca.Values)
                    if (el.Kind == TypedConstantKind.Primitive && el.Value is string es)
                        return es;
        }
        return null;
    }

    private static void AddCsv(List<string> target, string value)
    {
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            target.Add(part);
    }

    private static List<string> ExtractVerbList(ExpressionSyntax expr, SemanticModel model)
    {
        var verbs = new List<string>();
        foreach (var lit in expr.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>())
            if (lit.IsKind(SyntaxKind.StringLiteralExpression))
                verbs.Add(lit.Token.ValueText.ToUpperInvariant());
        return verbs.Distinct().ToList();
    }

    private static string? GetStringConstant(ExpressionSyntax? e, SemanticModel model)
    {
        if (e is null) return null;
        if (e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            return lit.Token.ValueText;
        var cv = model.GetConstantValue(e);
        return cv.HasValue ? cv.Value as string : null;
    }

    private static bool DerivesFromController(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.Name is "ControllerBase" or "Controller")
                return true;
        return false;
    }

    private static string ReplaceControllerToken(string route, string className)
    {
        if (!route.Contains("[controller]")) return route;
        var controllerName = className.EndsWith("Controller")
            ? className[..^"Controller".Length]
            : className;
        return route.Replace("[controller]", controllerName);
    }

    private static string CombinePaths(string basePath, string? methodPath)
    {
        if (string.IsNullOrWhiteSpace(methodPath)) return basePath;
        if (string.IsNullOrEmpty(basePath))        return methodPath;
        return basePath.TrimEnd('/') + '/' + methodPath.TrimStart('/');
    }

    private static string CombineGroupPath(string prefix, string path)
    {
        var p = path.StartsWith('/') ? path : '/' + path;
        if (string.IsNullOrEmpty(prefix)) return p;
        return "/" + prefix.Trim('/') + "/" + p.TrimStart('/');
    }

    // ─── Diagnóstico de rutas huérfanas (genérico; movido desde ConfigBindingEnricher) ──

    private static async Task ReportOrphanRoutesAsync(GraphRepository db, string project, IProgress<string>? progress)
    {
        if (progress is null) return;

        var orphans = (await db.QueryAsync(
            """
            SELECT n.name, n.file_path
            FROM nodes n
            WHERE n.project = @project
              AND n.label = 'Route'
              AND NOT EXISTS (
                  SELECT 1 FROM edges e
                  WHERE e.target_id = n.id AND e.type = 'HANDLES'
              )
            ORDER BY n.file_path, n.name
            """,
            new { project })).ToList();

        if (orphans.Count == 0)
        {
            progress.Report("RouteEnricher: todas las rutas Route tienen arista HANDLES.");
            return;
        }

        progress.Report(
            $"RouteEnricher: {orphans.Count} ruta(s) Route sin arista HANDLES " +
            "(Minimal APIs con handler no resoluble o rutas dinámicas):");

        foreach (var row in orphans)
            progress.Report($"  [huérfana] {row.name}  ({row.file_path})");
    }
}
