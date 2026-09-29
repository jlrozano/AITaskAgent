using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace CodebaseAnalysis.Core.Support;

public static class CodebaseMemoryLocator
{
    private static readonly string[] ExecutableCandidates = 
    [
        "codebase-memory-mcp.exe",
        "codebase-memory-mcp",
        "codebase-memory.exe",
        "codebase-memory"
    ];

    public static (string? ExePath, string? Error) Locate(IConfiguration? config = null)
    {
        // 1. Configuración explícita en appsettings
        if (config != null)
        {
            var configuredCommand = config["McpServers:CodebaseMemory:Command"]
                ?? config["CodebaseMemory:CliPath"];
            if (!string.IsNullOrWhiteSpace(configuredCommand))
            {
                if (File.Exists(configuredCommand))
                {
                    return (Path.GetFullPath(configuredCommand), null);
                }

                // Si se pasó solo el nombre sin ruta, buscar en PATH
                var inPath = FindInPath(configuredCommand);
                if (inPath != null)
                {
                    return (inPath, null);
                }
            }
        }

        // 2. Ruta estándar de instalación local en Windows (%LOCALAPPDATA%\Programs\codebase-memory-mcp\...)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var defaultInstallPath = Path.Combine(localAppData, "Programs", "codebase-memory-mcp", "codebase-memory-mcp.exe");
            if (File.Exists(defaultInstallPath))
            {
                return (defaultInstallPath, null);
            }
        }

        // 3. Buscar candidatos en PATH del sistema
        foreach (var candidate in ExecutableCandidates)
        {
            var found = FindInPath(candidate);
            if (found != null)
            {
                return (found, null);
            }
        }

        return (null, "No se encontró el ejecutable 'codebase-memory-mcp.exe' ni 'codebase-memory' en PATH, ni en %LOCALAPPDATA%\\Programs\\codebase-memory-mcp\\, ni en la configuración.");
    }

    /// <summary>Plazo de <c>--version</c>: responde al instante, así que más es un ejecutable que no lo entiende.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex VersionLine = new(
        @"^codebase-memory(?:-mcp)?(?:\.exe)?\s+v?(?<version>\d+(?:\.\d+)+[\w.+-]*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Versión de cbm según <c>codebase-memory-mcp --version</c> (<c>codebase-memory-mcp 0.6.1</c>), o null si el proceso no
    /// arranca, no termina en <see cref="VersionTimeout"/>, sale con error o no imprime una versión reconocible. La BD de cbm
    /// no guarda la versión que la produjo, así que es la única fuente fiable.
    /// </summary>
    public static async Task<string?> GetVersionAsync(string exePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(VersionTimeout);
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return null;
            // Sin argumentos reconocidos arranca el servidor MCP por stdin: cerrarlo lo hace terminar.
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            await ProcessCancellation.WaitForExitOrKillAsync(process, timeout.Token);
            return process.ExitCode == 0 ? ParseVersion(await stdout) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>La versión de la primera línea con forma <c>&lt;nombre&gt; X.Y[.Z…]</c>, o null.</summary>
    internal static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = VersionLine.Match(line);
            if (match.Success)
                return match.Groups["version"].Value;
        }
        return null;
    }

    private static string? FindInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path.Trim(), fileName);
                if (File.Exists(fullPath))
                {
                    return Path.GetFullPath(fullPath);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var fullExe = fullPath + ".exe";
                    if (File.Exists(fullExe))
                    {
                        return Path.GetFullPath(fullExe);
                    }
                }
            }
            catch
            {
                // Ignorar paths no accesibles
            }
        }

        return null;
    }
}
