using CodebaseAnalysis.Core.Progress;
using System.Threading.Channels;

namespace CodebaseAnalysisWeb.Analysis;

/// <summary>
/// Eventos de una ejecución, con <c>seq</c> monótono desde 1 y sin huecos, para el replay del SSE (plan §5.3).
/// </summary>
/// <remarks>
/// <see cref="Append"/> y <see cref="Subscribe"/> comparten cerrojo: la copia del replay y el alta del lector
/// en vivo son atómicas respecto a los añadidos, así que un cliente que conecta a mitad no ve ni huecos ni
/// duplicados. Cada lector tiene un canal sin límite propio: un cliente lento no frena al pipeline.
/// </remarks>
public sealed class AnalysisEventLog(string runId, int maxEvents = AnalysisEventLog.DefaultMaxEvents)
{
    public const int DefaultMaxEvents = 50_000;

    private readonly Lock _lock = new();
    private readonly LinkedList<AnalysisEventDto> _events = new();
    private readonly List<Channel<AnalysisEventDto>> _readers = [];
    private long _lastSeq;
    private long _droppedUpToSeq;
    private bool _completed;

    public long LastSeq
    {
        get
        {
            lock (_lock)
            {
                return _lastSeq;
            }
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_lock)
            {
                return _completed;
            }
        }
    }

    /// <summary>Asigna <c>seq</c>, guarda el evento y lo entrega a los lectores. Tras <see cref="Complete"/> se ignora.</summary>
    public AnalysisEventDto? Append(AnalysisEventDto evt)
    {
        lock (_lock)
        {
            if (_completed)
                return null;

            var stored = evt with { Seq = ++_lastSeq, RunId = runId };
            _events.AddLast(stored);
            while (_events.Count > maxEvents)
            {
                _droppedUpToSeq = _events.First!.Value.Seq;
                _events.RemoveFirst();
            }

            foreach (var reader in _readers)
                reader.Writer.TryWrite(stored);

            return stored;
        }
    }

    /// <summary>
    /// Los eventos con <c>seq &gt; after</c> ya guardados y un lector con los que lleguen después. Si se
    /// descartaron eventos que el cliente no ha visto, el replay empieza con un <c>log.truncated</c> cuyo
    /// <c>seq</c> es el último descartado. Con el log cerrado, el lector ya está completado.
    /// </summary>
    public (IReadOnlyList<AnalysisEventDto> Replay, AnalysisEventLogReader Live) Subscribe(long after)
    {
        lock (_lock)
        {
            var replay = new List<AnalysisEventDto>();
            if (_droppedUpToSeq > after)
            {
                replay.Add(new AnalysisEventDto
                {
                    Seq = _droppedUpToSeq,
                    RunId = runId,
                    Ts = DateTimeOffset.UtcNow,
                    Type = AnalysisEventDtoTypes.LogTruncated,
                    Level = AnalysisEventLevels.Warning,
                    Message = $"Se descartaron los eventos hasta el {_droppedUpToSeq} (límite de {maxEvents}).",
                    Data = new Dictionary<string, object?> { ["droppedUpToSeq"] = _droppedUpToSeq }
                });
            }

            replay.AddRange(_events.Where(e => e.Seq > after));

            var channel = Channel.CreateUnbounded<AnalysisEventDto>(new UnboundedChannelOptions { SingleReader = true });
            if (_completed)
                channel.Writer.TryComplete();
            else
                _readers.Add(channel);

            return (replay, new AnalysisEventLogReader(channel.Reader, () => Unsubscribe(channel)));
        }
    }

    /// <summary>Cierra el log: no admite más eventos y completa a los lectores, que terminan su SSE.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            if (_completed)
                return;

            _completed = true;
            foreach (var reader in _readers)
                reader.Writer.TryComplete();
            _readers.Clear();
        }
    }

    /// <summary>
    /// Vuelve a admitir eventos tras <see cref="Complete"/>, con el mismo <c>seq</c>: la regeneración de informes continúa
    /// la ejecución terminada. Los lectores que se completaron al cerrar no vuelven; el cliente reanuda con
    /// <c>after=&lt;último seq&gt;</c>.
    /// </summary>
    public void Reopen()
    {
        lock (_lock)
        {
            _completed = false;
        }
    }

    private void Unsubscribe(Channel<AnalysisEventDto> channel)
    {
        lock (_lock)
        {
            _readers.Remove(channel);
        }
        channel.Writer.TryComplete();
    }
}

/// <summary>Lector en vivo de un <see cref="AnalysisEventLog"/>; al liberarlo se da de baja.</summary>
public sealed class AnalysisEventLogReader(ChannelReader<AnalysisEventDto> reader, Action unsubscribe) : IDisposable
{
    private int _disposed;

    public ChannelReader<AnalysisEventDto> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            unsubscribe();
    }
}
