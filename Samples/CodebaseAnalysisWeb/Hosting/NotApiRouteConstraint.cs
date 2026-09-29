namespace CodebaseAnalysisWeb.Hosting;

/// <summary>Restricción de ruta: el valor no empieza por <c>api/</c>. Deja la SPA fuera de la API.</summary>
public sealed class NotApiRouteConstraint : IRouteConstraint
{
    public const string Name = "notapi";

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
    {
        var value = values.TryGetValue(routeKey, out var raw) ? raw?.ToString() : null;
        return value == null
            || !(value.Equals("api", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("api/", StringComparison.OrdinalIgnoreCase));
    }
}
