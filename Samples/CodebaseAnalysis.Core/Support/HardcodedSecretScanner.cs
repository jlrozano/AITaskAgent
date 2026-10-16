using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// Busca credenciales escritas como literal en los ficheros de código que conoce el grafo. Es un
/// paso determinista porque los auditores solo leen el código que decide el modelo: una contraseña
/// en un módulo pequeño, fuera de los hotspots precargados, no aparecía en ningún informe. Las
/// expresiones no dependen del lenguaje, y el valor se enmascara para que el informe cite la línea
/// sin reproducir el secreto.
/// </summary>
public static class HardcodedSecretScanner
{
    private const int MaxFindings = 40;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".accdb", ".mdb", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".pdf", ".dll", ".exe",
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".db", ".sqlite"
    };

    // Identificador con nombre de credencial asignado a un literal de cadena:
    // Const Password As String = "x", api_key = 'x', "secret": "x", token := "x".
    private static readonly Regex AssignmentRegex = new(
        @"(?<name>\b\w*(?:password|passwd|pwd|secret|api_?key|token|credential)\w*\b)[""']?\s*(?:As\s+String\s*)?(?:=|:=|:)\s*(?<q>[""'])(?<value>[^""'\r\n]{3,})\k<q>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Clave de cadena de conexión con valor literal: "...;PWD=x;..." o "Password=x".
    private static readonly Regex ConnectionStringRegex = new(
        @"\b(?<name>pwd|password)\s*=\s*(?<value>[^;""'\s&+]{3,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<string> ScanAsync(IEnumerable<string> files, string repoPath, CancellationToken ct)
    {
        var findings = new List<Dictionary<string, object>>();

        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (findings.Count >= MaxFindings)
                break;

            var path = Resolve(file, repoPath);
            if (path == null || BinaryExtensions.Contains(Path.GetExtension(path)) || new FileInfo(path).Length > MaxFileBytes)
                continue;

            string[] lines;
            try
            {
                lines = await AITaskAgent.FileTools.TextFileEncoding.ReadAllLinesAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length && findings.Count < MaxFindings; i++)
            {
                var line = lines[i];
                if (line.Contains('\0'))
                    break;

                var match = AssignmentRegex.Match(line);
                var kind = "literal asignado a un identificador de credencial";
                if (match.Success && NamesAReference(match.Groups["name"].Value))
                    match = Match.Empty;
                if (!match.Success)
                {
                    match = ConnectionStringRegex.Match(line);
                    kind = "credencial en cadena de conexión";
                }

                if (!match.Success || IsPlaceholder(match.Groups["value"].Value))
                    continue;

                var value = match.Groups["value"];
                findings.Add(new Dictionary<string, object>
                {
                    ["file"] = file,
                    ["start_line"] = i + 1,
                    ["identifier"] = match.Groups["name"].Value,
                    ["kind"] = kind,
                    ["evidence"] = (line[..value.Index] + Mask(value.Value) + line[(value.Index + value.Length)..]).Trim()
                });
            }
        }

        return findings.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(findings, Steps.SqlFacetExtractorStep.FacetJsonOptions);
    }

    private static string? Resolve(string file, string repoPath)
    {
        try
        {
            var path = Path.IsPathRooted(file) ? file : Path.Combine(repoPath, file);
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True si el identificador acaba en un segmento de referencia (<see cref="CredentialNames.NamesAReference"/>).</summary>
    public static bool NamesAReference(string identifier) => CredentialNames.NamesAReference(identifier);

    private static bool IsPlaceholder(string value) => CredentialNames.IsPlaceholder(value);

    private static string Mask(string value) => CredentialNames.Mask(value);
}
