using Serilog.Events;
using Serilog.Formatting;
using System.Text;

namespace FileToolsTestApp;

public class YamlLogFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write("Timestamp: ");
        output.WriteLine(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));

        output.Write("Level: ");
        output.WriteLine(logEvent.Level);

        output.Write("Message: ");
        // Indent message if multi-line
        var message = logEvent.RenderMessage();
        if (message.Contains('\n'))
        {
            output.WriteLine("|");
            foreach (var line in message.Split('\n'))
            {
                output.Write("  ");
                output.WriteLine(line.TrimEnd());
            }
        }
        else
        {
            output.WriteLine(message);
        }

        if (logEvent.Exception != null)
        {
            output.WriteLine("Exception:");
            output.Write("  Type: ");
            output.WriteLine(logEvent.Exception.GetType().FullName);
            output.Write("  Message: ");
            output.WriteLine(logEvent.Exception.Message);
            output.WriteLine("  Stacktrace: |");
            foreach (var line in logEvent.Exception.ToString().Split('\n'))
            {
                output.Write("    ");
                output.WriteLine(line.TrimEnd());
            }
        }

        if (logEvent.Properties.Count > 0)
        {
            output.WriteLine("Properties:");
            foreach (var property in logEvent.Properties)
            {
                WriteProperty(output, property.Key, property.Value, "  ");
            }
        }

        output.WriteLine("---"); // YAML Document separator
        output.WriteLine();
    }

    private void WriteProperty(TextWriter output, string key, LogEventPropertyValue value, string indentation)
    {
        output.Write(indentation);
        output.Write(key);
        output.Write(": ");

        if (value is ScalarValue scalar)
        {
            var str = scalar.Value?.ToString() ?? "null";
            // Quote string if it contains special characters or newlines
            if (str.Contains('\n') || str.Contains(':') || str.Contains('#'))
            {
                if (str.Contains('\n'))
                {
                    output.WriteLine("|");
                    foreach (var line in str.Split('\n'))
                    {
                        output.Write(indentation + "  ");
                        output.WriteLine(line.TrimEnd());
                    }
                }
                else
                {
                    output.WriteLine($"\"{str}\"");
                }
            }
            else
            {
                output.WriteLine(str);
            }
        }
        else if (value is SequenceValue sequence)
        {
            output.WriteLine();
            foreach (var element in sequence.Elements)
            {
                output.Write(indentation + "- ");
                // Treat list items as if they have no key, slightly complex for scalar vs object
                if (element is ScalarValue sv)
                {
                    output.WriteLine(sv.Value?.ToString() ?? "null");
                }
                else
                {
                    // For complex objects in list, we need to handle indentation carefuly
                    // Recursive call isn't perfect for Sequence items as WriteProperty expects key
                    // Simplified: just render string representation for complex items in list for now
                    // or implement a WriteValue method.
                    WriteValue(output, element, indentation + "  ");
                }
            }
        }
        else if (value is StructureValue structure)
        {
            output.WriteLine();
            foreach (var prop in structure.Properties)
            {
                WriteProperty(output, prop.Name, prop.Value, indentation + "  ");
            }
        }
        else if (value is DictionaryValue dictionary)
        {
            output.WriteLine();
            foreach (var kvp in dictionary.Elements)
            {
                var keyStr = kvp.Key.Value?.ToString() ?? "null";
                WriteProperty(output, keyStr, kvp.Value, indentation + "  ");
            }
        }
        else
        {
            output.WriteLine(value.ToString());
        }
    }

    private void WriteValue(TextWriter output, LogEventPropertyValue value, string indentation)
    {
        if (value is ScalarValue scalar)
        {
            // For sequence items, we effectively already printed "- "
            // But if this is called from recursive structure, we need newline?
            // Actually structure logic handles newline.
            // This helper is mainly for Sequence items context.
            output.WriteLine(scalar.Value?.ToString());
        }
        else if (value is StructureValue structure)
        {
            output.WriteLine();
            foreach (var prop in structure.Properties)
            {
                WriteProperty(output, prop.Name, prop.Value, indentation);
            }
        }
        // ... simplistic handling for others
        else
        {
            output.WriteLine(value.ToString());
        }
    }
}
