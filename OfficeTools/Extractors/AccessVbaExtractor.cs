using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using OfficeTools.Abstractions;
using OfficeTools.Models;

namespace OfficeTools.Extractors;

/// <summary>
/// Service that automates Microsoft Access via COM / PowerShell to export modules, forms,
/// reports and queries, and performs syntactic and semantic parsing of VBA code and UI controls.
/// </summary>
public class AccessVbaExtractor : IAccessVbaExtractor
{
    private static readonly Regex ProcPattern = new(
        @"^\s*(?:Public|Private|Friend)?\s*(?:Static)?\s*(Sub|Function|Property\s+Get|Property\s+Let|Property\s+Set)\s+([a-zA-Z0-9_]+)\s*(\(.*?\))?(?:\s+As\s+([a-zA-Z0-9_]+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EndProcPattern = new(
        @"^\s*End\s+(Sub|Function|Property)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ControlBeginPattern = new(
        @"^\s*Begin\s+(Label|Rectangle|Image|CommandButton|BoundObjectFrame|TextBox|ComboBox|ListBox|CheckBox|OptionButton|ToggleButton|Subform|UnboundObjectFrame|CustomControl|TabControl|Page)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public bool IsAccessComAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            var accessType = Type.GetTypeFromProgID("Access.Application");
            return accessType != null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<AccessVbaProject?> ExtractVbaAndFormsAsync(
        string databasePath,
        string? exportDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return null;

        var fullDbPath = Path.GetFullPath(databasePath);
        var targetDir = !string.IsNullOrWhiteSpace(exportDirectory)
            ? Path.GetFullPath(exportDirectory)
            : Path.Combine(Path.GetDirectoryName(fullDbPath)!, ".codebase", "access_export");

        Directory.CreateDirectory(targetDir);
        var modulesDir = Path.Combine(targetDir, "modules");
        var formsDir = Path.Combine(targetDir, "forms");
        var reportsDir = Path.Combine(targetDir, "reports");
        var queriesDir = Path.Combine(targetDir, "queries");

        Directory.CreateDirectory(modulesDir);
        Directory.CreateDirectory(formsDir);
        Directory.CreateDirectory(reportsDir);
        Directory.CreateDirectory(queriesDir);

        bool exportSuccess = false;

        if (IsAccessComAvailable())
        {
            Console.WriteLine($"[AccessVbaExtractor] 🚀 Exportando código VBA y Formularios vía Access COM...");
            exportSuccess = await RunPowerShellComExportAsync(fullDbPath, targetDir, cancellationToken);
        }
        else
        {
            Console.WriteLine("[AccessVbaExtractor] ℹ️ Access COM no está disponible en este entorno.");
        }

        // Si falló COM pero existen archivos exportados previamente en targetDir, procedemos a parsear
        bool hasExportedFiles = Directory.EnumerateFiles(modulesDir, "*.bas").Any() ||
                                Directory.EnumerateFiles(formsDir, "*.txt").Any();

        if (!exportSuccess && !hasExportedFiles)
        {
            Console.WriteLine("[AccessVbaExtractor] ⚠️ No se pudieron exportar módulos ni formularios de Access.");
            return null;
        }

        return await ParseExportDirectoryAsync(targetDir, fullDbPath, cancellationToken);
    }

    private static async Task<bool> RunPowerShellComExportAsync(
        string databasePath,
        string exportDir,
        CancellationToken cancellationToken)
    {
        var escapedDb = databasePath.Replace("'", "''");
        var escapedOut = exportDir.Replace("'", "''");

        var psCommand = $@"
$ErrorActionPreference = 'Stop'
$dbPath = '{escapedDb}'
$outDir = '{escapedOut}'
$modulesDir = Join-Path $outDir 'modules'
$formsDir = Join-Path $outDir 'forms'
$reportsDir = Join-Path $outDir 'reports'
$queriesDir = Join-Path $outDir 'queries'

$access = New-Object -ComObject Access.Application
$access.Visible = $false

try {{
    $access.OpenCurrentDatabase($dbPath)
    $currDb = $access.CurrentDb()

    foreach ($mod in $access.CurrentProject.AllModules) {{
        $target = Join-Path $modulesDir ""$($mod.Name).bas""
        try {{ $access.SaveAsText(5, $mod.Name, $target) }} catch {{}}
    }}

    foreach ($frm in $access.CurrentProject.AllForms) {{
        $target = Join-Path $formsDir ""$($frm.Name).txt""
        try {{ $access.SaveAsText(2, $frm.Name, $target) }} catch {{}}
    }}

    foreach ($rep in $access.CurrentProject.AllReports) {{
        $target = Join-Path $reportsDir ""$($rep.Name).txt""
        try {{ $access.SaveAsText(3, $rep.Name, $target) }} catch {{}}
    }}

    foreach ($qdf in $currDb.QueryDefs) {{
        if ($qdf.Name -notlike '~*') {{
            $target = Join-Path $queriesDir ""$($qdf.Name).sql""
            try {{ $qdf.SQL | Out-File -FilePath $target -Encoding utf8 }} catch {{}}
        }}
    }}
}} finally {{
    try {{ $access.CloseCurrentDatabase() }} catch {{}}
    try {{ $access.Quit() }} catch {{}}
    [System.Runtime.Interopservices.Marshal]::ReleaseComObject($access) | Out-Null
}}
";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand.Replace("\"", "\\\"").Replace("\r\n", " ")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(3), cancellationToken);
            var processTask = process.WaitForExitAsync(cancellationToken);

            var completed = await Task.WhenAny(processTask, timeoutTask);
            if (completed == timeoutTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Console.WriteLine("[AccessVbaExtractor] ⏱️ Timeout esperando exportación Access COM.");
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AccessVbaExtractor] ⚠️ Error ejecutando PowerShell Access COM: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public Task<AccessVbaProject> ParseExportDirectoryAsync(
        string exportDirectory,
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var project = new AccessVbaProject
        {
            DatabasePath = databasePath,
            ExportDirectory = exportDirectory,
            ExportedAt = DateTime.UtcNow
        };

        var modulesDir = Path.Combine(exportDirectory, "modules");
        var formsDir = Path.Combine(exportDirectory, "forms");
        var reportsDir = Path.Combine(exportDirectory, "reports");

        // 1. Parse standard and class modules (.bas, .cls)
        if (Directory.Exists(modulesDir))
        {
            foreach (var file in Directory.EnumerateFiles(modulesDir, "*.*"))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".bas" && ext != ".cls") continue;

                var modName = Path.GetFileNameWithoutExtension(file);
                var content = ReadTextFile(file);
                var procs = ParseProcedures(content, "Module", modName);

                var mod = new AccessModuleInfo
                {
                    Name = modName,
                    FilePath = file,
                    IsClassModule = ext == ".cls",
                    Procedures = procs
                };
                project.Modules.Add(mod);
            }
        }

        // 2. Parse forms (.txt)
        if (Directory.Exists(formsDir))
        {
            foreach (var file in Directory.EnumerateFiles(formsDir, "*.txt"))
            {
                var formName = Path.GetFileNameWithoutExtension(file);
                var content = ReadTextFile(file);
                var formInfo = ParseFormFile(content, formName, file);
                project.Forms.Add(formInfo);
            }
        }

        // 3. Parse reports (.txt)
        if (Directory.Exists(reportsDir))
        {
            foreach (var file in Directory.EnumerateFiles(reportsDir, "*.txt"))
            {
                var repName = Path.GetFileNameWithoutExtension(file);
                var content = ReadTextFile(file);
                var repInfo = ParseReportFile(content, repName, file);
                project.Reports.Add(repInfo);
            }
        }

        // 4. Infer Cross-procedure calls and data access
        AnalyzeRelationships(project);

        Console.WriteLine($"[AccessVbaExtractor] ✅ Parseo completado: {project.Modules.Count} módulos, {project.Forms.Count} formularios, {project.Reports.Count} informes ({project.TotalLoc} LOC en {project.AllProcedures.Count()} procedimientos).");
        return Task.FromResult(project);
    }

    private static AccessFormInfo ParseFormFile(string content, string formName, string filePath)
    {
        var form = new AccessFormInfo
        {
            Name = formName,
            FilePath = filePath
        };

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int codeBehindIndex = -1;

        // Extract UI controls and Caption from the design portion
        AccessFormControl? currentControl = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Equals("CodeBehindForm", StringComparison.OrdinalIgnoreCase))
            {
                codeBehindIndex = i + 1;
                break;
            }

            if (form.Caption == null && line.StartsWith("Caption =", StringComparison.OrdinalIgnoreCase))
            {
                var caption = line["Caption =".Length..].Trim().Trim('"');
                form.Caption = caption;
            }

            var ctrlMatch = ControlBeginPattern.Match(line);
            if (ctrlMatch.Success)
            {
                currentControl = new AccessFormControl
                {
                    ControlType = ctrlMatch.Groups[1].Value
                };
                form.Controls.Add(currentControl);
                continue;
            }

            if (currentControl != null)
            {
                if (line.StartsWith("Name =", StringComparison.OrdinalIgnoreCase))
                {
                    currentControl.Name = line["Name =".Length..].Trim().Trim('"');
                }
                else if (line.StartsWith("Caption =", StringComparison.OrdinalIgnoreCase))
                {
                    currentControl.Caption = line["Caption =".Length..].Trim().Trim('"');
                }
                else if (line.StartsWith("ControlSource =", StringComparison.OrdinalIgnoreCase))
                {
                    currentControl.ControlSource = line["ControlSource =".Length..].Trim().Trim('"');
                }
                else if (line.StartsWith("OnClick =", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("AfterUpdate =", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("OnDblClick =", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                        currentControl.Properties[parts[0].Trim()] = parts[1].Trim().Trim('"');
                }
            }
        }

        // Parse code behind
        if (codeBehindIndex >= 0 && codeBehindIndex < lines.Length)
        {
            var codeContent = string.Join("\n", lines.Skip(codeBehindIndex));
            var procs = ParseProcedures(codeContent, "Form", formName, lineOffset: codeBehindIndex);
            foreach (var p in procs)
            {
                p.IsEventHandler = true;
            }
            form.Procedures = procs;
        }

        return form;
    }

    private static AccessReportInfo ParseReportFile(string content, string repName, string filePath)
    {
        var rep = new AccessReportInfo
        {
            Name = repName,
            FilePath = filePath
        };

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int codeBehindIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals("CodeBehindForm", StringComparison.OrdinalIgnoreCase))
            {
                codeBehindIndex = i + 1;
                break;
            }
        }

        if (codeBehindIndex >= 0 && codeBehindIndex < lines.Length)
        {
            var codeContent = string.Join("\n", lines.Skip(codeBehindIndex));
            rep.Procedures = ParseProcedures(codeContent, "Report", repName, lineOffset: codeBehindIndex);
            foreach (var p in rep.Procedures)
            {
                p.IsEventHandler = true;
            }
        }

        return rep;
    }

    private static List<AccessVbaProcedure> ParseProcedures(string content, string parentType, string parentName, int lineOffset = 0)
    {
        var result = new List<AccessVbaProcedure>();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        AccessVbaProcedure? currentProc = null;
        var docLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();
            int currentLineNumber = lineOffset + i + 1;

            if (currentProc == null)
            {
                if (trimmed.StartsWith("'") || trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
                {
                    var comment = trimmed.TrimStart('\'').Trim();
                    if (comment.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
                        comment = comment[4..].Trim();
                    docLines.Add(comment);
                    continue;
                }

                var match = ProcPattern.Match(rawLine);
                if (match.Success)
                {
                    var pType = match.Groups[1].Value;
                    var pName = match.Groups[2].Value;
                    var pArgs = match.Groups[3].Success ? match.Groups[3].Value : "()";
                    var pRet = match.Groups[4].Success ? match.Groups[4].Value : "Void";

                    string kind = "Sub";
                    if (pType.Contains("Function", StringComparison.OrdinalIgnoreCase))
                        kind = "Function";
                    else if (pType.Contains("Property", StringComparison.OrdinalIgnoreCase))
                        kind = "Property";

                    currentProc = new AccessVbaProcedure
                    {
                        Name = pName,
                        Kind = kind,
                        ParentType = parentType,
                        ParentName = parentName,
                        Args = pArgs,
                        ReturnType = pRet,
                        StartLine = currentLineNumber,
                        Docstring = docLines.Count > 0 ? string.Join(" ", docLines) : null
                    };
                    docLines.Clear();
                    continue;
                }

                // Empty line clears pending comments
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    docLines.Clear();
                }
            }
            else
            {
                currentProc.BodyLines.Add(rawLine);

                if (EndProcPattern.IsMatch(rawLine))
                {
                    currentProc.EndLine = currentLineNumber;
                    result.Add(currentProc);
                    currentProc = null;
                    docLines.Clear();
                }
            }
        }

        return result;
    }

    private static void AnalyzeRelationships(AccessVbaProject project)
    {
        var allProcs = project.AllProcedures.ToList();
        var knownProcs = allProcs
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Regex patterns for SQL and table operations
        var tableFromPattern = new Regex(@"\b(?:FROM|JOIN|INTO|UPDATE|TABLE)\s+\[?([a-zA-Z0-9_#]+)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var openRecordsetPattern = new Regex(@"OpenRecordset\s*\(\s*[""']([a-zA-Z0-9_#]+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var passwordPattern = new Regex(@"(?:Password|pwd)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var truncatePattern = new Regex(@"\bTRUNCATE\s+TABLE\s+\[?([a-zA-Z0-9_#]+)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (var proc in allProcs)
        {
            var bodyText = string.Join("\n", proc.BodyLines);

            // 1. Calls to other procedures
            foreach (var kvp in knownProcs)
            {
                if (string.Equals(kvp.Key, proc.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Match Call <Name> or isolated proc call
                var callRegex = new Regex(@"\b(?:Call\s+)?" + Regex.Escape(kvp.Key) + @"\b", RegexOptions.IgnoreCase);
                if (callRegex.IsMatch(bodyText))
                {
                    foreach (var target in kvp.Value)
                    {
                        proc.Calls.Add(target.QualifiedName);
                    }
                }
            }

            // 2. Table Reads / Writes
            foreach (Match m in tableFromPattern.Matches(bodyText))
            {
                var tbl = m.Groups[1].Value;
                if (!IsSqlReservedKeyword(tbl) && !proc.ReadsTables.Contains(tbl, StringComparer.OrdinalIgnoreCase))
                {
                    proc.ReadsTables.Add(tbl);
                }
            }

            foreach (Match m in openRecordsetPattern.Matches(bodyText))
            {
                var tbl = m.Groups[1].Value;
                if (!proc.ReadsTables.Contains(tbl, StringComparer.OrdinalIgnoreCase))
                {
                    proc.ReadsTables.Add(tbl);
                }
            }

            // 3. Security issues & Anti-patterns
            var pwdMatch = passwordPattern.Match(bodyText);
            if (pwdMatch.Success)
            {
                proc.DetectedIssues.Add($"Hardcoded Credential detected: '{pwdMatch.Groups[1].Value}' (CWE-798)");
            }

            var truncMatch = truncatePattern.Match(bodyText);
            if (truncMatch.Success)
            {
                proc.DetectedIssues.Add($"Unprotected TRUNCATE TABLE on '{truncMatch.Groups[1].Value}' without transaction boundary");
            }
        }
    }

    private static bool IsSqlReservedKeyword(string word)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WHERE", "GROUP", "ORDER", "HAVING", "SELECT", "INSERT", "DELETE", "UPDATE",
            "SET", "VALUES", "INNER", "LEFT", "RIGHT", "OUTER", "JOIN", "ON", "AND", "OR",
            "NOT", "NULL", "AS", "DISTINCT", "TOP", "CASE", "WHEN", "THEN", "ELSE", "END",
            "UNION", "ALL", "EXISTS", "BETWEEN", "IN", "LIKE", "IS"
        };
        return keywords.Contains(word);
    }

    private static string ReadTextFile(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);

        // Check UTF-16LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        // Check UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // Heuristic: check if contains null bytes at odd positions (UTF-16LE without BOM)
        if (bytes.Length > 4 && bytes[1] == 0 && bytes[3] == 0)
            return Encoding.Unicode.GetString(bytes);

        return Encoding.UTF8.GetString(bytes);
    }
}
