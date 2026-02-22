using System.Diagnostics;
using BRMS.Core.Abstractions;
using BRMS.Core.Constants;
using BRMS.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;


namespace BRMS.Core.Core;

internal class TransformationException(DataTransformResult error) : Exception
{

    public DataTransformResult Error => error;
}

public abstract class DataTransform : Rule<DataTransformResult>, IDataTransform
{
    private JsonSchema _outputType = null!;
    public JsonSchema OutputType { get => _outputType; set => _outputType = value; }
    [JsonIgnore]
    public new string PropertyPath { get => base.PropertyPath; protected set => base.PropertyPath = "$"; }

}

/// <summary>
/// Clase base para todos los transformadores del sistema BRMS.
/// Define la funcionalidad común para transformar datos JSON.
/// </summary>
public abstract class Transformation<TSource, TTarget> : DataTransform where TSource : class where TTarget : class
{
    /// <summary>
    /// Indica si se deben verificar los modelos durante la transformación
    /// </summary>
    /// 

    private T? Convert<T>(JObject? value, BRMSExecutionContext context, string name) where T : class
    {
        try

        {
            T? result = value?.ToObject<T>();
            if (result != null)
            {
                Logger.LogDebug("Fuente de transformación convertida correctamente: {Result}", result);
            }
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError("Error al convertir la fuente de transformación: {Info}", new { ObjectName = name, Error = ex });
            throw new TransformationException(DataTransformResult.Fail(this, context, ex));

        }
    }

    protected sealed override async Task<DataTransformResult> Execute(Models.BRMSExecutionContext context, CancellationToken cancellationToken = default)
    {
        //ArgumentNullException.ThrowIfNull(context.InputType, nameof(context.InputType));

        var stopwatch = Stopwatch.StartNew();
        string transformationType = this.GetType().Name;

        Logger.LogInformation("Transformación iniciada: {Context}", LogContext(context, new Dictionary<string, object?> { { "TransformationType", transformationType } }));

        try
        {
            TSource? sourceNewValue = Convert<TSource>(context.NewValue, context, nameof(context.NewValue));
            if (sourceNewValue == null)
            {
                Logger.LogError("La fuente de transformación es nula: {Context}", new { context.Source, context.InputType, context.NewValue, context.OldValue });
                return DataTransformResult.Fail(this, context, null,
                    ResourcesManager.GetLocalizedMessage("VALIDATION_TransformationError_NullSourceValue"));
            }

            TSource? sourceOldValue = Convert<TSource>(context.OldValue, context, nameof(context.OldValue));

            TTarget targetValue = await Transform(sourceOldValue, sourceNewValue);

            if (targetValue == null)
            {
                Logger.LogError("El resultado de la transformación es nulo: {Context}", LogContext(context, new Dictionary<string, object?> { { "TransformationType", transformationType } }));
                return DataTransformResult.Fail(this, context, null,
                    ResourcesManager.GetLocalizedMessage("VALIDATION_TransformationError_NullResult", (object)nameof(context.NewValue)));
            }

            stopwatch.Stop();
            Logger.LogInformation("Transformación completada: {Info}", new { TransformationType = transformationType, ElapsedMs = stopwatch.ElapsedMilliseconds });

            return DataTransformResult.Ok(this, context,
                new BRMSExecutionContext(sourceOldValue == null ? null : JObject.FromObject(sourceOldValue),
                    JObject.FromObject(targetValue), context.Source, context.InputType)
                );


        }
        catch (TransformationException tex)
        {
            stopwatch.Stop();
            Logger.LogError("Transformación fallida: {Context}", LogContext(context, new Dictionary<string, object?> { { "TransformationType", transformationType }, { "Error", tex.Message } }));
            return tex.Error;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError("Transformación fallida: {Context}", LogContext(context, new Dictionary<string, object?> { { "TransformationType", transformationType }, { "Error", ex.Message } }));
            return DataTransformResult.Fail(this, context, ex);
        }

    }

    public abstract Task<TTarget> Transform(TSource? oldSourceValue, TSource newSourceValue);
}
