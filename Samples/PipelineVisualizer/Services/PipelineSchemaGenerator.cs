using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Steps;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace PipelineVisualizer.Services;

/// <summary>
/// Generates JSON schema from pipeline structure.
/// </summary>
public sealed class PipelineSchemaGenerator
{
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, JObject> _stepTypeMetadata;

    // Type hierarchy mapping for inheritance lookup
    private static readonly Dictionary<string, string> TypeHierarchy = new()
    {
        ["StatelessTemplateLlmStep"] = "BaseLlmStep",
        ["StatelessRewriterStep"] = "BaseLlmStep",
        ["IntentionAnalyzerStep"] = "BaseLlmStep",
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineSchemaGenerator"/> class.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    public PipelineSchemaGenerator(IConfiguration configuration)
    {
        _configuration = configuration;
        _stepTypeMetadata = LoadStepTypeMetadata();
    }

    /// <summary>
    /// Loads step type metadata from configuration.
    /// </summary>
    private Dictionary<string, JObject> LoadStepTypeMetadata()
    {
        var metadata = new Dictionary<string, JObject>();
        var stylesSection = _configuration.GetSection("AITaskAgent:StepTypeStyles");

        foreach (var child in stylesSection.GetChildren())
        {
            var typeName = child.Key;
            var icon = child["Icon"];
            var color = child["Color"];
            var borderColor = child["BorderColor"];
            var backgroundColor = child["BackgroundColor"];

            // Assuming category and description might also come from config or be derived
            // For now, returning empty strings as their source isn't specified in the instruction
            var category = child["Category"] ?? "";
            var description = child["Description"] ?? "";

            if (icon != null && color != null)
            {
                metadata[typeName] = new JObject
                {
                    ["category"] = category,
                    ["description"] = description,
                    ["icon"] = icon,
                    ["color"] = color,
                    ["borderColor"] = borderColor,
                    ["backgroundColor"] = backgroundColor
                };
            }
        }

        return metadata;
    }

    /// <summary>
    /// Gets metadata for a step type, with inheritance fallback.
    /// </summary>
    private JObject? GetStepTypeMetadata(string typeName)
    {
        var baseTypeName = typeName.Split('`')[0];

        // Direct lookup
        if (_stepTypeMetadata.TryGetValue(baseTypeName, out var metadata))
        {
            return metadata;
        }

        // Inheritance lookup
        if (TypeHierarchy.TryGetValue(baseTypeName, out var parentType))
        {
            return GetStepTypeMetadata(parentType);
        }

        return null;
    }

    /// <summary>
    /// Gets category for a step type.
    /// </summary>
    private static string GetCategory(string typeName)
    {
        return typeName switch
        {
            "BaseLlmStep" or "IntentionAnalyzerStep" or "StatelessTemplateLlmStep" or "StatelessRewriterStep" => "llm",
            "RouterStep" or "ParallelStep" => "control-flow",
            "GroupStep" => "container",
            "DelegatedStep" => "utility",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Gets description for a step type.
    /// </summary>
    private static string GetDescription(string typeName)
    {
        return typeName switch
        {
            "BaseLlmStep" => "Base class for LLM-powered steps",
            "IntentionAnalyzerStep" => "Analyzes user input to determine the intent",
            "RouterStep" => "Routes execution based on analyzed intention",
            "StatelessTemplateLlmStep" => "Executes LLM prompt based on a template",
            "StatelessRewriterStep" => "Specialized step for rewriting content",
            "ParallelStep" => "Executes multiple steps in parallel",
            "GroupStep" => "Logical grouping of sequential steps",
            "DelegatedStep" => "Executes C# code logic",
            _ => ""
        };
    }

    /// <summary>
    /// Generates a complete pipeline schema from a list of steps.
    /// </summary>
    public JObject GeneratePipelineSchema(string name, string description, string version, List<IStep> steps)
    {
        var schema = new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["version"] = version,
            ["pipeline"] = new JArray(steps.Select(GenerateStepSchema)),
            ["stepTypes"] = GenerateStepTypesMetadata(steps)
        };

        return schema;
    }

    /// <summary>
    /// Generates stepTypes metadata section based on steps used in the pipeline.
    /// </summary>
    private JObject GenerateStepTypesMetadata(List<IStep> steps)
    {
        var usedTypes = new HashSet<string>();
        CollectStepTypes(steps, usedTypes);

        var stepTypes = new JObject();
        foreach (var typeName in usedTypes)
        {
            var metadata = GetStepTypeMetadata(typeName);
            if (metadata != null)
            {
                var baseTypeName = typeName.Split('`')[0];
                stepTypes[baseTypeName] = metadata;
            }
        }

        return stepTypes;
    }

    /// <summary>
    /// Recursively collects all step types used in the pipeline.
    /// </summary>
    private void CollectStepTypes(IEnumerable<IStep> steps, HashSet<string> usedTypes)
    {
        foreach (var step in steps)
        {
            usedTypes.Add(step.GetType().Name);

            // Recursively collect from nested steps
            switch (step)
            {
                case GroupStep group:
                    CollectStepTypes(group.Steps, usedTypes);
                    break;
                case ParallelStep parallel:
                    CollectStepTypes(parallel.Branches, usedTypes);
                    break;
                case RouterStep router:
                    CollectStepTypes(router.Routes.Values, usedTypes);
                    break;
            }
        }
    }

    /// <summary>
    /// Generates schema for a single step (recursive for nested steps).
    /// </summary>
    private JObject GenerateStepSchema(IStep step)
    {
        var stepSchema = new JObject
        {
            ["name"] = step.Name,
            ["type"] = step.GetType().Name,
            ["inputType"] = FormatTypeName(step.InputType),
            ["outputType"] = FormatTypeName(step.OutputType)
        };

        switch (step)
        {
            // GroupStep - has nested steps
            case GroupStep group:
                stepSchema["steps"] = new JArray(group.Steps.Select(GenerateStepSchema));
                break;

            // ParallelStep - has parallel branches
            case ParallelStep parallel:
                stepSchema["steps"] = new JArray(parallel.Branches.Select(GenerateStepSchema));
                break;

            // RouterStep - has routes keyed by intention id
            case RouterStep router:
                var routesObj = new JObject();
                foreach (var route in router.Routes)
                {
                    routesObj[route.Key] = GenerateStepSchema(route.Value);
                }
                stepSchema["routes"] = routesObj;

                if (router.DefaultRoute is IStep defaultRoute)
                {
                    stepSchema["defaultRoute"] = defaultRoute.Name;
                }
                break;
        }

        return stepSchema;
    }

    /// <summary>
    /// Formats type name to be more readable (removes generic markers).
    /// </summary>
    private static string FormatTypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var genericArgs = type.GetGenericArguments();
        var baseName = type.Name.Split('`')[0];
        var argNames = string.Join(", ", genericArgs.Select(FormatTypeName));
        return $"{baseName}<{argNames}>";
    }
}
