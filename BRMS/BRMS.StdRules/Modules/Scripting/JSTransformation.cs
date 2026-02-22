using System.ComponentModel;
using BRMS.Core.Abstractions;
using BRMS.Core.Attributes;
using BRMS.Core.Models;
using BRMS.StdRules.Constants;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace BRMS.StdRules.Modules.Scripting;

internal record JsScriptResultContext(JObject? OldValue, JObject? NewValue);
[RuleName("JSTransformation")]
[Description(ResourcesKeys.Desc_JSTransformation_Description)]
public class JSTransformation : JsScriptRule, IDataTransform
{
    [Description(ResourcesKeys.Desc_JSTransformation_OutputType_Description)]
    public JsonSchema OutputType { get; set; } = null!;

    protected override ILogger Logger => base.Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public Task<object> Invoke(BRMSExecutionContext context, CancellationToken cancellationToken = default)
    {
        ScriptExecutionResult? scriptResult = ExecuteScript(context, true);
        if (scriptResult == null || !scriptResult.Success)
        {
            return Task.FromResult((object)DataTransformResult.Fail(this, context, scriptResult?.ErrorMessage ?? "Error procesando script de trasnfomación."));
        }

        string json = "";
        bool fail = false;
        if (scriptResult.Result != null)
        {
            try
            {
                json = JsonConvert.SerializeObject(scriptResult?.Result);
            }
            catch (Exception ex)
            {
                fail = true; json = $"Error serializando resultado ({scriptResult?.Result.ToString()}).\nError:{ex.Message}";
            }
        }

        if (fail || string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return Task.FromResult((object)DataTransformResult.Fail(this, context, $"El resultado de la trasnformación es invalido '{json}'"));
        }

        JsScriptResultContext? resultContext;
        try
        {
            resultContext = JsonConvert.DeserializeObject<JsScriptResultContext>(json);
        }
        catch
        {
            resultContext = null;
        }

        return resultContext == null || (resultContext.NewValue == null && resultContext.OldValue == null)
            ? Task.FromResult((object)DataTransformResult.Fail(this, context, $"El resultado de la trasnformación es invalido {(
                    resultContext == null ? "null" : json)}"))
            : (context.OldValue != null && resultContext.OldValue == null) || (context.OldValue == null && resultContext.OldValue != null)
            ? Task.FromResult((object)DataTransformResult.Fail(this, context, context.OldValue == null ? "oldValue de salida debe ser nulo, al igual que el valor de entrada." :
                                            "oldValue de salida no puede ser nulo debido a que tiene un valor en la entrada."))
            : (context.NewValue != null && resultContext.NewValue == null) || (context.NewValue == null && resultContext.NewValue != null)
            ? Task.FromResult((object)DataTransformResult.Fail(this, context, context.NewValue == null ? "newValue de salida debe ser nulo, al igual que el valor de entrada." :
                                            "newValue de salida no puede ser nulo debido a que tiene un valor en la entrada."))
            : Task.FromResult((object)DataTransformResult.Ok(this, context,
             new BRMSExecutionContext(resultContext.OldValue, resultContext.NewValue, context.Source, OutputType)));
    }

}
