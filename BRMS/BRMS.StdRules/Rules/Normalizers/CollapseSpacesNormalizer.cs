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
/// Normalizador que colapsa múltiples espacios internos a un solo espacio y recorta extremos.
/// </summary>
[RuleName("CollapseSpaces")]
[Description(ResourcesKeys.Desc_CollapseSpacesNormalizer_Description)]
[SupportedTypes(RuleInputType.String)]
public partial class CollapseSpacesNormalizer : Normalizer
{
    internal CollapseSpacesNormalizer() { }

    protected override Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("Iniciando CollapseSpacesNormalizer en {PropertyPath}", PropertyPath);

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

                    string? value = token.ToObject<string>();
                    if (value is not string original)
                    {
                        continue;
                    }

                    string normalized = MultipleSpacesRegex().Replace(original, " ").Trim();
                    bool hasChanges = !string.Equals(original, normalized, StringComparison.Ordinal);

                    if (hasChanges)
                    {
                        context.NewValue!.SetValueWithType(path, normalized);
                        Logger.LogInformation("CollapseSpaces normalizó {Path}: '{Original}' -> '{Normalized}'", path, original, normalized);
                        hasAnyChanges = true;
                    }
                    else
                    {
                        Logger.LogDebug("CollapseSpaces no realizó cambios en {Path}", path);
                    }
                }

                return Task.FromResult(new NormalizerResult(this, context, hasChanges: hasAnyChanges));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error ejecutando CollapseSpacesNormalizer en {PropertyPath}", PropertyPath);
                throw;
            }
        }
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex MultipleSpacesRegex();
}
