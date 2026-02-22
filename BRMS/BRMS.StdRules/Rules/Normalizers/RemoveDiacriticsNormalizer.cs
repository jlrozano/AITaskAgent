using System.ComponentModel;
using System.Globalization;
using System.Text;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Rules.Normalizers;

/// <summary>
/// Normalizador que elimina diacríticos (acentos y marcas) de cadenas. Opcionalmente preserva ñ/Ñ.
/// </summary>
[RuleName("RemoveDiacritics")]
[Description(ResourcesKeys.Desc_RemoveDiacriticsNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class RemoveDiacriticsNormalizer : Normalizer
{
    /// <summary>
    /// Si es true, preserva los caracteres ñ/Ñ. Por defecto true.
    /// </summary>
    [Description(ResourcesKeys.Desc_RemoveDiacriticsNormalizer_PreserveEnie_Description)]
    public bool PreserveEnie { get; init; } = true;

    internal RemoveDiacriticsNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("Iniciando RemoveDiacriticsNormalizer en {PropertyPath}", PropertyPath);
            try
            {
                IEnumerable<(JToken Token, string Path)> tokensToNormalize = GetTokensToNormalize(context);
                bool hasAnyChanges = false;

                foreach ((JToken? token, string? path) in tokensToNormalize)
                {
                    if (token == null || token.Type == JTokenType.Null)
                    {
                        continue;
                    }

                    string value = token.ToObject<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    string normalized = RemoveDiacritics(value, PreserveEnie);
                    bool hasChanges = !string.Equals(value, normalized, StringComparison.Ordinal);
                    if (hasChanges)
                    {
                        context.NewValue!.SetValueWithType(path, normalized);
                        Logger.LogInformation("RemoveDiacritics normalizó {Path}: '{Original}' -> '{Normalized}'", path, value, normalized);
                        hasAnyChanges = true;
                    }
                    else
                    {
                        Logger.LogDebug("RemoveDiacritics no realizó cambios en {Path}", path);
                    }
                }

                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error ejecutando RemoveDiacriticsNormalizer en {PropertyPath}", PropertyPath);
                throw;
            }
        }
    }

    private static string RemoveDiacritics(string input, bool preserveEnie)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        const char enieLowerPlaceholder = '\uF000';
        const char enieUpperPlaceholder = '\uF001';

        string working = input;
        if (preserveEnie)
        {
            working = working.Replace('ñ', enieLowerPlaceholder).Replace('Ñ', enieUpperPlaceholder);
        }

        string normalized = working.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (char ch in normalized)
        {
            UnicodeCategory uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (preserveEnie && (ch == enieLowerPlaceholder || ch == enieUpperPlaceholder))
            {
                sb.Append(ch == enieLowerPlaceholder ? 'ñ' : 'Ñ');
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
