using BRMS.Core.Abstractions;
using BRMS.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.Core.Core;

/// <summary>
/// Clase base para todos los validadores del sistema BRMS.
/// Define la funcionalidad común para validar datos JSON.
/// </summary>
public abstract class Validator : Rule<IRuleResult>, IValidator
{
    /// <summary>
    /// Obtiene los tokens a validar, soportando wildcards en arrays [*] y propiedades .*.
    /// Filtra automáticamente por compatibilidad de tipos.
    /// </summary>
    /// <param name="context">Contexto de ejecución BRMS</param>
    /// <returns>Enumerable de tuplas (Token, Path) donde Path es el path específico de cada elemento</returns>
    protected IEnumerable<(JToken Token, string Path)> GetTokensToValidate(BRMSExecutionContext context)
    {
        IEnumerable<JToken> tokens = context.NewValue?.SelectTokens(PropertyPath) ?? [];
        var tokenList = tokens.ToList();

        // Si no hay wildcards, comportamiento original
        if (!PropertyPath.Contains("[*]") && !PropertyPath.Contains(".*"))
        {
            if (tokenList.Count > 0 && IsTokenTypeCompatible(tokenList[0]))
            {
                yield return (tokenList[0], PropertyPath);
            }
            yield break;
        }

        // Procesar tokens con wildcards
        for (int i = 0; i < tokenList.Count; i++)
        {
            JToken token = tokenList[i];

            // Filtrar por compatibilidad de tipo
            if (!IsTokenTypeCompatible(token))
            {
                Logger.LogDebug("Saltando token en índice {Index} - tipo {TokenType} incompatible con tipos soportados {SupportedTypes}",
                    i, token.Type, string.Join(", ", InputTypes));
                continue;
            }

            // Generar path específico
            string specificPath = GenerateSpecificPath(PropertyPath, token);
            yield return (token, specificPath);
        }
    }

    /// <summary>
    /// Verifica si el tipo del token es compatible con los tipos soportados por esta regla.
    /// IMPORTANTE: Los tokens Null siempre se consideran compatibles para permitir validación de nulls.
    /// </summary>
    protected bool IsTokenTypeCompatible(JToken token)
    {
        if (InputTypes.Contains(RuleInputType.Any))
        {
            return true;
        }

        // Los tokens null siempre son compatibles - los validadores deciden si aceptan null o no
        return token.Type == JTokenType.Null || token.Type switch
        {
            JTokenType.String => InputTypes.Contains(RuleInputType.String) ||
                                 InputTypes.Contains(RuleInputType.String_Date),
            JTokenType.Integer => InputTypes.Contains(RuleInputType.Integer) ||
                                  InputTypes.Contains(RuleInputType.Number),
            JTokenType.Float => InputTypes.Contains(RuleInputType.Number),
            JTokenType.Array => InputTypes.Contains(RuleInputType.Array),
            JTokenType.Object => InputTypes.Contains(RuleInputType.Object),
            JTokenType.Boolean => InputTypes.Contains(RuleInputType.Boolean),
            JTokenType.Date => InputTypes.Contains(RuleInputType.String_Date),
            _ => false
        };
    }

    /// <summary>
    /// Genera el path específico reemplazando wildcards con índices/nombres reales.
    /// </summary>
    private string GenerateSpecificPath(string path, JToken token)
    {
        // Para .* necesitamos obtener el nombre de la propiedad del token
        if (path.Contains(".*") && token.Parent is JProperty property)
        {
            return path.Replace(".*", $".{property.Name}");
        }

        // Para [*] necesitamos obtener el path real del token
        if (path.Contains("[*]"))
        {
            // Obtener el path real del token y agregar $ si no lo tiene
            string tokenPath = token.Path;
            return tokenPath.StartsWith("$") ? tokenPath : $"$.{tokenPath}";
        }

        return path;
    }
}


