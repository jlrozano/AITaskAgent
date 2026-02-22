namespace BRMS.Core.Constants;

/// <summary>
/// Contains structured logging message templates with named parameters.
/// These messages are designed for structured logging with named parameters like {RuleName}, {Error}, etc.
/// </summary>
internal static class LogMessages
{
    // JSON Path Messages
    public static string JsonPathNotFound => "Ruta JSON no encontrada: {Path}";
    public static string JsonConversionError => "Error de conversión JSON en {Path}: {Error}";
    public static string TypeConversionError => "Error de conversión de tipo: esperado {ExpectedType}, recibido {ActualType}";

    // Rule execution messages
    public static string RuleCompleted => "Regla completada: {RuleName} en {ElapsedMs} ms";
    public static string RuleFailed => "Regla fallida: {RuleName}. Error: {Error}";
    public static string RuleSlowExecution => "Ejecución lenta: {RuleName} tardó {ElapsedMs} ms";
    public static string RuleExecutionError => "Error ejecutando regla {RuleName}: {Error}";

    // Email Validation Messages
    public static string EmailDomainVerificationError => "Error verificando dominio de correo: {Domain}";

    // Transformation Messages
    public static string TransformationStarted => "Transformación iniciada: {TransformationName}";
    public static string TransformationCompleted => "Transformación completada: {TransformationName}";
    public static string TransformationFailed => "Transformación fallida: {TransformationName}. Error: {Error}";
    public static string TransformationSourceConverted => "Fuente convertida a tipo {TargetType}";
    public static string TransformationSourceConversionFailed => "Fallo al convertir fuente a {TargetType}. Error: {Error}";
    public static string TransformationNullSource => "Fuente nula para transformación {TransformationName}";
    public static string TransformationNullResult => "Resultado nulo tras transformación {TransformationName}";
}
