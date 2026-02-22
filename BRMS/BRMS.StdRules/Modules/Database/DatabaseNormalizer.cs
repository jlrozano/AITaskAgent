using System.ComponentModel;
using BRMS.Core.Attributes;
using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BRMS.StdRules.Modules.Database;

/// <summary>
/// Normalizador que utiliza consultas SQL para normalizar valores de campos.
/// Permite ejecutar consultas SQL personalizadas contra una base de datos configurada
/// para transformar o normalizar valores según reglas de negocio específicas.
/// 
/// Soporta los siguientes parámetros en las consultas SQL:
/// - @Value: Se reemplaza con el valor actual del campo
/// - {oldValue.Propiedad}: Se reemplaza con propiedades del objeto original
/// - {newValue.Propiedad}: Se reemplaza con propiedades del objeto actual
/// </summary>
[RuleName("DatabaseNormalizer")]
[Description(ResourcesKeys.Desc_DatabaseNormalizer_Description)]
[SupportedTypes(RuleInputType.Any)]
[Scoped]
public class DatabaseNormalizer(IDataBaseQuery dataBaseQuery) : Normalizer
{
    private readonly IDataBaseQuery _dataBaseQuery = dataBaseQuery ?? throw new ArgumentNullException(nameof(dataBaseQuery));

    /// <summary>
    /// Consulta SQL que se ejecutará para normalizar el valor del campo.
    /// Puede contener parámetros como @Value, {oldValue.Propiedad}, {newValue.Propiedad}.
    /// La consulta debe retornar el valor normalizado o NULL si no requiere cambios.
    /// </summary>
    /// <example>
    /// Ejemplos de uso:
    /// - "SELECT UPPER(@Value)" - Convierte a mayúsculas
    /// - "SELECT codigo FROM tabla WHERE nombre = @Value" - Busca código por nombre
    /// - "SELECT COALESCE(@Value, {oldValue.ValorPorDefecto})" - Usa valor por defecto
    /// </example>
    [Description(ResourcesKeys.Desc_DatabaseNormalizer_SQLSmtp_Description)]
    public required string SQLSmtp { get; init; }

    /// <summary>
    /// Oculta la propiedad PropertyPath heredada para evitar confusión en la configuración.
    /// </summary>
    [JsonIgnore]
    public new string PropertyPath { get => base.PropertyPath; set => base.PropertyPath = value; }

    /// <summary>
    /// Alias de la base de datos sobre la que se realizará la consulta.
    /// Este nombre debe coincidir con una conexión configurada en el sistema.
    /// </summary>
    [Description(ResourcesKeys.Desc_Database_DataBaseName_Description)]
    public required string DataBaseName { get; init; }

    protected override async Task<NormalizerResult> Execute(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        using (Logger.BeginScope(LogContext(context)))
        {
            Logger.LogDebug("**Iniciando ejecución del DatabaseNormalizer** - El normalizador está comenzando la ejecución de la consulta SQL de normalización");

            try
            {
                ArgumentNullException.ThrowIfNull(context);

                if (string.IsNullOrWhiteSpace(SQLSmtp))
                {
                    Logger.LogError("Consulta SQL vacía para PropertyPath {PropertyPath}", PropertyPath);
                    return new NormalizerResult(this, context, hasChanges: false);
                }

                // Verificar si el token original es null
                JToken? token = context.NewValue?.SelectToken(PropertyPath);
                if (token == null || token.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                {
                    Logger.LogDebug("Valor es null para PropertyPath {PropertyPath}, no se requiere normalización", PropertyPath);
                    return new NormalizerResult(this, context, hasChanges: false);
                }

                object? originalValue = context.NewValue?.GetValueAs<object>(PropertyPath);

                Logger.LogDebug("**Procesando campo con DatabaseNormalizer** - Ejecutando consulta SQL para normalizar el valor del campo");

                // Reemplazar parámetros en la consulta SQL con el valor actual
                // Usamos el valor específico del PropertyPath para @Value, pero el contexto completo para {oldValue.X} y {newValue.X}
                string parameterizedQuery = ReplaceParameters(SQLSmtp, originalValue?.ToString() ?? "NULL", context);

                Logger.LogDebug("Ejecutando consulta SQL de normalización: {Query} para PropertyPath {PropertyPath}", parameterizedQuery, PropertyPath);

                // Ejecutar la consulta de normalización
                object normalizedValue = await _dataBaseQuery.Value<object>(DataBaseName, parameterizedQuery);

                bool hasChanges = false;
                if (normalizedValue != null && !Equals(originalValue, normalizedValue))
                {
                    context.NewValue!.SetValueWithType(PropertyPath, normalizedValue);
                    hasChanges = true;
                    Logger.LogInformation("Normalización de base de datos completada para PropertyPath {PropertyPath}: '{OriginalValue}' -> '{NormalizedValue}'",
                        PropertyPath, originalValue, normalizedValue);
                }
                else
                {
                    Logger.LogDebug("No se requieren cambios para PropertyPath {PropertyPath}, valor ya está normalizado o consulta retornó null", PropertyPath);
                }

                Logger.LogInformation("**Ejecución del DatabaseNormalizer completada exitosamente** - La normalización por base de datos finalizó sin errores");
                return new NormalizerResult(this, context, hasChanges: hasChanges);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "**Error en la ejecución del DatabaseNormalizer** - Ocurrió un problema durante la normalización por base de datos");
                return new NormalizerResult(this, context, hasChanges: false);
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
    private string ReplaceParameters(string sql, string originalValue, BRMSExecutionContext context)
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
            processedSQL = ReplaceContextParameters(processedSQL, "oldValue", context.OldValue);
        }

        // Reemplazar parámetros {newValue.Propiedad}
        if (context.NewValue != null)
        {
            processedSQL = ReplaceContextParameters(processedSQL, "newValue", context.NewValue);
        }

        return processedSQL;
    }

    /// <summary>
    /// Reemplaza parámetros del tipo {contextName.Propiedad} en la consulta SQL
    /// </summary>
    /// <param name="sql">Consulta SQL</param>
    /// <param name="contextName">Nombre del contexto (oldValue o newValue)</param>
    /// <param name="contextObject">Objeto JObject con las propiedades</param>
    /// <returns>SQL con parámetros reemplazados</returns>
    private string ReplaceContextParameters(string sql, string contextName, Newtonsoft.Json.Linq.JObject contextObject)
    {
        if (string.IsNullOrEmpty(sql) || contextObject == null)
        {
            return sql;
        }

        string pattern = $@"\{{{contextName}\.([^}}]+)\}}";
        return System.Text.RegularExpressions.Regex.Replace(sql, pattern, match =>
        {
            try
            {
                string propertyName = match.Groups[1].Value;
                JToken? token = contextObject.SelectToken(propertyName);
                return token?.ToString() ?? "NULL";
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error al reemplazar parámetro {Parameter} en consulta SQL", match.Value);
                return "NULL";
            }
        });
    }
}
