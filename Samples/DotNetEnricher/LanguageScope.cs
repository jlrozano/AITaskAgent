namespace DotNetEnricher;

/// <summary>
/// Los nodos de los que se ocupan los enriquecedores de este proyecto. En un repositorio con C# y Java, cbm da
/// <c>Class</c>, <c>Method</c> e <c>Interface</c> a los dos lenguajes, y el enriquecimiento de Java (en CodebaseAnalysis.Core)
/// escribe sobre los suyos las mismas propiedades que estos: <c>is_reachable</c> y <c>dead_code_*</c>, <c>detected_issues</c>,
/// <c>todo_markers</c>, <c>commented_code</c>, <c>fan_in</c>, <c>fan_out</c> y <c>hotspot</c>. Los enriquecedores que reinician
/// o recalculan esas marcas sobre todo el proyecto borraban o pisaban las de Java, así que lo hacen solo sobre los nodos que no
/// son de un fichero <c>.java</c>.
/// </summary>
internal static class LanguageScope
{
    /// <summary>Condición SQL de los nodos que no son de Java; <paramref name="alias"/> es el de la tabla <c>nodes</c> (vacío si no tiene).</summary>
    public static string NotJava(string alias = "") =>
        $"COALESCE({(alias.Length == 0 ? "" : alias + ".")}file_path, '') NOT LIKE '%.java'";
}
