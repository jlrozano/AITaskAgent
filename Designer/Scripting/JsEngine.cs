namespace AITaskAgent.Designer.Scripting;

using Jint;
using Jint.Native;
using Jint.Runtime;
using Newtonsoft.Json.Linq;

/// <summary>
/// Result of JavaScript script execution.
/// Equivalent to BRMS ScriptExecutionResult.
/// </summary>
public record ScriptExecutionResult
{
    public bool Success { get; init; }
    public object? Result { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }
    public IReadOnlyList<string> ConsoleOutput { get; init; } = [];

    public static ScriptExecutionResult SuccessResult(object? result, IEnumerable<string>? console = null) =>
    new()
    {
        Success = true,
        Result = result,
        ConsoleOutput = console?.ToList() ?? []
    };

    public static ScriptExecutionResult ErrorResult(string message, Exception? ex = null, IEnumerable<string>? console = null) =>
    new()
    {
        Success = false,
        ErrorMessage = message,
        Exception = ex,
        ConsoleOutput = console?.ToList() ?? []
    };
}

/// <summary>
/// JavaScript engine wrapper using Jint.
/// Provides a safe execution environment for user scripts.
/// </summary>
public sealed class JsEngine : IDisposable
{
    private Engine? _engine;
    private readonly List<string> _console = [];
    private readonly TimeSpan _timeout;

    public JsEngine(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        InitializeEngine();
    }

    private void InitializeEngine()
    {
        _engine = new Engine(options =>
        {
            options.TimeoutInterval(_timeout);
            options.LimitRecursion(100);
            options.MaxStatements(10000);
            options.Strict();
        });

        // Add console object
        _engine.SetValue("console", new
        {
            log = new Action<object?>(msg => _console.Add($"[LOG] {msg}")),
            warn = new Action<object?>(msg => _console.Add($"[WARN] {msg}")),
            error = new Action<object?>(msg => _console.Add($"[ERROR] {msg}"))
        });
    }

    /// <summary>
    /// Executes a JavaScript expression with context data.
    /// </summary>
    /// <param name="expression">JavaScript expression to evaluate.</param>
    /// <param name="context">Context data available as 'context' object.</param>
    /// <param name="additionalParameters">Additional named parameters.</param>
    /// <returns>Execution result.</returns>
    public ScriptExecutionResult Execute(
    string expression,
    JObject? context = null,
    Dictionary<string, object?>? additionalParameters = null)
    {
        if (_engine == null)
        {
            return ScriptExecutionResult.ErrorResult("Engine not initialized.");
        }

        _console.Clear();

        try
        {
            // Set context
            if (context != null)
            {
                _engine.SetValue("context", context.ToObject<dynamic>());
            }

            // Set additional parameters
            if (additionalParameters != null)
            {
                foreach (var (name, value) in additionalParameters)
                {
                    _engine.SetValue(name, value);
                }
            }

            // Execute
            var result = _engine.Evaluate(expression);
            var clrResult = ConvertToClr(result);

            return ScriptExecutionResult.SuccessResult(clrResult, _console);
        }
        catch (JavaScriptException jsEx)
        {
            return ScriptExecutionResult.ErrorResult(
                $"JavaScript error: {jsEx.Message} at line {jsEx.Location.Start.Line}",
                jsEx,
                _console);
        }
        catch (TimeoutException)
        {
            return ScriptExecutionResult.ErrorResult("Script execution timed out.", console: _console);
        }
        catch (Exception ex)
        {
            return ScriptExecutionResult.ErrorResult($"Script error: {ex.Message}", ex, _console);
        }
    }

    /// <summary>
    /// Executes a function with given parameters.
    /// </summary>
    public ScriptExecutionResult ExecuteFunction(
    string functionBody,
    JObject? input = null,
    JObject? output = null)
    {
        var wrappedExpression = $@"
(function(input, output) {{
{functionBody}
}})(input, output)
";

        var parameters = new Dictionary<string, object?>
        {
            ["input"] = input?.ToObject<dynamic>(),
            ["output"] = output?.ToObject<dynamic>()
        };

        return Execute(wrappedExpression, additionalParameters: parameters);
    }

    private static object? ConvertToClr(JsValue value)
    {
        return value.Type switch
        {
            Types.Undefined => null,
            Types.Null => null,
            Types.Boolean => value.AsBoolean(),
            Types.String => value.AsString(),
            Types.Number => value.AsNumber(),
            Types.Object when value.IsArray() => value.AsArray().Select(ConvertToClr).ToList(),
            Types.Object => value.ToObject() is { } obj ? JObject.FromObject(obj) : null,
            _ => value.ToObject()
        };
    }

    public void Dispose()
    {
        _engine = null;
    }
}
