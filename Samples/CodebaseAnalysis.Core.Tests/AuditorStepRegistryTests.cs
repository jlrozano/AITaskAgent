using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Execution;
using AITaskAgent.Core.Models;
using AITaskAgent.Core.Steps;
using CodebaseAnalysis.Core.Models;
using CodebaseAnalysis.Core.Preparation;

namespace CodebaseAnalysis.Core.Tests;

/// <summary>
/// <see cref="AuditorStepRegistry"/> (T15): una instancia por pipeline, sin estáticos, que guarda por informe el paso que lo
/// escribe y, para los de modo B, la tabla de límites de sus investigaciones; y que escribe <c>Timeout</c> en los pasos que el
/// framework deja asignar (<see cref="StepBase"/>) o que lo declaran (<see cref="IWritableTimeout"/>).
/// </summary>
public sealed class AuditorStepRegistryTests
{
    /// <summary>Paso de un auditor en modo A: un <see cref="StepBase"/> corriente, como los que crea la factoría.</summary>
    private sealed class SleepingStep(string name, TimeSpan sleep) : TypedStep<AnalysisPipelineInput, AnalysisPipelineInput>(name)
    {
        protected override async Task<AnalysisPipelineInput> ExecuteAsync(
            AnalysisPipelineInput input, PipelineContext context, int Attempt, AnalysisPipelineInput? LastStepResult, CancellationToken cancellationToken)
        {
            await Task.Delay(sleep, cancellationToken);
            return input;
        }
    }

    /// <summary>Envoltorio de modo B de mentira: implementa <c>IStep</c> a mano, con <c>Timeout { get; set; }</c>, y recibe la tabla de límites.</summary>
    private sealed class FakeWrapper(string name) : IStep, IWritableTimeout, IInvestigationTimeouts
    {
        public string Name { get; } = name;
        public IResume? Resume => null;
        public TimeSpan? Timeout { get; set; }
        public Type InputType => typeof(ReportFacetInput);
        public Type OutputType => typeof(SingleReportResult);
        public IReadOnlyDictionary<string, TimeSpan> Table { get; private set; } = new Dictionary<string, TimeSpan>();

        public void SetInvestigationTimeouts(IReadOnlyDictionary<string, TimeSpan> bySubsystemId) => Table = bySubsystemId;

        public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Un paso que no deja asignar su <c>Timeout</c> (solo lo declara): el registro lo dice en lugar de fallar.</summary>
    private sealed class ReadOnlyStep : IStep
    {
        public string Name => "ReadOnly";
        public IResume? Resume => null;
        public Type InputType => typeof(ReportFacetInput);
        public Type OutputType => typeof(ReportFacetInput);

        public Task<IStepResult> ExecuteAsync(IStepResult input, PipelineContext context, int attempt, IStepResult? lastStepResult, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void It_registers_each_report_with_its_step_and_tells_the_modes_apart_by_the_investigation_table()
    {
        var registry = new AuditorStepRegistry();
        var modeA = new SleepingStep("Auditor_CodeSmells", TimeSpan.Zero);
        var modeB = new FakeWrapper("Auditor_AnalisisFuncional");

        registry.Register(ReportType.CodeSmells, modeA);
        registry.Register(ReportType.AnalisisFuncional, modeB);

        Assert.Same(modeA, registry.StepOf(ReportType.CodeSmells));
        Assert.Same(modeB, registry.StepOf(ReportType.AnalisisFuncional));
        Assert.Null(registry.StepOf(ReportType.ErroresYBugs));
        Assert.False(registry.IsModeB(ReportType.CodeSmells));
        Assert.True(registry.IsModeB(ReportType.AnalisisFuncional));
        Assert.False(registry.IsModeB(ReportType.ErroresYBugs));
        Assert.Equal([ReportType.AnalisisFuncional, ReportType.CodeSmells], registry.Reports);
        Assert.Equal([ReportType.AnalisisFuncional], registry.ModeBReports);
        Assert.Equal([ReportType.CodeSmells], registry.ModeAReports);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Registering_a_report_again_replaces_its_step()
    {
        var registry = new AuditorStepRegistry();
        var first = new SleepingStep("a", TimeSpan.Zero);
        var second = new FakeWrapper("b");

        registry.Register(ReportType.ErroresYBugs, first);
        registry.Register(ReportType.ErroresYBugs, second);

        Assert.Same(second, registry.StepOf(ReportType.ErroresYBugs));
        Assert.True(registry.IsModeB(ReportType.ErroresYBugs));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Two_registries_are_independent_and_nothing_is_static()
    {
        var one = new AuditorStepRegistry();
        var two = new AuditorStepRegistry();

        one.Register(ReportType.CodeSmells, new SleepingStep("a", TimeSpan.Zero));

        Assert.Equal(1, one.Count);
        Assert.Equal(0, two.Count);
        var staticState = typeof(AuditorStepRegistry).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(f => !f.IsLiteral && !f.IsInitOnly).ToList();
        Assert.Empty(staticState);
    }

    [Fact]
    public void It_writes_the_timeout_into_a_step_base_and_into_a_step_that_declares_it_writable()
    {
        var registry = new AuditorStepRegistry();
        var auditor = new SleepingStep("Auditor_CodeSmells", TimeSpan.Zero) { Timeout = TimeSpan.FromMinutes(15) };
        var wrapper = new FakeWrapper("Auditor_AnalisisFuncional");
        registry.Register(ReportType.CodeSmells, auditor);
        registry.Register(ReportType.AnalisisFuncional, wrapper);

        Assert.True(registry.TrySetTimeout(ReportType.CodeSmells, TimeSpan.FromMinutes(33)));
        Assert.True(registry.TrySetTimeout(ReportType.AnalisisFuncional, TimeSpan.FromMinutes(44)));

        Assert.Equal(TimeSpan.FromMinutes(33), auditor.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(44), wrapper.Timeout);
    }

    [Fact]
    public void A_step_that_does_not_let_its_timeout_be_assigned_and_an_unregistered_report_return_false()
    {
        var registry = new AuditorStepRegistry();
        registry.Register(ReportType.DeudaTecnicaHotspots, new ReadOnlyStep());

        Assert.False(registry.TrySetTimeout(ReportType.DeudaTecnicaHotspots, TimeSpan.FromMinutes(1)));
        Assert.False(registry.TrySetTimeout(ReportType.CodigoMuerto, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void It_hands_the_table_of_investigation_limits_to_a_mode_b_wrapper_and_refuses_a_mode_a_report()
    {
        var registry = new AuditorStepRegistry();
        var wrapper = new FakeWrapper("Auditor_FlujosEndToEnd");
        registry.Register(ReportType.FlujosEndToEnd, wrapper);
        registry.Register(ReportType.CodeSmells, new SleepingStep("a", TimeSpan.Zero));
        var table = new Dictionary<string, TimeSpan> { ["billing"] = TimeSpan.FromMinutes(9), ["catalog"] = Timeout.InfiniteTimeSpan };

        Assert.True(registry.TrySetInvestigationTimeouts(ReportType.FlujosEndToEnd, table));
        Assert.False(registry.TrySetInvestigationTimeouts(ReportType.CodeSmells, table));
        Assert.False(registry.TrySetInvestigationTimeouts(ReportType.ErroresYBugs, table));

        Assert.Equal(TimeSpan.FromMinutes(9), wrapper.Table["billing"]);
        Assert.Equal(Timeout.InfiniteTimeSpan, wrapper.Table["catalog"]);
    }

    /// <summary>
    /// El criterio de A4: el valor que escribe el registro es el que aplica <c>TimeoutMiddleware</c> en el motor real. El paso
    /// duerme 30 s, tenía 15 min y el registro le deja 250 ms: termina en menos de 10 s y con error. El control es el mismo paso
    /// con 15 min y un sueño corto, que acaba bien (no falla por otra causa).
    /// </summary>
    [Fact]
    public async Task The_timeout_written_by_the_registry_is_the_one_the_real_engine_enforces()
    {
        var step = new SleepingStep("Auditor_CodeSmells", TimeSpan.FromSeconds(30)) { Timeout = TimeSpan.FromMinutes(15), MaxRetries = 1 };
        var control = new SleepingStep("Auditor_Control", TimeSpan.FromMilliseconds(600)) { Timeout = TimeSpan.FromMinutes(15), MaxRetries = 1 };
        var registry = new AuditorStepRegistry();
        registry.Register(ReportType.CodeSmells, step);
        registry.TrySetTimeout(ReportType.CodeSmells, TimeSpan.FromMilliseconds(250));

        var ok = await new Pipeline().ExecuteAsync("control", [control], new AnalysisPipelineInput(), new PipelineContext());
        var started = DateTime.UtcNow;
        var result = await new Pipeline().ExecuteAsync("run", [step], new AnalysisPipelineInput(), new PipelineContext());

        Assert.False(ok.HasError, ok.Error?.Message);
        Assert.True(result.HasError);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void The_reports_parallel_is_registered_apart_from_the_reports_and_its_timeout_can_be_written()
    {
        var registry = new AuditorStepRegistry();
        Assert.Null(registry.ReportsParallel);
        Assert.False(registry.TrySetReportsParallelTimeout(TimeSpan.FromMinutes(1)));

        var branch = new SleepingStep("Auditor_CodeSmells", TimeSpan.Zero);
        var parallel = new ParallelStep("Parallel", branch) { Timeout = TimeSpan.FromMinutes(60) };
        registry.Register(ReportType.CodeSmells, branch);
        registry.RegisterReportsParallel(parallel);

        Assert.Same(parallel, registry.ReportsParallel);
        Assert.True(registry.TrySetReportsParallelTimeout(TimeSpan.FromMinutes(90)));
        Assert.Equal(TimeSpan.FromMinutes(90), parallel.Timeout);
        // No cuenta como un informe: ni sale en las listas ni en el recuento.
        Assert.Equal(1, registry.Count);
        Assert.Equal([ReportType.CodeSmells], registry.Reports);

        var other = new ParallelStep("Other");
        registry.RegisterReportsParallel(other);
        Assert.Same(other, registry.ReportsParallel);
    }

    [Fact]
    public void Two_registries_never_share_their_parallel()
    {
        var first = new AuditorStepRegistry();
        var second = new AuditorStepRegistry();

        first.RegisterReportsParallel(new ParallelStep("Parallel"));

        Assert.Null(second.ReportsParallel);
    }
}
