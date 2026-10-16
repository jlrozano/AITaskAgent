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
/// <para>Lo que se guarda para el replay está acotado además en tamaño, sin tocar el <c>seq</c> ni lo que reciben los lectores en vivo:
/// las salidas de tool (<c>data.output</c> de <c>tool.completed</c>) suman como mucho <c>maxToolOutputBytes</c>, y al pasarse se quita
/// la de los más antiguos (que quedan con <c>outputDropped: true</c>); y de los <c>sharedpass.progress</c>, que traen el estado entero,
/// solo el último conserva la lista de lotes (los anteriores quedan con <c>superseded: true</c>).</para>
/// </remarks>
public sealed class AnalysisEventLog(string runId, int maxEvents = AnalysisEventLog.DefaultMaxEvents, long maxToolOutputBytes = AnalysisEventLog.DefaultMaxToolOutputBytes)
{
    public const int DefaultMaxEvents = 50_000;

    /// <summary>Bytes UTF-8 de salidas de tool que se conservan para el replay (4 MiB: unas 250 salidas al tope de 16 KiB).</summary>
    public const long DefaultMaxToolOutputBytes = 4L * 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly LinkedList<AnalysisEventDto> _events = new();
    private readonly Queue<(LinkedListNode<AnalysisEventDto> Node, long Bytes)> _outputs = new();
    private long _outputBytes;
    private LinkedListNode<AnalysisEventDto>? _lastSharedPass;
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
            var node = _events.AddLast(stored);
            Bound(node);
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

    /// <summary>Acota lo guardado (ver la clase): las salidas de tool más antiguas y los estados de la pasada ya superados.</summary>
    private void Bound(LinkedListNode<AnalysisEventDto> node)
    {
        var evt = node.Value;
        if (evt.Type == AnalysisEventDtoTypes.ToolCompleted && evt.Data?.GetValueOrDefault("output") is string output)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(output);
            _outputs.Enqueue((node, bytes));
            _outputBytes += bytes;
            while (_outputBytes > maxToolOutputBytes && _outputs.TryDequeue(out var oldest))
            {
                _outputBytes -= oldest.Bytes;
                if (oldest.Node.List != null)
                    oldest.Node.Value = Without(oldest.Node.Value, "output", "outputDropped");
            }
        }
        else if (evt.Type == AnalysisEventDtoTypes.SharedPassProgress)
        {
            if (_lastSharedPass?.List != null)
                _lastSharedPass.Value = Without(_lastSharedPass.Value, "batches", "superseded");
            _lastSharedPass = node;
        }
    }

    private static AnalysisEventDto Without(AnalysisEventDto evt, string key, string flag)
    {
        var data = evt.Data?.Where(kv => kv.Key != key).ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
        data[flag] = true;
        return evt with { Data = data };
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
