using System.ComponentModel;
using System.Globalization;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Rules.Validators;

/// <summary>
/// Validador para asegurar que una fecha esté dentro de un rango especificado.
/// </summary>
[RuleName("RangeDate")]
[Description(ResourcesKeys.Desc_RangeDateValidator_Description)]
[SupportedTypes(RuleInputType.String_Date)]
public class RangeDateValidator : Validator
{
    // Propiedades que pueden ser DateTime fijos o expresiones string
    [Description(ResourcesKeys.Desc_RangeDateValidator_MinValue_Description)]
    public required string Min { get; init; }

    [Description(ResourcesKeys.Desc_RangeDateValidator_MaxValue_Description)]
    public required string Max { get; init; }

    /// <summary>
    /// Indica si se permite valor null. Si es false (por defecto), un valor null fallará la validación.
    /// </summary>
    [Description(ResourcesKeys.Desc_Validator_AllowNull_Description)]
    public bool AllowNull { get; init; } = false;

    //// Regex para parsear expresiones como "Today-40y", "Now+3m", "StartOfYear-1d"
    //private static readonly Regex DateExpressionRegex = new Regex(
    //    @"^(?<anchor>Today|Now|StartOfYear|StartOfMonth|StartOfWeek|EndOfYear|EndOfMonth|EndOfWeek)(?:(?<operation>[+-])(?<amount>\d+)(?<unit>[yMwdhms]))?$",
    //    RegexOptions.IgnoreCase | RegexOptions.Compiled
    //);

    public RangeDateValidator() { }

    protected override Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Min))
        {
            return Task.FromResult<IRuleResult>(RuleResult.Fail(this, context, "El valor 'Min' no puede estar vacío"));
        }

        if (string.IsNullOrWhiteSpace(Max))
        {
            return Task.FromResult<IRuleResult>(RuleResult.Fail(this, context, "El valor 'Max' no puede estar vacío"));
        }

        // Evaluar Min y Max una sola vez
        DateTime? minDate = EvaluateDate(Min);
        DateTime? maxDate = EvaluateDate(Max);

        IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
        var errors = new List<string>();

        foreach ((JToken? token, string? path) in tokensToValidate)
        {
            string? value = token?.ToObject<string>();

            if (string.IsNullOrWhiteSpace(value))
            {
                if (!AllowNull)
                {
                    string errorMessage = ErrorMessage ?? "El valor de fecha no puede estar vacío";
                    errors.Add($"{path}: {errorMessage}");
                }
                continue;
            }

            bool parseResult = DateTime.TryParse(value, out DateTime date);

            if (!parseResult)
            {
                errors.Add($"{path}: El valor no es una fecha válida");
                continue;
            }

            DateTime dateValue = date;
            Logger.LogInformation("Successfully parsed '{Value}' as DateTime: {DateValue}", value, dateValue);

            // Verificar rango
            if (minDate.HasValue && dateValue < minDate.Value)
            {
                Logger.LogInformation("Date {DateValue} is before minimum {MinDate}, returning failure", dateValue, minDate.Value);
                errors.Add($"{path}: La fecha {dateValue:yyyy-MM-dd} es anterior al mínimo permitido {minDate.Value:yyyy-MM-dd}");
                continue;
            }

            if (maxDate.HasValue && dateValue > maxDate.Value)
            {
                Logger.LogInformation("Date {DateValue} is after maximum {MaxDate}, returning failure", dateValue, maxDate.Value);
                errors.Add($"{path}: La fecha {dateValue:yyyy-MM-dd} es posterior al máximo permitido {maxDate.Value:yyyy-MM-dd}");
                continue;
            }

            Logger.LogInformation("Date {DateValue} is within range, returning success", dateValue);
        }

        return errors.Count > 0
            ? Task.FromResult<IRuleResult>(RuleResult.Fail(this, context, string.Join("; ", errors)))
            : Task.FromResult<IRuleResult>(RuleResult.Ok(this, context));
    }

    /// <summary>
    /// Evalúa una fecha que puede ser un DateTime fijo o una expresión dinámica
    /// </summary>
    /// <param name="dateValue">DateTime fijo o string con expresión</param>
    /// <returns>DateTime evaluado o null si dateValue es null</returns>
    private static DateTime? EvaluateDate(object dateValue)
    {
        return dateValue switch
        {
            null => null,
            DateTime fixedDate => fixedDate,
            string expression => ParseDateExpression(expression),
            _ => throw new ArgumentException($"Tipo de fecha no soportado: {dateValue?.GetType()}")
        };
    }

    private static (DateTime, string, string) SplitDate(string dateStr)
    {

        string textDate = "";
        string amount = "";
        string unit = "";
        int mode = 1;
        foreach (char letter in dateStr)
        {
            if (letter == ' ')
            {
                continue;
            }

            switch (mode)
            {
                case 1:
                    if (char.IsLetter(letter))
                    {
                        textDate += char.ToLower(letter);
                    }
                    else
                    {
                        mode = 2;
                        amount += letter;
                    }
                    ;
                    break;
                case 2:
                    if (char.IsDigit(letter))
                    {
                        amount += letter;
                    }
                    else
                    {
                        mode = 3;
                        unit += char.ToLower(letter);
                    }
                    break;

            }
            ;
            if (mode == 3)
            {
                break;
            }
        }

        return (GetBaseDate(textDate), string.IsNullOrEmpty(amount) ? "0" : amount, unit);
    }

    /// <summary>
    /// Parsea expresiones dinámicas de fecha
    /// Formatos soportados:
    /// - Today±Ny (años): Today-40y, Today+5y
    /// - Today±NM (meses): Today-3M, Today+6M  
    /// - Today±Nw (semanas): Today-2w, Today+1w
    /// - Today±Nd (días): Today-30d, Today+7d
    /// - Today±Nh (horas): Today-12h, Today+24h
    /// - Today±Nm (minutos): Today-30m, Today+45m
    /// - Today±Ns (segundos): Today-60s, Today+30s
    /// - Now±N[unit] (igual que Today pero con hora actual)
    /// - StartOf/EndOf Year/Month/Week ± N[unit]
    /// </summary>
    private static DateTime ParseDateExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("Expresión de fecha vacía");
        }

        string token = expression.Trim();
        if (token.Length < 4)
        {
            throw new ArgumentException($"Expresión de fecha no válida: {expression}");
        }

        // si empieza por un numero, es una fecha
        if (char.IsDigit(token[0]))
        {
            // Intentar parsear como fecha fija
            return DateTime.TryParse(expression, out DateTime fixedDate)
                ? fixedDate
                : throw new ArgumentException($"Expresión de fecha no válida: {expression}");
        }

        (DateTime baseDate, string? amount, string? unit) = SplitDate(token);

        if (amount == "0")
        {
            return baseDate;
        }

        if (!int.TryParse(amount, out int amountInt))
        {
            throw new ArgumentException($"Expresión de fecha no válida: {expression}");
        }
        ;

        return ApplyDateOperation(baseDate, amountInt, unit);

    }

    /// <summary>
    /// Obtiene la fecha base según el tipo especificado
    /// </summary>
    private static DateTime GetBaseDate(string baseType)
    {
        DateTime now = DateTime.Now;

        return baseType switch
        {
            "today" => now.Date, // Solo fecha, sin hora
            "now" => now, // Fecha y hora actual
            "startofyear" => new DateTime(now.Year, 1, 1),
            "startofmonth" => new DateTime(now.Year, now.Month, 1),
            "startofweek" => GetStartOfWeek(now),
            "endofyear" => new DateTime(now.Year, 12, 31, 23, 59, 59),
            "endofmonth" => new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 59, 59),
            "endofweek" => GetEndOfWeek(now),
            _ => throw new ArgumentException($"Tipo de fecha base no reconocido: {baseType}")
        };
    }

    /// <summary>
    /// Aplica la operación de fecha según la unidad especificada
    /// </summary>
    private static DateTime ApplyDateOperation(DateTime baseDate, int amount, string unit)
    {
        return unit switch
        {
            "y" => baseDate.AddYears(amount),
            "M" => baseDate.AddMonths(amount), // M mayúscula para meses
            "w" => baseDate.AddDays(amount * 7),
            "d" => baseDate.AddDays(amount),
            "h" => baseDate.AddHours(amount),
            "m" => baseDate.AddMinutes(amount), // m minúscula para minutos
            "s" => baseDate.AddSeconds(amount),
            _ => throw new ArgumentException($"Unidad de tiempo no reconocida: {unit}")
        };
    }
    /// <summary>
    /// Obtiene el inicio de la semana (lunes)
    /// </summary>
    private static DateTime GetStartOfWeek(DateTime date)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        DayOfWeek firstDayOfWeek = culture.DateTimeFormat.FirstDayOfWeek;
        int diff = (7 + (date.DayOfWeek - firstDayOfWeek)) % 7;
        return date.AddDays(-diff).Date;
    }

    /// <summary>
    /// Obtiene el final de la semana (domingo)
    /// </summary>
    private static DateTime GetEndOfWeek(DateTime date)
    {
        return GetStartOfWeek(date).AddDays(6).AddHours(23).AddMinutes(59).AddSeconds(59);
    }
}

// Ejemplos de uso:
/*
// Configuraciones de ejemplo:

// Fecha fija tradicional
var validator1 = new RangeDateValidator 
{ 
    Min = new DateTime(2020, 1, 1), 
    Max = new DateTime(2030, 12, 31) 
};

// Expresiones dinámicas
var validator2 = new RangeDateValidator 
{ 
    Min = "Today-40y",        // 40 años atrás desde hoy
    Max = "Today+5y"          // 5 años adelante desde hoy
};

var validator3 = new RangeDateValidator 
{ 
    Min = "StartOfYear",      // Inicio del año actual
    Max = "EndOfYear+1y"      // Final del próximo año
};

var validator4 = new RangeDateValidator 
{ 
    Min = "Today-3M",         // 3 meses atrás (M mayúscula para meses)
    Max = "Today+6w"          // 6 semanas adelante
};

var validator5 = new RangeDateValidator 
{ 
    Min = "Now-24h",          // 24 horas atrás
    Max = "Now+12h"           // 12 horas adelante
};
*/
