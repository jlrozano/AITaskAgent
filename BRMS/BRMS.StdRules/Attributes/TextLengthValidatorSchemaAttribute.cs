using BRMS.Core.Attributes;
using NJsonSchema;

namespace BRMS.StdRules.Attributes;

/// <summary>
/// Personaliza el esquema JSON del TextLengthValidator para asegurar que
/// las propiedades de longitud sean números positivos.
/// </summary>
public sealed class TextLengthValidatorSchemaAttribute : CustomJsonSchemaAttribute
{
    public override void CustomizeSchema(JsonSchema baseSchema, Type ruleType)
    {
        // Asegurar que minLength y maxLength son números positivos
        if (baseSchema.Properties.TryGetValue("minLength", out JsonSchemaProperty? minLengthSchema))
        {
            minLengthSchema.Minimum = 0;
        }

        if (baseSchema.Properties.TryGetValue("maxLength", out JsonSchemaProperty? maxLengthSchema))
        {
            maxLengthSchema.Minimum = 0;
        }
    }
}
