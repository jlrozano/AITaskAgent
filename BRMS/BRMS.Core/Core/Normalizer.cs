using System.ComponentModel;
using BRMS.Core.Abstractions;
using BRMS.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BRMS.Core.Core;

/// <summary>
/// Clase base para todos los normalizadores del sistema BRMS.
/// Define la funcionalidad común para normalizar datos JSON.
/// </summary>
public abstract class Normalizer : Rule<NormalizerResult>, INormalizer
{
    /// <summary>
    /// Indica si se debe notificar el cambio realizado por la normalización
    /// </summary>
    [DefaultValue(true)]
    [Description("UI_MustNotifyChange_Description")]
    [JsonProperty("mustNotifyChange")]
    public bool MustNotifyChange { get; init; } = true;

    async Task<INormalizerResult> INormalizer.Invoke(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
#pragma warning disable CS8603 // Possible null reference return.
        return (await ((IRule)this).Invoke(context, cancellationToken)) as INormalizerResult;
#pragma warning restore CS8603 // Possible null reference return.
    }

    /// <summary>
    /// Obtiene los tokens a normalizar, soportando wildcards en arrays [*] y propiedades .*.
    /// Filtra automáticamente por compatibilidad de tipos.
    /// </summary>
    /// <param name="context">Contexto de ejecución BRMS</param>
    /// <returns>Enumerable de tuplas (Token, Path) donde Path es el path específico de cada elemento</returns>
    protected IEnumerable<(Newtonsoft.Json.Linq.JToken Token, string Path)> GetTokensToNormalize(BRMSExecutionContext context)
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
    /// IMPORTANTE: Los tokens Null siempre se consideran compatibles para permitir normalización de nulls.
    /// </summary>
    protected bool IsTokenTypeCompatible(Newtonsoft.Json.Linq.JToken token)
    {
        if (InputTypes.Contains(RuleInputType.Any))
        {
            return true;
        }

        // Los tokens null siempre son compatibles - los normalizers deciden si procesan null o no
        return token.Type == Newtonsoft.Json.Linq.JTokenType.Null || token.Type switch
        {
            Newtonsoft.Json.Linq.JTokenType.String => InputTypes.Contains(RuleInputType.String) ||
                                 InputTypes.Contains(RuleInputType.String_Date),
            Newtonsoft.Json.Linq.JTokenType.Integer => InputTypes.Contains(RuleInputType.Integer) ||
                                  InputTypes.Contains(RuleInputType.Number),
            Newtonsoft.Json.Linq.JTokenType.Float => InputTypes.Contains(RuleInputType.Number),
            Newtonsoft.Json.Linq.JTokenType.Array => InputTypes.Contains(RuleInputType.Array),
            Newtonsoft.Json.Linq.JTokenType.Object => InputTypes.Contains(RuleInputType.Object),
            Newtonsoft.Json.Linq.JTokenType.Boolean => InputTypes.Contains(RuleInputType.Boolean),
            Newtonsoft.Json.Linq.JTokenType.Date => InputTypes.Contains(RuleInputType.String_Date),
            _ => false
        };
    }

    /// <summary>
    /// Genera el path específico reemplazando wildcards con índices/nombres reales.
    /// </summary>
    private string GenerateSpecificPath(string path, Newtonsoft.Json.Linq.JToken token)
    {
        // Para .* necesitamos obtener el nombre de la propiedad del token
        if (path.Contains(".*") && token.Parent is Newtonsoft.Json.Linq.JProperty property)
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
