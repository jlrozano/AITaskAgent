using BRMS.Core.Abstractions;
using BRMS.Core.Core;
using Newtonsoft.Json.Linq;
using NJsonSchema;


namespace BRMS.Engine.Models;

/// <summary>
/// Configuración de un esquema y sus reglas.
/// </summary>
public class ValidationSchema
{

    private List<JObject> _rules = [];

    private readonly Dictionary<string, IRule> _compiledRules = [];
    /// <summary>
    /// Esquema JSON que define la estructura de salida.
    /// </summary>
    public JsonSchema DataModel { get; init; } = new();
    /// <summary>
    /// Lista de reglas a aplicar en orden.
    /// </summary>
    public IReadOnlyList<JObject> Rules
    {
        get
        {
            return _rules.AsReadOnly();
        }
        init
        {
            _rules = (value == null) ? [] : [.. value.Select(x =>
            {
                x["_id"] = Guid.NewGuid().ToString();
                return x;
            })];
        }
    }
    /// <summary>
    /// Lista de reglas construidas y listas para usar.
    /// </summary>
    internal IReadOnlyList<IRule> CompiledRules => [.. _compiledRules.Values];
    /// <summary>
    /// Construye las reglas del esquema usando el RuleManager.
    /// </summary>
    /// <returns>Lista de errores encontrados durante la construcción.</returns>
    public List<string> Build()
    {
        var errors = new List<string>();
        _compiledRules.Clear();
        foreach (JObject rule in Rules)
        {
            (string? error, IRule? ruleObj) = CompileRule(rule);
            if (error != null)
            {
                errors.Add(error);
            }
            else
            {
                _compiledRules.Add(rule["_id"]!.ToString(), ruleObj!);
            }
        }
        return errors;
    }

    private static (string?, IRule?) CompileRule(JObject rule)
    {

        // TODO: Hacer la validación contra el esquema de la regla RuleDescripcion.Parameters

        if (!rule.TryGetValue("_id", out JToken? token) || token == null)
        {
            rule["_id"] = Guid.NewGuid().ToString();
        }
        string? name = rule["ruleId"]?.ToString()?.Trim();
        string? propertyPath = rule["propertyPath"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return ("Todas las reglas deben tener un nombre.", null);
        }
        if (string.IsNullOrEmpty(propertyPath))
        {
            //return ("La regla " + name + " debe tener un propertyPath.", null);
            rule["propertyPath"] = "$";
        }
        IRule? ruleObj = RuleManager.GetRule<IRule>(name, rule);
        if (ruleObj == null)
        {
            return ("No se pudo construir la regla " + name + ". (¿Está registrada?)", null);
        }
        return (null, ruleObj);
    }

}


