using System.ComponentModel;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using PhoneNumbers;

namespace BRMS.StdRules.Rules.Normalizers;

/// <summary>
/// Normalizador que convierte números de teléfono a formato E.164 usando libphonenumber.
/// </summary>
[RuleName("PhoneToE164")]
[Description(ResourcesKeys.Desc_E164PhoneNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public class E164PhoneNormalizer : Normalizer
{
    /// <summary>
    /// Código ISO del país (ej. ES, US, MX). Por defecto "ES".
    /// </summary>
    [Description(ResourcesKeys.Desc_E164PhoneNormalizer_RegionIso_Description)]
    public string? RegionIso { get; init; } = "ES";

    internal E164PhoneNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("Iniciando E164PhoneNormalizer en {PropertyPath} (Region: {Region})", PropertyPath, RegionIso);

            try
            {
                IEnumerable<(JToken Token, string Path)> tokensToNormalize = GetTokensToNormalize(context);
                bool hasAnyChanges = false;
                var util = PhoneNumberUtil.GetInstance();
                string region = string.IsNullOrWhiteSpace(RegionIso) ? "ES" : RegionIso!.Trim().ToUpperInvariant();

                foreach ((JToken? token, string? path) in tokensToNormalize)
                {
                    if (token == null || token.Type == JTokenType.Null)
                    {
                        continue;
                    }

                    string? raw = token.ToObject<string>();
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    try
                    {
                        PhoneNumber parsed = util.Parse(raw, region);
                        if (!util.IsPossibleNumber(parsed) || !util.IsValidNumber(parsed))
                        {
                            Logger.LogWarning("Número inválido para región {Region}: {Raw} en {Path}", region, raw, path);
                            continue;
                        }

                        string normalized = util.Format(parsed, PhoneNumberFormat.E164);

                        bool hasChanges = !string.Equals(raw, normalized, StringComparison.Ordinal);
                        if (hasChanges)
                        {
                            context.NewValue!.SetValueWithType(path, normalized);
                            Logger.LogInformation("E164 normalizó {Path}: '{Raw}' -> '{Normalized}'", path, raw, normalized);
                            hasAnyChanges = true;
                        }
                        else
                        {
                            Logger.LogDebug("E164PhoneNormalizer no realizó cambios en {Path}", path);
                        }
                    }
                    catch (NumberParseException ex)
                    {
                        Logger.LogWarning(ex, "No se pudo parsear teléfono '{Raw}' con región {Region} en {Path}", raw, region, path);
                    }
                }

                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error ejecutando E164PhoneNormalizer en {PropertyPath}", PropertyPath);
                throw;
            }
        }
    }
}
