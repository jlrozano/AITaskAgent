using AITaskAgent.Core.Abstractions;
using AITaskAgent.Core.Steps;
using CodebaseAnalysis.Core.Models;

namespace CodebaseAnalysis.Core.Preparation;

/// <summary>
/// Un paso que declara su <c>Timeout</c> asignable sin heredar de <see cref="StepBase"/> (que ya lo tiene como <c>virtual { get; set; }</c>).
/// Lo implementa el envoltorio de modo B, que es un <see cref="IStep"/> escrito a mano: <see cref="IStep.Timeout"/> es de solo lectura
/// en la interfaz, y el presupuesto de tiempo necesita escribirlo.
/// </summary>
public interface IWritableTimeout
{
    TimeSpan? Timeout { get; set; }
}

/// <summary>
/// Lo que el envoltorio de un informe en modo B ofrece al presupuesto de tiempo: la tabla de límites de sus investigaciones. El
/// envoltorio aplica cada límite con un token propio a la investigación de ese subsistema (como el verificador con la suya,
/// <c>VerifyReportStep</c>), porque las invoca directamente y no pasan por <c>TimeoutMiddleware</c>.
/// </summary>
public interface IInvestigationTimeouts
{
    /// <summary>
    /// Sustituye la tabla de límites de las investigaciones, por identificador de subsistema (<c>Subsystem.Id</c>).
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> es sin límite. Un subsistema que no está en la tabla usa el límite que
    /// el envoltorio tenga por defecto.
    /// </summary>
    void SetInvestigationTimeouts(IReadOnlyDictionary<string, TimeSpan> bySubsystemId);
}

/// <summary>
/// Los pasos de los informes de un pipeline, por <see cref="ReportType"/>, para que <c>TimeBudgetStep</c> escriba en ellos el
/// <c>Timeout</c> que calcula (D6). La factoría crea una instancia por pipeline y la entrega por constructor a quien la necesita:
/// no hay estado estático y dos pipelines (o dos ejecuciones) nunca comparten registro.
/// </summary>
/// <remarks>
/// Un paso es de modo B si implementa <see cref="IInvestigationTimeouts"/>; sin ella, es un auditor de modo A (o un informe de modo
/// B que degradó a un auditor normal, que se presupuesta como tal). Registrar un informe otra vez sustituye su paso. Las lecturas
/// devuelven siempre el mismo orden (el del enumerado <see cref="ReportType"/>).
/// </remarks>
public sealed class AuditorStepRegistry
{
    private readonly Lock _lock = new();
    private readonly SortedDictionary<ReportType, IStep> _steps = [];

    /// <summary>Registra el paso que escribe el informe <paramref name="report"/>.</summary>
    public void Register(ReportType report, IStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_lock)
        {
            _steps[report] = step;
        }
    }

    private ParallelStep? _reportsParallel;

    /// <summary>
    /// Registra el paralelo que ejecuta los pasos de los informes (<c>TolerantReportsParallelStep</c>): su <c>Timeout</c> es un
    /// límite de arriba que no puede quedarse por debajo del de ninguna de sus ramas, y <c>TimeBudgetStep</c> lo ajusta con
    /// <see cref="TrySetReportsParallelTimeout"/>. Registrar otro sustituye al anterior.
    /// </summary>
    public void RegisterReportsParallel(ParallelStep parallel)
    {
        ArgumentNullException.ThrowIfNull(parallel);
        lock (_lock)
        {
            _reportsParallel = parallel;
        }
    }

    /// <summary>El paralelo de informes registrado; null si no hay (un registro armado a mano o un pipeline sin paralelo).</summary>
    public ParallelStep? ReportsParallel
    {
        get
        {
            lock (_lock)
            {
                return _reportsParallel;
            }
        }
    }

    /// <summary>Escribe <paramref name="timeout"/> en el paralelo de informes. False si no hay paralelo registrado.</summary>
    public bool TrySetReportsParallelTimeout(TimeSpan timeout)
    {
        if (ReportsParallel is not { } parallel)
            return false;
        parallel.Timeout = timeout;
        return true;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _steps.Count;
            }
        }
    }

    /// <summary>Informes registrados, en el orden del enumerado.</summary>
    public IReadOnlyList<ReportType> Reports
    {
        get
        {
            lock (_lock)
            {
                return [.. _steps.Keys];
            }
        }
    }

    /// <summary>Informes de modo B (su paso implementa <see cref="IInvestigationTimeouts"/>).</summary>
    public IReadOnlyList<ReportType> ModeBReports
    {
        get
        {
            lock (_lock)
            {
                return [.. _steps.Where(s => s.Value is IInvestigationTimeouts).Select(s => s.Key)];
            }
        }
    }

    /// <summary>Informes de modo A: los registrados que no son de modo B.</summary>
    public IReadOnlyList<ReportType> ModeAReports
    {
        get
        {
            lock (_lock)
            {
                return [.. _steps.Where(s => s.Value is not IInvestigationTimeouts).Select(s => s.Key)];
            }
        }
    }

    public IStep? StepOf(ReportType report)
    {
        lock (_lock)
        {
            return _steps.GetValueOrDefault(report);
        }
    }

    public bool IsModeB(ReportType report) => StepOf(report) is IInvestigationTimeouts;

    /// <summary>
    /// Escribe <paramref name="timeout"/> en el paso del informe. True si lo escribió; false si el informe no está registrado o su
    /// paso no deja asignar el <c>Timeout</c> (ni es un <see cref="StepBase"/> ni implementa <see cref="IWritableTimeout"/>).
    /// </summary>
    public bool TrySetTimeout(ReportType report, TimeSpan timeout)
    {
        switch (StepOf(report))
        {
            case StepBase stepBase:
                stepBase.Timeout = timeout;
                return true;
            case IWritableTimeout writable:
                writable.Timeout = timeout;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Entrega al envoltorio de modo B del informe la tabla de límites de sus investigaciones; false si no está registrado como modo B.</summary>
    public bool TrySetInvestigationTimeouts(ReportType report, IReadOnlyDictionary<string, TimeSpan> bySubsystemId)
    {
        if (StepOf(report) is not IInvestigationTimeouts investigations)
            return false;
        investigations.SetInvestigationTimeouts(bySubsystemId);
        return true;
    }
}
