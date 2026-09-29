using AITaskAgent.LLM.Models;
using Google.GenAI.Types;
using GeminiLlmService;
// System.Type (brought in by ImplicitUsings) and Google.GenAI.Types.Type collide under the bare
// name "Type" - alias the one these tests actually assert against.
using Type = Google.GenAI.Types.Type;

namespace GeminiLlmService.Tests;

/// <summary>
/// ConvertJsonSchemaToGeminiSchema is internal, but ToGeminiTool (public) is a thin wrapper around
/// it, so exercising the schema conversion through the tool's Parameters is enough surface without
/// needing InternalsVisibleTo.
///
/// The converter used to match nested values against Dictionary&lt;string, object&gt;/List&lt;object&gt;,
/// but Newtonsoft deserializes nested JSON objects/arrays targeting `object` as JObject/JArray - so
/// every nested properties/required/items block was silently dropped, and only the schema's
/// top-level type/description ever reached Gemini. These tests pin the properties/required/items
/// that specifically live below the root.
/// </summary>
public class GeminiTypeConvertersTests
{
    private static ToolDefinition ToolWithSchema(string parametersJsonSchema) => new()
    {
        Name = "test_tool",
        Description = "A tool used only to drive schema conversion.",
        ParametersJsonSchema = parametersJsonSchema
    };

    private static Schema ParametersOf(ToolDefinition tool) =>
        tool.ToGeminiTool().FunctionDeclarations![0].Parameters!;

    [Fact]
    public void First_level_properties_and_required_survive_conversion()
    {
        var schema = ParametersOf(ToolWithSchema("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age": { "type": "integer" }
              },
              "required": ["name"]
            }
            """));

        Assert.Equal(Type.OBJECT, schema.Type);
        Assert.Equal(Type.STRING, schema.Properties!["name"].Type);
        Assert.Equal(Type.INTEGER, schema.Properties!["age"].Type);
        Assert.Equal(["name"], schema.Required);
    }

    [Fact]
    public void Second_level_object_properties_and_required_survive_conversion()
    {
        var schema = ParametersOf(ToolWithSchema("""
            {
              "type": "object",
              "properties": {
                "address": {
                  "type": "object",
                  "properties": {
                    "city": { "type": "string" }
                  },
                  "required": ["city"]
                }
              }
            }
            """));

        var address = schema.Properties!["address"];
        Assert.Equal(Type.OBJECT, address.Type);
        Assert.Equal(Type.STRING, address.Properties!["city"].Type);
        Assert.Equal(["city"], address.Required);
    }

    [Fact]
    public void Array_items_schema_survives_conversion()
    {
        var schema = ParametersOf(ToolWithSchema("""
            {
              "type": "object",
              "properties": {
                "tags": {
                  "type": "array",
                  "items": { "type": "string" }
                }
              }
            }
            """));

        var tags = schema.Properties!["tags"];
        Assert.Equal(Type.ARRAY, tags.Type);
        Assert.Equal(Type.STRING, tags.Items!.Type);
    }

    [Fact]
    public void Array_of_objects_keeps_item_properties_and_required()
    {
        var schema = ParametersOf(ToolWithSchema("""
            {
              "type": "object",
              "properties": {
                "contacts": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "email": { "type": "string" }
                    },
                    "required": ["email"]
                  }
                }
              }
            }
            """));

        var contactItem = schema.Properties!["contacts"].Items!;
        Assert.Equal(Type.OBJECT, contactItem.Type);
        Assert.Equal(Type.STRING, contactItem.Properties!["email"].Type);
        Assert.Equal(["email"], contactItem.Required);
    }

    [Fact]
    public void Description_survives_conversion()
    {
        var schema = ParametersOf(ToolWithSchema("""
            {
              "type": "object",
              "description": "Root description",
              "properties": {
                "name": { "type": "string", "description": "Nested description" }
              }
            }
            """));

        Assert.Equal("Root description", schema.Description);
        Assert.Equal("Nested description", schema.Properties!["name"].Description);
    }

    [Fact]
    public void Schema_without_properties_falls_back_to_bare_object_type()
    {
        var schema = ParametersOf(ToolWithSchema("{}"));

        Assert.Equal(Type.OBJECT, schema.Type);
        Assert.Null(schema.Properties);
    }

    [Fact]
    public void Native_gemini_tool_is_reused_instead_of_reconverted()
    {
        var nativeTool = new Tool { FunctionDeclarations = [] };
        var tool = new ToolDefinition
        {
            Name = "reused",
            Description = "reused",
            ParametersJsonSchema = "{}",
            Native = new NativeObject
            {
                Provider = "Gemini",
                Value = nativeTool,
                TypeName = typeof(Tool).AssemblyQualifiedName!
            }
        };

        Assert.Same(nativeTool, tool.ToGeminiTool());
    }
}
