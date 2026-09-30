// Demo sin LLM: un paso de código (el "auditor") produce texto plano y un paso de plantilla (el
// "verificador") lo incrusta en su prompt. El verificador es un StatelessTemplateLlmStep real; el
// servicio LLM es un eco que devuelve el prompt renderizado, así que se prueba el camino de
// producción (TemplateLlmStep -> PromptTemplateResolver -> FileTemplateProvider ->
// TinyTemplateRendered) sin red.

using System.Text;
using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.StepResults;
using AITaskAgent.Core.Steps;
using AITaskAgent.LLM.Configuration;
using AITaskAgent.LLM.Results;
using AITaskAgent.LLM.Steps;
using AITaskAgent.LLM.Support;
using AITaskAgent.Support.Template;
using TextToTemplateDemo;

Console.OutputEncoding = Encoding.UTF8;

var templates = new FileTemplateProvider(
    Path.Combine(AppContext.BaseDirectory, "templates"),
    enableCache: false,
    validateTemplates: true);

var allIntact = true;

// ---------------------------------------------------------------------------------------------
// Caso A: salida string -> {{ input }}  y, en paralelo, clave de contexto -> {{ context.<clave> }}
// ---------------------------------------------------------------------------------------------
{
    var echo = new EchoLlmService();
    var original = SampleReport.Text;

    var auditor = new DelegatedStep<StepResult<string>, StepResult<string>>(
        "Auditor",
        (step, input, context, attempt, last) =>
        {
            // Nombre elegido libremente: la clave del Metadata es el nombre del placeholder.
            context.SetMetadata("informe_auditoria", original);
            return Task.FromResult(new StepResult<string>(step, original));
        });

    var verificador = new StatelessTemplateLlmStep<StepResult<string>, LlmStringStepResult>(
        "Verificador", EchoRole(echo), templates,
        promtTemplateString: "verificador_prompt", systemTemplateString: "verificador_system");

    var result = await RunAsync("AuditorVerificador", [auditor, verificador]);

    PrintCase("A", "StepResult<string> -> {{ input }} y {{ context.informe_auditoria }}", echo, original);
    allIntact &= Compare("{{ input }}", Between(echo.LastUserMessage, "input"), original);
    allIntact &= Compare("{{ context.informe_auditoria }}", Between(echo.LastUserMessage, "context.informe_auditoria"), original);
    allIntact &= Compare("salida del verificador (eco) == prompt enviado",
        (result as LlmStringStepResult)?.Content, echo.LastUserMessage ?? "");
}

// ---------------------------------------------------------------------------------------------
// Caso B: salida tipada -> {{ input.<propiedad> }}
// ---------------------------------------------------------------------------------------------
{
    var echo = new EchoLlmService();
    var original = SampleReport.Text;

    var auditor = new DelegatedStep<StepResult<string>, StepResult<InformeAuditoria>>(
        "AuditorTipado",
        (step, input, context, attempt, last) =>
            Task.FromResult(new StepResult<InformeAuditoria>(step, new InformeAuditoria(original))));

    var verificador = new StatelessTemplateLlmStep<StepResult<InformeAuditoria>, LlmStringStepResult>(
        "VerificadorTipado", EchoRole(echo), templates,
        promtTemplateString: "verificador_tipado_prompt", systemTemplateString: "verificador_system");

    await RunAsync("AuditorTipadoVerificador", [auditor, verificador]);

    PrintCase("B", "StepResult<InformeAuditoria> -> {{ input.informe }}", echo, original, printPrompt: false);
    allIntact &= Compare("{{ input.informe }}", Between(echo.LastUserMessage, "input.informe"), original);
}

// ---------------------------------------------------------------------------------------------
// Caso C: el nombre del paso como etiqueta -> ParallelStep -> {{ input.<NombrePaso>.value }}
// ---------------------------------------------------------------------------------------------
{
    var echo = new EchoLlmService();
    var original = SampleReport.Text;

    var auditor = new DelegatedStep<StepResult<string>, StepResult<string>>(
        "AuditorInforme1",
        (step, input, context, attempt, last) => Task.FromResult(new StepResult<string>(step, original)));

    var auditorias = new ParallelStep("Auditorias", auditor);

    var verificador = new StatelessTemplateLlmStep<ParallelResult, LlmStringStepResult>(
        "VerificadorParalelo", EchoRole(echo), templates,
        promtTemplateString: "verificador_paralelo_prompt", systemTemplateString: "verificador_system");

    await RunAsync("AuditoriasVerificador", [auditorias, verificador]);

    PrintCase("C", "ParallelResult -> {{ input.AuditorInforme1.value }}", echo, original, printPrompt: false);
    allIntact &= Compare("{{ input.AuditorInforme1.value }}", Between(echo.LastUserMessage, "input.AuditorInforme1.value"), original);
}

// ---------------------------------------------------------------------------------------------
// Caso D: qué nombres de placeholder resuelven con una entrada StepResult<string>.
// Mismo resolver que usa TemplateLlmStep.BuildUserMessageAsync.
// ---------------------------------------------------------------------------------------------
{
    var original = SampleReport.Text;
    var input = new StepResult<string>(new EmptyStep("Auditor"), original);
    var context = new PipelineContext();
    context.SetMetadata("informe_auditoria", original);

    Console.WriteLine();
    Console.WriteLine("=== Caso D: sondeo de placeholders con entrada StepResult<string> (PromptTemplateResolver.Resolve) ===");
    string[] probes =
    [
        "{{ value }}", "{{value}}", "{{ Value }}", "{{ VALUE }}",
        "{{ input.value }}", "{{ input.Value }}",
        "{{ input }}", "{{input}}", "{{ Input }}", "{{   INPUT   }}",
        "{{ context.informe_auditoria }}", "{{ Context.Informe_Auditoria }}",
    ];
    foreach (var probe in probes)
    {
        Console.WriteLine($"  {probe,-34} -> {Describe(PromptTemplateResolver.Resolve(probe, input, context), original)}");
    }

    Console.WriteLine();
    Console.WriteLine("  Contraste: TinyTemplateRendered.Render directo sobre el StepResult (ningún paso LLM lo hace así):");
    foreach (var probe in new[] { "{{ value }}", "{{ Value }}", "{{ stepname }}" })
    {
        Console.WriteLine($"  {probe,-34} -> {Describe(TinyTemplateRendered.Render(probe, input), original)}");
    }
}

// ---------------------------------------------------------------------------------------------
// Caso E: el texto interpolado contiene @[ruta] (inclusión legacy de TinyTemplateRendered)
// ---------------------------------------------------------------------------------------------
{
    var echo = new EchoLlmService();
    var original = SampleReport.TextWithIncludeLikeRefs;

    var auditor = new DelegatedStep<StepResult<string>, StepResult<string>>(
        "AuditorArroba",
        (step, input, context, attempt, last) =>
        {
            context.SetMetadata("informe_auditoria", original);
            return Task.FromResult(new StepResult<string>(step, original));
        });

    var verificador = new StatelessTemplateLlmStep<StepResult<string>, LlmStringStepResult>(
        "VerificadorArroba", EchoRole(echo), templates,
        promtTemplateString: "verificador_prompt", systemTemplateString: "verificador_system");

    await RunAsync("AuditorArrobaVerificador", [auditor, verificador]);

    PrintCase("E", "texto con @[ruta] -> {{ input }}", echo, original, printPrompt: false);
    var received = Between(echo.LastUserMessage, "input");
    Compare("{{ input }} con @[...]", received, original);
    PrintTail("recibido (cola)", received);
}

Console.WriteLine();
Console.WriteLine(allIntact
    ? "RESULTADO: casos A-C íntegros byte a byte."
    : "RESULTADO: algún caso A-C NO llegó íntegro.");
return allIntact ? 0 : 1;

// ---------------------------------------------------------------------------------------------

static LlmRole EchoRole(EchoLlmService service) =>
    new() { Provider = new LlmProvider { Service = service }, Model = "echo-model" };

static async Task<IStepResult> RunAsync(string name, IReadOnlyList<IStep> steps)
{
    // Contexto explícito: sin DI, PipelineContextFactory.Create lanza "not initialized".
    var result = await new Pipeline().ExecuteAsync(
        name, steps, new StepResult<string>(new EmptyStep("Seed"), ""), new PipelineContext());
    if (result.HasError)
    {
        throw new InvalidOperationException($"{name}: {result.Error?.Message}", result.Error?.OriginalException);
    }

    return result;
}

static void PrintCase(string id, string title, EchoLlmService echo, string original, bool printPrompt = true)
{
    Console.WriteLine();
    Console.WriteLine($"=== Caso {id}: {title} ===");
    if (!printPrompt)
    {
        return;
    }

    Console.WriteLine("--- system prompt renderizado ---");
    Console.WriteLine(echo.LastRequest?.SystemPrompt);
    Console.WriteLine("--- prompt de usuario renderizado ---");
    Console.WriteLine(echo.LastUserMessage);
    Console.WriteLine("--- texto original ---");
    Console.WriteLine(original);
    Console.WriteLine("--- fin ---");
}

// Segmento entre "=====BEGIN <tag>=====\n" y "\n=====END <tag>=====".
static string? Between(string? text, string tag)
{
    if (text is null)
    {
        return null;
    }

    var begin = $"=====BEGIN {tag}=====\n";
    var end = $"\n=====END {tag}=====";
    var start = text.IndexOf(begin, StringComparison.Ordinal);
    if (start < 0)
    {
        return null;
    }

    start += begin.Length;
    var stop = text.IndexOf(end, start, StringComparison.Ordinal);
    return stop < 0 ? null : text[start..stop];
}

static bool Compare(string label, string? received, string original)
{
    var a = Encoding.UTF8.GetBytes(original);
    var b = Encoding.UTF8.GetBytes(received ?? "");
    var firstDiff = -1;
    for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
    {
        if (i >= a.Length || i >= b.Length || a[i] != b[i])
        {
            firstDiff = i;
            break;
        }
    }

    var ok = received is not null && firstDiff < 0;
    Console.WriteLine(ok
        ? $"  [OK]   {label}: {b.Length} bytes idénticos al original ({a.Length})."
        : received is null
            ? $"  [FAIL] {label}: segmento no encontrado."
            : $"  [FAIL] {label}: {b.Length} bytes frente a {a.Length}; primer byte distinto en {firstDiff}.");
    return ok;
}

static string Describe(string rendered, string original) =>
    rendered == original ? "íntegro (== original)"
    : rendered.Length == 0 ? "VACÍO (no resuelve; sin error)"
    : rendered.Length > 70 ? $"otro ({rendered.Length} chars): {rendered[..70].ReplaceLineEndings(" ")}…"
    : $"literal/otro: \"{rendered}\"";

static void PrintTail(string label, string? text)
{
    Console.WriteLine($"--- {label} ---");
    var lines = (text ?? "").Split('\n');
    foreach (var line in lines.Skip(Math.Max(0, lines.Length - 5)))
    {
        Console.WriteLine(line);
    }

    Console.WriteLine("--- fin ---");
}

namespace TextToTemplateDemo
{
    /// <summary>Salida tipada del auditor: el nombre de la propiedad es el del placeholder.</summary>
    internal sealed record InformeAuditoria(string Informe);
}
