using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AITaskAgent.YAML.Execution;

/// <summary>
/// Converts YAML (YamlDotNet's representation model) to Newtonsoft <see cref="JToken"/>s, keeping
/// each node's position for error messages.
/// </summary>
/// <remarks>
/// Scalars follow the YAML 1.2 core schema: a quoted, literal (<c>|</c>) or folded (<c>&gt;</c>)
/// scalar is always a string, kept verbatim; a plain scalar is null (<c>~</c>, <c>null</c>, empty),
/// a boolean (<c>true</c>/<c>false</c>), an integer, a float, or else a string. The original text of
/// every scalar is kept too, so a value read as a number can still be used as text when the step
/// property is a string. Application tags (<c>!LlmStep</c>) are rejected: they belong to the YAML v1
/// format, where the tag selected the step type.
/// </remarks>
internal static partial class YamlJson
{
    /// <summary>Position of a YAML node, attached to the token built from it.</summary>
    internal sealed record Location(long Line, long Column)
    {
        public override string ToString() => $"line {Line}, column {Column}";
    }

    /// <summary>Raw text of a YAML scalar, attached to the token built from it.</summary>
    internal sealed record RawScalar(string Text);

    /// <summary>Parses every document of <paramref name="yaml"/>; empty documents are skipped.</summary>
    /// <exception cref="InvalidOperationException">Malformed YAML (with its position) or an application tag.</exception>
    public static List<JToken> LoadDocuments(string yaml)
    {
        YamlStream stream = [];
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"Invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            // YamlMappingNode keeps its children in a dictionary: a repeated key surfaces here.
            throw new InvalidOperationException($"Invalid YAML: {ex.Message} (a mapping repeats a key).", ex);
        }

        List<JToken> documents = [];
        foreach (YamlDocument document in stream.Documents)
        {
            if (document.RootNode is YamlScalarNode { Value: null or "" } scalar && scalar.Style == ScalarStyle.Plain)
            {
                continue; // empty document (for instance two consecutive '---')
            }

            documents.Add(ToToken(document.RootNode));
        }

        return documents;
    }

    /// <summary>" (line N, column M)" for a token built by this class, or an empty string.</summary>
    public static string Where(JToken? token)
        => token?.Annotation<Location>() is { } location ? $" ({location})" : string.Empty;

    /// <summary>Original YAML text of a scalar token, or null when it has none.</summary>
    public static string? RawText(JToken token) => token.Annotation<RawScalar>()?.Text;

    /// <summary>Maximum nesting of YAML nodes in one document (Newtonsoft's own default depth).</summary>
    internal const int MaxDepth = 64;

    /// <summary>Maximum number of nodes one document may expand to, counting every alias use again.</summary>
    internal const int MaxNodes = 200_000;

    private static JToken ToToken(YamlNode root) => new Converter().ToToken(root, 0);

    /// <summary>
    /// One conversion: aliases are resolved by YamlDotNet into shared nodes, so a self-referencing alias is a
    /// cycle and nested aliases multiply the output ("billion laughs"). Cycles, depth and total expanded
    /// nodes are checked here, before the recursion can overflow the stack or run for minutes.
    /// </summary>
    private sealed class Converter
    {
        private readonly HashSet<YamlNode> _ancestors = new(ReferenceEqualityComparer.Instance);
        private int _nodes;

        public JToken ToToken(YamlNode node, int depth)
        {
            RejectApplicationTag(node);

            if (depth >= MaxDepth)
            {
                throw new InvalidOperationException(
                    $"YAML nested deeper than {MaxDepth} levels at line {node.Start.Line}, column {node.Start.Column}.");
            }

            if (++_nodes > MaxNodes)
            {
                throw new InvalidOperationException(
                    $"YAML expands to more than {MaxNodes} nodes (line {node.Start.Line}, column {node.Start.Column}); " +
                    "nested aliases (*name) multiply the document.");
            }

            if (node is not YamlScalarNode && !_ancestors.Add(node))
            {
                throw new InvalidOperationException(
                    $"YAML alias cycle: the node at line {node.Start.Line}, column {node.Start.Column} contains an alias to itself.");
            }

            JToken token = node switch
            {
                YamlMappingNode mapping => ToObject(mapping, depth),
                YamlSequenceNode sequence => new JArray(sequence.Children.Select(child => ToToken(child, depth + 1))),
                YamlScalarNode scalar => ToValue(scalar),
                _ => throw new InvalidOperationException($"Unsupported YAML node at line {node.Start.Line}, column {node.Start.Column}.")
            };

            _ancestors.Remove(node);
            token.AddAnnotation(new Location(node.Start.Line, node.Start.Column));
            return token;
        }

        private JObject ToObject(YamlMappingNode mapping, int depth)
        {
            JObject obj = [];
            foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode keyScalar)
                {
                    throw new InvalidOperationException(
                        $"Mapping keys must be plain text (line {keyNode.Start.Line}, column {keyNode.Start.Column}).");
                }

                string key = keyScalar.Value ?? string.Empty;
                JToken value = ToToken(valueNode, depth + 1);
                obj.Add(new JProperty(key, value));
            }

            return obj;
        }
    }

    private static JValue ToValue(YamlScalarNode scalar)
    {
        string text = scalar.Value ?? string.Empty;
        JValue value = scalar.Style == ScalarStyle.Plain && !IsExplicitString(scalar)
            ? ResolvePlain(text)
            : new JValue(text);
        value.AddAnnotation(new RawScalar(text));
        return value;
    }

    private static bool IsExplicitString(YamlScalarNode scalar)
        => !scalar.Tag.IsEmpty && scalar.Tag.Value == "tag:yaml.org,2002:str";

    private static JValue ResolvePlain(string text)
    {
        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return JValue.CreateNull();
            case "true" or "True" or "TRUE":
                return new JValue(true);
            case "false" or "False" or "FALSE":
                return new JValue(false);
        }

        if (IntegerRegex().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            return new JValue(integer);
        }

        if (FloatRegex().IsMatch(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return new JValue(number);
        }

        return new JValue(text);
    }

    private static void RejectApplicationTag(YamlNode node)
    {
        if (node.Tag.IsEmpty || node.Tag.IsNonSpecific)
        {
            return;
        }

        string tag = node.Tag.Value;
        if (tag.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"YAML tag '{tag}' at line {node.Start.Line}, column {node.Start.Column} is not supported. " +
            "Steps are declared as mappings with 'id' and 'type' (for example 'type: LlmStep'), not with tags like '!LlmStep'.");
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();
}
