using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Extensions.Logging;

namespace BRMS.StdRules.Modules.Scripting;

public abstract class JsScriptRule : IDisposable
{
    //private bool disposed;
    private V8ScriptEngine? _engine;
    private dynamic? _function;
    private readonly List<(string, DynamicObjectWrapper)> _hostObjects = [];
    private readonly List<(string, object?)> _functionParameters = [];

    private readonly List<ConsoleMessage> _console = [];

    private static readonly Lazy<SerilogLoggerFactory> _loggerFactory = new(() => new(Log.Logger));
    private Microsoft.Extensions.Logging.ILogger? _logger;

    [Required]
    [Description(ResourcesKeys.Desc_JsScriptRule_Expression)]
    public string Expression { get; set; } = "";

    [Required]
    [Description(ResourcesKeys.Desc_JsScriptRule_Name)]
    public string RuleId { get; set; } = null!;

    [JsonIgnore]
    public string PropertyPath { get; protected set; } = "$";
    [JsonIgnore]
    public IReadOnlyList<RuleInputType> InputTypes { get; protected set; } = [RuleInputType.Any];
    /// <summary>
    /// Mensaje de error que se mostrar� cuando la validaci�n falle
    /// </summary>
    [Description(ResourcesKeys.Desc_JsScriptRule_ErrorMessage)]
    public string? ErrorMessage { get; set; }
    [Required]
    [Description(ResourcesKeys.Desc_JsScriptRule_ErrorSeverityLevel)]
    public ErrorSeverityLevelEnum ErrorSeverityLevel { get; set; }

    protected void AddHostObject(string name, object obj)
    {
        _hostObjects.Add((name, new DynamicObjectWrapper(JObject.FromObject(obj))));
    }
    protected void AddFunctionParameter(string name, object? obj)
    {
        _functionParameters.Add((name, obj));
    }
    protected virtual void InitializeJsEngine()
    {
        _engine = ScriptProgramBuilder.CreateEngine();

        // Configuración del console (siempre disponible)
        var consoleLogger = new ConsoleLogger(_console);
        _engine.AddHostObject("console", consoleLogger);

        // Configuración de objetos host adicionales (extensible)
        ConfigureHostObjects();

        // Agregar objetos host personalizados
        foreach ((string, DynamicObjectWrapper) obj in _hostObjects)
        {
            _engine?.AddHostObject(obj.Item1, obj.Item2);
        }

        // Configurar y ejecutar la función JavaScript
        string fnName = $"rule_{Guid.NewGuid():N}";
        string expresion = Expression.TrimStart().StartsWith("function") ? $"({Expression})()" : Expression;
        string extraParameters = _functionParameters.Count == 0 ? ""
            : $", {string.Join(", ", _functionParameters.Select(p => $"{p.Item1}"))}";

        _engine!.Execute($"function {fnName}(context{extraParameters}) {{\r\n\treturn {expresion};\r\n}}\r\n");
        _function = _engine.Script[fnName];
    }

    protected virtual Microsoft.Extensions.Logging.ILogger Logger => _logger ??= _loggerFactory.Value.CreateLogger(this.GetType());


    /// <summary>
    /// Método virtual para que las clases derivadas puedan configurar objetos host adicionales
    /// como el objeto 'db' para acceso a base de datos
    /// </summary>
    protected virtual void ConfigureHostObjects()
    {
        // Las clases derivadas pueden sobrescribir este método para agregar objetos host específicos
        // Por ejemplo: _engine.AddHostObject("db", new DbJsHostService(_connectionManager, _console, Logger));
    }

    private IEnumerable<ConsoleMessage> CloneConsole()
    {
        return [.. _console.ToArray()];
    }

    protected ScriptExecutionResult ExecuteScript(BRMSExecutionContext context, bool isNewInmutable)
    {
        try
        {
            if (_engine == null)
            {
                InitializeJsEngine();
            }
            var funcContext = new
            {
                oldValue = (context.OldValue == null) ? null : new DynamicObjectWrapper((context.OldValue as JObject)!, isImmutable: true),
                newValue = new DynamicObjectWrapper((context.NewValue as JObject)!, isNewInmutable),
                source = context.Source
            };
            _console.Clear();
            object?[] parameters = [funcContext, .. _functionParameters.Select(p => p.Item2)];
            return ScriptExecutionResult.SuccessResult(_function(funcContext), CloneConsole());
        }
        catch (ScriptEngineException ex)
        {
            Logger.LogError(ex, "Error ejecutando script JavaScript en regla {RuleId}", RuleId);
            return ScriptExecutionResult.ErrorResult($"Error en script JavaScript: {ex.ErrorDetails}", CloneConsole(), ex);
        }
        catch (Exception ex2)
        {
            Logger.LogError(ex2, "Error ejecutando script JavaScript en regla {RuleId}", RuleId);
            return ScriptExecutionResult.ErrorResult($"Error en script JavaScript: {ex2.Message}", CloneConsole(), ex2);
        }
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
        _function = null;
        //Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
