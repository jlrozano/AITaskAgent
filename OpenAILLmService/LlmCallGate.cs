using System.Collections.Concurrent;

namespace OpenAILLmService;

/// <summary>
/// Freno compartido de las llamadas a un mismo endpoint+deployment: limita cuántas hay en vuelo a la vez y, cuando el
/// proveedor responde 429/503 con una pista de espera, hace que todas las demás esperen a ese instante en lugar de
/// reintentar cada una por su cuenta.
/// </summary>
/// <remarks>
/// <para>
/// Sin esto, el reintento por petición (<c>Retry-After</c> de cada respuesta) deja a N llamadas concurrentes durmiendo lo
/// mismo y volviendo a salir juntas: la ráfaga se repite y vuelve a recibir 429. Con la pausa compartida solo la primera
/// paga el 429; las demás ni salen hasta que el proveedor dijo que podía volver a recibirlas.
/// </para>
/// <para>
/// La pausa se escalona con un jitter determinista (un contador, no un azar) para que los que esperaban no salgan todos
/// en el mismo milisegundo. El reloj y la espera son inyectables para poder probarlo sin dormir de verdad.
/// </para>
/// </remarks>
internal sealed class LlmCallGate
{
    /// <summary>Techo de una pausa compartida: un <c>Retry-After</c> absurdo no bloquea el análisis entero.</summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(120);

    private readonly SemaphoreSlim? _slots;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _pauseLock = new();
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private int _ticket;

    /// <param name="limit">Llamadas simultáneas máximas; 0 o menos = sin límite (la pausa compartida sí se aplica).</param>
    /// <param name="now">Reloj; inyectable en tests.</param>
    /// <param name="delay">Espera cancelable; inyectable en tests.</param>
    public LlmCallGate(int limit, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Limit = Math.Max(0, limit);
        _slots = Limit > 0 ? new SemaphoreSlim(Limit, Limit) : null;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    public int Limit { get; }

    /// <summary>Escalón del jitter tras una pausa: la espera n-ésima añade <c>(n mod 8) × JitterStep</c>.</summary>
    public TimeSpan JitterStep { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Instante antes del cual no sale ninguna llamada (<see cref="DateTimeOffset.MinValue"/> si no hay pausa).</summary>
    public DateTimeOffset NotBefore { get { lock (_pauseLock) return _notBefore; } }

    /// <summary>
    /// Espera un hueco y, si hay una pausa vigente, a que termine. El hueco se libera al desechar lo devuelto; si la
    /// espera se cancela, el hueco no se pierde.
    /// </summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (_slots != null)
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WaitForPauseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _slots?.Release();
            throw;
        }

        return new Slot(_slots);
    }

    /// <summary>
    /// Registra que el proveedor pidió esperar <paramref name="retryAfter"/>. Se conserva la mayor de las pausas vistas
    /// (acotada a <see cref="MaxPause"/>); una pista nula o de cero no pausa nada.
    /// </summary>
    public void ReportThrottle(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero) return;
        var until = _now() + (retryAfter < MaxPause ? retryAfter : MaxPause);
        lock (_pauseLock)
        {
            if (until > _notBefore) _notBefore = until;
        }
    }

    private async Task WaitForPauseAsync(CancellationToken cancellationToken)
    {
        var waited = false;
        while (true)
        {
            var remaining = NotBefore - _now();
            if (remaining <= TimeSpan.Zero) break;
            waited = true;
            await _delay(remaining, cancellationToken).ConfigureAwait(false);
        }

        if (waited && JitterStep > TimeSpan.Zero)
        {
            var n = (uint)Interlocked.Increment(ref _ticket) % 8;
            if (n > 0) await _delay(TimeSpan.FromTicks(JitterStep.Ticks * n), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Slot(SemaphoreSlim? slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}

/// <summary>
/// Registro de frenos por clave (<c>BaseUrl|deployment</c>). Es estático en la práctica porque el framework crea una
/// instancia del servicio por rol y todas apuntan a los mismos deployments: el límite del proveedor lo comparten ellas,
/// no cada una por separado, y un campo de instancia dejaría a N roles con N frenos independientes.
/// </summary>
internal sealed class LlmCallGateRegistry(
    Func<DateTimeOffset>? now = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeSpan? jitterStep = null)
{
    public static LlmCallGateRegistry Shared { get; } = new();

    private readonly ConcurrentDictionary<string, LlmCallGate> _gates = new();

    /// <summary>Misma URL (sin barra final, sin distinguir mayúsculas) y mismo deployment/modelo = misma clave.</summary>
    public static string KeyOf(string? baseUrl, string? model) =>
        $"{(baseUrl ?? "").TrimEnd('/').ToLowerInvariant()}|{model ?? ""}";

    /// <summary>
    /// El freno de la clave. Gana el límite de quien lo crea primero: roles distintos sobre el mismo deployment
    /// comparten el límite del proveedor, y dos valores distintos para él no tienen sentido.
    /// </summary>
    public LlmCallGate For(string key, int limit) =>
        _gates.GetOrAdd(key, _ => jitterStep is { } step
            ? new LlmCallGate(limit, now, delay) { JitterStep = step }
            : new LlmCallGate(limit, now, delay));
}
