using System.ComponentModel;
using System.Text.RegularExpressions;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules;

/// <summary>
/// Normalizador que realiza limpieza avanzada de cadenas con múltiples reglas de transformación.
/// </summary>

[RuleName("AdvancedClean")]
[Description(ResourcesKeys.Desc_AdvancedCleanNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public partial class AdvancedCleanNormalizer : Normalizer
{
    internal AdvancedCleanNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del AdvancedCleanNormalizer** - El normalizador de limpieza avanzada está comenzando su proceso de saneamiento de texto");

            try
            {
                Logger.LogDebug("**Procesando campo con AdvancedCleanNormalizer** - Aplicando reglas de limpieza avanzada al campo actual");

                IEnumerable<(JToken Token, string Path)> tokensToNormalize = GetTokensToNormalize(context);
                bool hasAnyChanges = false;

                foreach ((JToken? token, string? path) in tokensToNormalize)
                {
                    string? value = token?.ToObject<string>();

                    if (value == null)
                    {
                        Logger.LogDebug("Valor nulo encontrado para {Path}, no se requiere normalización", path);
                        continue;
                    }

                    string? normalizedValue = PerformAdvancedCleaning(value);

                    bool hasChanges = !string.Equals(value, normalizedValue, StringComparison.Ordinal);
                    if (hasChanges)
                    {
                        if (string.IsNullOrEmpty(normalizedValue))
                        {
                            normalizedValue = null;
                            Logger.LogInformation("Valor normalizado a null después de limpieza para {Path}", path);
                        }
                        else
                        {
                            Logger.LogInformation("Normalización completada para {Path}: '{OriginalValue}' -> '{NormalizedValue}'", path, value, normalizedValue);
                        }

                        context.NewValue?.SetValueWithType(path, normalizedValue);
                        hasAnyChanges = true;
                    }
                }

                Logger.LogInformation("**Ejecución del AdvancedCleanNormalizer completada exitosamente** - El proceso de limpieza avanzada finalizó sin errores");
                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del AdvancedCleanNormalizer** - Ocurrió un problema durante el proceso de limpieza avanzada");
                throw;
            }
        }
    }

    // Precompiled regex patterns for better performance
    [GeneratedRegex(@"(^#|(?<=\s)#|#$)")]
    private static partial Regex HashAtBoundariesRegex();

    [GeneratedRegex(@"(?<=[a-zA-Z])#(?=[a-zA-Z])")]
    private static partial Regex HashBetweenLettersRegex();

    [GeneratedRegex(@"#\s+")]
    private static partial Regex HashWithSpacesRegex();

    [GeneratedRegex(@"\.{2,}")]
    private static partial Regex MultipleDotsPatter();

    [GeneratedRegex(@"^[.,]+$")]
    private static partial Regex OnlyDotsCommasRegex();

    [GeneratedRegex(@"^[\.\s]+")]
    private static partial Regex StartingDotsSpacesRegex();

    [GeneratedRegex(@"^(.)\1+$")]
    private static partial Regex RepeatedCharacterRegex();

    [GeneratedRegex(@"[,:¿*\\|]")]
    private static partial Regex CharsToDotRegex();

    [GeneratedRegex(@"[-_]")]
    private static partial Regex CharsToSpaceRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpacesRegex();

    private static string? PerformAdvancedCleaning(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        string result = input;

        // 1. Handle '#' character at beginning, after space, or at end - completely remove it
        result = HashAtBoundariesRegex().Replace(result, "");

        // 2. Handle '#' character between letters - replace with 'ñ'
        result = HashBetweenLettersRegex().Replace(result, "ñ");

        // 3. Handle '#' at end followed by space - convert to single space
        result = HashWithSpacesRegex().Replace(result, " ");

        // 4. Remove or reduce sequences of dots (...) to single dot
        result = MultipleDotsPatter().Replace(result, ".");

        // 5. Convert short strings (1-3 chars) of only dots/commas to NULL
        if (result.Length >= 1 && result.Length <= 3 && OnlyDotsCommasRegex().IsMatch(result))
        {
            return null;
        }

        // 6. Remove dots/spaces at beginning of string
        result = StartingDotsSpacesRegex().Replace(result, "");

        // 7. Convert repeated character strings (>3 chars) to NULL
        if (result.Length > 3 && RepeatedCharacterRegex().IsMatch(result))
        {
            return null;
        }

        // 8. Replace specific characters
        // Convert to dot: , : ¿ * \ |
        result = CharsToDotRegex().Replace(result, ".");
        // Convert to space: - _
        result = CharsToSpaceRegex().Replace(result, " ");
        // Remove pattern "¡y]"
        result = result.Replace("¡y]", "");

        // 9. Consolidate multiple spaces to single space and trim whitespace
        result = MultipleSpacesRegex().Replace(result, " ").Trim();

        return result;
    }
}
