namespace BRMS.Core.Attributes;

/// <summary>
/// Atributo para especificar el nombre único de una regla.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RuleNameAttribute(string name) : Attribute
{
    /// <summary>
    /// Obtiene el nombre único de la regla.
    /// </summary>
    public string Name { get; } = name;
}
