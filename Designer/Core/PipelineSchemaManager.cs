namespace AITaskAgent.Designer.Core;

using AITaskAgent.Designer.Models;
using Newtonsoft.Json.Linq;

/// <summary>
/// Manages pipeline schemas with registration, validation, and hot-reload.
/// Equivalent to BRMS SchemaManager.
/// </summary>
public class PipelineSchemaManager
{
    private readonly Dictionary<string, PipelineSchema> _schemas = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Gets all registered schemas.
    /// </summary>
    public IReadOnlyList<PipelineSchema> Schemas
    {
        get
        {
            lock (_lock)
            {
                return [.. _schemas.Values];
            }
        }
    }

    /// <summary>
    /// Gets a schema by name.
    /// </summary>
    public PipelineSchema? GetSchema(string name)
    {
        lock (_lock)
        {
            return _schemas.GetValueOrDefault(name);
        }
    }

    /// <summary>
    /// Registers a new pipeline schema.
    /// </summary>
    /// <param name="name">Unique name for the schema.</param>
    /// <param name="schema">The schema to register.</param>
    /// <param name="errors">Validation errors if any.</param>
    /// <returns>True if registered successfully.</returns>
    public bool AddSchema(string name, PipelineSchema schema, out IEnumerable<string> errors)
    {
        lock (_lock)
        {
            if (_schemas.ContainsKey(name))
            {
                errors = [$"A schema with name '{name}' already exists."];
                return false;
            }

            // Build and validate
            var buildErrors = schema.Build();
            if (buildErrors.Count > 0)
            {
                errors = buildErrors;
                return false;
            }

            _schemas[name] = schema;
            errors = [];
            return true;
        }
    }

    /// <summary>
    /// Registers a schema without returning errors.
    /// </summary>
    public bool AddSchema(string name, PipelineSchema schema)
    {
        return AddSchema(name, schema, out _);
    }

    /// <summary>
    /// Removes a registered schema.
    /// </summary>
    public bool RemoveSchema(string name)
    {
        lock (_lock)
        {
            return _schemas.Remove(name);
        }
    }

    /// <summary>
    /// Updates an existing schema (hot-reload).
    /// </summary>
    public bool UpdateSchema(string name, PipelineSchema newSchema, out IEnumerable<string> errors)
    {
        lock (_lock)
        {
            if (!_schemas.ContainsKey(name))
            {
                errors = [$"Schema '{name}' not found."];
                return false;
            }

            var buildErrors = newSchema.Build();
            if (buildErrors.Count > 0)
            {
                errors = buildErrors;
                return false;
            }

            _schemas[name] = newSchema;
            errors = [];
            return true;
        }
    }

    /// <summary>
    /// Loads a schema from JSON.
    /// </summary>
    public bool LoadSchemaFromJson(string name, string json, out IEnumerable<string> errors)
    {
        try
        {
            var schema = JObject.Parse(json).ToObject<PipelineSchema>();
            if (schema == null)
            {
                errors = ["Failed to deserialize pipeline schema."];
                return false;
            }

            return AddSchema(name, schema, out errors);
        }
        catch (Exception ex)
        {
            errors = [$"JSON parsing error: {ex.Message}"];
            return false;
        }
    }

    /// <summary>
    /// Gets all schema names.
    /// </summary>
    public IEnumerable<string> GetSchemaNames()
    {
        lock (_lock)
        {
            return [.. _schemas.Keys];
        }
    }
}
