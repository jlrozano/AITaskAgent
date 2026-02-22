using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;
using Serilog.Extensions.Logging;

namespace BRMS.Core.Core;


public abstract class Rule : IRule
{
    private string? _name = null;
    private static readonly Lazy<SerilogLoggerFactory> _loggerFactory = new(() => new(Log.Logger));
    private Microsoft.Extensions.Logging.ILogger? _logger;
    /// <summary>
    /// Id de la regla para identificación. Debe ser uno de los registrados con RuleMagager.AddRule. 
    /// </summary>
    [Required]
    [Description("UI_RuleId_Description")]
    public string RuleId
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_name))
            {
                _name = RuleManager.GetRuleName(this.GetType());
            }
            return _name;
        }
        set
        {
            _name = value;
        }
    }

    /// <summary>
    /// Ruta JSONPath para acceder al campo a procesar
    /// </summary>
    [Required]
    [DefaultValue("$")]
    [JsonProperty]
    [Description("UI_PropertyPath_Description")]
    [SampleValue("$.propertyName")]
    public string PropertyPath { get; set; } = "$";
    /// <summary>
    /// Nivel de severidad del error. Error o incidencia
    /// </summary>
    [Required]
    [Description("UI_ErrorSeverityLevel_Description")]
    public ErrorSeverityLevelEnum ErrorSeverityLevel { get; init; }
    /// <summary>
    /// Mensaje de error personalizado para esta regla
    /// </summary>
    [Description("UI_ErrorMessage_Description")]
    [SampleValue("Error!!!")]
    public string? ErrorMessage { get; init; }

    [JsonIgnore]
    public IReadOnlyList<RuleInputType> InputTypes { get; protected set; } = [RuleInputType.Any];

    protected Microsoft.Extensions.Logging.ILogger Logger => _logger ??= _loggerFactory.Value.CreateLogger(this.GetType());

    protected static object LogContext(BRMSExecutionContext context, Dictionary<string, object?>? additional = null)
    {
        additional ??= [];

        additional.Add(nameof(context.NewValue), context.NewValue);
        additional.Add(nameof(context.OldValue), context.OldValue);
        additional.Add(nameof(context.Source), context.Source);
        additional.Add(nameof(context.InputType), context.InputType);

        return additional;
    }

    protected abstract Task<object> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken);
    Task<object> IRule.Invoke(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context, nameof(context));

        switch (context.Operation)
        {
            case OperationType.Error:
                throw new ArgumentNullException(nameof(context), "NewValue y OldValue son nulos.");
            case OperationType.Insert:
                ArgumentNullException.ThrowIfNull(context.NewValue, nameof(context.NewValue));
                break;
            case OperationType.Update:
                ArgumentNullException.ThrowIfNull(context.NewValue, nameof(context.NewValue));
                ArgumentNullException.ThrowIfNull(context.OldValue, nameof(context.OldValue));
                break;
            case OperationType.Delete:
                ArgumentNullException.ThrowIfNull(context.OldValue, nameof(context.OldValue));
                break;
        }
        return Invoke(context, cancellationToken);

    }


}

/// <summary>
/// Clase base para todas las reglas del sistema BRMS.
/// Define la estructura común para validadores y normalizadores.
/// </summary>
public abstract class Rule<T> : Rule, IRule<T> where T : class, IRuleResult
{

    public Rule()
    {
        // Obtener el atributo de la clase concreta
        var attr = (SupportedTypesAttribute?)Attribute.GetCustomAttribute(
            this.GetType(), typeof(SupportedTypesAttribute));
        if (attr != null)
        {
            InputTypes = attr.Types;
        }
    }

    async Task<T> IRule<T>.Invoke(BRMSExecutionContext context, CancellationToken cancellationToken)
    {
        object res = await (this as IRule).Invoke(context, cancellationToken);
        return (T)res;
    }

    protected override sealed async Task<object> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default)
    {
        T res = await Execute(context, cancellationToken);
        return res;
    }

    protected abstract Task<T> Execute(BRMSExecutionContext context, CancellationToken cancellationToken = default);

}

