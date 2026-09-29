using AITaskAgent.Core.Models;
using AITaskAgent.FileTools.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace AITaskAgent.FileTools.Tests;

/// <summary>
/// Reading tools decode each file with the same rule as the code analyzer: UTF-8 when the bytes are valid UTF-8 (with or
/// without a BOM), otherwise <see cref="FileToolsConfiguration.DefaultEncoding"/>. Reading everything as UTF-8 turned
/// every accented character of an ISO-8859-1 file into U+FFFD, and a <c>grep_search</c> for an accented word found
/// nothing there.
/// </summary>
[Collection(FileToolsConfigurationCollection.Name)]
public class SourceEncodingToolTests : IDisposable
{
    private readonly string _originalRoot = FileToolsConfiguration.RootDirectory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fta-enc-" + Guid.NewGuid());

    public SourceEncodingToolTests()
    {
        Directory.CreateDirectory(_root);
        FileToolsConfiguration.RootDirectory = _root;
    }

    public void Dispose()
    {
        FileToolsConfiguration.RootDirectory = _originalRoot;
        FileToolsConfiguration.ClearAdditionalAllowedDirectories();
        FileToolsConfiguration.DefaultEncoding = null!;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static PipelineContext Context() => new();

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private string WriteLatin1(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
        return path;
    }

    private string WriteUtf8(string name, string text, bool bom = false)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [.. new UTF8Encoding(bom).GetPreamble(), .. new UTF8Encoding(false).GetBytes(text)]);
        return path;
    }

    [Fact]
    public async Task View_file_shows_the_accents_of_a_latin1_file()
    {
        var file = WriteLatin1("Pedido.bas", "' Añade la acción\nSub Validación()\nEnd Sub");

        var result = await new ViewFileTool().ExecuteAsync($$"""{"AbsolutePath": "{{Json(file)}}"}""", Context(), "test", NullLogger.Instance);

        Assert.Contains("1: ' Añade la acción", result);
        Assert.Contains("2: Sub Validación()", result);
        Assert.DoesNotContain('�', result);
    }

    [Fact]
    public async Task View_file_reads_utf8_with_and_without_bom_as_utf8()
    {
        var plain = WriteUtf8("plain.cs", "// Año de la acción");
        var withBom = WriteUtf8("bom.cs", "// Año de la acción", bom: true);

        var fromPlain = await new ViewFileTool().ExecuteAsync($$"""{"AbsolutePath": "{{Json(plain)}}"}""", Context(), "test", NullLogger.Instance);
        var fromBom = await new ViewFileTool().ExecuteAsync($$"""{"AbsolutePath": "{{Json(withBom)}}"}""", Context(), "test", NullLogger.Instance);

        Assert.Contains("1: // Año de la acción", fromPlain);
        // Sin el BOM delante de la primera línea.
        Assert.Contains("1: // Año de la acción", fromBom);
    }

    [Fact]
    public async Task Grep_search_finds_an_accented_word_in_latin1_and_utf8_files()
    {
        WriteLatin1("Latin.bas", "Dim x\n' registrar la acción del usuario\n");
        WriteUtf8("Utf8.cs", "// otra acción\nclass A {}\n");

        var result = await new GrepSearchTool().ExecuteAsync(
            $$"""{"SearchPath": "{{Json(_root)}}", "Query": "acción"}""", Context(), "test", NullLogger.Instance);

        Assert.Contains("Latin.bas", result);
        Assert.Contains("Utf8.cs", result);
        Assert.Contains("registrar la acción del usuario", result);
    }

    [Fact]
    public async Task Grep_search_ignores_case_of_accented_letters_in_a_latin1_file()
    {
        WriteLatin1("Latin.bas", "' VALIDACIÓN DEL PEDIDO\n");

        var result = await new GrepSearchTool().ExecuteAsync(
            $$"""{"SearchPath": "{{Json(_root)}}", "Query": "validación", "CaseInsensitive": true}""", Context(), "test", NullLogger.Instance);

        Assert.Contains("VALIDACIÓN DEL PEDIDO", result);
    }

    [Fact]
    public async Task Replace_file_content_keeps_a_latin1_file_in_latin1()
    {
        var file = WriteLatin1("Pedido.bas", "' Añade la acción\nx = 1\n");

        var result = await new ReplaceFileContentTool().ExecuteAsync($$"""
            {
                "TargetFile": "{{Json(file)}}",
                "TargetContent": "x = 1",
                "ReplacementContent": "x = 2 ' corrección",
                "StartLine": 2,
                "EndLine": 2,
                "AllowMultiple": false
            }
            """, Context(), "test", NullLogger.Instance);

        Assert.Contains("Successfully replaced", result);
        // Los bytes siguen siendo Latin-1: ni U+FFFD ni la tilde reescrita en UTF-8.
        Assert.Equal(Encoding.Latin1.GetBytes("' Añade la acción\nx = 2 ' corrección\n"), await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task View_code_item_shows_the_accents_of_a_latin1_csharp_file()
    {
        var file = WriteLatin1("Pedido.cs", "class Pedido\n{\n    string Estado() => \"acción\";\n}\n");

        var result = await new ViewCodeItemTool().ExecuteAsync(
            $$"""{"File": "{{Json(file)}}", "NodePaths": ["Pedido.Estado"]}""", Context(), "test", NullLogger.Instance);

        Assert.Contains("\"acción\"", result);
    }

    [Fact]
    public async Task View_file_outline_reads_a_latin1_file_with_the_same_rule()
    {
        var file = WriteLatin1("pedido.py", "class Validación:\n    pass\n");

        var result = await new ViewFileOutlineTool().ExecuteAsync(
            $$"""{"AbsolutePath": "{{Json(file)}}"}""", Context(), "test", NullLogger.Instance);

        Assert.Contains("[MATCH] class Validación", result);
    }

    [Fact]
    public async Task The_configured_default_encoding_decodes_what_is_not_utf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var windows1252 = Encoding.GetEncoding(1252);
        var file = Path.Combine(_root, "Precio.bas");
        // 0x80 es el euro en windows-1252 y un carácter de control en Latin-1.
        await File.WriteAllBytesAsync(file, windows1252.GetBytes("' Precio en € con acción"));
        FileToolsConfiguration.DefaultEncoding = windows1252;

        var result = await new ViewFileTool().ExecuteAsync($$"""{"AbsolutePath": "{{Json(file)}}"}""", Context(), "test", NullLogger.Instance);

        Assert.Contains("1: ' Precio en € con acción", result);
    }

    [Fact]
    public void Without_configuration_the_default_encoding_is_latin1_and_null_restores_it()
    {
        Assert.Equal(Encoding.Latin1.WebName, FileToolsConfiguration.DefaultEncoding.WebName);

        FileToolsConfiguration.DefaultEncoding = Encoding.UTF32;
        FileToolsConfiguration.DefaultEncoding = null!;

        Assert.Equal(Encoding.Latin1.WebName, FileToolsConfiguration.DefaultEncoding.WebName);
    }

    [Fact]
    public void Decode_prefers_the_bom_then_valid_utf8_then_the_default()
    {
        var latin1 = TextFileEncoding.Decode(Encoding.Latin1.GetBytes("acción"), Encoding.Latin1);
        var utf8 = TextFileEncoding.Decode(new UTF8Encoding(false).GetBytes("acción"), Encoding.Latin1);
        var utf8Bom = TextFileEncoding.Decode([0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes("acción")], Encoding.Latin1);
        var utf16 = TextFileEncoding.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("acción")], Encoding.Latin1);

        Assert.Equal(("acción", "iso-8859-1"), (latin1.Text, latin1.Encoding.WebName));
        Assert.Equal(("acción", "utf-8"), (utf8.Text, utf8.Encoding.WebName));
        Assert.Empty(utf8.Encoding.GetPreamble());
        Assert.Equal(("acción", "utf-8"), (utf8Bom.Text, utf8Bom.Encoding.WebName));
        // El BOM se conserva para reescribir el fichero igual.
        Assert.Equal(3, utf8Bom.Encoding.GetPreamble().Length);
        Assert.Equal(("acción", "utf-16"), (utf16.Text, utf16.Encoding.WebName));
    }

    [Fact]
    public async Task Read_all_lines_splits_like_the_framework()
    {
        const string text = "uno\r\ndós\ntres\rcuatro\n";
        var file = WriteLatin1("lines.txt", text);

        Assert.Equal(await File.ReadAllLinesAsync(WriteUtf8("same.txt", text)), await TextFileEncoding.ReadAllLinesAsync(file));
    }
}
