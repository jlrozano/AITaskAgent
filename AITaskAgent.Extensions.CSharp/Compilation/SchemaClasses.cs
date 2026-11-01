using NJsonSchema;
using NJsonSchema.CodeGeneration.CSharp;
using System.Text;
using System.Text.RegularExpressions;

namespace AITaskAgent.CSharp;

/// <summary>One class generated from a package schema.</summary>
/// <param name="SchemaName">Name of the schema (its file name without <c>.json</c>).</param>
/// <param name="ClassName">The root class (and the global alias the author's code uses).</param>
/// <param name="Namespace">The namespace holding the class and its nested types.</param>
/// <param name="Code">The generated source.</param>
internal sealed record SchemaClass(string SchemaName, string ClassName, string Namespace, string Code);

/// <summary>
/// Schema-first: one C# class per schema of the package, generated with <c>NJsonSchema.CodeGeneration.CSharp</c> (which lives
/// only in this extension). Each lives in its own namespace (<c>Workflow.Schemas.&lt;Name&gt;</c>, so two schemas that define
/// a nested type of the same name do not clash) and is reached through a global alias with the class's name.
/// </summary>
internal static partial class SchemaClasses
{
    public static async Task<(IReadOnlyList<SchemaClass> Classes, IReadOnlyList<CSharpDiagnostic> Warnings)> GenerateAsync(IReadOnlyDictionary<string, string> schemas)
    {
        List<SchemaClass> classes = [];
        List<CSharpDiagnostic> warnings = [];
        HashSet<string> used = new(StringComparer.Ordinal);

        foreach ((string name, string json) in schemas.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            string root = Unique(ClassNameOf(name), used);
            try
            {
                JsonSchema schema = await JsonSchema.FromJsonAsync(json);
                schema.Title = root;
                string ns = "Workflow.Schemas." + root;
                CSharpGeneratorSettings settings = new()
                {
                    Namespace = ns,
                    ClassStyle = CSharpClassStyle.Poco,
                    JsonLibrary = CSharpJsonLibrary.NewtonsoftJson,
                    GenerateJsonMethods = false,
                    GenerateDataAnnotations = true,
                    RequiredPropertiesMustBeDefined = true,
                    ArrayType = "System.Collections.Generic.List",
                    ArrayInstanceType = "System.Collections.Generic.List",
                    DictionaryType = "System.Collections.Generic.Dictionary",
                    DictionaryInstanceType = "System.Collections.Generic.Dictionary",
                };

                string code = new CSharpGenerator(schema, settings).GenerateFile(root);
                classes.Add(new SchemaClass(name, root, ns, code));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                used.Remove(root);
                warnings.Add(new CSharpDiagnostic(
                    "WFCS004", CSharpSeverity.Warning, $"No class was generated for schema '{name}': {ex.Message}", $"schema:{name}"));
            }
        }

        return (classes, warnings);
    }

    /// <summary>The class name of a schema name: letters and digits, first letter upper case, no digit first.</summary>
    internal static string ClassNameOf(string schemaName)
    {
        StringBuilder text = new();
        bool upper = true;
        foreach (char c in schemaName)
        {
            if (char.IsLetterOrDigit(c))
            {
                text.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        string result = text.Length == 0 ? "Schema" : text.ToString();
        return char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        string candidate = name;
        for (int i = 2; !used.Add(candidate); i++)
        {
            candidate = name + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    internal static partial Regex NotIdentifier();
}
