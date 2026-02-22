using System.ComponentModel;
using System.Text.RegularExpressions;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using BRMS.StdRules.Modules.Database;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.DataBase;

/// <summary>
/// Validador que utiliza consultas SQL para validar valores de campos.
/// Permite ejecutar consultas SQL personalizadas contra una base de datos configurada
/// para validar valores según reglas de negocio específicas almacenadas en la base de datos.
/// 
/// Soporta los siguientes parámetros en las consultas SQL:
/// - @Value: Se reemplaza con el valor actual del campo
/// - {oldValue.Propiedad}: Se reemplaza con propiedades del objeto original
/// - {newValue.Propiedad}: Se reemplaza con propiedades del objeto actual
/// 
/// La consulta SQL debe retornar un valor que indique si la validación es exitosa.
/// </summary>
[RuleName("DatabaseValidator")]
[Description(ResourcesKeys.Desc_DatabaseValidator_Description)]
[SupportedTypes(RuleInputType.Any)]
[Scoped]
public partial class DatabaseValidator : Validator
{
    private readonly IDataBaseQuery _dataBaseQuery;

    /// <summary>
    /// Consulta SQL que se ejecutará para validar el valor del campo.
    /// Puede contener parámetros como @Value, {oldValue.Propiedad}, {newValue.Propiedad}.
    /// La consulta debe retornar un valor que indique si la validación es exitosa (true/false, 1/0, etc.).
    /// </summary>
    /// <example>
    /// Ejemplos de uso:
    /// - "SELECT COUNT(*) > 0 FROM tabla WHERE codigo = @Value" - Verifica existencia
    /// - "SELECT @Value BETWEEN 1 AND 100" - Valida rango
    /// - "SELECT CASE WHEN {newValue.Tipo} = 'A' THEN @Value > 0 ELSE 1 END" - Validación condicional
    /// </example>
    [Description(ResourcesKeys.Desc_DatabaseValidator_SQLSmtp_Description)]
    public required string SQLSmtp { get; init; }

    /// <summary>
    /// Oculta la propiedad PropertyPath heredada para evitar confusión en la configuración.
    /// </summary>
    [JsonIgnore]
    public new string PropertyPath { get => base.PropertyPath; set => base.PropertyPath = "$"; }

    public DatabaseValidator(IDataBaseQuery dataBaseQuery)
    {
        _dataBaseQuery = dataBaseQuery ?? throw new ArgumentNullException(nameof(dataBaseQuery));
        base.PropertyPath = "$";
    }
    /// <summary>
    /// Alias de la base de datos sobre la que se realizará la consulta.
    /// Este nombre debe coincidir con una conexión configurada en el sistema.
    /// </summary>
    [Description(ResourcesKeys.Desc_Database_DataBaseName_Description)]
    public required string DataBaseName { get; init; }

    protected override async Task<IRuleResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del DatabaseValidator** - El validador está comenzando la ejecución de la consulta SQL de validación");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                if (string.IsNullOrWhiteSpace(SQLSmtp))
                {
                    string errorMessage = "La consulta SQL (SQLSmtp) no puede estar vacía";
                    Logger.LogError("Consulta SQL vacía para PropertyPath {PropertyPath}", PropertyPath);
                    return new RuleResult(this, context, errorMessage);
                }

                Logger.LogDebug("**Procesando campo con DatabaseValidator** - Ejecutando consulta SQL para validar el valor del campo");

                IEnumerable<(JToken Token, string Path)> tokensToValidate = GetTokensToValidate(context);
                var errors = new List<string>();

                foreach ((JToken? token, string? path) in tokensToValidate)
                {
                    object? value = token?.ToObject<object>();

                    // Reemplazar parámetros en la consulta SQL
                    string processedSQL = ReplaceParameters(SQLSmtp, value?.ToString() ?? "NULL", context);

                    Logger.LogDebug("Ejecutando consulta SQL: {Query} para {Path}", processedSQL, path);

                    // Ejecutar la consulta de validación
                    bool isValid = await _dataBaseQuery.IsValid(DataBaseName, processedSQL);

                    if (isValid)
                    {
                        Logger.LogDebug("Validación de base de datos exitosa para {Path}", path);
                    }
                    else
                    {
                        string errorMessage = ErrorMessage ?? $"El valor no cumple con los criterios de validación de la base de datos";
                        Logger.LogInformation("Validación de base de datos falló para {Path}: {Value}", path, value);
                        errors.Add($"{path}: {errorMessage}");
                    }
                }

                IRuleResult result = errors.Count > 0
                    ? new RuleResult(this, context, string.Join("; ", errors))
                    : new RuleResult(this, context);

                Logger.LogInformation("**Ejecución del DatabaseValidator completada exitosamente** - La validación por base de datos finalizó sin errores");
                return result;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del DatabaseValidator** - Ocurrió un problema durante la validación por base de datos");
                string errorMessage = ErrorMessage ?? $"Error al ejecutar la validación de base de datos: {ex.Message}";
                return new RuleResult(this, context, errorMessage);
            }
        }
    }

    /// <summary>
    /// Reemplaza los parámetros en la consulta SQL con los valores del contexto.
    /// Soporta: @Value, {oldValue.Propiedad}, {newValue.Propiedad}
    /// </summary>
    /// <param name="sql">Consulta SQL con parámetros</param>
    /// <param name="originalValue">Valor original del campo</param>
    /// <param name="context">Contexto de ejecución con oldValue y newValue</param>
    /// <returns>Consulta SQL con parámetros reemplazados</returns>
    private static string ReplaceParameters(string sql, string originalValue, BRMSExecutionContext context)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return sql;
        }

        string processedSQL = sql;

        // Reemplazar @Value con el valor original
        processedSQL = processedSQL.Replace("@Value", originalValue ?? "NULL");

        // Reemplazar parámetros {oldValue.Propiedad}
        if (context.OldValue != null)
        {
            processedSQL = ReplaceContextParameters(processedSQL, context.OldValue, context.NewValue);
        }

        return processedSQL;
    }

    private static string ReplaceContextParameters(string template, JObject? oldValue, JObject? newValue)
    {

        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        // Regex para capturar {newValue.propiedad} o {oldValue.propiedad}
        Regex regex = TemplateRegex();

        return regex.Replace(template, match =>
        {
            string source = match.Groups[1].Value;   // "newValue" o "oldValue"
            string property = match.Groups[2].Value; // "propiedad"

            JObject? sourceObj = source == "newValue" ? newValue : oldValue;

            if (sourceObj == null)
            {
                return string.Empty;
            }

            JToken? token = sourceObj.SelectToken(property);
            return token?.Type == JTokenType.Null ? "NULL" : (token?.ToString() ?? string.Empty);
        });
    }

    [GeneratedRegex(@"\{(newValue|oldValue)\.([^\}]+)\}")]
    private static partial Regex TemplateRegex();
}
