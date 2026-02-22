

using BRMS.Core.Abstractions;

namespace BRMS.Core.Models;

/// <summary>
/// Resultado de la ejecución de una regla de transformación de datos
/// </summary>
public class DataTransformResult(IDataTransform rule, BRMSExecutionContext context, BRMSExecutionContext? outputContext, string? errorMessage = null) : RuleResult(rule, context, errorMessage), ITransformResult
{
    public BRMSExecutionContext? OutputContext { get; } = outputContext;

    public static DataTransformResult Ok(IDataTransform rule, BRMSExecutionContext context, BRMSExecutionContext outputContext) =>
            new(rule, context, outputContext);
    public static DataTransformResult Fail(IDataTransform rule, BRMSExecutionContext context, string errorMessage) =>
        new(rule, context, null, string.IsNullOrWhiteSpace(errorMessage) ? "Error" : errorMessage);
    public static DataTransformResult Fail(IDataTransform rule, BRMSExecutionContext context, Exception? ex, string? alternateErrorMessage = null)
    {
        alternateErrorMessage ??= ex?.Message ?? "Error";
        return new(rule, context, null, alternateErrorMessage)
        {
            Exception = ex
        };
    }
}
