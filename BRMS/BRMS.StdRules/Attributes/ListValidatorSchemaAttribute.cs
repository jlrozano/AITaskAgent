using BRMS.Core.Attributes;
using NJsonSchema;

namespace BRMS.StdRules.Attributes;

/// <summary>
/// Personaliza el esquema JSON del ListValidator para asegurar que las listas
/// no contengan duplicados.
/// </summary>
public sealed class ListValidatorSchemaAttribute : CustomJsonSchemaAttribute
{
    public override void CustomizeSchema(JsonSchema baseSchema, Type ruleType)
    {
        // Asegurar que contains y notContains son arrays sin duplicados
        if (baseSchema.Properties.TryGetValue("contains", out JsonSchemaProperty? containsSchema))
        {
            containsSchema.UniqueItems = true;
        }

        if (baseSchema.Properties.TryGetValue("notContains", out JsonSchemaProperty? notContainsSchema))
        {
            notContainsSchema.UniqueItems = true;
        }
    }
}
