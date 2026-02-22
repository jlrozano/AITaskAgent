using System.Reflection;
using System.Text.RegularExpressions;
using PhoneNumbers;

namespace BRMS.Core.Extensions;

/// <summary>
/// Métodos de extensión para normalización y validación de entradas comunes (teléfono, email, documento).
/// Centraliza las reglas de normalización para uso transversal.
/// </summary>
public static class NormalizationExtensions
{
    // Regex de email razonable (no cubre todos los casos de RFC pero evita entradas inválidas comunes)
#pragma warning disable SYSLIB1045
    private static readonly Regex EmailRegex = new(
        pattern: @"^[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}$",
        options: RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
#pragma warning restore SYSLIB1045

    /// <summary>
    /// Normaliza un email: trim, lower-case y validación básica por regex.
    /// Devuelve null si no es válido.
    /// </summary>
    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        string normalized = email.Trim().ToLowerInvariant();
        return EmailRegex.IsMatch(normalized) ? normalized : null;
    }

    /// <summary>
    /// Normaliza un teléfono usando libphonenumber-csharp.
    /// Devuelve E.164 si es posible. Si el parse falla, devuelve una versión sanitizada (dígitos y '+') o null si queda vacía.
    /// </summary>
    public static string? NormalizePhone(string? input, string defaultRegion = "ES", bool strictValidation = false)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var phoneUtil = PhoneNumberUtil.GetInstance();
        try
        {
            PhoneNumber number = phoneUtil.Parse(input, defaultRegion);
            return strictValidation && !phoneUtil.IsValidNumber(number) ? null : phoneUtil.Format(number, PhoneNumberFormat.E164);
        }
        catch (NumberParseException)
        {
            // Fallback: sanitizar
            string sanitized = new([.. input.Where(c => char.IsDigit(c) || c == '+')]);
            return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
        }
    }

    /// <summary>
    /// Normaliza el tipo de documento a MAYÚSCULAS (sin validación específica).
    /// </summary>
    public static string? NormalizeDocumentType(string? tipo)
    {
        return string.IsNullOrWhiteSpace(tipo) ? null : tipo.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Normaliza y opcionalmente valida un documento de identidad por país (ISO como ESP, FRA, GBR, USA).
    /// Intenta usar dinámicamente la librería NationalDocumentValidator si está disponible en el AppDomain.
    /// Si la librería no está disponible o la validación falla, devuelve null; en caso de éxito, devuelve el valor normalizado.
    /// </summary>
    public static string? NormalizeDocumentNumber(string? number, string? countryIso = "ESP", bool validate = true)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }
        // Normalizamos: quitamos espacios y separadores, dejamos solo letras y dígitos y pasamos a MAYÚSCULAS
        string normalized = new([.. number.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit)]);
        if (!validate)
        {
            return normalized;
        }

        try
        {
            Assembly? asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name?.Equals("NationalDocumentValidator", StringComparison.OrdinalIgnoreCase) ?? false);

            if (asm == null)
            {
                return normalized; // librería no disponible, devolvemos normalizado sin validar
            }

            Type? countriesType = asm.GetType("NationalDocumentValidator.Countries");
            Type? factoryType = asm.GetType("NationalDocumentValidator.DocumentValidatorFactory");
            if (countriesType == null || factoryType == null)
            {
                return normalized;
            }

            string iso = (countryIso ?? "ESP").Trim().ToUpperInvariant();
            object? enumValue = null;
            try
            {
                enumValue = Enum.Parse(countriesType, iso, ignoreCase: true);
            }
            catch
            {
                return normalized; // país no reconocido por la librería
            }

            MethodInfo? getValidatorMethod = factoryType.GetMethod("GetValidator", [countriesType]);
            if (getValidatorMethod == null)
            {
                return normalized;
            }

            object? validator = getValidatorMethod.Invoke(null, [enumValue]);
            if (validator == null)
            {
                return normalized;
            }

            MethodInfo? isValidMethod = validator.GetType().GetMethod("IsValid", [typeof(string)]);
            if (isValidMethod == null)
            {
                return normalized;
            }

            object? isValid = isValidMethod.Invoke(validator, [normalized]);
            return isValid is bool b && b ? normalized : null;
        }
        catch
        {
            // En caso de cualquier excepción, devolvemos el valor normalizado (sin validación)
            return normalized;
        }
    }
}
