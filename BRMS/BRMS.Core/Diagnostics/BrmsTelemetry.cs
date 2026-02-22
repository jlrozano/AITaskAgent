using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BRMS.Core.Diagnostics;

/// <summary>
/// Provee acceso centralizado a la telemetría del motor BRMS.
/// </summary>
public static class BrmsTelemetry
{
    // Usamos el prefijo BDP.BRMS para que sea capturado automáticamente por el filtro "BDP.*" 
    // configurado en los servicios host (BDPService, etc).
    public const string ServiceName = "BDP.BRMS";
    public const string ServiceVersion = "1.0.0";

    // ActivitySource para Trazas Distribuidas
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    // Meter para Métricas
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    // Métricas Predefinidas

    /// <summary>
    /// Contador total de ejecuciones del motor
    /// </summary>
    public static readonly Counter<long> ExecutionsCounter = Meter.CreateCounter<long>(
        "brms.executions.total",
        description: "Número total de ejecuciones del motor de reglas");

    /// <summary>
    /// Histograma de duración de ejecuciones en milisegundos
    /// </summary>
    public static readonly Histogram<double> ExecutionDuration = Meter.CreateHistogram<double>(
        "brms.execution.duration",
        unit: "ms",
        description: "Duración de la ejecución del procesamiento de reglas");

    /// <summary>
    /// Contador de ejecuciones de reglas individuales
    /// </summary>
    public static readonly Counter<long> RuleExecutionsCounter = Meter.CreateCounter<long>(
        "brms.rules.executions",
        description: "Número de ejecuciones de reglas individuales");
}