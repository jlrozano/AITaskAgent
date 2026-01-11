using Serilog.Core;
using Serilog.Events;
using System.Threading.Channels;

namespace PipelineVisualizer.Services;

/// <summary>
/// Custom Serilog sink that broadcasts log events via a Channel for SSE streaming.
/// All log levels are captured and made available to subscribers.
/// </summary>
public sealed class SerilogSseSink : ILogEventSink
{
    private readonly Channel<LogEvent> _channel = Channel.CreateBounded<LogEvent>(
        new BoundedChannelOptions(1000) 
        { 
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

    /// <summary>
    /// Emits a log event to the channel for SSE subscribers.
    /// </summary>
    public void Emit(LogEvent logEvent) => _channel.Writer.TryWrite(logEvent);

    /// <summary>
    /// Subscribes to receive log events. Returns a ChannelReader for async enumeration.
    /// </summary>
    public ChannelReader<LogEvent> Subscribe() => _channel.Reader;
}
