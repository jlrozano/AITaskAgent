using AITaskAgent.Observability;
using Newtonsoft.Json;
using Serilog.Events;
using System.Threading.Channels;

namespace PipelineVisualizer.Services;

/// <summary>
/// Bridges EventChannel and SerilogSseSink to SSE responses.
/// Merges pipeline events and log events into a single SSE stream.
/// </summary>
public sealed class SseEventBroadcaster(
    EventChannel eventChannel,
    SerilogSseSink serilogSink,
    ILogger<SseEventBroadcaster> logger)
{
    private readonly JsonSerializerSettings _jsonSettings = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore
    };

    /// <summary>
    /// Streams all events (pipeline + logs) to the HTTP response as SSE.
    /// </summary>
    public async Task StreamAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        // Send initial comment to flush headers and establish connection
        await response.WriteAsync(": connected\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);

        var subscription = eventChannel.CreateSubscription(1000);
        var logReader = serilogSink.Subscribe();

        logger.LogInformation("SSE client connected");

        try
        {
            await Task.WhenAll(
                StreamPipelineEventsAsync(subscription.Reader, response, cancellationToken),
                StreamLogEventsAsync(logReader, response, cancellationToken)
            );
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SSE client disconnected");
        }
        finally
        {
            await subscription.DisposeAsync();
        }
    }

    private async Task StreamPipelineEventsAsync(
        ChannelReader<IProgressEvent> reader,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        await foreach (var evt in reader.ReadAllAsync(cancellationToken))
        {
            var payload = new
            {
                type = "pipeline",
                eventType = evt.EventType,
                stepName = evt.StepName,
                correlationId = evt.CorrelationId,
                timestamp = evt.Timestamp,
                data = evt
            };

            await WriteEventAsync(response, payload, cancellationToken);
        }
    }

    private async Task StreamLogEventsAsync(
        ChannelReader<LogEvent> reader,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        await foreach (var logEvent in reader.ReadAllAsync(cancellationToken))
        {
            var payload = new
            {
                type = "log",
                level = logEvent.Level.ToString(),
                timestamp = logEvent.Timestamp,
                message = logEvent.RenderMessage(),
                properties = logEvent.Properties.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToString())
            };

            await WriteEventAsync(response, payload, cancellationToken);
        }
    }

    private async Task WriteEventAsync(HttpResponse response, object payload, CancellationToken cancellationToken)
    {
        var json = JsonConvert.SerializeObject(payload, _jsonSettings);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
