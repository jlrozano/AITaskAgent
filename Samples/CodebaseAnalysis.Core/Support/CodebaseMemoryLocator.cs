using System.Diagnostics;
using System.Runtime.InteropServices;
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
