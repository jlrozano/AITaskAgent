using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Attributes;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace AITaskAgent.CSharp;

/// <summary>
/// Runs C# declared in the pipeline (YAML <c>type: CSharpStep</c>) in the host process. The text of <see cref="Code"/> is the
/// <i>body</i> of <c>static async Task&lt;IStepResult&gt; Step(StepCall call)</c>: the host compiles the bodies of all the
/// <c>CSharpStep</c>s of a package, together with the package's shared declarations, into one unit (one static method per step)
/// and registers it as <see cref="ICSharpUnit"/>; this step is the delegated step that calls its method.
/// </summary>
/// <remarks>
/// <para>The body has the full API: <see cref="StepCall.Context"/> (the <c>PipelineContext</c>), the previous result
/// (<see cref="StepCall.Input"/>), the attempt, the last result and the services of the run, and it may return
/// <c>NextSteps</c> (<see cref="StepCall.Ok(object?, IEnumerable{IStep})"/>). It returns an <see cref="IStepResult"/>
/// (<c>return call.Ok(value);</c>).</para>
/// <para><b>Not isolated.</b> The code runs in the process of the host with its permissions (files, network, environment, memory);
/// an <c>Environment.Exit</c> ends the host and an endless loop cannot be stopped (cancellation is cooperative: use
/// <see cref="StepCall.CancellationToken"/>). That is why a package that carries C# has to be trusted before it runs
/// (<see cref="ICSharpStepPolicy"/>). A hook of the unit that is not a step does not need this step at all.</para>
/// <para>It derives from <see cref="StepBase"/> and not from <c>DelegatedStep</c>: the latter carries no
/// <see cref="CancellationToken"/> and, closed over <see cref="IStepResult"/>, makes two overloads of <c>ExecuteAsync</c> collide.</para>
/// </remarks>
[StepTag("CSharpStep")]
[Description("Runs C# in the host process; the code is the body of a method that receives the StepCall (context, previous result) and returns an IStepResult.")]
public sealed partial class CSharpStep : StepBase
{
    /// <summary>Creates the step; the registry sets <see cref="StepBase.Name"/> from the YAML <c>id</c>.</summary>
    public CSharpStep() : this("CSharpStep")
    {
    }

    /// <summary>Creates the step with an explicit name (programmatic use).</summary>
    public CSharpStep(string name) : base(name, typeof(IStepResult), typeof(JsonStepResult))
    {
        // A failing body is deterministic: retrying it only repeats the failure.
        MaxRetries = 1;
    }

    /// <summary>Body of the method (multiline <c>|</c> block in YAML). See the type remarks.</summary>
    [Description("Body of the step's method: receives call (StepCall) and must return an IStepResult, e.g. return call.Ok(value);")]
    public string Code { get; set; } = "";

    /// <inheritdoc/>
    protected override async Task<IStepResult> ExecuteAsync(
        IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken)
    {
        InitializeLogger(context);
        IServiceProvider services = context.ServiceProvider;

        if (services.GetService<ICSharpStepPolicy>()?.Deny(Name) is { } reason)
        {
            return Fail(reason);
        }

        if (services.GetService<ICSharpUnit>() is not { } unit)
        {
            return Fail("there is no compiled C# unit: the host that runs this pipeline has to compile the code of the package and register it (AddCSharpUnit).");
        }

        if (!unit.TryGetStep(Name, out CSharpStepMethod? method))
        {
            return Fail($"the compiled unit has no method for step '{Name}': the unit was compiled from another version of the package.");
        }

        try
        {
            IStepResult? result = await method(new StepCall(this, input, context, attempt, lastStepResult, services, cancellationToken));
            return result ?? Fail("the code returned null: return call.Ok(...) or call.Fail(...).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The unit is compiled with #line, so the stack trace names workflow.yaml and the YAML line of the failing statement.
            Match where = YamlFrame().Match(ex.StackTrace ?? string.Empty);
            return Fail($"{ex.GetType().Name}: {ex.Message}{(where.Success ? $" (workflow.yaml, line {where.Groups[1].Value})" : string.Empty)}", ex);
        }
    }

    private IStepResult Fail(string message, Exception? exception = null)
    {
        string full = $"CSharpStep '{Name}' failed: {message}";
        Logger.LogError(exception, "{Message}", full);
        return CreateErrorResult(full, exception);
    }

    [GeneratedRegex(@"workflow\.yaml:line (\d+)")]
    private static partial Regex YamlFrame();
}
