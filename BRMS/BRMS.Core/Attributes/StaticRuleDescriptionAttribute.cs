namespace BRMS.Core.Attributes;

/// <summary>
/// Atributo para marcar un método estático que devuelve RuleDescription.
/// Este método será ejecutado automáticamente por el RuleManager para obtener
/// la descripción de la regla sin necesidad de instanciar la clase.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class StaticRuleDescriptionAttribute : Attribute
{
    /// <summary>
    /// Inicializa una nueva instancia del atributo StaticRuleDescriptionAttribute.
    /// </summary>
    public StaticRuleDescriptionAttribute()
    {
    }
}
