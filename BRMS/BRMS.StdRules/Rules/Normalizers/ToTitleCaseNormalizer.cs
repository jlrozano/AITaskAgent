using System.ComponentModel;
using System.Globalization;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Rules.Normalizers;

/// <summary>
/// Normalizador que convierte un string a formato Title Case respetando cultura especificada.
/// </summary>
[RuleName("ToTitleCase")]
[Description(ResourcesKeys.Desc_ToTitleCaseNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class ToTitleCaseNormalizer : Normalizer
{
    /// <summary>
    /// Código de cultura (por defecto es "es-ES").
    /// </summary>
    [Description(ResourcesKeys.Desc_ToTitleCaseNormalizer_Culture_Description)]
    public string? Culture { get; init; } = "es-ES";

    internal ToTitleCaseNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("Iniciando ToTitleCaseNormalizer en {PropertyPath}", PropertyPath);
            try
            {
                IEnumerable<(JToken Token, string Path)> tokensToNormalize = GetTokensToNormalize(context);
                bool hasAnyChanges = false;
                CultureInfo culture = GetCultureOrDefault(Culture);
                TextInfo textInfo = culture.TextInfo;

                foreach ((JToken? token, string? path) in tokensToNormalize)
                {
                    if (token == null || token.Type == JTokenType.Null)
                    {
                        continue;
                    }

                    string? value = token.ToObject<string>();
                    if (value is not string original)
                    {
                        continue;
                    }

                    string normalized = textInfo.ToTitleCase(original.ToLower(culture));

                    bool hasChanges = !string.Equals(original, normalized, StringComparison.Ordinal);
                    if (hasChanges)
                    {
                        context.NewValue?.SetValueWithType(path, normalized);
                        Logger.LogInformation("ToTitleCase normalizó {Path}: '{Original}' -> '{Normalized}'", path, original, normalized);
                        hasAnyChanges = true;
                    }
                    else
                    {
                        Logger.LogDebug("ToTitleCase no realizó cambios en {Path}", path);
                    }
                }

                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error ejecutando ToTitleCaseNormalizer en {PropertyPath}", PropertyPath);
                throw;
            }
        }
    }

    private static CultureInfo GetCultureOrDefault(string? cultureCode)
    {
        try
        {
            return string.IsNullOrWhiteSpace(cultureCode) ? CultureInfo.GetCultureInfo("es-ES") : CultureInfo.GetCultureInfo(cultureCode);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("es-ES");
        }
    }
}
