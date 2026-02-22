using System.ComponentModel;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Rules.Validators;


/// <summary>
/// Validador que verifica que la longitud del texto esté dentro del rango especificado.
/// </summary>
[RuleName("TextLength")]
[Description(ResourcesKeys.Desc_TextLengthValidator_Description)]
[SupportedTypes(RuleInputType.String)]
public class TextLengthValidator : Validator
{
    [Description(ResourcesKeys.Desc_TextLengthValidator_MinLength_Description)]
    public int? MinLength { get; init; }

    [Description(ResourcesKeys.Desc_TextLengthValidator_MaxLength_Description)]
    public int? MaxLength { get; init; }

    internal TextLengthValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
        var errors = new List<string>();

        foreach ((JToken? token, string? path) in tokensToValidate)
        {
            string value = token?.ToObject<string>() ?? "";
            Logger.LogDebug("**Procesando campo {Path}. MinLength: {MinLength}  MaxLength {MaxLength}. Value length: {ValueLength} ", path, MinLength, MaxLength, value.Length);

            if (MinLength.HasValue && value.Length < MinLength.Value)
            {
                errors.Add($"{path}: {ErrorMessage ?? $"Longitud menor que {MinLength}"}");
            }
            else if (MaxLength.HasValue && value.Length > MaxLength.Value)
            {
                errors.Add($"{path}: {ErrorMessage ?? $"Longitud mayor que {MaxLength}"}");
            }
        }

        IRuleResult result = errors.Count > 0
            ? new RuleResult(this, context, string.Join("; ", errors))
            : new RuleResult(this, context);

        return Task.FromResult(result);
    }
}
