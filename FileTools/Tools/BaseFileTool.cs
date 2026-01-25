using System.Diagnostics;
using AITaskAgent.Core.Models;
using AITaskAgent.LLM.Models;
using AITaskAgent.LLM.Tools.Abstractions;
using AITaskAgent.LLM.Tools.Base;
using AITaskAgent.Observability.Events;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AITaskAgent.FileTools.Tools;

/// <summary>
/// Abstract base class for file system tools providing common JSON parsing, observability and error handling.
/// </summary>
public abstract partial class BaseFileTool : LlmTool
{
    // These must be implemented by concrete tools but are handled by LlmTool for parameters if not overridden.
    // However, existing tools override GetDefinition, so we keep these abstract to force implementation
    // or rely on GetDefinition override.
    // LlmTool defines abstract Name/Description.

    // Abstract ExecuteInternalAsync matching the signature we expect derived classes to implement
    protected abstract Task<string> ExecuteInternalAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken);

    // Override LlmTool's InternalExecuteAsync to bridge to our ExecuteInternalAsync
    protected override async Task<string> InternalExecuteAsync(
        string argumentsJson,
        PipelineContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Bridge method: LlmTool calls this, we call the derived class's implementation
        return await ExecuteInternalAsync(argumentsJson, context, logger, cancellationToken);
    }

    // Helper for derived tools to send messages (Enrichment feature)
    protected async Task NotifyProgressAsync(
        string message,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        await context.SendEventAsync(new StepProgressEvent
        {
            StepName = this.Name,
            EventType = "ToolProgress",
            Message = message,
            CorrelationId = context.CorrelationId
        }, cancellationToken);
    }

    /// <summary>
    /// Case-insensitive JSON serializer settings for parsing LLM tool arguments.
    /// LLMs may send property names in different casings (path vs Path vs DirectoryPath).
    /// </summary>
    private static readonly JsonSerializerSettings CaseInsensitiveSettings = new()
    {
        // Custom contract resolver that ignores case when matching properties
        ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            NamingStrategy = null // No specific naming strategy - accept any casing
        }
    };

    /// <summary>
    /// Attempts to parse JSON arguments into the specified type with case-insensitive property matching.
    /// Returns a tuple of (success, result, errorMessage).
    /// On failure, errorMessage contains structured error with retry guidance.
    /// </summary>
    protected static (bool Success, T? Result, string? Error) TryParseArguments<T>(string json, string? expectedSchemaHint = null, ILogger? logger = null)
    {
        logger?.LogTrace("[FileTool] TryParseArguments input: {Json}, ExpectedType: {Type}", json, typeof(T).Name);

        if (string.IsNullOrWhiteSpace(json))
        {
            logger?.LogTrace("[FileTool] Empty JSON, returning default");
            return (true, default, null);
        }

        try
        {
            var jObj = Newtonsoft.Json.Linq.JObject.Parse(json);
            return ParseInternal<T>(jObj, logger);
        }
        catch (JsonException ex)
        {
            // Intentar reparar errores comunes de LLMs (como GLM-4.7) que olvidan comillas en rutas
            var sanitizedJson = SanitizeLlmJson(json);

            // Solo reintentamos si la sanitización cambió algo
            if (!string.Equals(json, sanitizedJson, StringComparison.Ordinal))
            {
                try
                {
                    logger?.LogWarning("[FileTool] JSON parse error: {Error}. Retrying with sanitized JSON: {SanitizedJson}", ex.Message, sanitizedJson);
                    // Reintento recursivo (o llamada directa a la lógica de parseo si se extrajera, aquí simplificamos re-llamando a JObject.Parse y la lógica de diccionario)

                    var jObj = Newtonsoft.Json.Linq.JObject.Parse(sanitizedJson);
                    // ... repetimos la lógica de mapeo ... 
                    // Para evitar duplicar código masivo, lo ideal sería refactorizar. 
                    // Dado que estamos en un parche, llamaremos a una versión interna o recursivamente a TryParseArguments PERO evitando bucles infinitos.
                    // Como TryParseArguments es estática y genérica, podemos llamarla.
                    // PERO necesitamos evitar recursión infinita si falla de nuevo.
                    // Una forma simple: intentar parsear a JObject aquí y si funciona, seguir el flujo normal "happy path" que teníamos arriba, o simplemente devolver el resultado de una llamada recursiva pasando un flag (que no tenemos en la firma).

                    // Estrategia: Parsear a T usando JsonConvert con el JSON sanitizado como fallback rápido.
                    // Si el diccionario case-insensitive era crítico, esto podría perderlo, pero para argumentos "fixeados" suele ser suficiente. de todas formas intentemos el mapeo manual si es posible.

                    // Opción más limpia sin cambiar firma:
                    // Intentar deserializar el sanitizado.
                    return TryParseArguments<T>(sanitizedJson, expectedSchemaHint, logger, isRetry: true);
                }
                catch (Exception retryEx)
                {
                    logger?.LogWarning("[FileTool] Sanitized JSON retry failed: {Error}", retryEx.Message);
                    // Fall through to original error reporting
                }
            }

            logger?.LogWarning("[FileTool] JSON parse error: {Error}, Input: {Json}", ex.Message, json);
            var errorMessage = $"""
                TOOL_CALL_ERROR: Invalid arguments format.
                Error: {ex.Message}
                Received: {json}
                {(expectedSchemaHint != null ? $"Expected format: {expectedSchemaHint}" : "")}
                
                Guidance: You must retry this tool call with correct JSON arguments. Do not apologize - just call the tool again with fixed arguments.
                """;
            return (false, default, errorMessage);
        }
    }

    /// <summary>
    /// Sobrecarga privada para soportar reintentos y evitar recursión infinita.
    /// </summary>
    private static (bool Success, T? Result, string? Error) TryParseArguments<T>(string json, string? expectedSchemaHint, ILogger? logger, bool isRetry)
    {
        if (isRetry)
        {
            // Versión simplificada para el reintento: confiamos en que si el JSON es válido ahora, el deserializador estándar o el parseo manual funcionarán.
            // Simplemente llamamos a la lógica principal pero sin el bloque catch que intenta sanitizar de nuevo.
            try
            {
                var jObj = Newtonsoft.Json.Linq.JObject.Parse(json);
                // (Logica duplicada del 'happy path' original... para evitar duplicación real, deberíamos refactorizar el cuerpo principal a un método 'ParseInternal')
                // Por simplicidad en este parche, usaremos JsonConvert directo para el reintento, que suele ser suficiente para "Argumentos arreglados".
                // Si necesitamos case-insensitivity estricta del mapeo manual, es mejor refactorizar.
                // Refactorizaremos extrayendo la lógica de parseo central.
                return ParseInternal<T>(jObj, logger);
            }
            catch (Exception ex)
            {
                return (false, default, ex.Message);
            }
        }
        return TryParseArguments<T>(json, expectedSchemaHint, logger);
    }

    private static (bool Success, T? Result, string? Error) ParseInternal<T>(Newtonsoft.Json.Linq.JObject jObj, ILogger? logger)
    {
        // Log all received properties for debugging
        logger?.LogDebug("[FileTool] Received properties: {Properties}",
            string.Join(", ", jObj.Properties().Select(p => $"{p.Name}={p.Value}")));

        // Create a case-insensitive dictionary from the JSON
        var caseInsensitiveDict = new Dictionary<string, Newtonsoft.Json.Linq.JToken?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in jObj.Properties())
        {
            caseInsensitiveDict[prop.Name] = prop.Value;
        }

        // Map to expected record properties using reflection
        var targetType = typeof(T);
        var constructor = targetType.GetConstructors().FirstOrDefault();
        if (constructor != null)
        {
            var parameters = constructor.GetParameters();
            var args = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var paramName = parameters[i].Name!;

                // Try to find a matching property (case-insensitive)
                if (caseInsensitiveDict.TryGetValue(paramName, out var value) && value != null)
                {
                    args[i] = value.ToObject(parameters[i].ParameterType);
                    logger?.LogTrace("[FileTool] Mapped parameter '{Param}' = '{Value}'", paramName, args[i]);
                }
                else
                {
                    // Check for common aliases: path -> DirectoryPath, Directory, etc.
                    var aliases = GetParameterAliases(paramName);
                    foreach (var alias in aliases)
                    {
                        if (caseInsensitiveDict.TryGetValue(alias, out value) && value != null)
                        {
                            args[i] = value.ToObject(parameters[i].ParameterType);
                            logger?.LogDebug("[FileTool] Mapped alias '{Alias}' -> '{Param}' = '{Value}'", alias, paramName, args[i]);
                            break;
                        }
                    }
                }
            }

            var result = (T)constructor.Invoke(args);
            logger?.LogDebug("[FileTool] Successfully parsed arguments: {Result}", result);
            return (true, result, null);
        }

        // Fallback: standard deserialization
        // Note: Since we parsed to JObject successfully, converting to T via standard way is trivial
        var serializer = JsonSerializer.Create(JsonConvert.DefaultSettings?.Invoke() ?? new JsonSerializerSettings());
        var fallbackResult = jObj.ToObject<T>(serializer);
        logger?.LogDebug("[FileTool] Fallback parse result: {Result}", fallbackResult);
        return (true, fallbackResult, null);
    }

    /// <summary>
    /// Sanitizes JSON strings from LLMs that might be malformed.
    /// e.g. unquoted paths: {"Path": C:\User\Ops} -> {"Path": "C:\User\Ops"}
    /// </summary>
    private static string SanitizeLlmJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        // Regex explanation:
        // (?<=""[\w\d_]+""\s*:\s*)  -> Lookbehind for "key": (with optional spaces)
        // (?![""{\[\d]|true|false|null) -> Negative lookahead to ensure value DOES NOT start with quote, {, [, digit, or bool/null keywords
        // ([^,}\]]+)                -> Match the value content until a separator (, or } or ])
        // (?=\s*[,}\]])             -> Lookahead for the separator

        var pattern = @"(?<=""[\w\d_]+""\s*:\s*)(?![""{\[\d]|true|false|null)([^,}\]]+)(?=\s*[,}\]])";

        return System.Text.RegularExpressions.Regex.Replace(json, pattern, match =>
        {
            var val = match.Value.Trim();
            // Escape backslashes if they aren't already escaped? 
            // Users might send C:\Path (single backslash). JSON requires double.
            // If we just quote it "C:\Path", the parser might choke on \P if it treats \ as escape.
            // But C# strings in JSON... "C:\\Path" is standard.
            // If input is raw C:\Path, and we make it "C:\Path", valid JSON parsers expect escaped backslashes.
            // So we should also escape backslashes.
            val = val.Replace("\\", "\\\\");
            return $"\"{val}\"";
        });
    }

    /// <summary>
    /// Returns common aliases for parameter names that LLMs might use.
    /// </summary>
    private static string[] GetParameterAliases(string paramName) => paramName.ToLowerInvariant() switch
    {
        "directorypath" => ["path", "directory", "dir", "folder"],
        "directory" => ["path", "directorypath", "dir", "folder"],
        "filepath" => ["path", "file", "absolutepath", "targetfile"],
        "absolutepath" => ["path", "filepath", "file", "targetfile"],  // LLM often sends TargetFile
        "targetfile" => ["path", "filepath", "file", "absolutepath"],
        "searchpath" => ["path", "directory", "dir"],
        "searchdirectory" => ["path", "directory", "dir", "searchpath"],
        _ => []
    };

    /// <summary>
    /// Simple parse without error handling. Use TryParseArguments for better error messages.
    /// </summary>
    protected static T? ParseArguments<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonConvert.DeserializeObject<T>(json);
    }

    /// <summary>
    /// Validates if the path is within the configured RootDirectory.
    /// </summary>
    protected static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(FileToolsConfiguration.RootDirectory))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetFullPath(FileToolsConfiguration.RootDirectory);

        // Normalize both paths to end with directory separator for consistent comparison
        if (!fullPath.EndsWith(Path.DirectorySeparatorChar))
        {
            fullPath += Path.DirectorySeparatorChar;
        }

        if (!rootPath.EndsWith(Path.DirectorySeparatorChar))
        {
            rootPath += Path.DirectorySeparatorChar;
        }

        // Check if fullPath is the same as rootPath OR starts with rootPath
        if (!fullPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Access denied: Path '{path}' is outside the authorized root directory '{FileToolsConfiguration.RootDirectory}'.");
        }
    }

    /// <summary>
    /// Resolves a relative path against the configured RootDirectory.
    /// If path is null/empty, returns RootDirectory.
    /// If path is absolute, returns it (after validation).
    /// If path is relative, combines with RootDirectory.
    /// </summary>
    protected static string ResolvePath(string? path)
    {
        string root = string.IsNullOrWhiteSpace(FileToolsConfiguration.RootDirectory)
            ? AppContext.BaseDirectory
            : FileToolsConfiguration.RootDirectory;

        if (string.IsNullOrWhiteSpace(path))
        {
            return root;
        }

        if (Path.IsPathRooted(path))
        {
            // It's absolute, just return it (Validation will check it later)
            return path;
        }

        // It's relative, combine with root
        return Path.GetFullPath(Path.Combine(root, path));
    }

    // We don't implement ParametersSchema here because derived classes (like ListDirTool)
    // override GetDefinition() manually, so they don't use LlmTool's default GetDefinition.
    // To satisfy LlmTool abstract requirement, we can implement it as null or throw, 
    // BUT we must implement it.
    // A clean way is to return empty BinaryData since it won't be used if GetDefinition is overridden.
    protected override BinaryData ParametersSchema => new BinaryData("{}");
}
