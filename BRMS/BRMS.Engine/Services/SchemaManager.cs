using BRMS.Core.Core;
using BRMS.Core.Extensions;
using BRMS.Engine.Models;
using Newtonsoft.Json;


namespace BRMS.Engine.Services;


internal class SchemaManager
{

    private readonly Dictionary<string, ValidationSchema> _schemasDict = [];
    private readonly List<string> _errors = [];

    [JsonIgnore]
    public IReadOnlyList<ValidationSchema> Schemas => [.. _schemasDict.Values];

    public ValidationSchema? GetSchema(string schemaName)
    {
        return !_schemasDict.TryGetValue(schemaName, out ValidationSchema? schema) ? null : schema;
    }
    public static bool ValidateSchema(ValidationSchema schema, out IEnumerable<string> errors)
    {
        var err = new List<string>();

        if (schema.DataModel == null)
        {
            err.Add("La propiedad 'DataSchema' debe tener un valor.");
        }
        else
        {
            err.AddRange(schema.DataModel.Validate());

        }

        err.AddRange(RuleManager.ValidateRuleConfigurations(schema.Rules ?? []));
        errors = err;
        return err.Count == 0;
    }
    public bool AddSchema(string name, ValidationSchema newSchema, out IEnumerable<string> validationErrors)
    {

        if (_schemasDict.ContainsKey(name))
        {
            validationErrors = [$"Ya existe un esquema con ese nombre. ({name})"];
            return false;
        }

        if (!ValidateSchema(newSchema, out validationErrors))
        {
            return false;
        }

        _schemasDict.Add(name, newSchema);
        return true;
    }

    public bool AddSchema(string name, ValidationSchema newSchema)
    {

        if (_schemasDict.ContainsKey(name))
        {
            return false;
        }

        if (!ValidateSchema(newSchema, out _))
        {
            return false;
        }

        _schemasDict.Add(name, newSchema);
        return true;
    }

    public bool RemoveSchema(string name)
    {
        return _schemasDict.Remove(name);
    }
}

