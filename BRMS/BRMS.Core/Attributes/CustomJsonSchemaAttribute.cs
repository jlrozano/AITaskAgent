using NJsonSchema;

namespace BRMS.Core.Attributes;

/// <summary>
/// Atributo abstracto que permite a las reglas personalizar su esquema JSON
/// después de que se genere el esquema base.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public abstract class CustomJsonSchemaAttribute : Attribute
{
    /// <summary>
    /// Personaliza el esquema JSON base generado para una regla.
    /// </summary>
    /// <param name="baseSchema">El esquema base generado automáticamente</param>
    /// <param name="ruleType">El tipo de la regla que está siendo procesada</param>
    public abstract void CustomizeSchema(JsonSchema baseSchema, Type ruleType);
}
