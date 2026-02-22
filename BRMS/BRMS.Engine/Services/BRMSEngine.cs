using System.Diagnostics;
using System.Reflection;
using BRMS.Core.Abstractions;
using BRMS.Core.Core;
using BRMS.Core.Core.NugetUtils;
using BRMS.Core.Diagnostics;
using BRMS.Core.Extensions;
using BRMS.Core.Models;
using BRMS.Engine.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace BRMS.Engine.Services;


/// <summary>
/// 
/// </summary>
public interface IRegisterPluginAssembly
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="assembly"></param>
    /// <returns></returns>
    IRegisterPluginAssembly RegisterPluginAssembly(Assembly assembly);
}
/// <summary>
/// 
/// </summary>
public class BRMSEngine
{

    internal readonly NuGetPackageLoader PluginLoader;
    //private readonly List<string> _processingErrors = [];
    private readonly ILogger _logger;
    private static bool _isInitialized = false;
    private static BRMSEngine? _engine = null!;
    private static readonly Lock _lock = new();
    private readonly SchemaManager _schemas = new();

    private class ScehmaRule(string message) : IRule
    {
        public string RuleId => "SchemaRule";

        public string PropertyPath => "$";

        public ErrorSeverityLevelEnum ErrorSeverityLevel => ErrorSeverityLevelEnum.Error;

        public IReadOnlyList<RuleInputType> InputTypes => [];

        public string? ErrorMessage => message;

        public Task<object> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<object>(new SchemaError(message, context));
        }
    }
    private class SchemaError(string message, BRMSExecutionContext context) : IRuleResult
    {
        public string RuleId => "SchemaValidation";
        public bool Success => false;
        public ResultLevelEnum ResultLevel => ResultLevelEnum.Error;
        public string Message => message;

        public BRMSExecutionContext Context => context;

        public Exception? Exception { get; set; } = null!;

        public string PropertyPath => "$";

        public IRule Rule { get; } = new ScehmaRule(message);


    }
    /// <summary>
    /// BRMSEngine property
    /// </summary>
    public static BRMSEngine Engine => _engine ?? throw new Exception("BRMS Engine no está inicializado. Use ServiceCollectio.AddBrmsEngine al inicializar.");
    #region Inicializaciones privadas
    internal static async Task<BRMSEngine> Create(IServiceCollection serviceCollection, NuGetLoaderConfig? nugets, ILogger logger)
    {

        if (_engine == null)
        {
            lock (_lock)
            {
                _engine ??= new BRMSEngine(nugets, logger);
            }

            await _engine.Initialize(serviceCollection, logger);

        }
        return _engine!;
    }
    private BRMSEngine(NuGetLoaderConfig? nugets, ILogger logger)
    {
        _logger = logger;
        PluginLoader = new NuGetPackageLoader(nugets, logger);
    }
    private async Task Initialize(IServiceCollection serviceCollection, ILogger logger)
    {
        if (_isInitialized)
        {
            logger.LogWarning("El motor ya está inicializado");
            return;
        }

        logger.LogInformation("Inicializando motor BRMS...");

        if (PluginLoader != null)
        {

            logger.LogInformation("Cargando plugins...");
            bool pluginsLoaded;
            try
            {
                pluginsLoaded = await PluginLoader.LoadPluginsAsync(serviceCollection);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Excepción durante la inicialización del motor");
                throw;
            }

            if (!pluginsLoaded)
            {
                logger.LogError("Error cargando plugins: {@Error}", PluginLoader.LoadErrors);
                throw new Exception($"Error cargando plugins. \n{string.Join("\n", PluginLoader.LoadErrors)}");
            }
        }

        _isInitialized = true;
        logger.LogInformation("Motor BRMS inicializado correctamente");


    }
    internal async Task RegisterPluginsAsync(IServiceProvider serviceProvider)
    {
        if (PluginLoader != null)
        {
            await PluginLoader.RegisterPluginsAsync(serviceProvider);
        }
    }

    #endregion


    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    /// <param name="schema"></param>
    /// <param name="errors"></param>
    /// <returns></returns>
    public bool RegisterSchema(string name, ValidationSchema schema, out IEnumerable<string> errors)
    {
        return _schemas.AddSchema(name, schema, out errors);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    /// <param name="schema"></param>
    /// <returns></returns>
    public bool RegisterSchema(string name, ValidationSchema schema)
    {
        return RegisterSchema(name, schema, out _);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="name"></param>
    public void UnRegisterSchema(string name)
    {
        _ = _schemas.RemoveSchema(name);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="oldData"></param>
    /// <param name="newData"></param>
    /// <param name="schemaName"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public Task<ProcessingResult> ProcessJson(JObject? oldData, JObject? newData, string schemaName)
    {
        ValidationSchema? schema = _schemas.GetSchema(schemaName);
        if (schema == null)
        {
            _logger.LogError("Esquema no encontrado: {SchemaName}", schemaName);
            throw new ArgumentException($"Esquema no encontrado: {schemaName}", nameof(schemaName));
        }
        return ProcessJson(oldData, newData, schema);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="oldData"></param>
    /// <param name="newData"></param>
    /// <param name="schema"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public async Task<ProcessingResult> ProcessJson(JObject? oldData, JObject? newData, ValidationSchema schema)
    {

        ArgumentNullException.ThrowIfNull(schema, nameof(schema));
        if (!_isInitialized)
        {
            _logger.LogError("Se intentó procesar JSON sin inicializar el motor");
            throw new InvalidOperationException("El motor BRMS no ha sido inicializado. Llame a Initialize() primero.");
        }

        if (newData == null)
        {
            _logger.LogError("newData es null");
            throw new ArgumentException("Los datos JSON no pueden estar vacíos", nameof(newData));
        }

        if (schema.CompiledRules.Count == 0 && schema.Rules.Count > 0)
        {
            List<string> ruleErrors = schema.Build();
            if (ruleErrors.Count > 0)
            {
                throw new InvalidOperationException($"Hay reglas invalidas en el esquema: \n{string.Join("\n", ruleErrors)}");
            }
        }
        var processingErrors = new List<string>();

        try
        {
            var context = new BRMSExecutionContext(oldData, newData, "", schema.DataModel);
            var normalizationResults = new List<INormalizerResult>();
            var ruleResults = new List<IRuleResult>();
            //if (sourceConfig?.Transformation != null) 
            //{
            //    var dataTransform = RuleManager.GetRule<IDataTransform>(sourceConfig?.Transformation["ruleId"]?.ToString()??"", sourceConfig?.Transformation);

            //    if (dataTransform != null && ValidateValueWithSchema(context.OldValue, sourceConfig!.InputType, schemaErrors, $"{source}.inputType.OldValue" )
            //        && ValidateValueWithSchema(context.NewValue, sourceConfig.InputType, schemaErrors, $"{source}.inputType.NewValue"))
            //    {
            //        dataTransform.OutputType = schema.DataSchema;

            //        var result = await dataTransform.Invoke(context);
            //        var trasnformResult = result as ITransformResult;
            //        if (trasnformResult == null || !trasnformResult.Success || trasnformResult.OutputContext == null)
            //        {
            //            object? error = trasnformResult?.Exception;
            //            error ??= trasnformResult?.Message ?? "Error desconocido. (null result).";

            //            _logger.LogError("Error en tranformación de {Source} en el esquema {SchemaName}\n{Error}",
            //                source, schema.Name, error);
            //            schemaErrors.Add($"{source}.transformation", [ trasnformResult?.Message ?? "Error desconocido. (null result)."]);
            //        }
            //        else 
            //            context = trasnformResult.OutputContext!;
            //    }
            //    else
            //    {
            //        // modificar
            //    }
            //}

            _ = SchemaManager.ValidateSchema(schema, out IEnumerable<string>? errors);
            if (errors.Any())
            {
                return new ProcessingResult
                {
                    Success = false,
                    ProcessedJson = context.NewValue ?? context.OldValue ?? [],
                    NormalizationResults = [],
                    RuleResults = [new SchemaError(string.Join("\r\n", errors), context)],
                    ProcessingErrors = [.. errors]
                };
            }

            Dictionary<string, IList<string>> schemaErrors = [];

            // Instrumentación: Validación JsonSchema
            using (Activity? validationActivity = BrmsTelemetry.ActivitySource.StartActivity("BRMSEngine.JsonSchemaValidation"))
            {
                if (!ValidateValueWithSchema(context.OldValue, schema.DataModel, schemaErrors, "outputType.oldValue") ||
                    !ValidateValueWithSchema(context.NewValue, schema.DataModel, schemaErrors, "outputType.newValue"))
                {
                    _ = (validationActivity?.SetStatus(ActivityStatusCode.Error, "JsonSchema validation failed"));
                    _ = (validationActivity?.SetTag("brms.validation.errors_count", schemaErrors.Count));

                    BrmsTelemetry.ExecutionsCounter.Add(1, new KeyValuePair<string, object?>("status", "validation_error"));
                    return new ProcessingResult
                    {
                        Success = false,
                        ProcessedJson = context.NewValue ?? context.OldValue ?? [],
                        NormalizationResults = [],
                        RuleResults = [new SchemaError(string.Join("\r\n", schemaErrors.Select(v => $"{v.Key} : {string.Join("\n", v.Value)}")), context)],
                        ProcessingErrors = [.. schemaErrors.Select(v => $"{v.Key} : {string.Join("\n", v.Value)}")]
                    };
                }
            }

            foreach (IRule? rule in schema.CompiledRules.Where(c => c is not IDataTransform).OrderBy(c => c is INormalizer ? 0 : 1))
            {
                // Instrumentación: Ejecución de Regla Individual
                using Activity? ruleActivity = BrmsTelemetry.ActivitySource.StartActivity("BRMSEngine.ExecuteRule");
                _ = (ruleActivity?.SetTag("brms.rule.id", rule.RuleId));
                _ = (ruleActivity?.SetTag("brms.rule.type", rule.GetType().Name));

                try
                {
                    // Ejecutar la regla según su tipo
                    if (rule is INormalizer normalizer)
                    {
                        _ = (ruleActivity?.SetTag("brms.rule.category", "Normalizer"));
                        INormalizerResult result = await normalizer.Invoke(context);
                        normalizationResults.Add(result);
                        _logger.LogDebug("Normalizer {Rule} ejecutado", rule.RuleId);

                        // Métricas
                        BrmsTelemetry.RuleExecutionsCounter.Add(1,
                            new KeyValuePair<string, object?>("rule_id", rule.RuleId),
                            new KeyValuePair<string, object?>("category", "Normalizer"),
                            new KeyValuePair<string, object?>("status", result.Success ? "success" : "failure"));

                        if (!result.Success)
                        {
                            _ = (ruleActivity?.SetStatus(ActivityStatusCode.Error));
                            _ = (ruleActivity?.SetTag("brms.rule.error", result.Message));
                        }
                    }
                    else if (rule is IValidator validator)
                    {
                        _ = (ruleActivity?.SetTag("brms.rule.category", "Validator"));
                        IRuleResult result = await validator.Invoke(context);
                        ruleResults.Add(result);
                        _logger.LogDebug("Validator {Rule} ejecutado", rule.RuleId);

                        // Métricas
                        BrmsTelemetry.RuleExecutionsCounter.Add(1,
                            new KeyValuePair<string, object?>("rule_id", rule.RuleId),
                            new KeyValuePair<string, object?>("category", "Validator"),
                            new KeyValuePair<string, object?>("status", result.Success ? "success" : "failure"));

                        if (!result.Success && result.ResultLevel == ResultLevelEnum.Error)
                        {
                            _ = (ruleActivity?.SetStatus(ActivityStatusCode.Error));
                            _ = (ruleActivity?.SetTag("brms.rule.error", result.Message));
                        }
                    }
                    else
                    {
                        string warn = $"Warning: Regla {rule.GetType().Name} no es ni normalizador ni validador";
                        if (!processingErrors.Contains(rule.GetType().Name))
                        {
                            schemaErrors.Add(rule.GetType().Name, [warn]);
                        }
                        _logger.LogWarning("Warning: Regla {Name} no es ni normalizador ni validador", rule.GetType().Name);
                        _ = (ruleActivity?.SetStatus(ActivityStatusCode.Ok, "Skipped/Unknown Type"));
                    }
                }
                catch (Exception ex)
                {
                    string err = $"Error procesando regla {rule.GetType().Name}: {ex.Message}";
                    processingErrors.Add(err);

                    _ = (ruleActivity?.SetStatus(ActivityStatusCode.Error, ex.Message));
                    BrmsTelemetry.RuleExecutionsCounter.Add(1,
                            new KeyValuePair<string, object?>("rule_id", rule.RuleId),
                            new KeyValuePair<string, object?>("status", "exception"));

#pragma warning disable CA2254 // Template should be a static expression
                    _logger.LogError(ex, err);
#pragma warning restore CA2254 // Template should be a static expression
                }
            }
            _logger.LogInformation("Procesamiento de JSON completado para esquema");
            return new ProcessingResult
            {
                Success = !ruleResults.Any(v => !v.Success && v.ResultLevel == ResultLevelEnum.Error),
                ProcessedJson = context.NewValue ?? [],
                NormalizationResults = normalizationResults,
                RuleResults = ruleResults,
                ProcessingErrors = [.. processingErrors]
            };
        }
        catch (Exception ex)
        {
            string err = $"Error procesando JSON: {ex.Message}";
            processingErrors.Add(err);
#pragma warning disable CA2254 // Template should be a static expression
            _logger.LogError(ex, err);
#pragma warning restore CA2254 // Template should be a static expression
            return new ProcessingResult
            {
                Success = false,
                ProcessedJson = newData,
                ProcessingErrors = [.. processingErrors]
            };
        }
    }

    /// <summary>
    /// Obtiene información sobre las reglas disponibles en el sistema.
    /// </summary>
    public RulesInfo GetAvailableRules()
    {
        _logger.LogDebug("Obteniendo información de reglas disponibles...");

        IEnumerable<RuleDescription> rules = RuleManager.GetAllRuleDescriptions();

        var validators = new List<RuleDescription>();
        var normalizers = new List<RuleDescription>();
        int ruleCount = 0;
        foreach (RuleDescription rule in rules)
        {
            if (typeof(IValidator).IsAssignableFrom(rule.Type))

            {
                validators.Add(rule);
            }

            else if (typeof(INormalizer).IsAssignableFrom(rule.Type))
            {
                normalizers.Add(rule);
            }
            else
            {
                continue;
            }
            ruleCount++;
        }
        _logger.LogInformation("Información de reglas obtenida: {TotalRules} reglas", ruleCount);
        return new RulesInfo
        {
            TotalRules = ruleCount,
            Validators = [.. validators],
            Normalizers = [.. normalizers],
            LoadedPlugins = PluginLoader?.LoadedPlugins.Count ?? 0,
            LoadErrors = [.. PluginLoader?.LoadErrors ?? []]
        };
    }

    private static bool ValidateValueWithSchema(JObject? value, JsonSchema? schema, Dictionary<string, IList<string>> errors, string name)
    {

        if (value != null && schema != null && !value.IsValid(schema, out IList<string>? valueErrors))
        {
            errors.Add(name, valueErrors);
            return false;
        }
        return true;
    }

}



/// <summary>
/// 
/// </summary>
public static class BRMSDI
{

    private class RegisterPluginAssembly(IServiceCollection serviceCollection) : IRegisterPluginAssembly
    {

        IRegisterPluginAssembly IRegisterPluginAssembly.RegisterPluginAssembly(Assembly assembly)
        {
            BRMSEngine.Engine.PluginLoader.LoadAssembly(serviceCollection, assembly);
            return this;
        }
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="sc"></param>
    /// <param name="nugets"></param>
    /// <param name="logger"></param>
    /// <returns></returns>
    public static async Task<IRegisterPluginAssembly> AddBrmsEngine(this IServiceCollection sc, NuGetLoaderConfig? nugets, ILogger logger)
    {
        _ = sc.AddSingleton(await BRMSEngine.Create(sc, nugets, logger));
        return new RegisterPluginAssembly(sc);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static async Task<IApplicationBuilder> UseBrmsEngine(this IApplicationBuilder app)
    {

        RuleManager.SetServiceProvider(app.ApplicationServices);
        await BRMSEngine.Engine.RegisterPluginsAsync(app.ApplicationServices);
        return app;
    }
}
