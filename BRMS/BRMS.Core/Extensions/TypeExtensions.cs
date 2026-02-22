
namespace BRMS.Core.Extensions;



internal static class TypeExtensions
{
    /// <summary>
    /// Devuelve el nombre del tipo sin los sufijos indicados (case-insensitive).
    /// Si hay varios sufijos encadenados, los elimina todos.
    /// </summary>
    public static string GetNameWithoutSuffixes(this Type type, params string[] suffixes)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (suffixes == null || suffixes.Length == 0)
        {
            return type.Name;
        }

        string name = type.Name;
        foreach (string suffix in suffixes)
        {
            if (!string.IsNullOrEmpty(suffix) &&
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }
}
